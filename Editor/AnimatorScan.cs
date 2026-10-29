using System;
using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>Which animator parameters one layer reads and which it animates.</summary>
    internal sealed class LayerInfo
    {
        public readonly string Name;
        public readonly string Controller;
        public readonly HashSet<string> Reads = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> Writes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>FX layers that FermataFT's named layer-control behaviours in this layer refer to by name.</summary>
        public readonly HashSet<string> NamedLayerTargets = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The scanned layer: a VirtualLayer at build time, an AnimatorControllerLayer in the inspector.</summary>
        public readonly object Source;

        public LayerInfo(string name, string controller, object source)
        {
            Name = name;
            Controller = controller;
            Source = source;
        }

        public override string ToString() => string.IsNullOrEmpty(Controller) ? $"'{Name}'" : $"'{Name}' ({Controller})";
    }

    internal static class AnimatorScan
    {
        /// <summary>Scans a layer of an NDMF virtual controller (build time).</summary>
        public static LayerInfo Scan(VirtualLayer layer, string controller)
        {
            var info = new LayerInfo(layer.Name, controller, layer);
            foreach (var node in layer.AllReachableNodes())
            {
                switch (node)
                {
                    case VirtualStateMachine sm:
                        ScanBehaviours(sm.Behaviours, info);
                        foreach (var t in sm.EntryTransitions) ScanConditions(t.Conditions, info);
                        foreach (var t in sm.AnyStateTransitions) ScanConditions(t.Conditions, info);
                        foreach (var list in sm.StateMachineTransitions.Values)
                        foreach (var t in list)
                            ScanConditions(t.Conditions, info);
                        break;
                    case VirtualState state:
                        ScanBehaviours(state.Behaviours, info);
                        AddRead(info, state.SpeedParameter);
                        AddRead(info, state.TimeParameter);
                        AddRead(info, state.CycleOffsetParameter);
                        AddRead(info, state.MirrorParameter);
                        break;
                    case VirtualTransitionBase transition:
                        ScanConditions(transition.Conditions, info);
                        break;
                    case VirtualBlendTree tree:
                        ScanBlendTreeParameters(tree.BlendType, tree.BlendParameter, tree.BlendParameterY, info);
                        if (tree.BlendType == BlendTreeType.Direct)
                            foreach (var child in tree.Children)
                                AddRead(info, child.DirectBlendParameter);
                        break;
                    case VirtualClip clip:
                        foreach (var binding in clip.GetFloatCurveBindings())
                            if (IsAnimatorParameter(binding)) info.Writes.Add(binding.propertyName);
                        break;
                }
            }
            return info;
        }

        /// <summary>Scans a layer of an original controller asset, read-only (inspector checks).</summary>
        public static LayerInfo Scan(AnimatorControllerLayer layer, string controller, ClipCache clips)
        {
            var info = new LayerInfo(layer.name, controller, layer);
            ScanStateMachine(layer.stateMachine, info, new HashSet<Object>(), clips);
            return info;
        }

        static void ScanStateMachine(AnimatorStateMachine sm, LayerInfo info, HashSet<Object> visited, ClipCache clips)
        {
            if (!sm || !visited.Add(sm)) return;
            ScanBehaviours(sm.behaviours, info);
            foreach (var t in sm.entryTransitions) ScanConditions(t.conditions, info);
            foreach (var t in sm.anyStateTransitions) ScanConditions(t.conditions, info);
            foreach (var child in sm.stateMachines)
            {
                foreach (var t in sm.GetStateMachineTransitions(child.stateMachine)) ScanConditions(t.conditions, info);
                ScanStateMachine(child.stateMachine, info, visited, clips);
            }
            foreach (var child in sm.states)
            {
                var state = child.state;
                if (!state) continue;
                ScanBehaviours(state.behaviours, info);
                foreach (var t in state.transitions) ScanConditions(t.conditions, info);
                if (state.speedParameterActive) AddRead(info, state.speedParameter);
                if (state.timeParameterActive) AddRead(info, state.timeParameter);
                if (state.cycleOffsetParameterActive) AddRead(info, state.cycleOffsetParameter);
                if (state.mirrorParameterActive) AddRead(info, state.mirrorParameter);
                ScanMotion(state.motion, info, visited, clips);
            }
        }

        static void ScanMotion(Motion motion, LayerInfo info, HashSet<Object> visited, ClipCache clips)
        {
            if (!motion || !visited.Add(motion)) return;
            if (motion is BlendTree tree)
            {
                ScanBlendTreeParameters(tree.blendType, tree.blendParameter, tree.blendParameterY, info);
                foreach (var child in tree.children)
                {
                    if (tree.blendType == BlendTreeType.Direct) AddRead(info, child.directBlendParameter);
                    ScanMotion(child.motion, info, visited, clips);
                }
            }
            else if (motion is AnimationClip clip)
            {
                info.Writes.UnionWith(clips.AnimatorParameters(clip));
            }
        }

        static void ScanBlendTreeParameters(BlendTreeType type, string x, string y, LayerInfo info)
        {
            switch (type)
            {
                case BlendTreeType.Direct:
                    break;
                case BlendTreeType.Simple1D:
                    AddRead(info, x);
                    break;
                default:
                    AddRead(info, x);
                    AddRead(info, y);
                    break;
            }
        }

        static void ScanConditions(IEnumerable<AnimatorCondition> conditions, LayerInfo info)
        {
            foreach (var condition in conditions) AddRead(info, condition.parameter);
        }

        static void ScanBehaviours(IEnumerable<StateMachineBehaviour> behaviours, LayerInfo info)
        {
            foreach (var behaviour in behaviours)
            {
                if (behaviour && behaviour.GetType().FullName == FermataProfile.NamedLayerControlTypeName
                    && behaviour.GetType().GetField(FermataProfile.NamedLayerControlTargetField)?.GetValue(behaviour) is string target)
                    info.NamedLayerTargets.Add(target);
                if (!(behaviour is VRCAvatarParameterDriver driver) || driver.parameters == null) continue;
                foreach (var p in driver.parameters)
                {
                    if (p == null) continue;
                    info.Writes.Add(p.name);
                    if (p.type == VRC_AvatarParameterDriver.ChangeType.Copy) AddRead(info, p.source);
                    if (p.type == VRC_AvatarParameterDriver.ChangeType.Add) AddRead(info, p.name);
                }
            }
        }

        static void AddRead(LayerInfo info, string parameter)
        {
            if (!string.IsNullOrEmpty(parameter)) info.Reads.Add(parameter);
        }

        static bool IsAnimatorParameter(EditorCurveBinding binding)
        {
            return binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path);
        }

        /// <summary>Caches the animator parameters each clip animates, keyed by clip and its dirty count.</summary>
        internal sealed class ClipCache
        {
            readonly Dictionary<(int, int), string[]> _cache = new Dictionary<(int, int), string[]>();

            public string[] AnimatorParameters(AnimationClip clip)
            {
                var key = (clip.GetInstanceID(), EditorUtility.GetDirtyCount(clip));
                if (_cache.TryGetValue(key, out var parameters)) return parameters;
                var list = new List<string>();
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (IsAnimatorParameter(binding)) list.Add(binding.propertyName);
                return _cache[key] = list.ToArray();
            }
        }
    }
}
