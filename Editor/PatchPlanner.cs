using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.Components;

namespace Maberikku.FermataNativeFT.Editor
{
    internal enum Severity
    {
        Info,
        Warning,
        Error
    }

    internal readonly struct Finding
    {
        public readonly Severity Severity;
        public readonly string Message;

        public Finding(Severity severity, string message)
        {
            Severity = severity;
            Message = message;
        }
    }

    /// <summary>One FermataFT input that native face tracking will drive.</summary>
    internal sealed class ForwardTarget
    {
        /// <summary>Animator parameter written by the behavior, e.g. "v2/JawOpen".</summary>
        public readonly string Parameter;

        public readonly string Expression;

        /// <summary>Set when the expression is a bare tracking key; it is then forwarded with combine mode Max and costs no instructions.</summary>
        public readonly string SingleKey;

        public readonly int Instructions;

        public ForwardTarget(string parameter, string expression)
        {
            Parameter = parameter;
            Expression = expression;
            SingleKey = NativeExpression.IsSingleKey(expression) ? expression.Trim() : null;
            Instructions = SingleKey != null ? 0 : NativeExpression.CountInstructions(expression);
        }

        public VRCAvatarDescriptor.FaceTrackingParameterBehavior ToBehavior()
        {
            var behavior = new VRCAvatarDescriptor.FaceTrackingParameterBehavior
            {
                targetMode = VRCAvatarDescriptor.ParameterBehaviorTargetMode.ForwardToAnimator,
                outputParameterName = Parameter,
                targetBlendSetName = "",
                targetBlendshapeName = "",
                weightBlendSetName = "",
            };
            if (SingleKey != null)
            {
                var key = (VRCAvatarDescriptor.FaceTrackingKey)Enum.Parse(typeof(VRCAvatarDescriptor.FaceTrackingKey), SingleKey);
                behavior.combineMode = VRCAvatarDescriptor.ParameterBehaviorCombineMode.Max;
                behavior.inputTrackingKeys = new[] { (int)key };
                behavior.inputTrackingKeyNames = new[] { SingleKey };
                behavior.inputInverted = new[] { false };
                behavior.customExpression = "";
            }
            else
            {
                behavior.combineMode = VRCAvatarDescriptor.ParameterBehaviorCombineMode.Custom;
                behavior.customExpression = Expression;
                behavior.inputTrackingKeys = Array.Empty<int>();
                behavior.inputTrackingKeyNames = Array.Empty<string>();
                behavior.inputInverted = Array.Empty<bool>();
            }
            return behavior;
        }
    }

    internal sealed class PatchPlan
    {
        public readonly List<Finding> Findings = new List<Finding>();
        public LayerInfo BinaryLayer;
        public LayerInfo SmoothingLayer;
        public readonly List<ForwardTarget> Targets = new List<ForwardTarget>();

        /// <summary>Parameters that only the binary decode layer reads: Fermata's bit-packed sync, which goes away with it.</summary>
        public readonly HashSet<string> DecodeOnlyParameters = new HashSet<string>(StringComparer.Ordinal);

        public int ExistingBehaviors;
        public int ExistingInstructions;

        public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);
        public int NewInstructions => Targets.Sum(t => t.Instructions);

