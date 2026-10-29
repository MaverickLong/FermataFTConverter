using System;
using System.Collections.Generic;
using System.Linq;
using Maberikku.FermataNativeFT.Editor;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(FermataNativeFTPlugin))]

namespace Maberikku.FermataNativeFT.Editor
{
    [RunsOnPlatforms(WellKnownPlatforms.VRChatAvatar30)]
    internal sealed class FermataNativeFTPlugin : Plugin<FermataNativeFTPlugin>
    {
        public override string QualifiedName => "com.maberikku.fermata-native-ft";
        public override string DisplayName => Messages.Title;

        protected override void Configure()
        {
            // After Modular Avatar has merged FermataFT's FX and parameters, but before MA purges its components
            // (FermataFT's ftSet is one of them) and before Avatar Optimizer rewrites animators.
            // FermataFT's own named-layer pass runs later, in PlatformFinish.
            var seq = InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("nadena.dev.modular-avatar.late-transform-stages")
                .BeforePlugin("com.anatawa12.avatar-optimizer");
            seq.WithRequiredExtension(typeof(AnimatorServicesContext), _ =>
            {
                seq.Run("Forward native face tracking into FermataFT", BridgePass.Execute);
            });

            // The list of baked blendshapes only exists for Avatar Optimizer (see FermataNativeFTBakedShapes).
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("com.anatawa12.avatar-optimizer")
                .Run("Remove the baked blendshape list", context =>
                {
                    foreach (var list in context.AvatarRootObject.GetComponentsInChildren<FermataNativeFTBakedShapes>(true))
                        Object.DestroyImmediate(list);
                });
        }
    }

    internal static class BridgePass
    {
        public static void Execute(BuildContext context)
        {
            var markers = context.AvatarRootObject.GetComponentsInChildren<FermataNativeFTBridge>(true);
            if (markers.Length == 0) return;
            try
            {
                Patch(context, markers);
            }
            finally
            {
                foreach (var marker in markers)
                    if (marker) Object.DestroyImmediate(marker);
            }
        }

        static void Patch(BuildContext context, FermataNativeFTBridge[] markers)
        {
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            var controllers = context.Extension<AnimatorServicesContext>().ControllerContext.Controllers;
            controllers.TryGetValue(VRCAvatarDescriptor.AnimLayerType.FX, out var fx);
            var fxLayers = fx != null ? fx.Layers.Select(l => AnimatorScan.Scan(l, "FX")).ToList() : new List<LayerInfo>();

            if (!FermataProfile.IsPresent(descriptor, fxLayers))
            {
                BridgeError.Report(ErrorSeverity.NonFatal, Messages.NoFermata);
                return;
            }
            if (fx == null)
            {
                BridgeError.Report(ErrorSeverity.Error, "The avatar has no FX controller after Modular Avatar ran, so FermataFT's animator cannot be patched.");
                return;
            }

            var otherLayers = controllers
                .Where(kv => kv.Value != null && kv.Value != fx)
                .SelectMany(kv => kv.Value.Layers.Select(l => AnimatorScan.Scan(l, kv.Key.ToString())))
                .ToList();
            var withCustom = markers.Where(m => m.customForwards != null && m.customForwards.Count > 0).ToList();
            var plan = PatchPlanner.Plan(fxLayers, otherLayers, descriptor,
                withCustom.FirstOrDefault()?.customForwards, seesAllLayers: true);
            if (withCustom.Count > 1)
                plan.Error($"{withCustom.Count} {Messages.Title} components have custom expressions. Keep them on one.");

            if (descriptor.expressionParameters == null)
                plan.Error("The avatar has no Expression Parameters after Modular Avatar ran.");
            foreach (var target in plan.Targets)
                if (fx.Parameters.TryGetValue(target.Parameter, out var p) && p.type != AnimatorControllerParameterType.Float)
                    plan.Error($"FX parameter '{target.Parameter}' is a {p.type}; native face tracking can only forward floats.");

            foreach (var finding in plan.Findings.Where(f => f.Severity == Severity.Warning))
                BridgeError.Report(ErrorSeverity.NonFatal, finding.Message);
            if (plan.HasErrors)
            {
                foreach (var finding in plan.Findings.Where(f => f.Severity == Severity.Error))
                    BridgeError.Report(ErrorSeverity.Error, finding.Message);
                return;
            }

            var binaryLayer = (VirtualLayer)plan.BinaryLayer.Source;
            var expectedLayers = fx.Layers.Where(l => l != binaryLayer).Select(l => new LayerSnapshot(l)).ToList();
            var parametersBefore = ParameterSnapshot.Take(descriptor.expressionParameters);

            fx.RemoveLayer(binaryLayer);
            DeclareInputs(fx, plan);
            var (bitsBefore, bitsAfter, removed) = UpdateExpressionParameters(context, descriptor, fx, plan);
            AddBehaviors(descriptor, plan);
            KeepEyesTracked(descriptor, plan);

            var problems = new List<string>();
            CheckLayers(fx, expectedLayers, problems);
            ParameterSnapshot.Check(parametersBefore, ParameterSnapshot.Take(descriptor.expressionParameters), plan, removed, problems);
            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                    BridgeError.Report(ErrorSeverity.Error, "Self-check failed, FermataFT's configuration would not be preserved: " + problem);
                return;
            }

