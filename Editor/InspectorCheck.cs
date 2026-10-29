using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Maberikku.FermataNativeFT.Editor
{
    internal sealed class InspectorReport
    {
        public readonly List<Finding> Findings = new List<Finding>();

        /// <summary>Set once FermataFT was found and its animators were analysed.</summary>
        public PatchPlan Plan;

        public int SyncedBitsFreed;
    }

    /// <summary>
    /// Runs the build's checks against the avatar as authored, before Modular Avatar merges anything,
    /// so problems show up in the inspector instead of at upload.
    /// </summary>
    internal static class InspectorCheck
    {
        static readonly AnimatorScan.ClipCache Clips = new AnimatorScan.ClipCache();

        public static InspectorReport Run(FermataNativeFTBridge marker)
        {
            var report = new InspectorReport();
            var descriptor = marker.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (EditorUtility.IsPersistent(marker) || (!descriptor && PrefabStageUtility.GetPrefabStage(marker.gameObject) != null))
            {
                report.Findings.Add(new Finding(Severity.Info, "Drag this prefab onto your avatar, next to the FermataFT prefab."));
                return report;
            }

            if (!descriptor)
            {
                report.Findings.Add(new Finding(Severity.Error, "This prefab must be placed inside an avatar."));
                return report;
            }

            var markers = descriptor.GetComponentsInChildren<FermataNativeFTBridge>(true)
                .Where(m => m.GetComponentInParent<VRCAvatarDescriptor>(true) == descriptor)
                .ToList();
            if (markers.Count > 1)
                report.Findings.Add(new Finding(Severity.Info, $"This avatar has {markers.Count} {Messages.Title} components (one is enough)."));
            if (markers.Count(m => m.customForwards != null && m.customForwards.Count > 0) > 1)
                report.Findings.Add(new Finding(Severity.Error, $"Several {Messages.Title} components have custom expressions (keep them on one)."));

            var settings = FermataProfile.FindSettings(descriptor);
            if (settings.Count > 1)
            {
                report.Findings.Add(new Finding(Severity.Error, "This avatar has more than one FermataFT prefab (one is enough)."));
                return report;
            }

            var fxLayers = CollectFxLayers(descriptor);
            if (settings.Count == 0)
            {
                if (!FermataProfile.HasOscmoothLayers(fxLayers))
                {
                    report.Findings.Add(new Finding(Severity.Warning, Messages.NoFermata));
                    return report;
                }
                report.Findings.Add(new Finding(Severity.Info,
                    "No FermataFT prefab was found, but the avatar's FX contains FermataFT's OSCmooth layers " +
                    "(an older install that edited the FX directly). Those layers will be patched."));
            }

            report.Plan = PatchPlanner.Plan(fxLayers, Enumerable.Empty<LayerInfo>(), descriptor, marker.customForwards, seesAllLayers: false);
            report.Findings.AddRange(report.Plan.Findings);
            if (!report.Plan.HasErrors) report.SyncedBitsFreed = EstimateFreedBits(descriptor, report.Plan);
            return report;
        }

        /// <summary>The FX layers Modular Avatar will merge: the avatar's own FX plus every FX Merge Animator.</summary>
        static List<LayerInfo> CollectFxLayers(VRCAvatarDescriptor descriptor)
        {
            var merges = descriptor.GetComponentsInChildren<ModularAvatarMergeAnimator>(true)
                .Where(m => m.layerType == VRCAvatarDescriptor.AnimLayerType.FX
                            && m.GetComponentInParent<VRCAvatarDescriptor>(true) == descriptor)
                .ToList();

            var controllers = new List<AnimatorController>();
            if (!merges.Any(m => m.mergeAnimatorMode == MergeAnimatorMode.Replace))
            {
                foreach (var layer in descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
                    if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX && !layer.isDefault && layer.animatorController is AnimatorController ac)
                        controllers.Add(ac);
            }
            foreach (var merge in merges)
                if (merge.animator is AnimatorController ac)
                    controllers.Add(ac);

            return controllers.SelectMany(c => c.layers.Select(l => AnimatorScan.Scan(l, c.name, Clips))).ToList();
        }

        /// <summary>Approximate synced bits that the build frees, from Modular Avatar Parameters and the descriptor's own parameters.</summary>
        static int EstimateFreedBits(VRCAvatarDescriptor descriptor, PatchPlan plan)
        {
            var affected = new HashSet<string>(plan.Targets.Select(t => t.Parameter).Concat(plan.DecodeOnlyParameters));
            var bits = new Dictionary<string, int>();

            foreach (var component in descriptor.GetComponentsInChildren<ModularAvatarParameters>(true))
            foreach (var p in component.parameters)
            {
                var name = string.IsNullOrEmpty(p.remapTo) ? p.nameOrPrefix : p.remapTo;
                if (p.isPrefix || p.internalParameter || p.localOnly || p.syncType == ParameterSyncType.NotSynced || !affected.Contains(name)) continue;
                bits[name] = p.syncType == ParameterSyncType.Bool ? 1 : 8;
            }

            var own = descriptor.customExpressions ? descriptor.expressionParameters : null;
            if (own != null && own.parameters != null)
                foreach (var p in own.parameters)
                    if (p != null && p.networkSynced && affected.Contains(p.name))
                        bits[p.name] = p.valueType == VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType.Bool ? 1 : 8;

            return bits.Values.Sum();
        }
    }
}