        public void Error(string message) => Findings.Add(new Finding(Severity.Error, message));
    }

    /// <summary>
    /// Decides what to change, from scanned layers. Shared by the inspector checks and the build pass so both
    /// report the same problems.
    /// </summary>
    internal static class PatchPlanner
    {
        /// <param name="fxLayers">All layers of the merged FX controller, in order.</param>
        /// <param name="otherLayers">Layers of the other playable controllers.</param>
        public static PatchPlan Plan(IReadOnlyList<LayerInfo> fxLayers, IEnumerable<LayerInfo> otherLayers, VRCAvatarDescriptor descriptor)
        {
            var plan = new PatchPlan();
            plan.BinaryLayer = FindLayer(fxLayers, FermataProfile.BinaryDecodeLayer, plan);
            plan.SmoothingLayer = FindLayer(fxLayers, FermataProfile.SmoothingLayer, plan);
            if (plan.HasErrors) return plan;

            var kept = fxLayers.Where(l => l != plan.BinaryLayer).Concat(otherLayers).ToList();
            var keptReads = new HashSet<string>(kept.SelectMany(l => l.Reads), StringComparer.Ordinal);

            // Every v2 input still read after the binary layer is gone must be driven natively.
            var unknown = new List<string>();
            foreach (var parameter in keptReads.Where(p => p.StartsWith(FermataProfile.InputPrefix, StringComparison.Ordinal))
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var v2Name = parameter.Substring(FermataProfile.InputPrefix.Length);
                if (!V2Formulas.TryGet(v2Name, out var expression))
                {
                    unknown.Add(parameter);
                    continue;
                }
                var badInputs = NativeExpression.Inputs(expression).Where(i => !NativeExpression.TrackingKeys.Contains(i)).ToList();
                if (badInputs.Count > 0)
                {
                    plan.Error($"Internal error: the formula for '{parameter}' uses unknown tracking keys: {string.Join(", ", badInputs)}.");
                    continue;
                }
                plan.Targets.Add(new ForwardTarget(parameter, expression));
            }
            foreach (var parameter in unknown)
                plan.Error($"'{parameter}' is read by {ReadersOf(parameter, kept)}, but there is no native face tracking formula for it.");

            // Nothing that stays may animate a forwarded input, or it would fight the native value.
            foreach (var layer in kept)
            foreach (var target in plan.Targets)
                if (layer.Writes.Contains(target.Parameter))
                    plan.Error($"Layer {layer} animates '{target.Parameter}', which native face tracking will drive. Remove that animation or ask for this case to be supported.");

            // Anything else the binary layer produced must not be needed by a remaining layer.
            var targetNames = new HashSet<string>(plan.Targets.Select(t => t.Parameter), StringComparer.Ordinal);
            foreach (var parameter in plan.BinaryLayer.Writes.Where(keptReads.Contains).Where(p => !targetNames.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
                plan.Error($"'{parameter}' is decoded by {plan.BinaryLayer} and read by {ReadersOf(parameter, kept)}, but it cannot be driven by native face tracking.");

            // FermataFT resolves its named layer controls after this tool runs and fails if a name is gone.
            foreach (var layer in kept.Where(l => l.NamedLayerTargets.Contains(FermataProfile.BinaryDecodeLayer)))
                plan.Error($"Layer {layer} controls '{FermataProfile.BinaryDecodeLayer}' by name, so that layer cannot be removed. This FermataFT version is not supported yet.");

            if (!plan.Targets.Any(t => plan.SmoothingLayer.Reads.Contains(t.Parameter)))
                plan.Error($"Layer {plan.SmoothingLayer} reads none of the '{FermataProfile.InputPrefix}*' inputs. This FermataFT layout is not supported.");

            foreach (var parameter in plan.BinaryLayer.Reads)
                if (!keptReads.Contains(parameter))
                    plan.DecodeOnlyParameters.Add(parameter);

            CheckDescriptor(plan, descriptor);
            return plan;
        }

        static LayerInfo FindLayer(IReadOnlyList<LayerInfo> layers, string name, PatchPlan plan)
        {
            var matches = layers.Where(l => l.Name == name).ToList();
            if (matches.Count == 1) return matches[0];
            plan?.Error(matches.Count == 0
                ? $"FermataFT's FX layer '{name}' was not found. This FermataFT version or layout is not supported."
                : $"FermataFT's FX layer '{name}' appears {matches.Count} times; it must appear exactly once. Check for duplicated FermataFT prefabs.");
            return null;
        }

        static string ReadersOf(string parameter, IEnumerable<LayerInfo> layers)
        {
            return string.Join(", ", layers.Where(l => l.Reads.Contains(parameter)).Select(l => l.ToString()));
        }

        static void CheckDescriptor(PatchPlan plan, VRCAvatarDescriptor descriptor)
        {
            var existing = descriptor.faceTrackingParameterBehaviors?.Where(b => b != null).ToList()
                           ?? new List<VRCAvatarDescriptor.FaceTrackingParameterBehavior>();
            plan.ExistingBehaviors = existing.Count;
            plan.ExistingInstructions = existing
                .Where(b => b.combineMode == VRCAvatarDescriptor.ParameterBehaviorCombineMode.Custom && !string.IsNullOrWhiteSpace(b.customExpression))
                .Sum(b => NativeExpression.CountInstructions(b.customExpression));

            var targetNames = new HashSet<string>(plan.Targets.Select(t => t.Parameter), StringComparer.Ordinal);
            foreach (var b in existing)
                if (b.targetMode == VRCAvatarDescriptor.ParameterBehaviorTargetMode.ForwardToAnimator && targetNames.Contains(b.outputParameterName))
                    plan.Error($"The avatar descriptor already has a face tracking behavior forwarding to '{b.outputParameterName}'. Remove it; this tool sets it up at upload.");

            if (plan.ExistingBehaviors + plan.Targets.Count > NativeExpression.MaxBehaviors)
                plan.Error($"The avatar would have {plan.ExistingBehaviors + plan.Targets.Count} face tracking parameter behaviors; VRChat allows {NativeExpression.MaxBehaviors}.");

            var total = plan.ExistingInstructions + plan.NewInstructions;
            if (total > NativeExpression.MaxInstructions)
                plan.Error($"Face tracking custom expressions would use about {total} instructions ({plan.ExistingInstructions} already on the avatar); VRChat allows {NativeExpression.MaxInstructions} and would skip some.");
        }
    }
}