            if (markers.Any(m => m.bakeExactShapes))
                ShapeBake.Run(context, fx, controllers.Values.Where(c => c != null).Distinct().ToList(), plan, descriptor);

            // The SDK only validates the scene avatar, never the behaviors added here, so surface its warnings now.
            foreach (var warning in FaceTrackingExpressionValidator.CollectWarnings(descriptor))
                BridgeError.Report(ErrorSeverity.NonFatal, "VRChat SDK: " + warning);

            var custom = plan.Targets.Count(t => t.SingleKey == null);
            // BridgeError.Report(ErrorSeverity.Information,
            //     $"FermataFT Inputs: {plan.Targets.Count}" +
            //     $"Custom Expressions: {custom}" +
            //     $"Instructions: {plan.ExistingInstructions + plan.NewInstructions}/{NativeExpression.MaxInstructions}" +
            //     $"Removed FX layer: '{FermataProfile.BinaryDecodeLayer}'" +
            //     $"Removed Synced Parameters: {removed.Count} " +
            //     $"Synced bits: {bitsBefore} -> {bitsAfter}" +
            //     $"Eye Lock States: {plan.EyeLockStates.Count}");
        }

        /// <summary>
        /// VRChat produces the gaze and blink tracking keys from its own eye look, which every Tracking Control
        /// "eyes: Animation" stops. FermataFT uses those locks while its face tracking drives the eyes and during
        /// gestures; with OSC input they never stopped the tracking data. So every lock becomes "eyes: Tracking",
        /// and VRChat's eye look is routed to face tracking only, so it neither turns the eye bones nor blinks on top of
        /// FermataFT. With FermataFT's eye tracking off, the eyes therefore stay still.
        /// </summary>
        static void KeepEyesTracked(VRCAvatarDescriptor descriptor, PatchPlan plan)
        {
            if (!plan.UnlockEyes) return;
            foreach (var (_, _, s, _) in plan.EyeLockStates)
            {
                var state = (VirtualState)s;
                var behaviours = state.Behaviours;
                foreach (var control in behaviours.OfType<VRCAnimatorTrackingControl>()
                             .Where(c => c && c.trackingEyes == VRC_AnimatorTrackingControl.TrackingType.Animation).ToList())
                {
                    var copy = Object.Instantiate(control);
                    copy.name = control.name;
                    copy.trackingEyes = VRC_AnimatorTrackingControl.TrackingType.Tracking;
                    behaviours = behaviours.Replace(control, copy);
                }
                state.Behaviours = behaviours;
            }

            descriptor.enableEyeLook = true;
            descriptor.customEyeLookSettings.eyeGazeMode = VRCAvatarDescriptor.CustomEyeLookSettings.EyeGazeMode.FaceTracking;
            descriptor.customEyeLookSettings.eyelidType = VRCAvatarDescriptor.EyelidType.FaceTracking;
        }

        /// <summary>Makes sure every forwarded input exists as a float in the FX controller.</summary>
        static void DeclareInputs(VirtualAnimatorController fx, PatchPlan plan)
        {
            var parameters = fx.Parameters;
            foreach (var target in plan.Targets.Where(t => !parameters.ContainsKey(t.Parameter)))
            {
                parameters = parameters.Add(target.Parameter, new AnimatorControllerParameter
                {
                    name = target.Parameter,
                    type = AnimatorControllerParameterType.Float,
                });
            }
            if (parameters != fx.Parameters) fx.Parameters = parameters;
        }

        /// <summary>
        /// Drops the bit-packed sync parameters and makes every forwarded input a local (unsynced) parameter.
        /// Native face tracking syncs itself, and every client computes the forwarded values on its own.
        /// </summary>
        static (int before, int after, HashSet<string> removed) UpdateExpressionParameters(
            BuildContext context, VRCAvatarDescriptor descriptor, VirtualAnimatorController fx, PatchPlan plan)
        {
            var asset = descriptor.expressionParameters;
            if (!context.IsTemporaryAsset(asset))
            {
                var original = asset;
                asset = Object.Instantiate(original);
                asset.name = original.name;
                context.AssetSaver.SaveAsset(asset);
                descriptor.expressionParameters = asset;
            }

            var bitsBefore = asset.CalcTotalCost();
            var inMenus = MenuParameters(descriptor.expressionsMenu);
            var list = (asset.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>()).ToList();
            var removed = new HashSet<string>(
                list.Where(p => p != null && plan.DecodeOnlyParameters.Contains(p.name) && !inMenus.Contains(p.name)).Select(p => p.name),
                StringComparer.Ordinal);
            list.RemoveAll(p => p != null && removed.Contains(p.name));

            foreach (var target in plan.Targets)
            {
                var existing = list.FirstOrDefault(p => p != null && p.name == target.Parameter);
                if (existing != null)
                {
                    existing.networkSynced = false;
                    continue;
                }
                list.Add(new VRCExpressionParameters.Parameter
                {
                    name = target.Parameter,
                    valueType = VRCExpressionParameters.ValueType.Float,
                    defaultValue = fx.Parameters[target.Parameter].defaultFloat,
                    saved = false,
                    networkSynced = false,
                });
            }

            asset.parameters = list.ToArray();
            return (bitsBefore, asset.CalcTotalCost(), removed);
        }

