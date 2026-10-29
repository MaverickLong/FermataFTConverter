using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.Components;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>
    /// Helpers for VRChat native face tracking custom expressions.
    /// </summary>
    internal static class NativeExpression
    {
        /// <summary>Per-avatar budget for all custom expressions; the client skips custom behaviors past it.</summary>
        public const int MaxInstructions = 512;

        /// <summary>Per-avatar limit on parameter behaviors.</summary>
        public const int MaxBehaviors = 128;

        public static readonly HashSet<string> TrackingKeys = new HashSet<string>(
            Enum.GetNames(typeof(VRCAvatarDescriptor.FaceTrackingKey))
                .Where(n => n != nameof(VRCAvatarDescriptor.FaceTrackingKey.Max)),
            StringComparer.Ordinal);

        static readonly HashSet<string> Functions = new HashSet<string>(StringComparer.Ordinal) { "abs", "min", "max", "clamp" };

        /// <summary>
        /// Instruction estimate using the SDK's counting rule: every number, input, function call and operator
        /// (unary minus included) is one instruction; parentheses and commas are free.
        /// </summary>
        public static int CountInstructions(string expression)
        {
            return Tokenize(expression, null);
        }

        /// <summary>Input names (tracking keys, Voice, Viseme_*) referenced by an expression.</summary>
        public static List<string> Inputs(string expression)
        {
            var inputs = new List<string>();
            Tokenize(expression, inputs);
            return inputs;
        }

        /// <summary>True when the expression is a single tracking key, which can be forwarded without a custom expression.</summary>
        public static bool IsSingleKey(string expression)
        {
            return TrackingKeys.Contains(expression.Trim());
        }

        static int Tokenize(string expression, List<string> inputs)
        {
            int count = 0;
            int i = 0;
            while (i < expression.Length)
            {
                char c = expression[i];
                if (char.IsDigit(c) || c == '.')
                {
                    while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.')) i++;
                    count++;
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
                    string identifier = expression.Substring(start, i - start);
                    if (!Functions.Contains(identifier)) inputs?.Add(identifier);
                    count++;
                }
                else
                {
                    if (c == '+' || c == '-' || c == '*' || c == '/') count++;
                    i++;
                }
            }
            return count;
        }
    }
}
