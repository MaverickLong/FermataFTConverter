using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>
    /// The value native face tracking feeds one baked blendshape: a custom expression, or a single tracking key
    /// (inverted or not) that costs no instructions.
    /// </summary>
    internal sealed class BakeDriver
    {
        public readonly string Expression;
        public readonly string InputKey;
        public readonly bool Inverted;
        public readonly int Instructions;
        public readonly bool IsValid;
        public readonly string Key;

        BakeDriver(string expression, string inputKey, bool inverted)
        {
            Expression = expression;
            InputKey = inputKey;
            Inverted = inverted;
            if (inputKey != null)
            {
                IsValid = true;
                Key = (inverted ? "1-" : "") + inputKey;
                return;
            }
            var check = NativeExpression.Check(expression);
            IsValid = check.IsValid;
            Instructions = check.Instructions;
            Key = expression;
        }

        public static BakeDriver Custom(string expression) => new BakeDriver(expression, null, false);

        public static BakeDriver Passthrough(string key, bool inverted) => new BakeDriver(null, key, inverted);

        public VRCAvatarDescriptor.FaceTrackingParameterBehavior ToBehavior(string blendshape, string weightSet)
        {
            var behavior = new VRCAvatarDescriptor.FaceTrackingParameterBehavior
            {
                targetMode = VRCAvatarDescriptor.ParameterBehaviorTargetMode.ApplyToBlendshape,
                outputParameterName = "",
                targetBlendSetName = "",
                targetBlendshapeName = blendshape,
                weightBlendSetName = weightSet ?? "",
            };
            if (InputKey != null)
            {
                var key = (VRCAvatarDescriptor.FaceTrackingKey)Enum.Parse(typeof(VRCAvatarDescriptor.FaceTrackingKey), InputKey);
                behavior.combineMode = VRCAvatarDescriptor.ParameterBehaviorCombineMode.Max;
                behavior.inputTrackingKeys = new[] { (int)key };
                behavior.inputTrackingKeyNames = new[] { InputKey };
                behavior.inputInverted = new[] { Inverted };
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

    /// <summary>
    /// The product of a term's factors on ordinary animator parameters (menu toggles, gesture fades, ...). It becomes a
    /// Blendshapes Set whose weight the animator keeps equal to that product, and the set scales the term's behaviors.
    /// </summary>
    internal sealed class BakeGate
    {
        public readonly IReadOnlyList<Factor> Factors;
        public readonly string Key;
        public string Name;

        public BakeGate(IReadOnlyList<Factor> factors, string key)
        {
            Factors = factors;
            Key = key;
        }
    }

    /// <summary>One native behavior and the blendshape it drives: a driver, a gate, and what the blendshape moves.</summary>
    internal sealed class BakePiece
    {
        public readonly string Key;
        public readonly BakeDriver Driver;
        public readonly BakeGate Gate;

        /// <summary>Source blendshape to its weight (in Unity's 0..100 units) when the driver is 1 and the gate is open.</summary>
        public readonly Dictionary<(string Path, string Name), float> Contributions = new Dictionary<(string, string), float>();

        public string Blendshape;

        public BakePiece(string key, BakeDriver driver, BakeGate gate)
        {
            Key = key;
            Driver = driver;
            Gate = gate;
        }
    }

    /// <summary>
    /// Bakes the parts of FermataFT's face that native face tracking can reproduce exactly. A single-state, Write
    /// Defaults blend-tree layer that reads forwarded inputs is rewritten as a sum of terms per blendshape (see
    /// <see cref="BlendTreeTerms"/>). A term moves to native face tracking when it depends on forwarded inputs through
    /// piecewise linear factors, and on other animator parameters only through factors the animator can still compute.
    /// The term's clips stop animating that blendshape, and new blendshapes made from the same mesh deltas take over:
    /// <list type="bullet">
    /// <item>one input: one blendshape per linear segment of its curve, driven by that segment clamped to 0..1;</item>
    /// <item>several inputs: one blendshape driven by the whole product as a custom expression.</item>
    /// </list>
    /// Factors on other parameters are multiplied in the animator into a parameter that a Blendshapes Set copies every
    /// frame, and the set scales the term's behaviors. Parameters that only ever hold 0 or 1 merge their factors, so
    /// branches FermataFT duplicates per toggle collapse. Everything else keeps running in the animator on the forwarded
    /// inputs. Selection runs in rounds: a forwarded input the animator no longer reads loses its behavior, and the freed
    /// budget goes to the next round.
    /// </summary>
    internal static class ShapeBake
    {
        const string NamePrefix = "FNFT/";
        /// <summary>A float this tool adds with default 1 and never writes, for Direct children that always play.</summary>
        internal const string OneParameter = NamePrefix + "One";
        const string GatePrefix = NamePrefix + "Gate/";
        const string ConstPrefix = NamePrefix + "Const/";
        const int MaxSets = 128;
        const int MaxMeshes = 16;
        const int MaxSetsPerBehaviour = 64;

        sealed class Source
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public int Index;
            public float FrameWeight;
            public float Displacement;
        }

        sealed class Candidate
        {
            public LayerBake Layer;
            public (string Path, string Name) Shape;
            public Term Term;
            public BakeGate Gate;
            public List<(BakeDriver Driver, float Amount)> Parts;

            /// <summary>Curves the animator stops evaluating when this term goes native.</summary>
            public int Curves;

            /// <summary>Forwarded inputs the term reads.</summary>
            public List<ForwardTarget> Targets;

            /// <summary>How far the term moves the mesh at most; breaks ties.</summary>
            public float Movement;

            public Source Source;
        }

        sealed class LayerBake
        {
            public VirtualLayer Layer;
            public VirtualState State;
            public Dictionary<(string Path, string Name), List<Term>> Before;

            /// <summary>Blendshapes some other layer animates; baking them would change how the layers combine.</summary>
            public HashSet<(string, string)> Elsewhere;
        }

        /// <summary>Everything one bake decides, accumulated over its selection rounds.</summary>
        sealed class Plan
        {
            public readonly List<string> Notes = new List<string>();
            public readonly List<LayerBake> Layers = new List<LayerBake>();
            public readonly List<Candidate> Accepted = new List<Candidate>();
            public readonly List<BakePiece> Pieces = new List<BakePiece>();
            public readonly Dictionary<string, BakePiece> PieceByKey = new Dictionary<string, BakePiece>(StringComparer.Ordinal);
            public readonly List<BakeGate> Gates = new List<BakeGate>();
            public readonly Dictionary<string, BakeGate> GateByKey = new Dictionary<string, BakeGate>(StringComparer.Ordinal);
            public readonly Dictionary<(string, string), Source> Sources = new Dictionary<(string, string), Source>();

            /// <summary>Forwarded inputs whose behaviors were removed because nothing in the animator reads them any more.</summary>
            public readonly List<ForwardTarget> Trimmed = new List<ForwardTarget>();

            /// <summary>Curves and blend tree nodes the animator evaluates in the baked layers.</summary>
            public (int Curves, int Nodes) TreeBefore, TreeAfter;

            /// <summary>Curves of OSCmooth's smoothing layer, removed once anything is native.</summary>
            public int SmoothingCurves;

            public bool SmoothingRemoved;

            public Transform Root;
            public VirtualAnimatorController Fx;
            public VRCAvatarDescriptor Descriptor;
            public PatchPlan Patch;
            public Survey Survey;
            public List<VirtualLayer> AllLayers => Survey.AllLayers;
            public VirtualLayer Smoothing => Survey.Smoothing;
            public LayerInfo SmoothingInfo => Survey.SmoothingInfo;
            public Dictionary<string, ForwardTarget> Inputs => Survey.Inputs;
            public Dictionary<ForwardTarget, float> Rest;
            public HashSet<string> Binary;
            public HashSet<(string, string)> Reserved;
        }

        /// <summary>The layers this tool may rewrite, and what it needs to know about the controllers around them.</summary>
        internal sealed class Survey
        {
            public List<VirtualLayer> AllLayers;
            public VirtualLayer Smoothing;
            public LayerInfo SmoothingInfo;

            /// <summary>
            /// Forwarded inputs by the parameter blend trees read: the input itself, and its OSCmooth copy when only the
            /// smoothing layer writes it. The copy settles on the forwarded value.
            /// </summary>
            public Dictionary<string, ForwardTarget> Inputs;

            /// <summary>FX layers whose weight a layer control animates (virtual indices), or that FermataFT controls by name.</summary>
            public HashSet<int> Controlled;

            public HashSet<string> Named;

            /// <summary>Single-state Write Defaults blend-tree layers that read forwarded inputs and output exactly their blend tree.</summary>
            public readonly List<(VirtualLayer Layer, VirtualState State)> FaceLayers = new List<(VirtualLayer, VirtualState)>();

            public readonly List<string> Notes = new List<string>();
        }

        public static Survey Look(VirtualAnimatorController fx, IReadOnlyList<VirtualAnimatorController> controllers, PatchPlan patch)
        {
            var survey = new Survey { AllLayers = controllers.SelectMany(c => c.Layers).ToList() };
            var infos = survey.AllLayers.ToDictionary(l => l, l => AnimatorScan.Scan(l, ""));
            survey.Smoothing = patch.SmoothingLayer?.Source as VirtualLayer;
            survey.SmoothingInfo = survey.Smoothing != null && infos.TryGetValue(survey.Smoothing, out var info) ? info : null;

            survey.Inputs = new Dictionary<string, ForwardTarget>(StringComparer.Ordinal);
            foreach (var t in patch.Targets) survey.Inputs[t.Parameter] = t;
            if (survey.SmoothingInfo != null)
            {
                foreach (var t in patch.Targets)
                {
                    var proxy = FermataProfile.ProxyPrefix + t.Parameter;
                    if (survey.SmoothingInfo.Writes.Contains(proxy) && survey.SmoothingInfo.Reads.Contains(t.Parameter)
                        && infos.All(kv => kv.Key == survey.Smoothing || !kv.Value.Writes.Contains(proxy)))
                        survey.Inputs[proxy] = t;
                }
            }

            survey.Controlled = new HashSet<int>();
            foreach (var layer in survey.AllLayers)
            foreach (var node in layer.AllReachableNodes())
            {
                var behaviours = node is VirtualState s ? s.Behaviours : node is VirtualStateMachine m ? m.Behaviours : null;
                if (behaviours == null) continue;
                foreach (var control in behaviours.OfType<VRCAnimatorLayerControl>())
                    if (control && control.playable == VRC_AnimatorLayerControl.BlendableLayer.FX)
                        survey.Controlled.Add(control.layer);
            }
            survey.Named = new HashSet<string>(infos.Values.SelectMany(i => i.NamedLayerTargets), StringComparer.Ordinal);

            // Rewriting a blend tree is exact under Write Defaults on; Unity blends unreliably when a controller mixes both.
            var writeDefaults = fx.Layers.SelectMany(l => l.StateMachine?.AllStates() ?? Enumerable.Empty<VirtualState>())
                .Select(s => s.WriteDefaultValues).Distinct().ToList();
            if (writeDefaults.Contains(false))
            {
                survey.Notes.Add(writeDefaults.Contains(true) ? "FX mixes Write Defaults on and off" : "FX has Write Defaults off");
                return survey;
            }

            foreach (var layer in fx.Layers)
            {
                if (layer == survey.Smoothing) continue;
                var state = SingleState(layer);
                if (state == null || !BlendTreeTerms.Parameters(state.Motion).Any(survey.Inputs.ContainsKey)) continue;
                var problem = LayerProblem(layer, state, survey.Controlled, survey.Named);
                if (problem != null) survey.Notes.Add($"layer '{layer.Name}' {problem}");
                else survey.FaceLayers.Add((layer, state));
            }
            return survey;
        }

        /// <summary>
        /// Folds the blendshapes FermataFT's face layers only ever set to 0 into one constant clip, then prunes what no
        /// longer animates anything. Exact, so it runs on every build, with or without the shape bake.
        /// </summary>
        public static void Fold(Transform root, VirtualAnimatorController fx, Survey survey)
        {
            foreach (var note in survey.Notes)
                Debug.Log($"[{Messages.Title}] Zero folding skipped: {note}.");
            var none = new HashSet<string>();
            var empty = VirtualClip.Create(NamePrefix + "Empty");
            foreach (var (layer, state) in survey.FaceLayers)
            {
                BlendTreeTerms.Detach(state);
                var before = BlendTreeTerms.Analyze(state, none);
                var countBefore = Count(new[] { state.Motion });
                var folded = FoldZeros(fx, root, state);
                if (folded.Count == 0) continue;
                state.Motion = Prune(state.Motion, empty);

                var after = BlendTreeTerms.Analyze(state, none);
                var writes = BlendshapeWrites(layer);
                foreach (var shape in before.Keys.Union(after.Keys))
                {
                    if (folded.Contains(shape))
                    {
                        if (!writes.Contains(shape))
                            BridgeError.Report(ErrorSeverity.Error, $"Zero folding self-check failed: '{layer.Name}' no longer animates {shape.Item1}:{shape.Item2}.");
                        continue;
                    }
                    var e = Describe(before.TryGetValue(shape, out var b) ? b : new List<Term>());
                    var k = Describe(after.TryGetValue(shape, out var a) ? a : new List<Term>());
                    if (e != k)
                        BridgeError.Report(ErrorSeverity.Error, $"Zero folding self-check failed: '{layer.Name}' {shape.Item1}:{shape.Item2} changed from {e} to {k}.");
                }
                var countAfter = Count(new[] { state.Motion });
                Debug.Log($"[{Messages.Title}] Zero folding, '{layer.Name}': {folded.Count} blendshapes, " +
                          $"{countBefore.Curves} -> {countAfter.Curves} curves, {countBefore.Nodes} -> {countAfter.Nodes} nodes.");
            }
        }

        static string Describe(List<Term> terms) =>
            string.Join(" + ", terms.Select(Describe).OrderBy(s => s, StringComparer.Ordinal));

        public static void Run(BuildContext context, VirtualAnimatorController fx, Survey survey, PatchPlan patch, VRCAvatarDescriptor descriptor,
            IReadOnlyList<VirtualAnimatorController> controllers)
        {
            Plan plan;
            try
            {
                plan = Prepare(context.AvatarRootTransform, fx, survey, patch, descriptor, controllers);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                BridgeError.Report(ErrorSeverity.NonFatal, $"Shape Bake: skipped ({e.Message}).");
                return;
            }
            if (plan.Layers.Count == 0)
            {
                var why = plan.Notes.Count > 0 ? string.Join("; ", plan.Notes) : "no blend-tree layer reads the forwarded inputs";
                BridgeError.Report(ErrorSeverity.NonFatal, $"Shape Bake: nothing baked ({why}).");
                return;
            }

            plan.TreeBefore = Count(plan.Layers.Select(l => l.State.Motion));

            // Baking can leave a forwarded input unread; dropping its behavior frees budget for another round.
            const int maxRounds = 8;
            for (var round = 0; round < maxRounds; round++)
            {
                Strip(SelectRound(plan));
                if (TrimForwards(plan) == 0) break;
            }
            if (plan.Accepted.Count == 0 && plan.Trimmed.Count == 0)
            {
                BridgeError.Report(ErrorSeverity.NonFatal, "Shape Bake: nothing baked (no exactly bakeable shapes fit the budget).");
                return;
            }

            // Native shapes follow tracking directly; the animator's part must see the same values at the same time.
            if (plan.Accepted.Count > 0) DisableSmoothing(plan);

            var empty = VirtualClip.Create(NamePrefix + "Empty");
            foreach (var bake in plan.Layers) bake.State.Motion = Prune(bake.State.Motion, empty);
            plan.TreeAfter = Count(plan.Layers.Select(l => l.State.Motion));

            Finish(context, fx, descriptor, plan);

            var problems = SelfCheck(plan);
            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                    BridgeError.Report(ErrorSeverity.Error, "Shape Bake self-check failed: " + problem);
                return;
            }

            Debug.Log($"[{Messages.Title}] Shape Bake details:\n" + string.Join("\n", plan.Pieces.Select(p =>
                $"{p.Blendshape} = {p.Driver.Key} ({p.Driver.Instructions} instr), gate {p.Gate?.Name ?? "none"}: " +
                string.Join(", ", p.Contributions.Select(c => $"{c.Key.Name} {c.Value.ToString("0.##", CultureInfo.InvariantCulture)}")))));
            var shapes = plan.Accepted.Select(c => c.Shape).Distinct().Count();
            BridgeError.Report(ErrorSeverity.Information,
                $"Shape Bake: {plan.Accepted.Count} terms of {shapes} blendshapes, {plan.Pieces.Count} native shapes, " +
                $"{plan.Pieces.Sum(p => p.Driver.Instructions)} instructions, {plan.Gates.Count} gates, " +
                $"{plan.Trimmed.Count} forwards dropped ({plan.Trimmed.Sum(t => t.Instructions)} instructions). " +
                $"Face tree: {plan.TreeBefore.Curves} -> {plan.TreeAfter.Curves} curves, {plan.TreeBefore.Nodes} -> {plan.TreeAfter.Nodes} nodes. " +
                (plan.SmoothingRemoved ? $"OSCmooth: off ({plan.SmoothingCurves} curves)." : "OSCmooth: kept."));
        }

        /// <summary>Finds the bakeable layers and everything that stays fixed while terms are selected.</summary>
        static Plan Prepare(Transform root, VirtualAnimatorController fx, Survey survey, PatchPlan patch, VRCAvatarDescriptor descriptor,
            IReadOnlyList<VirtualAnimatorController> controllers)
        {
            var plan = new Plan { Root = root, Fx = fx, Descriptor = descriptor, Patch = patch, Survey = survey };
            plan.Notes.AddRange(survey.Notes);
            var problem = SmoothingProblem(plan);
            if (problem != null)
            {
                plan.Notes.Add(problem);
                return plan;
            }

            plan.Binary = BinaryParameters(descriptor, controllers);
            plan.Rest = new Dictionary<ForwardTarget, float>();
            foreach (var t in patch.Targets)
            {
                var value = NativeExpression.Evaluate(t.Expression);
                if (value.HasValue) plan.Rest[t] = Mathf.Clamp(value.Value, -1f, 1f);
            }
            foreach (var (layer, state) in survey.FaceLayers)
            {
                BlendTreeTerms.Detach(state);
                plan.Layers.Add(new LayerBake { Layer = layer, State = state, Before = BlendTreeTerms.Analyze(state, plan.Binary) });
            }
            plan.Reserved = DescriptorShapes(root, descriptor);
            var writes = plan.AllLayers.ToDictionary(l => l, BlendshapeWrites);
            foreach (var bake in plan.Layers)
                bake.Elsewhere = new HashSet<(string, string)>(plan.AllLayers.Where(l => l != bake.Layer).SelectMany(l => writes[l]));
            return plan;
        }

        /// <summary>
        /// Why OSCmooth's smoothing can't be switched off (null when it can): something outside the smoothing layer
        /// reads a parameter that only the smoothing layer writes and that no forward could write directly instead.
        /// </summary>
        static string SmoothingProblem(Plan plan)
        {
            if (plan.Smoothing == null || plan.SmoothingInfo == null) return null;
            var reads = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layer in plan.AllLayers.Where(l => l != plan.Smoothing))
                reads.UnionWith(LiveReads(layer));
            var stranded = plan.SmoothingInfo.Writes
                .Where(p => reads.Contains(p) && !(plan.Inputs.TryGetValue(p, out var t) && p == FermataProfile.ProxyPrefix + t.Parameter))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            return stranded.Count == 0 ? null
                : $"OSCmooth can't be turned off: '{plan.Smoothing.Name}' also writes {string.Join(", ", stranded.Select(p => $"'{p}'"))}, which other layers read";
        }

        /// <summary>
        /// Forwards write the copies FermataFT's blend trees read, instead of OSCmooth's inputs, and the smoothing
        /// layer goes. When a layer control or FermataFT's named control refers to it, it stays but plays nothing.
        /// </summary>
        static void DisableSmoothing(Plan plan)
        {
            var smoothing = plan.Smoothing;
            if (smoothing == null) return;
            var behaviors = plan.Descriptor.faceTrackingParameterBehaviors ?? new List<VRCAvatarDescriptor.FaceTrackingParameterBehavior>();
            foreach (var target in plan.Patch.Targets)
            {
                var proxy = FermataProfile.ProxyPrefix + target.Parameter;
                if (!plan.Inputs.ContainsKey(proxy)) continue;
                foreach (var b in behaviors)
                    if (b != null && b.targetMode == VRCAvatarDescriptor.ParameterBehaviorTargetMode.ForwardToAnimator && b.outputParameterName == target.Parameter)
                        b.outputParameterName = proxy;
            }

            plan.SmoothingCurves = Count(smoothing.StateMachine?.AllStates().Select(s => s.Motion) ?? Enumerable.Empty<VirtualMotion>()).Curves;
            if (plan.Survey.Controlled.Contains(smoothing.VirtualLayerIndex) || plan.Survey.Named.Contains(smoothing.Name))
            {
                foreach (var state in smoothing.StateMachine?.AllStates() ?? Enumerable.Empty<VirtualState>())
                    state.Motion = null;
            }
            else
            {
                plan.Fx.RemoveLayer(smoothing);
            }
            plan.SmoothingRemoved = true;
        }

        /// <summary>Analyses the layers as they are now and selects more terms with whatever budget is left.</summary>
        static List<Candidate> SelectRound(Plan plan)
        {
            var candidates = new List<Candidate>();
            foreach (var bake in plan.Layers)
            {
                var current = BlendTreeTerms.Analyze(bake.State, plan.Binary);
                foreach (var pair in current.OrderBy(p => p.Key.Path, StringComparer.Ordinal).ThenBy(p => p.Key.Name, StringComparer.Ordinal))
                {
                    if (bake.Elsewhere.Contains(pair.Key) || plan.Reserved.Contains(pair.Key)) continue;
                    var source = GetSource(plan.Root, pair.Key, plan.Sources);
                    if (source == null) continue;
                    foreach (var term in pair.Value)
                    {
                        var candidate = MakeCandidate(plan, bake, pair.Key, term, source);
                        if (candidate != null) candidates.Add(candidate);
                    }
                }
            }

            // Inputs something other than these candidates still reads can't be freed, however many candidates go native.
            var removable = new Dictionary<object, HashSet<(string, string)>>();
            foreach (var c in candidates)
            foreach (var at in c.Term.Occurrences)
            {
                if (!removable.TryGetValue(at.Key, out var set)) removable[at.Key] = set = new HashSet<(string, string)>();
                set.Add(c.Shape);
            }
            var blocked = new HashSet<ForwardTarget>();
            foreach (var layer in plan.AllLayers.Where(l => l != plan.Smoothing))
            foreach (var parameter in LiveReads(layer, removable))
                if (plan.Inputs.TryGetValue(parameter, out var target))
                    blocked.Add(target);
            return Select(plan, candidates, blocked);
        }

        static VirtualState SingleState(VirtualLayer layer)
        {
            var sm = layer.StateMachine;
            if (sm == null || sm.StateMachines.Count > 0 || sm.States.Count != 1) return null;
            return sm.States[0].State;
        }

        /// <summary>Why the layer's output may differ from its blend tree's, so baking it wouldn't be exact; null when it can be baked.</summary>
        static string LayerProblem(VirtualLayer layer, VirtualState state, HashSet<int> controlled, HashSet<string> named)
        {
            if (layer.BlendingMode != AnimatorLayerBlendingMode.Override) return "is additive";
            if (layer.DefaultWeight != 1f) return "has weight below 1";
            if (layer.AvatarMask != null) return "has an avatar mask";
            if (layer.SyncedLayerIndex >= 0) return "is a synced layer";
            if (controlled.Contains(layer.VirtualLayerIndex) || named.Contains(layer.Name)) return "has its weight animated";
            var sm = layer.StateMachine;
            if (sm.AnyStateTransitions.Count > 0 || sm.EntryTransitions.Count > 0 || state.Transitions.Count > 0) return "has transitions";
            return null;
        }

        static HashSet<(string, string)> BlendshapeWrites(VirtualLayer layer)
        {
            var set = new HashSet<(string, string)>();
            foreach (var node in layer.AllReachableNodes())
            {
                if (!(node is VirtualClip clip)) continue;
                foreach (var b in clip.GetFloatCurveBindings())
                    if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith(BlendTreeTerms.BlendShapePrefix, StringComparison.Ordinal))
                        set.Add((b.path, b.propertyName.Substring(BlendTreeTerms.BlendShapePrefix.Length)));
            }
            return set;
        }

        /// <summary>Blendshapes that VRChat itself drives from the descriptor (visemes, eyelids, existing face tracking).</summary>
        static HashSet<(string, string)> DescriptorShapes(Transform root, VRCAvatarDescriptor d)
        {
            var set = new HashSet<(string, string)>();
            void Add(SkinnedMeshRenderer r, string name)
            {
                if (r && !string.IsNullOrEmpty(name)) set.Add((AnimationUtility.CalculateTransformPath(r.transform, root), name));
            }

            if (d.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape)
                foreach (var name in d.VisemeBlendShapes ?? Array.Empty<string>())
                    Add(d.VisemeSkinnedMesh, name);
            if (d.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape)
                Add(d.VisemeSkinnedMesh, d.MouthOpenBlendShapeName);

            var eyes = d.customEyeLookSettings;
            if (d.enableEyeLook && eyes.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes && eyes.eyelidsSkinnedMesh && eyes.eyelidsSkinnedMesh.sharedMesh)
                foreach (var index in eyes.eyelidsBlendshapes ?? Array.Empty<int>())
                    if (index >= 0 && index < eyes.eyelidsSkinnedMesh.sharedMesh.blendShapeCount)
                        Add(eyes.eyelidsSkinnedMesh, eyes.eyelidsSkinnedMesh.sharedMesh.GetBlendShapeName(index));

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in d.faceTrackingBlendshapesSetList ?? new List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet>())
            {
                if (s == null) continue;
                names.UnionWith(s.faceTrackingBlendshapeNames ?? Array.Empty<string>());
                names.UnionWith(s.visemeBlendshapeNames ?? Array.Empty<string>());
            }
            foreach (var b in d.faceTrackingParameterBehaviors ?? new List<VRCAvatarDescriptor.FaceTrackingParameterBehavior>())
                if (b != null) names.Add(b.targetBlendshapeName);
            foreach (var r in d.faceTrackingMeshes ?? new List<SkinnedMeshRenderer>())
            foreach (var name in names)
                Add(r, name);
            return set;
        }

        /// <summary>The mesh blendshape behind an animated one, when baking it is exact: one frame, and at rest by default.</summary>
        static Source GetSource(Transform root, (string Path, string Name) shape, Dictionary<(string, string), Source> cache)
        {
            if (cache.TryGetValue(shape, out var cached)) return cached;
            Source source = null;
            var t = string.IsNullOrEmpty(shape.Path) ? root : root.Find(shape.Path);
            var renderer = t ? t.GetComponent<SkinnedMeshRenderer>() : null;
            var mesh = renderer ? renderer.sharedMesh : null;
            var index = mesh ? mesh.GetBlendShapeIndex(shape.Name) : -1;
            if (index >= 0 && mesh.GetBlendShapeFrameCount(index) == 1 && mesh.GetBlendShapeFrameWeight(index, 0) > 0f
                && renderer.GetBlendShapeWeight(index) == 0f)
            {
                source = new Source { Renderer = renderer, Mesh = mesh, Index = index, FrameWeight = mesh.GetBlendShapeFrameWeight(index, 0) };
                var deltas = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(index, 0, deltas, null, null);
                source.Displacement = deltas.Sum(v => v.magnitude);
            }
            cache[shape] = source;
            return source;
        }

        static Candidate MakeCandidate(Plan plan, LayerBake bake, (string, string) shape, Term term, Source source)
        {
            var inputs = plan.Inputs;
            var rest = plan.Rest;
            var gates = plan.GateByKey;
            if (term.Opaque != null || term.IsZero) return null;
            var tracked = term.Factors.Where(f => inputs.ContainsKey(f.Parameter)).ToList();
            var others = term.Factors.Where(f => !inputs.ContainsKey(f.Parameter)).ToList();
            if (tracked.Count == 0 || tracked.Any(f => !rest.ContainsKey(inputs[f.Parameter]))) return null;
            // A set weight is clamped to 0..1; Direct blend weights are taken to stay within it.
            if (others.Any(f => !f.Linear && f.Values.Any(v => v < 0f))) return null;

            BakeGate gate = null;
            if (others.Count > 0)
            {
                var key = string.Join(";", others.Select(f => f.Key));
                if (!gates.TryGetValue(key, out gate)) gates[key] = gate = new BakeGate(others, key);
            }

            List<(BakeDriver, float)> parts;
            float magnitude;
            if (tracked.Count == 1 && Math.Abs(tracked[0].Evaluate(rest[inputs[tracked[0].Parameter]])) <= Term.Epsilon)
            {
                var input = inputs[tracked[0].Parameter];
                parts = Segments(term.Coefficient, tracked[0], input, rest[input]);
                magnitude = 1f;
            }
            else
            {
                parts = Product(term.Coefficient, tracked, inputs, out magnitude);
            }
            if (parts == null || parts.Count == 0 || parts.Any(p => !p.Item1.IsValid)) return null;

            return new Candidate
            {
                Layer = bake,
                Shape = shape,
                Term = term,
                Gate = gate,
                Parts = parts,
                Source = source,
                Curves = term.Occurrences.Count,
                Targets = tracked.Select(f => inputs[f.Parameter]).Distinct().ToList(),
                Movement = Math.Abs(term.Coefficient) * magnitude * source.Displacement / source.FrameWeight,
            };
        }

        /// <summary>
        /// The factor as a sum of clamped ramps anchored at the input's rest value, each its own driver. Every ramp is 0
        /// at rest, and a native output is clamped to 0..1 anyway, so the drivers need no clamp of their own.
        /// </summary>
        static List<(BakeDriver, float)> Segments(float coefficient, Factor factor, ForwardTarget input, float rest)
        {
            var (knots, values) = Restrict(factor, input, rest);
            var parts = new List<(BakeDriver, float)>();
            for (var i = 0; i + 1 < knots.Count; i++)
            {
                float a = knots[i], b = knots[i + 1], rise = values[i + 1] - values[i];
                if (Math.Abs(rise) <= Term.Epsilon) continue;
                if (b - a < 1e-4f) return null;
                // Above rest the ramp rises from a to b; below rest it falls from a to b (rest is a knot).
                parts.Add(a >= rest ? (Ramp(input, a, b, true), coefficient * rise) : (Ramp(input, a, b, false), -coefficient * rise));
            }
            return parts;
        }

        /// <summary>clamp((E - a) / (b - a), 0, 1) rising, or clamp((b - E) / (b - a), 0, 1) falling, without the clamp.</summary>
        static BakeDriver Ramp(ForwardTarget input, float a, float b, bool rising)
        {
            if (input.SingleKey != null && a == 0f && b == 1f) return BakeDriver.Passthrough(input.SingleKey, !rising);
            var e = Wrap(input.Expression);
            var scale = 1f / (b - a);
            return BakeDriver.Custom(rising ? Scale(Offset(e, a), scale) : Scale(b == 0f ? "-" + e : Num(b) + " - " + e, scale));
        }

        /// <summary>The whole term as one expression, split by sign since a native output is clamped to 0..1.</summary>
        static List<(BakeDriver, float)> Product(float coefficient, List<Factor> factors, Dictionary<string, ForwardTarget> inputs, out float magnitude)
        {
            var texts = new List<string>();
            float low = 1f, high = 1f;
            foreach (var factor in factors)
            {
                var input = inputs[factor.Parameter];
                var (knots, values) = Restrict(factor, input, null);
                var text = PiecewiseText(input, knots, values);
                if (text == null)
                {
                    magnitude = 0f;
                    return null;
                }
                texts.Add(Wrap(text));
                float min = values.Min(), max = values.Max();
                var corners = new[] { low * min, low * max, high * min, high * max };
                low = corners.Min();
                high = corners.Max();
            }
            magnitude = Math.Max(Math.Abs(low), Math.Abs(high));
            var product = string.Join(" * ", texts);
            var parts = new List<(BakeDriver, float)>();
            if (high > Term.Epsilon) parts.Add((BakeDriver.Custom(product), coefficient));
            if (low < -Term.Epsilon) parts.Add((BakeDriver.Custom("-" + Wrap(product)), -coefficient));
            return parts;
        }

        /// <summary>F(E) as its value at the bottom of the input's range plus a clamped ramp per segment.</summary>
        static string PiecewiseText(ForwardTarget input, List<float> knots, List<float> values)
        {
            var text = Math.Abs(values[0]) > Term.Epsilon ? Num(values[0]) : null;
            for (var i = 0; i + 1 < knots.Count; i++)
            {
                float a = knots[i], b = knots[i + 1], rise = values[i + 1] - values[i];
                if (Math.Abs(rise) <= Term.Epsilon) continue;
                if (b - a < 1e-4f) return null;
                var ramp = ClampedRamp(input, a, b);
                var size = Math.Abs(rise);
                var scaled = Math.Abs(size - 1f) <= Term.Epsilon ? ramp : Num(size) + " * " + ramp;
                text = text == null ? (rise > 0 ? scaled : "-" + scaled) : text + (rise > 0 ? " + " : " - ") + scaled;
            }
            return text ?? "0";
        }

        /// <summary>clamp((E - a) / (b - a), 0, 1), leaving out bounds a single tracking key (0..1) can't cross.</summary>
        static string ClampedRamp(ForwardTarget input, float a, float b)
        {
            var inner = Scale(Offset(Wrap(input.Expression), a), 1f / (b - a));
            if (input.SingleKey != null)
            {
                if (a <= 0f && b >= 1f) return inner;
                if (a <= 0f) return $"min(1, {inner})";
                if (b >= 1f) return $"max(0, {inner})";
            }
            return $"clamp({inner}, 0, 1)";
        }

        /// <summary>
        /// The factor's knots and values over the range its input can take: 0..1 for a single tracking key, -1..1
        /// otherwise (forwarded values are clamped to it). The rest value, if given, becomes a knot.
        /// </summary>
        static (List<float>, List<float>) Restrict(Factor factor, ForwardTarget input, float? rest)
        {
            float low = input.SingleKey != null ? 0f : -1f, high = 1f;
            var knots = new List<float> { low, high };
            if (rest.HasValue) knots.Add(Mathf.Clamp(rest.Value, low, high));
            if (!factor.Linear) knots.AddRange(factor.Knots.Where(k => k > low && k < high));
            knots = knots.Distinct().OrderBy(k => k).ToList();
            return (knots, knots.Select(factor.Evaluate).ToList());
        }

        static string Offset(string e, float a) => a == 0f ? e : a > 0f ? e + " - " + Num(a) : e + " + " + Num(-a);

        static string Scale(string x, float k) => Math.Abs(k - 1f) <= Term.Epsilon ? x : Wrap(x) + " * " + Num(k);

        static string Wrap(string e) => IsAtom(e) || IsWrapped(e) ? e : "(" + e + ")";

        static bool IsAtom(string e) => e.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.');

        /// <summary>True when the whole text is one parenthesised group.</summary>
        static bool IsWrapped(string e)
        {
            if (e.Length < 2 || e[0] != '(') return false;
            var depth = 0;
            for (var i = 0; i < e.Length; i++)
            {
                if (e[i] == '(') depth++;
                else if (e[i] == ')' && --depth == 0) return i == e.Length - 1;
            }
            return false;
        }

        static string Num(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>
        /// Picks terms by the curves they take out of the blend tree per instruction, until the next one would exceed a
        /// native face tracking limit. When a term is the last reader of a forwarded input, the forward's instructions
        /// count back. Ties go to the term that moves the mesh more. Terms that share a driver and gate share its
        /// behavior and blendshape.
        /// </summary>
        static List<Candidate> Select(Plan plan, List<Candidate> candidates, HashSet<ForwardTarget> blocked)
        {
            var readers = new Dictionary<ForwardTarget, int>();
            foreach (var c in candidates)
            foreach (var t in c.Targets)
                readers[t] = (readers.TryGetValue(t, out var n) ? n : 0) + 1;

            // The descriptor holds the forwards (minus trimmed ones); pieces from earlier rounds aren't added to it yet.
            var descriptor = plan.Descriptor;
            var behaviors = descriptor.faceTrackingParameterBehaviors?.Where(b => b != null).ToList()
                            ?? new List<VRCAvatarDescriptor.FaceTrackingParameterBehavior>();
            var behaviorBudget = NativeExpression.MaxBehaviors - behaviors.Count - plan.Pieces.Count;
            var instructionBudget = NativeExpression.MaxInstructions - behaviors
                .Where(b => b.combineMode == VRCAvatarDescriptor.ParameterBehaviorCombineMode.Custom && !string.IsNullOrWhiteSpace(b.customExpression))
                .Sum(b => NativeExpression.CountInstructions(b.customExpression)) - plan.Pieces.Sum(p => p.Driver.Instructions);
            var setBudget = MaxSets - (descriptor.faceTrackingBlendshapesSetList?.Count ?? 0) - plan.Gates.Count;
            var meshes = new HashSet<SkinnedMeshRenderer>(descriptor.faceTrackingMeshes?.Where(r => r) ?? Enumerable.Empty<SkinnedMeshRenderer>());
            meshes.UnionWith(plan.Accepted.Select(c => c.Source.Renderer));

            var pieces = plan.PieceByKey;
            var gates = new HashSet<BakeGate>(plan.Gates);
            var accepted = new List<Candidate>();
            var remaining = candidates.ToList();
            while (true)
            {
                Candidate best = null;
                (float Score, float Movement) bestRank = (-1f, 0f);
                foreach (var c in remaining)
                {
                    var added = c.Parts.Select(p => PieceKey(p.Driver, c.Gate)).Distinct().Where(k => !pieces.ContainsKey(k)).ToList();
                    var instructions = c.Parts.GroupBy(p => PieceKey(p.Driver, c.Gate)).Where(g => !pieces.ContainsKey(g.Key))
                        .Sum(g => g.First().Driver.Instructions);
                    var newGate = c.Gate != null && !gates.Contains(c.Gate) ? 1 : 0;
                    var newMesh = meshes.Contains(c.Source.Renderer) ? 0 : 1;
                    if (added.Count > behaviorBudget || instructions > instructionBudget || newGate > setBudget || meshes.Count + newMesh > MaxMeshes) continue;
                    var freed = c.Targets.Where(t => !blocked.Contains(t) && readers[t] == 1).ToList();
                    var cost = Math.Max(1, instructions - freed.Sum(t => t.Instructions) + 1);
                    var rank = ((float)c.Curves / cost, c.Movement);
                    if (rank.Item1 > bestRank.Score || (rank.Item1 == bestRank.Score && rank.Movement > bestRank.Movement))
                    {
                        best = c;
                        bestRank = rank;
                    }
                }
                if (best == null) break;

                remaining.Remove(best);
                foreach (var t in best.Targets) readers[t]--;
                plan.Accepted.Add(best);
                accepted.Add(best);
                meshes.Add(best.Source.Renderer);
                if (best.Gate != null && gates.Add(best.Gate))
                {
                    setBudget--;
                    plan.Gates.Add(best.Gate);
                }
                foreach (var (driver, amount) in best.Parts)
                {
                    var key = PieceKey(driver, best.Gate);
                    if (!pieces.TryGetValue(key, out var piece))
                    {
                        pieces[key] = piece = new BakePiece(key, driver, best.Gate);
                        plan.Pieces.Add(piece);
                        behaviorBudget--;
                        instructionBudget -= driver.Instructions;
                    }
                    piece.Contributions.TryGetValue(best.Shape, out var sum);
                    piece.Contributions[best.Shape] = sum + amount;
                }
            }
            return accepted;
        }

        static string PieceKey(BakeDriver driver, BakeGate gate) => driver.Key + "@" + (gate?.Key ?? "");

        /// <summary>The animator stops producing the accepted terms: their clip slots no longer animate those blendshapes.</summary>
        static void Strip(List<Candidate> accepted)
        {
            var removals = new Dictionary<Occurrence, List<(string Path, string Name)>>();
            foreach (var c in accepted)
            foreach (var at in c.Term.Occurrences)
            {
                if (!removals.TryGetValue(at, out var list)) removals[at] = list = new List<(string, string)>();
                list.Add(c.Shape);
            }
            foreach (var pair in removals)
            {
                var clip = pair.Key.Clip.Clone();
                foreach (var (path, name) in pair.Value)
                    clip.SetFloatCurve(path, typeof(SkinnedMeshRenderer), BlendTreeTerms.BlendShapePrefix + name, null);
                pair.Key.SetClip(clip);
            }
        }

        /// <summary>
        /// Removes the forward behavior of every FermataFT input that no layer reads any more, directly or through its
        /// OSCmooth copy. Extra custom forwards stay. Returns how many were removed.
        /// </summary>
        static int TrimForwards(Plan plan)
        {
            if (plan.SmoothingInfo == null) return 0;
            var needed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layer in plan.AllLayers)
            {
                if (layer == plan.Smoothing) continue;
                foreach (var parameter in LiveReads(layer))
                    if (plan.Inputs.TryGetValue(parameter, out var target))
                        needed.Add(target.Parameter);
            }
            var behaviors = plan.Descriptor.faceTrackingParameterBehaviors;
            if (behaviors == null) return 0;
            var removed = 0;
            foreach (var target in plan.Patch.Targets)
            {
                if (needed.Contains(target.Parameter) || !plan.SmoothingInfo.Reads.Contains(target.Parameter) || plan.Trimmed.Contains(target)) continue;
                // With OSCmooth off, the forward writes the copy instead.
                var index = behaviors.FindIndex(b => b != null && b.targetMode == VRCAvatarDescriptor.ParameterBehaviorTargetMode.ForwardToAnimator
                                                     && (b.outputParameterName == target.Parameter
                                                         || b.outputParameterName == FermataProfile.ProxyPrefix + target.Parameter));
                if (index < 0) continue;
                behaviors.RemoveAt(index);
                plan.Trimmed.Add(target);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// Parameters a layer reads in a way that can change what it does: transition conditions, state behaviours and
        /// state parameters, and blend tree parameters only where the subtree still animates something.
        /// </summary>
        /// <param name="removed">Blendshape bindings per clip slot (<see cref="Occurrence.Key"/>) to treat as already gone.</param>
        static HashSet<string> LiveReads(VirtualLayer layer, Dictionary<object, HashSet<(string, string)>> removed = null)
        {
            var reads = new HashSet<string>(StringComparer.Ordinal);
            void Conditions(IEnumerable<AnimatorCondition> conditions)
            {
                foreach (var c in conditions) reads.Add(c.parameter);
            }
            void Behaviours(IEnumerable<StateMachineBehaviour> behaviours)
            {
                foreach (var driver in behaviours.OfType<VRCAvatarParameterDriver>())
                {
                    if (!driver || driver.parameters == null) continue;
                    foreach (var p in driver.parameters)
                    {
                        if (p == null) continue;
                        if (p.type == VRC_AvatarParameterDriver.ChangeType.Copy) reads.Add(p.source);
                        if (p.type == VRC_AvatarParameterDriver.ChangeType.Add) reads.Add(p.name);
                    }
                }
            }
            foreach (var node in layer.AllReachableNodes())
            {
                switch (node)
                {
                    case VirtualStateMachine sm:
                        Behaviours(sm.Behaviours);
                        break;
                    case VirtualState state:
                        Behaviours(state.Behaviours);
                        foreach (var p in new[] { state.SpeedParameter, state.TimeParameter, state.CycleOffsetParameter, state.MirrorParameter })
                            if (!string.IsNullOrEmpty(p)) reads.Add(p);
                        LiveMotion(state.Motion, reads, state, removed);
                        break;
                    case VirtualTransitionBase transition:
                        Conditions(transition.Conditions);
                        break;
                }
            }
            return reads;
        }

        static bool LiveMotion(VirtualMotion motion, HashSet<string> reads, object slot = null, Dictionary<object, HashSet<(string, string)>> removed = null)
        {
            switch (motion)
            {
                case VirtualClip clip:
                    if (clip.GetObjectCurveBindings().Any()) return true;
                    HashSet<(string, string)> gone = null;
                    if (removed != null && slot != null) removed.TryGetValue(slot, out gone);
                    return clip.GetFloatCurveBindings().Any(b => gone == null || !(b.type == typeof(SkinnedMeshRenderer)
                        && b.propertyName.StartsWith(BlendTreeTerms.BlendShapePrefix, StringComparison.Ordinal)
                        && gone.Contains((b.path, b.propertyName.Substring(BlendTreeTerms.BlendShapePrefix.Length)))));
                case VirtualBlendTree tree:
                    var live = false;
                    for (var i = 0; i < tree.Children.Count; i++)
                    {
                        var child = tree.Children[i];
                        if (!LiveMotion(child.Motion, reads, (tree, i), removed)) continue;
                        live = true;
                        if (tree.BlendType == BlendTreeType.Direct) reads.Add(child.DirectBlendParameter);
                    }
                    if (live && tree.BlendType != BlendTreeType.Direct)
                    {
                        reads.Add(tree.BlendParameter);
                        if (tree.BlendType != BlendTreeType.Simple1D) reads.Add(tree.BlendParameterY);
                    }
                    return live;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Parameters that only ever hold 0 or 1: every value the avatar can give them (defaults, parameter drivers,
        /// menu toggles and buttons) is 0 or 1, and nothing else writes them (clips, puppets, contacts, PhysBones,
        /// VRChat's own continuous parameters). OSC from outside the avatar can't be seen and is assumed not to.
        /// </summary>
        static HashSet<string> BinaryParameters(VRCAvatarDescriptor descriptor, IReadOnlyList<VirtualAnimatorController> controllers)
        {
            var values = new Dictionary<string, HashSet<float>>(StringComparer.Ordinal);
            var other = new HashSet<string>(StringComparer.Ordinal)
            {
                "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight", "Viseme", "Voice", "VelocityX",
                "VelocityY", "VelocityZ", "VelocityMagnitude", "AngularY", "Upright", "TrackingType", "VRMode",
                "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent", "AvatarVersion",
            };
            var prefixes = new List<string>();
            void Value(string name, float value)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (!values.TryGetValue(name, out var set)) values[name] = set = new HashSet<float>();
                set.Add(value);
            }

            foreach (var controller in controllers)
            {
                foreach (var p in controller.Parameters.Values)
                    Value(p.name, p.type == AnimatorControllerParameterType.Float ? p.defaultFloat
                        : p.type == AnimatorControllerParameterType.Int ? p.defaultInt : p.defaultBool ? 1f : 0f);
                foreach (var layer in controller.Layers)
                foreach (var node in layer.AllReachableNodes())
                {
                    if (node is VirtualClip clip)
                    {
                        foreach (var b in clip.GetFloatCurveBindings())
                            if (b.type == typeof(Animator) && string.IsNullOrEmpty(b.path)) other.Add(b.propertyName);
                        continue;
                    }
                    var behaviours = node is VirtualState s ? s.Behaviours : node is VirtualStateMachine m ? m.Behaviours : null;
                    if (behaviours == null) continue;
                    foreach (var driver in behaviours.OfType<VRCAvatarParameterDriver>())
                    {
                        if (!driver || driver.parameters == null) continue;
                        foreach (var p in driver.parameters)
                        {
                            if (p == null) continue;
                            if (p.type == VRC_AvatarParameterDriver.ChangeType.Set) Value(p.name, p.value);
                            else other.Add(p.name);
                        }
                    }
                }
            }

            foreach (var p in descriptor.expressionParameters?.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>())
                if (p != null) Value(p.name, p.defaultValue);
            var visited = new HashSet<VRCExpressionsMenu>();
            var pending = new Stack<VRCExpressionsMenu>();
            if (descriptor.expressionsMenu) pending.Push(descriptor.expressionsMenu);
            while (pending.Count > 0)
            {
                var menu = pending.Pop();
                if (!visited.Add(menu) || menu.controls == null) continue;
                foreach (var control in menu.controls)
                {
                    if (control == null) continue;
                    Value(control.parameter?.name, control.value);
                    foreach (var sub in control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>())
                        if (!string.IsNullOrEmpty(sub?.name)) other.Add(sub.name);
                    if (control.subMenu) pending.Push(control.subMenu);
                }
            }

            // Contacts and PhysBones write parameters named by a field called "parameter" (a prefix for PhysBones).
            foreach (var component in descriptor.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!component) continue;
                var field = component.GetType().GetField("parameter");
                if (field != null && field.FieldType == typeof(string) && field.GetValue(component) is string prefix && prefix.Length > 0)
                    prefixes.Add(prefix);
            }

            return new HashSet<string>(values.Where(kv => kv.Value.All(v => v == 0f || v == 1f) && !other.Contains(kv.Key)
                                                          && !prefixes.Any(p => kv.Key.StartsWith(p, StringComparison.Ordinal)))
                .Select(kv => kv.Key), StringComparer.Ordinal);
        }

        /// <summary>
        /// Blendshapes the layer only ever sets to 0, at rest by default, come out of a Write Defaults blend tree as 0
        /// whatever the weights are. FermataFT writes the avatar's own expression shapes to 0 like this in dozens of
        /// clips. Their curves are replaced by one constant clip that always plays, so the layer still animates them.
        /// </summary>
        static HashSet<(string, string)> FoldZeros(VirtualAnimatorController fx, Transform root, VirtualState state)
        {
            var zeroOnly = new Dictionary<(string, string), bool>();
            var slots = BlendTreeTerms.Slots(state).ToList();
            foreach (var slot in slots)
            {
                var clip = slot.Clip;
                foreach (var b in clip.GetFloatCurveBindings())
                {
                    if (b.type != typeof(SkinnedMeshRenderer) || !b.propertyName.StartsWith(BlendTreeTerms.BlendShapePrefix, StringComparison.Ordinal)) continue;
                    var shape = (b.path, b.propertyName.Substring(BlendTreeTerms.BlendShapePrefix.Length));
                    var zero = !clip.IsMarkerClip && BlendTreeTerms.ConstantValue(clip.GetFloatCurve(b)) == 0f;
                    zeroOnly[shape] = (!zeroOnly.TryGetValue(shape, out var soFar) || soFar) && zero;
                }
            }
            var fold = new HashSet<(string, string)>(zeroOnly.Where(kv => kv.Value && RestsAtZero(root, kv.Key)).Select(kv => kv.Key));
            if (fold.Count == 0) return fold;

            foreach (var slot in slots)
            {
                var clip = slot.Clip;
                var hits = clip.GetFloatCurveBindings().Where(b => b.type == typeof(SkinnedMeshRenderer)
                    && b.propertyName.StartsWith(BlendTreeTerms.BlendShapePrefix, StringComparison.Ordinal)
                    && fold.Contains((b.path, b.propertyName.Substring(BlendTreeTerms.BlendShapePrefix.Length)))).ToList();
                if (hits.Count == 0) continue;
                var copy = clip.Clone();
                foreach (var b in hits) copy.SetFloatCurve(b, null);
                slot.SetClip(copy);
            }

            var constant = VirtualClip.Create(NamePrefix + "Zero");
            foreach (var (path, name) in fold.OrderBy(s => s.Item1, StringComparer.Ordinal).ThenBy(s => s.Item2, StringComparer.Ordinal))
                constant.SetFloatCurve(path, typeof(SkinnedMeshRenderer), BlendTreeTerms.BlendShapePrefix + name, AnimationCurve.Constant(0f, 1f / 60f, 0f));
            EnsureOne(fx);
            var always = new VirtualBlendTree.VirtualChildMotion { Motion = constant, DirectBlendParameter = OneParameter };
            if (state.Motion is VirtualBlendTree top && top.BlendType == BlendTreeType.Direct && !top.NormalizedBlendValues)
            {
                top.Children = top.Children.Add(always);
            }
            else
            {
                var wrapper = VirtualBlendTree.Create(NamePrefix + "Root");
                wrapper.BlendType = BlendTreeType.Direct;
                wrapper.Children = ImmutableList.Create(
                    new VirtualBlendTree.VirtualChildMotion { Motion = state.Motion, DirectBlendParameter = OneParameter }, always);
                state.Motion = wrapper;
            }
            return fold;
        }

        static bool RestsAtZero(Transform root, (string Path, string Name) shape)
        {
            var t = string.IsNullOrEmpty(shape.Path) ? root : root.Find(shape.Path);
            var renderer = t ? t.GetComponent<SkinnedMeshRenderer>() : null;
            var index = renderer && renderer.sharedMesh ? renderer.sharedMesh.GetBlendShapeIndex(shape.Name) : -1;
            return index >= 0 && renderer.GetBlendShapeWeight(index) == 0f;
        }

        static void EnsureOne(VirtualAnimatorController fx)
        {
            fx.SetParameter(OneParameter, new AnimatorControllerParameter { name = OneParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
        }

        /// <summary>
        /// Drops what no longer animates anything: Direct children go, 1D and 2D children become an empty clip (their
        /// thresholds still shape their siblings' weights). Returns null when nothing in the motion is left.
        /// </summary>
        static VirtualMotion Prune(VirtualMotion motion, VirtualClip empty)
        {
            switch (motion)
            {
                case VirtualClip clip:
                    return clip.GetFloatCurveBindings().Any() || clip.GetObjectCurveBindings().Any() ? clip : null;
                case VirtualBlendTree tree:
                    var children = new List<VirtualBlendTree.VirtualChildMotion>();
                    var live = false;
                    foreach (var child in tree.Children)
                    {
                        var kept = Prune(child.Motion, empty);
                        live |= kept != null;
                        if (kept == null && tree.BlendType == BlendTreeType.Direct) continue;
                        children.Add(new VirtualBlendTree.VirtualChildMotion
                        {
                            Motion = kept ?? empty,
                            CycleOffset = child.CycleOffset,
                            DirectBlendParameter = child.DirectBlendParameter,
                            Mirror = child.Mirror,
                            Threshold = child.Threshold,
                            Position = child.Position,
                            TimeScale = child.TimeScale,
                        });
                    }
                    if (!live) return null;
                    tree.Children = children.ToImmutableList();
                    return tree;
                default:
                    return null;
            }
        }

        /// <summary>Curves in the clips and blend tree nodes the animator evaluates for these motions.</summary>
        static (int Curves, int Nodes) Count(IEnumerable<VirtualMotion> motions)
        {
            var curves = 0;
            var nodes = 0;
            void Walk(VirtualMotion motion)
            {
                switch (motion)
                {
                    case VirtualClip clip:
                        curves += clip.GetFloatCurveBindings().Count() + clip.GetObjectCurveBindings().Count();
                        break;
                    case VirtualBlendTree tree:
                        nodes++;
                        foreach (var child in tree.Children) Walk(child.Motion);
                        break;
                }
            }
            foreach (var motion in motions) Walk(motion);
            return (curves, nodes);
        }

        /// <summary>Creates the baked blendshapes, the gate layers and the behaviors for every accepted piece.</summary>
        static void Finish(BuildContext context, VirtualAnimatorController fx, VRCAvatarDescriptor descriptor, Plan plan)
        {
            if (plan.Pieces.Count == 0) return;
            var renderers = BakeMeshes(context, plan);
            if (plan.Gates.Count > 0) AddGateLayers(fx, descriptor, plan);

            if (descriptor.faceTrackingMeshes == null) descriptor.faceTrackingMeshes = new List<SkinnedMeshRenderer>();
            foreach (var renderer in renderers.Keys)
                if (!descriptor.faceTrackingMeshes.Contains(renderer))
                    descriptor.faceTrackingMeshes.Add(renderer);
            if (descriptor.faceTrackingParameterBehaviors == null)
                descriptor.faceTrackingParameterBehaviors = new List<VRCAvatarDescriptor.FaceTrackingParameterBehavior>();
            foreach (var piece in plan.Pieces)
                descriptor.faceTrackingParameterBehaviors.Add(piece.Driver.ToBehavior(piece.Blendshape, piece.Gate?.Name));

            var baked = descriptor.gameObject.GetComponent<FermataNativeFTBakedShapes>();
            if (!baked) baked = descriptor.gameObject.AddComponent<FermataNativeFTBakedShapes>();
            foreach (var pair in renderers)
                baked.entries.Add(new FermataNativeFTBakedShapes.Entry { renderer = pair.Key, blendshapes = pair.Value });
        }

        /// <summary>Adds one single-frame blendshape per piece, made of the source deltas it carries, to copies of the meshes.</summary>
        static Dictionary<SkinnedMeshRenderer, List<string>> BakeMeshes(BuildContext context, Plan plan)
        {
            var copies = new Dictionary<SkinnedMeshRenderer, (Mesh Original, Mesh Copy)>();
            var added = new Dictionary<SkinnedMeshRenderer, List<string>>();
            foreach (var source in plan.Pieces.SelectMany(p => p.Contributions.Keys).Distinct().Select(s => plan.Sources[s]))
            {
                if (copies.ContainsKey(source.Renderer)) continue;
                var copy = Object.Instantiate(source.Mesh);
                copy.name = source.Mesh.name;
                context.AssetSaver.SaveAsset(copy);
                copies[source.Renderer] = (source.Mesh, copy);
                added[source.Renderer] = new List<string>();
                source.Renderer.sharedMesh = copy;
            }

            var next = 0;
            foreach (var piece in plan.Pieces)
            {
                string name;
                do name = NamePrefix + (next++).ToString("000", CultureInfo.InvariantCulture);
                while (copies.Values.Any(m => m.Copy.GetBlendShapeIndex(name) >= 0));
                piece.Blendshape = name;

                foreach (var group in piece.Contributions.GroupBy(c => plan.Sources[c.Key].Renderer))
                {
                    var (original, copy) = copies[group.Key];
                    var count = original.vertexCount;
                    Vector3[] vertices = new Vector3[count], normals = new Vector3[count], tangents = new Vector3[count];
                    Vector3[] v = new Vector3[count], n = new Vector3[count], t = new Vector3[count];
                    foreach (var contribution in group)
                    {
                        var source = plan.Sources[contribution.Key];
                        original.GetBlendShapeFrameVertices(source.Index, 0, v, n, t);
                        var scale = contribution.Value / source.FrameWeight;
                        for (var i = 0; i < count; i++)
                        {
                            vertices[i] += v[i] * scale;
                            normals[i] += n[i] * scale;
                            tangents[i] += t[i] * scale;
                        }
                    }
                    copy.AddBlendShapeFrame(name, 100f, vertices, normals, tangents);
                    added[group.Key].Add(name);
                }
            }
            return added;
        }

        /// <summary>
        /// One layer multiplies each gate's factors into a parameter, mirroring the original blend trees; another
        /// re-enters a state every frame so a Blendshapes Set behaviour copies those parameters into the set weights
        /// (it only reads them on state entry).
        /// </summary>
        static void AddGateLayers(VirtualAnimatorController fx, VRCAvatarDescriptor descriptor, Plan plan)
        {
            var parameters = fx.Parameters;
            var existingSets = new HashSet<string>((descriptor.faceTrackingBlendshapesSetList ?? new List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet>())
                .Where(s => s != null).Select(s => s.setName), StringComparer.Ordinal);
            var index = 0;
            foreach (var gate in plan.Gates)
            {
                string name;
                do name = GatePrefix + (index++).ToString(CultureInfo.InvariantCulture);
                while (parameters.ContainsKey(name) || existingSets.Contains(name));
                gate.Name = name;
                fx.SetParameter(name, new AnimatorControllerParameter { name = name, type = AnimatorControllerParameterType.Float });
            }
            fx.SetParameter(OneParameter, new AnimatorControllerParameter { name = OneParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });

            var empty = VirtualClip.Create(NamePrefix + "Empty");
            var gateTree = VirtualBlendTree.Create(NamePrefix + "Gates");
            gateTree.BlendType = BlendTreeType.Direct;
            gateTree.Children = plan.Gates.Select(g => new VirtualBlendTree.VirtualChildMotion
            {
                Motion = Chain(fx, g.Factors, 0, g.Name, empty),
                DirectBlendParameter = OneParameter,
            }).ToImmutableList();
            var gateLayer = fx.AddLayer(new LayerPriority(int.MaxValue), NamePrefix + "Gates");
            gateLayer.DefaultWeight = 1f;
            var gateState = gateLayer.StateMachine.AddState("Gates", gateTree);
            gateState.WriteDefaultValues = true;
            gateLayer.StateMachine.DefaultState = gateState;

            var weightLayer = fx.AddLayer(new LayerPriority(int.MaxValue), NamePrefix + "Gate Weights");
            weightLayer.DefaultWeight = 1f;
            var a = weightLayer.StateMachine.AddState("Apply A");
            var b = weightLayer.StateMachine.AddState("Apply B");
            foreach (var (from, to) in new[] { (a, b), (b, a) })
            {
                from.WriteDefaultValues = true;
                from.Behaviours = plan.Gates.Select((g, i) => (g, i)).GroupBy(x => x.i / MaxSetsPerBehaviour)
                    .Select(group => (StateMachineBehaviour)SetWeights(group.Select(x => x.g))).ToImmutableList();
                var transition = VirtualStateTransition.Create();
                transition.SetDestination(to);
                transition.Conditions = ImmutableList.Create(new AnimatorCondition
                {
                    mode = AnimatorConditionMode.Greater,
                    parameter = OneParameter,
                    threshold = 0.5f,
                });
                transition.ExitTime = null;
                transition.Duration = 0f;
                transition.HasFixedDuration = true;
                from.Transitions = ImmutableList.Create(transition);
            }
            weightLayer.StateMachine.DefaultState = a;

            if (descriptor.faceTrackingBlendshapesSetList == null)
                descriptor.faceTrackingBlendshapesSetList = new List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet>();
            var defaults = fx.Parameters;
            foreach (var gate in plan.Gates)
            {
                var weight = gate.Factors.Aggregate(1f, (w, f) => w * f.Evaluate(defaults.TryGetValue(f.Parameter, out var p) ? p.defaultFloat : 0f));
                descriptor.faceTrackingBlendshapesSetList.Add(new VRCAvatarDescriptor.FaceTrackingBlendshapesSet
                {
                    setName = gate.Name,
                    setWeight = Mathf.Clamp01(weight),
                    faceTrackingBlendshapeNames = Enumerable.Repeat("", (int)VRCAvatarDescriptor.FaceTrackingKey.Max).ToArray(),
                    faceTrackingBlendshapeMultipliers = Enumerable.Repeat(1f, (int)VRCAvatarDescriptor.FaceTrackingKey.Max).ToArray(),
                    visemeBlendshapeNames = Enumerable.Repeat("", (int)VRC_AvatarDescriptor.Viseme.Count).ToArray(),
                    visemeBlendshapeMultipliers = Enumerable.Repeat(1f, (int)VRC_AvatarDescriptor.Viseme.Count).ToArray(),
                });
            }
        }

        /// <summary>A blend tree whose output parameter is the product of the factors from <paramref name="index"/> on.</summary>
        static VirtualMotion Chain(VirtualAnimatorController fx, IReadOnlyList<Factor> factors, int index, string output, VirtualClip empty)
        {
            if (index == factors.Count)
            {
                var clip = VirtualClip.Create(output);
                clip.SetFloatCurve("", typeof(Animator), output, AnimationCurve.Constant(0f, 1f / 60f, 1f));
                return clip;
            }
            var factor = factors[index];
            var rest = Chain(fx, factors, index + 1, output, empty);
            var tree = VirtualBlendTree.Create(output);
            if (factor.Linear)
            {
                tree.BlendType = BlendTreeType.Direct;
                tree.Children = ImmutableList.Create(new VirtualBlendTree.VirtualChildMotion { Motion = rest, DirectBlendParameter = factor.Parameter });
                return tree;
            }
            tree.BlendType = BlendTreeType.Simple1D;
            tree.BlendParameter = factor.Parameter;
            tree.UseAutomaticThresholds = false;
            tree.Children = factor.Knots.Select((knot, i) => new VirtualBlendTree.VirtualChildMotion
            {
                Motion = Scaled(fx, rest, factor.Values[i], output, empty),
                Threshold = knot,
            }).ToImmutableList();
            return tree;
        }

        static VirtualMotion Scaled(VirtualAnimatorController fx, VirtualMotion motion, float value, string output, VirtualClip empty)
        {
            if (Math.Abs(value) <= Term.Epsilon) return empty;
            if (Math.Abs(value - 1f) <= Term.Epsilon) return motion;
            var constant = ConstPrefix + value.ToString("R", CultureInfo.InvariantCulture);
            if (!fx.Parameters.ContainsKey(constant))
                fx.SetParameter(constant, new AnimatorControllerParameter { name = constant, type = AnimatorControllerParameterType.Float, defaultFloat = value });
            var tree = VirtualBlendTree.Create(output);
            tree.BlendType = BlendTreeType.Direct;
            tree.Children = ImmutableList.Create(new VirtualBlendTree.VirtualChildMotion { Motion = motion, DirectBlendParameter = constant });
            return tree;
        }

        static VRCAnimatorFaceTrackingBlendshapesSet SetWeights(IEnumerable<BakeGate> gates)
        {
            var behaviour = ScriptableObject.CreateInstance<VRCAnimatorFaceTrackingBlendshapesSet>();
            behaviour.name = NamePrefix + "Gate Weights";
            behaviour.blendSets = gates.Select(g => new VRC_AnimatorFaceTrackingBlendshapesSet.BlendSet
            {
                type = VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter,
                sourceParameter = g.Name,
                blendshapesSetName = g.Name,
                weight = 1f,
                convertRange = false,
                sourceMin = 0f,
                sourceMax = 1f,
                destMin = 0f,
                destMax = 1f,
                lerpSpeed = 0f,
            }).ToList();
            return behaviour;
        }

        /// <summary>
        /// Analyses the edited layers again: every blendshape must keep exactly its terms that weren't baked, so the
        /// animator and the baked shapes together still produce the original weights.
        /// </summary>
        static List<string> SelfCheck(Plan plan)
        {
            var problems = new List<string>();
            foreach (var bake in plan.Layers)
            {
                var after = BlendTreeTerms.Analyze(bake.State, plan.Binary);
                foreach (var shape in bake.Before.Keys.Union(after.Keys))
                {
                    var expected = (bake.Before.TryGetValue(shape, out var before) ? before : new List<Term>()).Select(Describe).ToList();
                    foreach (var c in plan.Accepted.Where(c => c.Layer == bake && c.Shape == shape))
                        expected.Remove(Describe(c.Term));
                    var actual = (after.TryGetValue(shape, out var a) ? a : new List<Term>()).Select(Describe).ToList();
                    var e = string.Join(" + ", expected.OrderBy(s => s, StringComparer.Ordinal));
                    var k = string.Join(" + ", actual.OrderBy(s => s, StringComparer.Ordinal));
                    if (e != k) problems.Add($"'{bake.Layer.Name}' {shape.Path}:{shape.Name} kept {k}, expected {e}.");
                }
            }

            foreach (var layer in plan.AllLayers.Where(l => l != plan.Smoothing))
            foreach (var parameter in LiveReads(layer))
                if (plan.Inputs.TryGetValue(parameter, out var target) && plan.Trimmed.Contains(target))
                    problems.Add($"'{target.Parameter}' is no longer forwarded but layer '{layer.Name}' still reads '{parameter}'.");
            return problems;
        }

        // Opaque terms have no stable identity between analyses, so they are compared by count only.
        static string Describe(Term t) => t.Opaque != null
            ? "opaque"
            : t.Coefficient.ToString("0.####", CultureInfo.InvariantCulture) + "*[" + t.Signature + "]";
    }
}
