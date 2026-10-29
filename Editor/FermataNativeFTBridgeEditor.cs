using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Maberikku.FermataNativeFT.Editor
{
    [CustomEditor(typeof(FermataNativeFTBridge))]
    internal sealed class FermataNativeFTBridgeEditor : UnityEditor.Editor
    {
        // Re-checking scans FermataFT's animators, so hierarchy churn is coalesced.
        const double MinRecheckInterval = 0.5;

        InspectorReport _report;
        bool _dirty = true;
        double _lastCheck = double.NegativeInfinity;
        bool _showTargets;

        void OnEnable()
        {
            EditorApplication.hierarchyChanged += MarkDirty;
            EditorApplication.projectChanged += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
            _dirty = true;
        }

        void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkDirty;
            EditorApplication.projectChanged -= MarkDirty;
            Undo.undoRedoPerformed -= MarkDirty;
        }

        void MarkDirty()
        {
            _dirty = true;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.LabelField(Messages.Intro, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();

            if (_dirty)
            {
                if (_report == null || EditorApplication.timeSinceStartup - _lastCheck >= MinRecheckInterval)
                {
                    _report = InspectorCheck.Run((FermataNativeFTBridge)target);
                    _lastCheck = EditorApplication.timeSinceStartup;
                    _dirty = false;
                }
                else
                {
                    EditorApplication.delayCall += () =>
                    {
                        if (this) Repaint();
                    };
                }
            }

            foreach (var finding in _report.Findings)
                EditorGUILayout.HelpBox(finding.Message, ToMessageType(finding.Severity));

            var plan = _report.Plan;
            if (plan != null && !plan.HasErrors)
            {
                var custom = plan.Targets.Count(t => t.SingleKey == null);
                EditorGUILayout.HelpBox(
                    $"Ready. At upload, native face tracking will drive {plan.Targets.Count} FermataFT inputs " +
                    $"({custom} custom expressions, about {plan.ExistingInstructions + plan.NewInstructions} of " +
                    $"{NativeExpression.MaxInstructions} instructions). Layer '{FermataProfile.BinaryDecodeLayer}' will be removed " +
                    $"and about {_report.SyncedBitsFreed} synced parameter bits freed. OSCmooth smoothing and all other " +
                    "FermataFT settings stay as they are.",
                    MessageType.Info);

                if (plan.UnlockEyes)
                {
                    EditorGUILayout.HelpBox(
                        $"For native eye tracking, the {plan.EyeLockStates.Count} animator states that lock the eyes " +
                        "(FermataFT's eye tracking and gesture states) keep VRChat's eye input on, and the avatar's Eye Gaze " +
                        "Mode and Eyelids are set to Face Tracking. With FermataFT's Eye Tracking menu toggle off, the eyes stay still.",
                        MessageType.Info);
                }

                _showTargets = EditorGUILayout.Foldout(_showTargets, "Forwarded inputs", true);
                if (_showTargets)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        foreach (var t in plan.Targets)
                            EditorGUILayout.LabelField(t.Parameter, t.Expression, EditorStyles.wordWrappedLabel);
                    }
                }
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Check again")) MarkDirty();
        }

        static MessageType ToMessageType(Severity severity)
        {
            switch (severity)
            {
                case Severity.Error: return MessageType.Error;
                case Severity.Warning: return MessageType.Warning;
                default: return MessageType.Info;
            }
        }
    }
}
