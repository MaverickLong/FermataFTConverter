using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.UIElements;

namespace Maberikku.FermataNativeFT.Editor
{
    internal static class Messages
    {
        public const string Title = "Fermata Native FT Bridge";

        public const string Intro =
            "Drives FermataFT paremeters via VRChat native face tracking.";

        public const string NoFermata =
            "No FermataFT was found on this avatar." +
            "Add FermataFT to the avatar, or remove this prefab.";
    }

    /// <summary>An NDMF console entry.</summary>
    internal sealed class BridgeError : IError
    {
        readonly string _message;

        BridgeError(ErrorSeverity severity, string message)
        {
            Severity = severity;
            _message = message;
        }

        public ErrorSeverity Severity { get; }

        public string ToMessage() => $"[{Messages.Title}] {_message}";

        public VisualElement CreateVisualElement(ErrorReport report)
        {
            var label = new Label(ToMessage());
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        public void AddReference(ObjectReference reference)
        {
        }

        public static void Report(ErrorSeverity severity, string message)
        {
            var error = new BridgeError(severity, message);
            ErrorReport.ReportError(error);
            if (severity >= ErrorSeverity.Error) Debug.LogError(error.ToMessage());
            else if (severity == ErrorSeverity.NonFatal) Debug.LogWarning(error.ToMessage());
            else Debug.Log(error.ToMessage());
        }
    }
}
