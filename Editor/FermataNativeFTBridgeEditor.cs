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
                    $"FermataFT Inputs: {plan.Targets.Count}\n" +
                    $"Custom Expressions: {custom}\n" +
                    $"Instructions: {plan.ExistingInstructions + plan.NewInstructions}/{NativeExpression.MaxInstructions}\n" +
                    $"Removed FX layer: '{FermataProfile.BinaryDecodeLayer}'\n" +
                    $"Removed Synced Parameters: {_report.SyncedBitsFreed}\n" +
                    $"Eye Lock States: {plan.EyeLockStates.Count}\n",
                    MessageType.Info);

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
