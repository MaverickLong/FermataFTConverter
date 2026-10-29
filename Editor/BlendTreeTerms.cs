using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor.Animations;
using UnityEngine;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>How a blend-tree weight depends on one animator parameter.</summary>
    internal sealed class Factor
    {
        public readonly string Parameter;

        /// <summary>A Direct blend tree child: the weight is the parameter's value.</summary>
        public readonly bool Linear;

        /// <summary>
        /// A 1D blend tree: piecewise linear through (Knots[i], Values[i]) and constant beyond the first and last knot.
        /// Values are scaled so the largest magnitude is 1; the scale is in the term's coefficient.
        /// </summary>
        public readonly float[] Knots;

        public readonly float[] Values;

        /// <summary>Identifies the factor exactly; equal keys mean equal functions.</summary>
        public readonly string Key;

        Factor(string parameter, bool linear, float[] knots, float[] values)
        {
            Parameter = parameter;
            Linear = linear;
            Knots = knots;
            Values = values;
            Key = linear
                ? parameter + "|w"
                : parameter + "|" + string.Join(",", knots.Select((k, i) => Num(k) + ":" + Num(values[i])));
        }

        public static Factor Weight(string parameter) => new Factor(parameter, true, null, null);

        public static Factor Curve(string parameter, float[] knots, float[] values) => new Factor(parameter, false, knots, values);

        public float Evaluate(float x)
        {
            if (Linear) return x;
            if (x <= Knots[0]) return Values[0];
            for (var i = 1; i < Knots.Length; i++)
                if (x <= Knots[i])
                    return Values[i - 1] + (Values[i] - Values[i - 1]) * (x - Knots[i - 1]) / (Knots[i] - Knots[i - 1]);
            return Values[Values.Length - 1];
        }

        static string Num(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A clip slot in a layer's motion tree: a blend tree child, or the state's own motion.</summary>
    internal sealed class Occurrence
    {
        public readonly VirtualBlendTree Tree;
        public readonly int Child;
        public readonly VirtualState State;

        public Occurrence(VirtualBlendTree tree, int child, VirtualState state)
        {
            Tree = tree;
            Child = child;
            State = state;
        }

        public VirtualClip Clip => (Tree != null ? Tree.Children[Child].Motion : State.Motion) as VirtualClip;

        public void SetClip(VirtualClip clip)
        {
            if (Tree == null)
            {
                State.Motion = clip;
                return;
            }
            var old = Tree.Children[Child];
            Tree.Children = Tree.Children.SetItem(Child, new VirtualBlendTree.VirtualChildMotion
            {
                Motion = clip,
                CycleOffset = old.CycleOffset,
                DirectBlendParameter = old.DirectBlendParameter,
                Mirror = old.Mirror,
                Threshold = old.Threshold,
                Position = old.Position,
                TimeScale = old.TimeScale,
            });
        }
    }

    /// <summary>One summand of a blendshape's weight: <see cref="Coefficient"/> times the product of <see cref="Factors"/>.</summary>
    internal sealed class Term
    {
        public const float Epsilon = 1e-6f;

        static int _nextOpaqueId;

        /// <summary>In blendshape weight units, like the clip values it comes from.</summary>
        public readonly float Coefficient;

        /// <summary>Sorted by key. A parameter can appear more than once (nested blend trees on the same parameter).</summary>
        public readonly IReadOnlyList<Factor> Factors;

        /// <summary>Why this part of the weight is not such a product (2D blend, animated over time, ...); null when it is.</summary>
        public readonly string Opaque;

        /// <summary>Every clip slot that contributes to this term; each slot belongs to exactly one term per blendshape.</summary>
        public readonly IReadOnlyList<Occurrence> Occurrences;

        /// <summary>Terms with the same signature differ only in their coefficient. Opaque terms are all distinct.</summary>
        public readonly string Signature;

        Term(float coefficient, IReadOnlyList<Factor> factors, string opaque, IReadOnlyList<Occurrence> occurrences)
        {
            Coefficient = coefficient;
            Factors = factors;
            Opaque = opaque;
            Occurrences = occurrences;
            Signature = opaque != null ? "#" + _nextOpaqueId++ : string.Join(";", factors.Select(f => f.Key));
        }

        public static Term Constant(float value, Occurrence at) =>
            new Term(value, Array.Empty<Factor>(), null, new[] { at });

        public static Term MakeOpaque(string reason, IReadOnlyList<Occurrence> occurrences) =>
            new Term(0f, Array.Empty<Factor>(), reason, occurrences);

        public Term With(float coefficient, IReadOnlyList<Occurrence> occurrences) =>
            Opaque != null ? MakeOpaque(Opaque, occurrences) : new Term(coefficient, Factors, null, occurrences);

        public Term Times(Factor factor)
        {
            if (Opaque != null) return this;
            var factors = Factors.Append(factor).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
            return new Term(Coefficient, factors, null, Occurrences);
        }

        public bool IsZero => Opaque == null && Math.Abs(Coefficient) < Epsilon;
    }

    /// <summary>
    /// Rewrites the blendshape weights a single-state blend-tree layer produces as sums of <see cref="Term"/>s.
    /// Follows Unity's blending: a Direct tree adds its children scaled by their parameters, a 1D tree interpolates
    /// linearly between neighbouring thresholds and holds the end values beyond them, and a child that doesn't
    /// animate a blendshape contributes its default value, which the bake requires to be 0.
    /// </summary>
    internal static class BlendTreeTerms
    {
        public const string BlendShapePrefix = "blendShape.";

        public static Dictionary<(string Path, string Name), List<Term>> Analyze(VirtualState state) =>
            Motion(state.Motion, new Occurrence(null, -1, state));

        /// <summary>
        /// Replaces the state's motion with a copy in which no blend tree is shared, so each clip slot is reached by
        /// exactly one path and can be edited on its own. Clips stay shared until they are edited.
        /// </summary>
        public static void Detach(VirtualState state)
        {
            state.Motion = Copy(state.Motion);
        }

        static VirtualMotion Copy(VirtualMotion motion)
        {
            if (!(motion is VirtualBlendTree tree)) return motion;
            var copy = VirtualBlendTree.Create(tree.Name);
            copy.BlendType = tree.BlendType;
            copy.BlendParameter = tree.BlendParameter;
            copy.BlendParameterY = tree.BlendParameterY;
            copy.MinThreshold = tree.MinThreshold;
            copy.MaxThreshold = tree.MaxThreshold;
            copy.UseAutomaticThresholds = tree.UseAutomaticThresholds;
            copy.NormalizedBlendValues = tree.NormalizedBlendValues;
            copy.Children = tree.Children.Select(c => new VirtualBlendTree.VirtualChildMotion
            {
                Motion = Copy(c.Motion),
                CycleOffset = c.CycleOffset,
                DirectBlendParameter = c.DirectBlendParameter,
                Mirror = c.Mirror,
                Threshold = c.Threshold,
                Position = c.Position,
                TimeScale = c.TimeScale,
            }).ToImmutableList();
            return copy;
        }

        /// <summary>Every parameter the motion's blend trees read.</summary>
        public static IEnumerable<string> Parameters(VirtualMotion motion)
        {
            if (!(motion is VirtualBlendTree tree)) yield break;
            if (tree.BlendType == BlendTreeType.Direct)
            {
                foreach (var c in tree.Children) yield return c.DirectBlendParameter;
            }
            else
            {
                yield return tree.BlendParameter;
                if (tree.BlendType != BlendTreeType.Simple1D) yield return tree.BlendParameterY;
            }
            foreach (var c in tree.Children)
            foreach (var p in Parameters(c.Motion))
                yield return p;
        }

        static Dictionary<(string, string), List<Term>> Motion(VirtualMotion motion, Occurrence at)
        {
            switch (motion)
            {
                case VirtualClip clip:
                    return Clip(clip, at);
                case VirtualBlendTree tree:
                    return Tree(tree, at.State);
                default:
                    return new Dictionary<(string, string), List<Term>>();
            }
        }

        static Dictionary<(string, string), List<Term>> Clip(VirtualClip clip, Occurrence at)
        {
            var result = new Dictionary<(string, string), List<Term>>();
            foreach (var binding in clip.GetFloatCurveBindings())
            {
                if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith(BlendShapePrefix, StringComparison.Ordinal)) continue;
                var shape = (binding.path, binding.propertyName.Substring(BlendShapePrefix.Length));
                var value = clip.IsMarkerClip ? null : ConstantValue(clip.GetFloatCurve(binding));
                result[shape] = new List<Term>
                {
                    value.HasValue ? Term.Constant(value.Value, at) : Term.MakeOpaque("animates over time", new[] { at }),
                };
            }
            return result;
        }

        static float? ConstantValue(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return null;
            var keys = curve.keys;
            var value = keys[0].value;
            if (float.IsNaN(value) || float.IsInfinity(value)) return null;
            foreach (var key in keys)
                if (key.value != value || !Flat(key.inTangent) || !Flat(key.outTangent))
                    return null;
            return value;
        }

        // Equal values joined by flat or stepped tangents stay constant between keys.
        static bool Flat(float tangent) => tangent == 0f || float.IsInfinity(tangent);

        static Dictionary<(string, string), List<Term>> Tree(VirtualBlendTree tree, VirtualState state)
        {
            var children = tree.Children;
            var results = children.Select((c, i) => Motion(c.Motion, new Occurrence(tree, i, state))).ToList();
            var shapes = results.SelectMany(r => r.Keys).Distinct().ToList();
            var output = new Dictionary<(string, string), List<Term>>();

            switch (tree.BlendType)
            {
                case BlendTreeType.Direct:
                {
                    var normalized = tree.NormalizedBlendValues;
                    foreach (var shape in shapes)
                    {
                        var terms = new List<Term>();
                        for (var i = 0; i < children.Count; i++)
                        {
                            if (!results[i].TryGetValue(shape, out var childTerms)) continue;
                            foreach (var t in childTerms)
                                terms.Add(normalized ? Term.MakeOpaque("normalised Direct blend tree", t.Occurrences)
                                    : t.Times(Factor.Weight(children[i].DirectBlendParameter)));
                        }
                        output[shape] = Normalize(terms);
                    }
                    break;
                }
                case BlendTreeType.Simple1D:
                {
                    var order = Enumerable.Range(0, children.Count).OrderBy(i => children[i].Threshold).ToList();
                    var knots = order.Select(i => children[i].Threshold).ToArray();
                    foreach (var shape in shapes)
                    {
                        var terms = new List<Term>();
                        var groups = new Dictionary<string, (Term First, float[] Values, List<Occurrence> Occurrences)>(StringComparer.Ordinal);
                        var signatures = new List<string>();
                        for (var k = 0; k < order.Count; k++)
                        {
                            if (!results[order[k]].TryGetValue(shape, out var childTerms)) continue;
                            foreach (var t in childTerms)
                            {
                                if (t.Opaque != null)
                                {
                                    terms.Add(t);
                                    continue;
                                }
                                if (!groups.TryGetValue(t.Signature, out var group))
                                {
                                    groups[t.Signature] = group = (t, new float[order.Count], new List<Occurrence>());
                                    signatures.Add(t.Signature);
                                }
                                group.Values[k] += t.Coefficient;
                                group.Occurrences.AddRange(t.Occurrences);
                            }
                        }
                        foreach (var signature in signatures)
                        {
                            var group = groups[signature];
                            terms.Add(Along(tree.BlendParameter, knots, group.Values, group.First, group.Occurrences));
                        }
                        output[shape] = Normalize(terms);
                    }
                    break;
                }
                default:
                {
                    // 2D blends are only followed when every child produces the same weight, which makes the blend irrelevant.
                    foreach (var shape in shapes)
                    {
                        var perChild = results.Select(r => r.TryGetValue(shape, out var t) ? t : new List<Term>()).ToList();
                        var descriptions = perChild.Select(Describe).ToList();
                        if (descriptions.All(d => d == descriptions[0]))
                        {
                            output[shape] = perChild[0].Select(t => t.With(t.Coefficient,
                                perChild.SelectMany(c => c.Where(o => o.Signature == t.Signature)).SelectMany(o => o.Occurrences).ToList())).ToList();
                        }
                        else
                        {
                            output[shape] = perChild.SelectMany(c => c).Select(t => Term.MakeOpaque("2D blend", t.Occurrences)).ToList();
                        }
                    }
                    break;
                }
            }
            return output;
        }

        /// <summary>A 1D tree's sum of child terms with one signature: the children's coefficients interpolated over the parameter.</summary>
        static Term Along(string parameter, float[] knots, float[] values, Term first, List<Occurrence> occurrences)
        {
            var k = new List<float>();
            var v = new List<float>();
            for (var i = 0; i < knots.Length; i++)
            {
                if (k.Count > 0 && knots[i] == k[k.Count - 1])
                {
                    // Unity blends nothing between equal thresholds, so the weight would jump there.
                    if (Math.Abs(values[i] - v[v.Count - 1]) > Term.Epsilon)
                        return Term.MakeOpaque($"two '{parameter}' children at threshold {knots[i]}", occurrences);
                    continue;
                }
                k.Add(knots[i]);
                v.Add(values[i]);
            }
            if (v.All(x => Math.Abs(x - v[0]) <= Term.Epsilon)) return first.With(v[0], occurrences);
            var scale = v.Max(Math.Abs);
            return first.With(scale, occurrences).Times(Factor.Curve(parameter, k.ToArray(), v.Select(x => x / scale).ToArray()));
        }

        /// <summary>Merges terms with the same signature and drops zero ones.</summary>
        static List<Term> Normalize(List<Term> terms)
        {
            var result = new List<Term>();
            foreach (var group in terms.GroupBy(t => t.Signature, StringComparer.Ordinal))
            {
                var list = group.ToList();
                var merged = list.Count == 1 ? list[0] : list[0].With(list.Sum(t => t.Coefficient), list.SelectMany(t => t.Occurrences).ToList());
                if (!merged.IsZero) result.Add(merged);
            }
            return result;
        }

        static string Describe(List<Term> terms) =>
            string.Join("|", terms.Select(t => t.Signature + "@" + t.Coefficient.ToString("R", CultureInfo.InvariantCulture))
                .OrderBy(s => s, StringComparer.Ordinal));
    }
}
