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

    /// <summary>One animator parameter that native face tracking will drive.</summary>
    internal sealed class ForwardTarget
    {
        /// <summary>Animator parameter written by the behavior, e.g. "v2/JawOpen".</summary>
        public readonly string Parameter;

        /// <summary>The expression that will run: the custom one if there is one, otherwise the built-in formula.</summary>
        public readonly string Expression;

        /// <summary>The built-in formula, or null when only a custom expression forwards this parameter.</summary>
        public readonly string BuiltIn;

        public readonly bool IsCustom;

        /// <summary>Set when the expression is a bare tracking key; it is then forwarded with combine mode Max and costs no instructions.</summary>
        public readonly string SingleKey;

        public readonly int Instructions;
        public readonly IReadOnlyList<string> Inputs;

        public ForwardTarget(string parameter, string expression, string builtIn, bool isCustom)
        {
            Parameter = parameter;
            Expression = expression.Trim();
            BuiltIn = builtIn;
            IsCustom = isCustom;
            var check = NativeExpression.Check(Expression);
            Inputs = check.Inputs;
            SingleKey = NativeExpression.IsSingleKey(Expression) ? Expression : null;
            Instructions = SingleKey != null ? 0 : check.Instructions;
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

        /// <summary>States whose Tracking Control hands the eyes to animation, in every remaining layer.</summary>
        public readonly List<(LayerInfo Layer, string Name, object State, bool TurnsEyeTrackingOn)> EyeLockStates =
            new List<(LayerInfo, string, object, bool)>();

        /// <summary>
        /// VRChat produces the gaze and blink tracking keys from its own eye look, which every eye lock stops. FermataFT
        /// locks the eyes whenever its face tracking drives them, so they must all stay on tracking instead, with VRChat's
        /// own eye look routed to face tracking so it has no visible effect of its own.
        /// </summary>
        public bool UnlockEyes => EyeLockStates.Any(s => s.TurnsEyeTrackingOn);

        public int ExistingBehaviors;
        public int ExistingInstructions;

        public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);
        public int NewInstructions => Targets.Sum(t => t.Instructions);

        public void Error(string message) => Findings.Add(new Finding(Severity.Error, message));
        public void Warning(string message) => Findings.Add(new Finding(Severity.Warning, message));
    }

    /// <summary>
    /// Decides what to change, from scanned layers. Shared by the inspector checks and the build pass so both
    /// report the same problems.
    /// </summary>
    internal static class PatchPlanner
    {
        /// <param name="fxLayers">All layers of the merged FX controller, in order.</param>
        /// <param name="otherLayers">Layers of the other playable controllers.</param>
        /// <param name="customForwards">The bridge's custom expressions.</param>
        /// <param name="seesAllLayers">False when only the FX layers were scanned, as in the inspector.</param>
        public static PatchPlan Plan(IReadOnlyList<LayerInfo> fxLayers, IEnumerable<LayerInfo> otherLayers, VRCAvatarDescriptor descriptor,
            IEnumerable<CustomForward> customForwards, bool seesAllLayers)
        {
            var plan = new PatchPlan();
            plan.BinaryLayer = FindLayer(fxLayers, FermataProfile.BinaryDecodeLayer, plan);
            plan.SmoothingLayer = FindLayer(fxLayers, FermataProfile.SmoothingLayer, plan);
            if (plan.HasErrors) return plan;

            var kept = fxLayers.Where(l => l != plan.BinaryLayer).Concat(otherLayers).ToList();
            var keptReads = new HashSet<string>(kept.SelectMany(l => l.Reads), StringComparer.Ordinal);
            var custom = ResolveCustom(customForwards, plan);

            // Every v2 input still read after the binary layer is gone must be driven natively.
            var detected = keptReads.Where(p => p.StartsWith(FermataProfile.InputPrefix, StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (var parameter in detected)
            {
                V2Formulas.TryGet(parameter.Substring(FermataProfile.InputPrefix.Length), out var builtIn);
                if (custom.TryGetValue(parameter, out var expression))
                {
                    plan.Targets.Add(new ForwardTarget(parameter, expression, builtIn, true));
                }
                else if (builtIn == null)
                {
                    plan.Error($"'{parameter}' is read by {ReadersOf(parameter, kept)}, but there is no built-in formula for it. Add a custom expression for it.");
                }
                else if (!NativeExpression.Check(builtIn).IsValid)
                {
                    plan.Error($"Internal error: the built-in formula for '{parameter}' is invalid: {string.Join(" ", NativeExpression.Check(builtIn).Errors)}");
                }
                else
                {
                    plan.Targets.Add(new ForwardTarget(parameter, builtIn, builtIn, false));
                }
            }

            // Custom expressions for parameters FermataFT doesn't read are extra forwards, e.g. for the avatar's own animators.
            foreach (var pair in custom.Where(c => !detected.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                var builtIn = pair.Key.StartsWith(FermataProfile.InputPrefix, StringComparison.Ordinal)
                              && V2Formulas.TryGet(pair.Key.Substring(FermataProfile.InputPrefix.Length), out var formula)
                    ? formula
                    : null;
                plan.Targets.Add(new ForwardTarget(pair.Key, pair.Value, builtIn, true));
                if (seesAllLayers && !keptReads.Contains(pair.Key))
                    plan.Warning($"No animator layer reads '{pair.Key}', so its custom expression only adds a forwarded parameter.");
            }

            if (descriptor.lipSync != VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShapeFaceTracking)
                foreach (var target in plan.Targets.Where(t => t.Inputs.Any(NativeExpression.IsViseme)))
                    plan.Warning($"The expression for '{target.Parameter}' reads visemes, which only have values with LipSync Mode 'Viseme BlendShape Face Tracking'.");

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

            foreach (var layer in kept)
            foreach (var (name, state, turnsOn) in layer.EyeLockStates)
                plan.EyeLockStates.Add((layer, name, state, turnsOn));

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

        /// <summary>Valid custom expressions by parameter. Rows left completely blank are ignored; anything else invalid is an error.</summary>
        static Dictionary<string, string> ResolveCustom(IEnumerable<CustomForward> customForwards, PatchPlan plan)
        {
            var custom = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in customForwards ?? Enumerable.Empty<CustomForward>())
            {
                var name = entry?.parameter?.Trim() ?? "";
                var expression = entry?.expression?.Trim() ?? "";
                if (name.Length == 0 && expression.Length == 0) continue;
                if (name.Length == 0)
                {
                    plan.Error($"The custom expression '{expression}' has no parameter name.");
                    continue;
                }
                if (!seen.Add(name))
                {
                    plan.Error($"'{name}' has more than one custom expression; keep one.");
                    continue;
                }
                var check = NativeExpression.Check(expression);
                if (!check.IsValid)
                {
                    plan.Error($"The custom expression for '{name}' is invalid: {string.Join(" ", check.Errors)}");
                    continue;
                }
                custom[name] = expression;
            }
            return custom;
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
