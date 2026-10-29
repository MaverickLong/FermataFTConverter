using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VRC.SDK3.Avatars.Components;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>The result of checking one custom expression.</summary>
    internal sealed class ExpressionCheck
    {
        public readonly List<string> Errors = new List<string>();

        /// <summary>Inputs the expression reads: tracking keys, Voice and Viseme_* names.</summary>
        public readonly List<string> Inputs = new List<string>();

        /// <summary>The SDK's estimate: every number, input, function call and operator counts 1.</summary>
        public int Instructions;

        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// VRChat native face tracking custom expressions: the documented syntax and limits, checked the way the SDK's
    /// FaceTrackingExpressionValidator does, so the inspector and the build report the same problems.
    /// </summary>
    internal static class NativeExpression
    {
        /// <summary>Per-avatar budget for all custom expressions; the client skips custom behaviors past it.</summary>
        public const int MaxInstructions = 512;

        /// <summary>Per-avatar limit on parameter behaviors.</summary>
        public const int MaxBehaviors = 128;

        public const int MaxLength = 512;
        public const int MaxExpressionInstructions = 64;
        const int MaxTokens = 128;
        const int MaxNesting = 16;
        const int MaxStackDepth = 16;
        const int MaxNumberLength = 20;
        const float MaxNumberMagnitude = 10000f;

        public const string Voice = "Voice";

        public static readonly HashSet<string> TrackingKeys = new HashSet<string>(
            Enum.GetNames(typeof(VRCAvatarDescriptor.FaceTrackingKey))
                .Where(n => n != nameof(VRCAvatarDescriptor.FaceTrackingKey.Max)),
            StringComparer.Ordinal);

        public static readonly string[] Visemes = Enumerable.Range(0, (int)VRCAvatarDescriptor.Viseme.Count)
            .Select(v => VRCAvatarDescriptor.GetFaceTrackingVisemeInputName((VRCAvatarDescriptor.Viseme)v))
            .ToArray();

        static readonly HashSet<string> AllInputs = new HashSet<string>(TrackingKeys.Append(Voice).Concat(Visemes), StringComparer.Ordinal);

        static readonly Dictionary<string, int> FunctionArity = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "abs", 1 }, { "min", 2 }, { "max", 2 }, { "clamp", 3 },
        };

        public static bool IsViseme(string input) => Array.IndexOf(Visemes, input) >= 0;

        /// <summary>True when the expression is a single tracking key, which can be forwarded without a custom expression.</summary>
        public static bool IsSingleKey(string expression) => TrackingKeys.Contains(expression.Trim());

        public static int CountInstructions(string expression) => Check(expression).Instructions;

        public static ExpressionCheck Check(string expression)
        {
            var check = new ExpressionCheck();
            if (string.IsNullOrWhiteSpace(expression))
            {
                check.Errors.Add("The expression is empty.");
                return check;
            }
            if (expression.Length > MaxLength)
                check.Errors.Add($"It is {expression.Length} characters long; VRChat allows {MaxLength}.");

            try
            {
                var tokens = Tokenize(expression);
                if (tokens.Count > MaxTokens)
                    check.Errors.Add($"It has {tokens.Count} tokens; VRChat allows {MaxTokens}.");
                var parser = new Parser(tokens, check);
                var depth = parser.ParseAll();
                if (parser.MaxNesting > MaxNesting)
                    check.Errors.Add($"It nests {parser.MaxNesting} levels deep; VRChat allows {MaxNesting}.");
                if (depth > MaxStackDepth)
                    check.Errors.Add($"It needs an evaluation stack {depth} deep; VRChat allows {MaxStackDepth}.");
            }
            catch (SyntaxError e)
            {
                check.Errors.Add(e.Message);
            }

            if (check.Instructions > MaxExpressionInstructions)
                check.Errors.Add($"It uses about {check.Instructions} instructions; VRChat allows {MaxExpressionInstructions} per expression.");
            return check;
        }

        enum Kind
        {
            Number,
            Name,
            Symbol
        }

        readonly struct Token
        {
            public readonly Kind Kind;
            public readonly string Text;
            public readonly int Position;

            public Token(Kind kind, string text, int position)
            {
                Kind = kind;
                Text = text;
                Position = position;
            }

            public bool Is(string symbol) => Kind == Kind.Symbol && Text == symbol;
        }

        sealed class SyntaxError : Exception
        {
            public SyntaxError(string message) : base(message)
            {
            }
        }

        static List<Token> Tokenize(string expression)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < expression.Length)
            {
                var c = expression[i];
                var start = i;
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < expression.Length && char.IsDigit(expression[i + 1])))
                {
                    var seenPoint = false;
                    while (i < expression.Length && (char.IsDigit(expression[i]) || (expression[i] == '.' && !seenPoint)))
                    {
                        if (expression[i] == '.') seenPoint = true;
                        i++;
                    }
                    var text = expression.Substring(start, i - start);
                    if (text.Length > MaxNumberLength)
                        throw new SyntaxError($"The number '{text}' at position {start + 1} is too long.");
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || Math.Abs(value) > MaxNumberMagnitude)
                        throw new SyntaxError($"The number '{text}' at position {start + 1} is outside the supported range (±{MaxNumberMagnitude}).");
                    tokens.Add(new Token(Kind.Number, text, start));
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
                    tokens.Add(new Token(Kind.Name, expression.Substring(start, i - start), start));
                }
                else if ("+-*/(),".IndexOf(c) >= 0)
                {
                    tokens.Add(new Token(Kind.Symbol, c.ToString(), start));
                    i++;
                }
                else
                {
                    throw new SyntaxError($"Unexpected character '{c}' at position {start + 1}.");
                }
            }
            return tokens;
        }

        /// <summary>
        /// Recursive descent over expression := term (('+' | '-') term)*, term := unary (('*' | '/') unary)*,
        /// unary := '-' unary | value. Each method returns the evaluation stack depth its part needs.
        /// </summary>
        sealed class Parser
        {
            readonly List<Token> _tokens;
            readonly ExpressionCheck _check;
            int _next;
            int _nesting;

            public int MaxNesting { get; private set; }

            public Parser(List<Token> tokens, ExpressionCheck check)
            {
                _tokens = tokens;
                _check = check;
            }

            public int ParseAll()
            {
                var depth = Expression();
                if (_next < _tokens.Count)
                {
                    var t = _tokens[_next];
                    throw new SyntaxError(t.Is(")")
                        ? $"')' at position {t.Position + 1} has no matching '('."
                        : $"Missing operator before '{t.Text}' at position {t.Position + 1}.");
                }
                return depth;
            }

            bool Accept(string symbol)
            {
                if (_next >= _tokens.Count || !_tokens[_next].Is(symbol)) return false;
                _next++;
                return true;
            }

            int Expression()
            {
                var depth = Term();
                while (Accept("+") || Accept("-"))
                {
                    _check.Instructions++;
                    depth = Math.Max(depth, 1 + Term());
                }
                return depth;
            }

            int Term()
            {
                var depth = Unary();
                while (Accept("*") || Accept("/"))
                {
                    _check.Instructions++;
                    depth = Math.Max(depth, 1 + Unary());
                }
                return depth;
            }

            int Unary()
            {
                if (Accept("-"))
                {
                    _check.Instructions++;
                    return Unary();
                }
                return Value();
            }

            int Value()
            {
                if (_next >= _tokens.Count)
                    throw new SyntaxError("The expression ends where a value is expected.");
                var t = _tokens[_next++];
                switch (t.Kind)
                {
                    case Kind.Number:
                        _check.Instructions++;
                        return 1;
                    case Kind.Name when _next < _tokens.Count && _tokens[_next].Is("("):
                        return Call(t);
                    case Kind.Name:
                        Input(t);
                        _check.Instructions++;
                        return 1;
                }
                if (t.Is("("))
                {
                    Enter();
                    var depth = Expression();
                    Close(t);
                    return depth;
                }
                throw new SyntaxError($"Expected a value at position {t.Position + 1}, found '{t.Text}'.");
            }

            int Call(Token name)
            {
                if (!FunctionArity.TryGetValue(name.Text, out var arity))
                    throw new SyntaxError($"Unknown function '{name.Text}' at position {name.Position + 1}. Supported: abs, min, max, clamp.");
                var open = _tokens[_next++];
                Enter();
                _check.Instructions++;
                var depth = 0;
                var count = 0;
                do
                {
                    depth = Math.Max(depth, count + Expression());
                    count++;
                } while (Accept(","));
                Close(open);
                if (count != arity)
                    throw new SyntaxError($"{name.Text}() takes {arity} argument{(arity == 1 ? "" : "s")}, but got {count}.");
                return depth;
            }

            void Enter()
            {
                _nesting++;
                MaxNesting = Math.Max(MaxNesting, _nesting);
            }

            void Close(Token open)
            {
                if (!Accept(")"))
                    throw new SyntaxError($"'(' at position {open.Position + 1} is never closed.");
                _nesting--;
            }

            void Input(Token t)
            {
                if (AllInputs.Contains(t.Text))
                {
                    _check.Inputs.Add(t.Text);
                    return;
                }
                if (FunctionArity.ContainsKey(t.Text))
                    throw new SyntaxError($"{t.Text} at position {t.Position + 1} is a function; use {t.Text}(...).");
                var match = AllInputs.FirstOrDefault(n => string.Equals(n, t.Text, StringComparison.OrdinalIgnoreCase));
                throw new SyntaxError(match != null
                    ? $"Unknown input '{t.Text}' at position {t.Position + 1}. Names are case-sensitive: did you mean '{match}'?"
                    : $"Unknown input '{t.Text}' at position {t.Position + 1}.");
            }
        }
    }
}
