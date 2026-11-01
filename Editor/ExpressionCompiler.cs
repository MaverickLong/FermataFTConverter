using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>
    /// An expression as a constant plus scaled atoms, with the range it can take. Atoms are tracking keys (0..1) and calls
    /// such as max(a, b) over keys. An expression that isn't linear in those is one atom, clamped the way a forwarded
    /// value is, so its value here always equals the forwarded input's.
    /// </summary>
    internal sealed class LinearForm
    {
        public readonly float Constant;
        public readonly IReadOnlyList<(string Atom, float Coefficient)> Terms;
        public readonly float Low, High;

        LinearForm(float constant, IEnumerable<(string, float)> terms, float low, float high)
        {
            Constant = constant;
            Terms = terms.Where(t => Math.Abs(t.Item2) > 1e-7f).ToList();
            Low = low;
            High = high;
        }

        /// <summary>The forwarded value of this expression: linear when it can be, and always within -1..1.</summary>
        public static LinearForm Of(string expression)
        {
            var parsed = Parser.TryParse(expression);
            if (parsed != null)
            {
                var low = parsed.Constant + parsed.Terms.Values.Sum(c => Math.Min(c, 0f));
                var high = parsed.Constant + parsed.Terms.Values.Sum(c => Math.Max(c, 0f));
                if (low >= -1f && high <= 1f)
                    return new LinearForm(parsed.Constant, parsed.Terms.Select(kv => (kv.Key, kv.Value)), low, high);
            }
            return new LinearForm(0f, new[] { ($"clamp({expression}, -1, 1)", 1f) }, -1f, 1f);
        }

        /// <summary>(E - a) * scale, as a form of its own.</summary>
        public LinearForm Shifted(float a, float scale)
        {
            var bounds = new[] { (Low - a) * scale, (High - a) * scale };
            return new LinearForm((Constant - a) * scale, Terms.Select(t => (t.Atom, t.Coefficient * scale)), bounds.Min(), bounds.Max());
        }

        /// <summary>a*x + b*y, with no clamp: this isn't a forwarded value but arithmetic on two of them.</summary>
        public static LinearForm Combine(float a, LinearForm x, float b, LinearForm y)
        {
            var terms = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (atom, c) in x.Terms.Select(t => (t.Atom, t.Coefficient * a)).Concat(y.Terms.Select(t => (t.Atom, t.Coefficient * b))))
            {
                terms.TryGetValue(atom, out var sum);
                terms[atom] = sum + c;
            }
            var ends = new[] { a * x.Low, a * x.High };
            var ends2 = new[] { b * y.Low, b * y.High };
            return new LinearForm(a * x.Constant + b * y.Constant, terms.Select(kv => (kv.Key, kv.Value)), ends.Min() + ends2.Min(), ends.Max() + ends2.Max());
        }

        public LinearForm Plus(float constant) => new LinearForm(Constant + constant, Terms, Low + constant, High + constant);

        /// <summary>The shortest spelling tried: each term scaled on its own, or one common scale around the sum.</summary>
        public string Text()
        {
            var plain = Join(Terms, Constant);
            var scales = Terms.Select(t => Math.Abs(t.Coefficient)).Distinct().ToList();
            if (Terms.Count < 2 || scales.Count != 1 || Math.Abs(scales[0] - 1f) <= ExpressionText.Epsilon) return plain;
            // k * (a + b) + c, or c - k * (a + b) when every term is negative.
            var negative = Terms.All(t => t.Coefficient < 0f);
            var inner = Join(Terms.Select(t => (t.Atom, negative ? 1f : Math.Sign(t.Coefficient) * 1f)).ToList(), 0f);
            var scaled = ExpressionText.Num(scales[0]) + " * (" + inner + ")";
            string factored;
            if (Math.Abs(Constant) <= ExpressionText.Epsilon) factored = negative ? "-" + scaled : scaled;
            else if (negative) factored = Constant > 0f ? ExpressionText.Num(Constant) + " - " + scaled : "-" + scaled + " - " + ExpressionText.Num(-Constant);
            else factored = scaled + (Constant > 0f ? " + " + ExpressionText.Num(Constant) : " - " + ExpressionText.Num(-Constant));
            return NativeExpression.CountInstructions(factored) < NativeExpression.CountInstructions(plain) ? factored : plain;
        }

        static string Join(IReadOnlyCollection<(string Atom, float Coefficient)> terms, float constant)
        {
            var text = "";
            // Positive terms first: a leading minus costs an instruction that "b - a" doesn't.
            foreach (var (atom, c) in terms.OrderBy(t => t.Coefficient < 0f ? 1 : 0))
            {
                var size = Math.Abs(c);
                var part = Math.Abs(size - 1f) <= ExpressionText.Epsilon ? atom : ExpressionText.Num(size) + " * " + atom;
                text += text.Length == 0 ? (c < 0f ? "-" + part : part) : (c < 0f ? " - " : " + ") + part;
            }
            if (Math.Abs(constant) > ExpressionText.Epsilon)
                text = text.Length == 0 ? ExpressionText.Num(constant)
                    : constant > 0f ? ExpressionText.Num(constant) + (text[0] == '-' ? " - " + text.Substring(1) : " + " + text)
                    : text + " - " + ExpressionText.Num(-constant);
            return text.Length == 0 ? "0" : text;
        }

        /// <summary>Parses + - * / over numbers, keys and calls. Null when a product of two non-constants makes it non-linear.</summary>
        sealed class Parser
        {
            public sealed class Value
            {
                public float Constant;
                public Dictionary<string, float> Terms = new Dictionary<string, float>(StringComparer.Ordinal);
                public bool IsConstant => Terms.Count == 0;
            }

            readonly string _s;
            int _i;

            Parser(string s) { _s = s; }

            public static Value TryParse(string expression)
            {
                var parser = new Parser(expression);
                var value = parser.Sum();
                parser.Skip();
                return value != null && parser._i == expression.Length ? value : null;
            }

            void Skip()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            static Value Add(Value a, Value b, float sign)
            {
                var r = new Value { Constant = a.Constant + sign * b.Constant, Terms = new Dictionary<string, float>(a.Terms, StringComparer.Ordinal) };
                foreach (var kv in b.Terms)
                {
                    r.Terms.TryGetValue(kv.Key, out var c);
                    r.Terms[kv.Key] = c + sign * kv.Value;
                }
                return r;
            }

            static Value Times(Value a, float k) => new Value { Constant = a.Constant * k, Terms = a.Terms.ToDictionary(kv => kv.Key, kv => kv.Value * k, StringComparer.Ordinal) };

            Value Sum()
            {
                var left = Product();
                while (left != null)
                {
                    Skip();
                    if (_i >= _s.Length || (_s[_i] != '+' && _s[_i] != '-')) break;
                    var sign = _s[_i++] == '+' ? 1f : -1f;
                    var right = Product();
                    if (right == null) return null;
                    left = Add(left, right, sign);
                }
                return left;
            }

            Value Product()
            {
                var left = Unary();
                while (left != null)
                {
                    Skip();
                    if (_i >= _s.Length || (_s[_i] != '*' && _s[_i] != '/')) break;
                    var op = _s[_i++];
                    var right = Unary();
                    if (right == null) return null;
                    if (op == '/')
                    {
                        if (!right.IsConstant || right.Constant == 0f) return null;
                        left = Times(left, 1f / right.Constant);
                    }
                    else if (right.IsConstant) left = Times(left, right.Constant);
                    else if (left.IsConstant) left = Times(right, left.Constant);
                    else return null;
                }
                return left;
            }

            Value Unary()
            {
                Skip();
                if (_i >= _s.Length) return null;
                if (_s[_i] == '-')
                {
                    _i++;
                    var inner = Unary();
                    return inner == null ? null : Times(inner, -1f);
                }
                if (_s[_i] == '(')
                {
                    _i++;
                    var inner = Sum();
                    Skip();
                    if (inner == null || _i >= _s.Length || _s[_i] != ')') return null;
                    _i++;
                    return inner;
                }
                var start = _i;
                if (char.IsDigit(_s[_i]) || _s[_i] == '.')
                {
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    return float.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? new Value { Constant = number } : null;
                }
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                if (_i == start) return null;
                var name = _s.Substring(start, _i - start);
                Skip();
                if (_i < _s.Length && _s[_i] == '(')
                {
                    // Only a max of plain keys has a known range (0..1); any other call makes the whole input one atom.
                    var open = _i;
                    var depth = 0;
                    for (; _i < _s.Length; _i++)
                    {
                        if (_s[_i] == '(') depth++;
                        else if (_s[_i] == ')' && --depth == 0) break;
                    }
                    if (_i >= _s.Length) return null;
                    _i++;
                    var arguments = _s.Substring(open + 1, _i - open - 2).Split(',').Select(a => a.Trim()).ToList();
                    if (name != "max" || !arguments.All(NativeExpression.TrackingKeys.Contains)) return null;
                    return new Value { Terms = { [$"max({string.Join(", ", arguments)})"] = 1f } };
                }
                return NativeExpression.TrackingKeys.Contains(name) ? new Value { Terms = { [name] = 1f } } : null;
            }
        }
    }

    /// <summary>
    /// 2D blend weights in closed form, measured against Unity 2022.3 on a grid over -1..1 (error under 0.00001):
    /// <list type="bullet">
    /// <item>Freeform Cartesian with all points on x = y is a 1D blend on (x + y) / 2.</item>
    /// <item>Simple Directional with a center and a convex ring: in the wedge between neighbours A and B, the point is
    /// u*A + v*B. Each ring child weighs u or v, the center 1 - u - v, and past the ring u and v are divided by u + v.
    /// Freeform Directional depends on the angle, which expressions can't compute, so it stays in the animator.</item>
    /// </list>
    /// </summary>
    internal static class TwoDBlend
    {
        public const string AveragePrefix = "FNFT/Average/";

        public static bool OnDiagonal(IReadOnlyList<Vector2> positions) =>
            positions.Count >= 2 && positions.All(p => Math.Abs(p.x - p.y) <= 1e-5f) && positions.Select(p => p.x).Distinct().Count() == positions.Count;

        /// <summary>The ring in angle order, or null unless there's a center at the origin and a strictly surrounding convex ring.</summary>
        static List<int> Ring(IReadOnlyList<Vector2> positions)
        {
            if (positions.Count(p => p == Vector2.zero) != 1) return null;
            var ring = Enumerable.Range(0, positions.Count).Where(i => positions[i] != Vector2.zero)
                .OrderBy(i => Mathf.Atan2(positions[i].y, positions[i].x)).ToList();
            if (ring.Count < 3) return null;
            for (var k = 0; k < ring.Count; k++)
            {
                Vector2 a = positions[ring[k]], b = positions[ring[(k + 1) % ring.Count]], c = positions[ring[(k + 2) % ring.Count]];
                if (Cross(a, b) <= 1e-5f || Cross(b - a, c - b) < -1e-5f) return null;
            }
            return ring;
        }

        public static bool IsConvexFan(IReadOnlyList<Vector2> positions) => Ring(positions) != null;

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>The linear function that is 1 at <paramref name="at"/> and 0 at <paramref name="zero"/>, as (coefficient of x, of y).</summary>
        static Vector2 Barycentric(Vector2 at, Vector2 zero)
        {
            var det = Cross(at, zero);
            return new Vector2(zero.y / det, -zero.x / det);
        }

        public static float Evaluate(Factor factor, float x, float y)
        {
            var positions = factor.Positions;
            var ring = Ring(positions);
            var center = Array.IndexOf(positions, Vector2.zero);
            var p = new Vector2(x, y);
            var scale = 1f;
            for (var k = 0; k < ring.Count; k++)
            {
                Vector2 a = positions[ring[k]], b = positions[ring[(k + 1) % ring.Count]];
                scale = Mathf.Max(scale, Vector2.Dot(Barycentric(a, b) + Barycentric(b, a), p));
            }
            var result = factor.Values[center];
            for (var k = 0; k < ring.Count; k++)
            {
                Vector2 at = positions[ring[k]], next = positions[ring[(k + 1) % ring.Count]], previous = positions[ring[(k + ring.Count - 1) % ring.Count]];
                var hat = Mathf.Max(0f, Mathf.Min(Vector2.Dot(Barycentric(at, next), p), Vector2.Dot(Barycentric(at, previous), p)));
                result += (factor.Values[ring[k]] - factor.Values[center]) * hat / scale;
            }
            return result;
        }

        /// <summary>The factor as expression text over the two inputs' expressions.</summary>
        public static string Text(Factor factor, string x, string y)
        {
            var positions = factor.Positions;
            var ring = Ring(positions);
            var center = factor.Values[Array.IndexOf(positions, Vector2.zero)];
            LinearForm formX = LinearForm.Of(x), formY = LinearForm.Of(y);
            string Linear(Vector2 c) => LinearForm.Combine(c.x, formX, c.y, formY).Text();

            // The ring's gauge: 1 on the ring, the most a wedge's u + v reaches. Opposite edges share one abs().
            var edges = new List<Vector2>();
            for (var k = 0; k < ring.Count; k++)
            {
                Vector2 a = positions[ring[k]], b = positions[ring[(k + 1) % ring.Count]];
                var edge = Barycentric(a, b) + Barycentric(b, a);
                if (!edges.Any(e => (e - edge).sqrMagnitude < 1e-8f)) edges.Add(edge);
            }
            var pairs = edges.Where(e => !edges.Any(o => (o + e).sqrMagnitude < 1e-8f && Index(edges, o) < Index(edges, e))).ToList();
            var gauge = pairs.Select(e => edges.Any(o => (o + e).sqrMagnitude < 1e-8f) ? $"abs({Linear(e)})" : Linear(e))
                .Aggregate((a, b) => $"max({a}, {b})");
            var numerator = Numerator(factor, ring, center, Linear);
            var text = $"{ExpressionText.Wrap(numerator)} / max(1, {gauge})";
            return Math.Abs(center) <= ExpressionText.Epsilon ? text : ExpressionText.Num(center) + " + " + text;
        }

        static int Index(List<Vector2> list, Vector2 value) => list.FindIndex(v => (v - value).sqrMagnitude < 1e-8f);

        /// <summary>
        /// What the ring's values add up to before dividing by the gauge. Values that lie on one plane cut off at 0 need no
        /// hat per child, which is what eye look's shapes come down to: max(x, 0) instead of three weights added up.
        /// </summary>
        static string Numerator(Factor factor, List<int> ring, float center, Func<Vector2, string> linear)
        {
            var positions = factor.Positions;
            var rise = ring.Select(i => factor.Values[i] - center).ToList();
            // Fit the plane from two independent points that are above 0.
            var lifted = Enumerable.Range(0, ring.Count).Where(k => rise[k] > ExpressionText.Epsilon).ToList();
            for (var a = 0; a < lifted.Count; a++)
            for (var b = a + 1; b < lifted.Count; b++)
            {
                Vector2 pa = positions[ring[lifted[a]]], pb = positions[ring[lifted[b]]];
                var det = Cross(pa, pb);
                if (Math.Abs(det) < 1e-5f) continue;
                float ra = rise[lifted[a]], rb = rise[lifted[b]];
                var plane = new Vector2((ra * pb.y - rb * pa.y) / det, (pa.x * rb - pb.x * ra) / det);
                var fits = true;
                for (var k = 0; k < ring.Count && fits; k++)
                {
                    float here = Vector2.Dot(plane, positions[ring[k]]), next = Vector2.Dot(plane, positions[ring[(k + 1) % ring.Count]]);
                    fits = Math.Abs(Math.Max(here, 0f) - rise[k]) <= 1e-4f && !(here > 1e-4f && next < -1e-4f) && !(here < -1e-4f && next > 1e-4f);
                }
                if (fits) return $"max(0, {linear(plane)})";
            }

            var parts = new List<string>();
            for (var k = 0; k < ring.Count; k++)
            {
                if (Math.Abs(rise[k]) <= ExpressionText.Epsilon) continue;
                Vector2 at = positions[ring[k]], next = positions[ring[(k + 1) % ring.Count]], previous = positions[ring[(k + ring.Count - 1) % ring.Count]];
                var hat = $"max(0, min({linear(Barycentric(at, next))}, {linear(Barycentric(at, previous))}))";
                var size = Math.Abs(rise[k]);
                var scaled = Math.Abs(size - 1f) <= ExpressionText.Epsilon ? hat : ExpressionText.Num(size) + " * " + hat;
                parts.Add(rise[k] < 0f ? "-" + scaled : scaled);
            }
            return parts.Count == 0 ? "0" : parts.Aggregate((a, b) => b[0] == '-' ? a + " - " + b.Substring(1) : a + " + " + b);
        }
    }

    /// <summary>Builds expression text from linear forms, spending no instruction a range or the output clamp makes unnecessary.</summary>
    internal static class ExpressionText
    {
        public const float Epsilon = 1e-6f;

        public static string Num(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        public static string Wrap(string e) => IsAtom(e) || IsWrapped(e) ? e : "(" + e + ")";

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

        /// <summary>(E - a) / (b - a) with no clamp, for a driver whose 0..1 output clamp does the clamping. a above b falls.</summary>
        public static string Ramp(LinearForm input, float a, float b) => input.Shifted(a, 1f / (b - a)).Text();

        /// <summary>clamp((E - a) / (b - a), 0, 1), leaving out each bound the input's range can't cross.</summary>
        public static string ClampedRamp(LinearForm input, float a, float b)
        {
            var form = input.Shifted(a, 1f / (b - a));
            var text = form.Text();
            var needLow = form.Low < -Epsilon;
            var needHigh = form.High > 1f + Epsilon;
            if (needLow && needHigh) return $"clamp({text}, 0, 1)";
            if (needLow) return $"max(0, {text})";
            return needHigh ? $"min(1, {text})" : text;
        }

        /// <summary>
        /// A piecewise linear function of the input, constant beyond its first and last knot. A falling single ramp is
        /// spelled as a ramp between its reversed knots, which costs less than one minus a rising one.
        /// </summary>
        public static string Piecewise(LinearForm input, IReadOnlyList<float> knots, IReadOnlyList<float> values)
        {
            var moving = Enumerable.Range(0, knots.Count - 1).Where(i => Math.Abs(values[i + 1] - values[i]) > Epsilon).ToList();
            if (moving.Count == 0) return Num(values[0]);
            if (moving.Count == 1)
            {
                var i = moving[0];
                float from = values[i], to = values[i + 1];
                // One segment across the input's whole range is a plain linear function: no ramp, no clamp.
                if (knots[i] <= input.Low + Epsilon && knots[i + 1] >= input.High - Epsilon)
                    return input.Shifted(knots[i], (to - from) / (knots[i + 1] - knots[i])).Plus(from).Text();
                // The whole function is one ramp between two levels; with levels 0 and 1 it needs nothing else.
                if (Math.Min(from, to) >= -Epsilon && Math.Abs(Math.Max(from, to) - 1f) <= Epsilon && Math.Abs(Math.Min(from, to)) <= Epsilon)
                    return to > from ? ClampedRamp(input, knots[i], knots[i + 1]) : ClampedRamp(input, knots[i + 1], knots[i]);
            }

            var text = Math.Abs(values[0]) > Epsilon ? Num(values[0]) : null;
            foreach (var i in moving)
            {
                var rise = values[i + 1] - values[i];
                var ramp = ClampedRamp(input, knots[i], knots[i + 1]);
                var size = Math.Abs(rise);
                var scaled = Math.Abs(size - 1f) <= Epsilon ? Wrap(ramp) : Num(size) + " * " + Wrap(ramp);
                text = text == null ? (rise > 0f ? scaled : "-" + scaled) : text + (rise > 0f ? " + " : " - ") + scaled;
            }
            return text;
        }
    }
}