        static HashSet<string> MenuParameters(VRCExpressionsMenu root)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<VRCExpressionsMenu>();
            var pending = new Stack<VRCExpressionsMenu>();
            if (root) pending.Push(root);
            while (pending.Count > 0)
            {
                var menu = pending.Pop();
                if (!visited.Add(menu) || menu.controls == null) continue;
                foreach (var control in menu.controls)
                {
                    if (control == null) continue;
                    if (!string.IsNullOrEmpty(control.parameter?.name)) names.Add(control.parameter.name);
                    if (control.subParameters != null)
                        foreach (var sub in control.subParameters)
                            if (!string.IsNullOrEmpty(sub?.name)) names.Add(sub.name);
                    if (control.subMenu) pending.Push(control.subMenu);
                }
            }
            return names;
        }

        static void AddBehaviors(VRCAvatarDescriptor descriptor, PatchPlan plan)
        {
            descriptor.enableFaceTracking = true;
            descriptor.useAllTrackingKeys = true;
            if (descriptor.faceTrackingParameterBehaviors == null)
                descriptor.faceTrackingParameterBehaviors = new List<VRCAvatarDescriptor.FaceTrackingParameterBehavior>();
            foreach (var target in plan.Targets)
                descriptor.faceTrackingParameterBehaviors.Add(target.ToBehavior());
        }

        readonly struct LayerSnapshot
        {
            readonly VirtualLayer _layer;
            readonly string _name;
            readonly float _weight;
            readonly AnimatorLayerBlendingMode _blending;
            readonly VirtualStateMachine _stateMachine;

            public LayerSnapshot(VirtualLayer layer)
            {
                _layer = layer;
                _name = layer.Name;
                _weight = layer.DefaultWeight;
                _blending = layer.BlendingMode;
                _stateMachine = layer.StateMachine;
            }

            public bool Matches(VirtualLayer layer) =>
                layer == _layer && layer.Name == _name && layer.DefaultWeight.Equals(_weight)
                && layer.BlendingMode == _blending && layer.StateMachine == _stateMachine;

            public override string ToString() => $"'{_name}'";
        }

        static void CheckLayers(VirtualAnimatorController fx, List<LayerSnapshot> expected, List<string> problems)
        {
            var actual = fx.Layers.ToList();
            if (actual.Count != expected.Count)
            {
                problems.Add($"FX has {actual.Count} layers, expected {expected.Count}.");
                return;
            }
            for (int i = 0; i < actual.Count; i++)
                if (!expected[i].Matches(actual[i]))
                    problems.Add($"FX layer {i} changed (expected {expected[i]}).");
        }

        /// <summary>Expression parameter values relevant to preserving FermataFT's settings.</summary>
        static class ParameterSnapshot
        {
            public static Dictionary<string, (VRCExpressionParameters.ValueType type, bool saved, float value, bool synced)> Take(VRCExpressionParameters asset)
            {
                var result = new Dictionary<string, (VRCExpressionParameters.ValueType, bool, float, bool)>(StringComparer.Ordinal);
                foreach (var p in asset.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>())
                    if (p != null && !string.IsNullOrEmpty(p.name))
                        result[p.name] = (p.valueType, p.saved, p.defaultValue, p.networkSynced);
                return result;
            }

            public static void Check(
                Dictionary<string, (VRCExpressionParameters.ValueType type, bool saved, float value, bool synced)> before,
                Dictionary<string, (VRCExpressionParameters.ValueType type, bool saved, float value, bool synced)> after,
                PatchPlan plan, HashSet<string> removed, List<string> problems)
            {
                var targets = new HashSet<string>(plan.Targets.Select(t => t.Parameter), StringComparer.Ordinal);
                foreach (var kv in before)
                {
                    var found = after.TryGetValue(kv.Key, out var now);
                    if (removed.Contains(kv.Key))
                    {
                        if (found) problems.Add($"'{kv.Key}' should have been removed.");
                    }
                    else if (!found)
                    {
                        problems.Add($"Expression parameter '{kv.Key}' disappeared.");
                    }
                    else if (targets.Contains(kv.Key))
                    {
                        if (now.synced || now.type != kv.Value.type || now.saved != kv.Value.saved || !now.value.Equals(kv.Value.value))
                            problems.Add($"Forwarded input '{kv.Key}' changed more than its sync flag.");
                    }
                    else if (!now.Equals(kv.Value))
                    {
                        problems.Add($"Expression parameter '{kv.Key}' changed.");
                    }
                }
                foreach (var name in after.Keys.Where(n => !before.ContainsKey(n) && !targets.Contains(n)))
                    problems.Add($"Unexpected new expression parameter '{name}'.");
            }
        }
    }
}
