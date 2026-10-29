using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

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
        IMGUIContainer _status;
        CustomForwardList _custom;

        SerializedProperty CustomForwards => serializedObject.FindProperty(nameof(FermataNativeFTBridge.customForwards));

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
            _status?.MarkDirtyRepaint();
        }

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            _status = new IMGUIContainer(DrawStatus);
            root.Add(_status);
            _custom = new CustomForwardList(serializedObject, CustomForwards);
            root.Add(_custom.Root);
            root.TrackSerializedObjectValue(serializedObject, _ =>
            {
                _custom.Refresh();
                MarkDirty();
            });
            return root;
        }

        void DrawStatus()
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
                        if (this) _status?.MarkDirtyRepaint();
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
                    $"Customised Inputs: {plan.Targets.Count(t => t.IsCustom)}\n" +
                    $"Instructions: {plan.ExistingInstructions + plan.NewInstructions}/{NativeExpression.MaxInstructions}\n" +
                    $"Removed FX layer: '{FermataProfile.BinaryDecodeLayer}'\n" +
                    $"Removed Synced Parameters: {_report.SyncedBitsFreed}\n" +
                    $"Eye Lock States: {plan.EyeLockStates.Count}\n",
                    MessageType.Info);
            }

            if (plan != null && plan.Targets.Count > 0)
            {
                _showTargets = EditorGUILayout.Foldout(_showTargets, "Forwarded inputs", true);
                if (_showTargets)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        foreach (var t in plan.Targets)
                            DrawTarget(t);
                    }
                }
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Check again")) MarkDirty();
        }

        void DrawTarget(ForwardTarget t)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(t.Parameter, t.Expression, EditorStyles.wordWrappedLabel);
                if (t.IsCustom)
                {
                    GUILayout.Label("custom", EditorStyles.miniLabel, GUILayout.Width(60));
                }
                else if (GUILayout.Button(new GUIContent("Override", "Copy to Custom Expressions to edit it."), EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    _custom.Add(t.Parameter, t.Expression);
                }
            }
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

    /// <summary>
    /// The editable list of custom expressions, checked as they are typed. Follows the design of Pawlygon VRC Face
    /// Tracking's Face Tracking Parameters inspector (MIT, see THIRD_PARTY_NOTICES.md).
    /// </summary>
    internal sealed class CustomForwardList
    {
        readonly SerializedObject _serializedObject;
        readonly SerializedProperty _list;
        readonly VisualElement _rows = new VisualElement();
        readonly List<(Image icon, Label message)> _states = new List<(Image, Label)>();
        int _renderedCount = -1;

        public readonly VisualElement Root = new VisualElement();

        public CustomForwardList(SerializedObject serializedObject, SerializedProperty list)
        {
            _serializedObject = serializedObject;
            _list = list;
            Root.style.marginTop = 6;
            var title = new Label("Custom Expressions");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            Root.Add(title);
            Root.Add(_rows);
            var add = new Button(() => Add("", "")) { text = "Add" };
            add.style.alignSelf = Align.FlexStart;
            Root.Add(add);
            Refresh();
        }

        public void Add(string parameter, string expression)
        {
            _serializedObject.Update();
            var index = _list.arraySize++;
            var element = _list.GetArrayElementAtIndex(index);
            element.FindPropertyRelative(nameof(CustomForward.parameter)).stringValue = parameter;
            element.FindPropertyRelative(nameof(CustomForward.expression)).stringValue = expression;
            _serializedObject.ApplyModifiedProperties();
            Refresh();
        }

        void Remove(int index)
        {
            _serializedObject.Update();
            if (index >= _list.arraySize) return;
            _list.DeleteArrayElementAtIndex(index);
            _serializedObject.ApplyModifiedProperties();
            Refresh();
        }

        public void Refresh()
        {
            _serializedObject.Update();
            // Rows are only rebuilt when entries are added or removed, so typing keeps focus.
            if (_list.arraySize != _renderedCount) Rebuild();

            var names = Enumerable.Range(0, _list.arraySize)
                .Select(i => _list.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(CustomForward.parameter)).stringValue.Trim())
                .ToList();
            for (var i = 0; i < _states.Count && i < names.Count; i++)
            {
                var expression = _list.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(CustomForward.expression)).stringValue;
                var (problem, note) = Describe(names[i], expression, names.Count(n => n == names[i]) > 1);
                var (icon, message) = _states[i];
                icon.image = problem != null ? EditorGUIUtility.IconContent("console.erroricon.sml").image : null;
                icon.tooltip = problem ?? "";
                message.text = problem ?? note;
                message.style.color = problem != null ? new Color(0.9f, 0.35f, 0.3f) : new Color(0.55f, 0.55f, 0.55f);
                message.style.display = string.IsNullOrEmpty(message.text) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        static (string problem, string note) Describe(string name, string expression, bool duplicate)
        {
            if (name.Length == 0 && string.IsNullOrWhiteSpace(expression)) return (null, "Blank: ignored.");
            if (name.Length == 0) return ("Enter a parameter name.", null);
            if (duplicate) return ($"'{name}' is listed more than once.", null);
            var check = NativeExpression.Check(expression);
            if (!check.IsValid) return (check.Errors[0], null);
            return (null, NativeExpression.IsSingleKey(expression) ? "0 instructions (single key)" : $"{check.Instructions} instructions");
        }

        void Rebuild()
        {
            _renderedCount = _list.arraySize;
            _rows.Clear();
            _states.Clear();
            if (_list.arraySize == 0)
            {
                var none = new Label("None. Use Override on a forwarded input, or Add.");
                none.style.color = new Color(0.55f, 0.55f, 0.55f);
                _rows.Add(none);
                return;
            }

            for (var i = 0; i < _list.arraySize; i++)
            {
                var element = _list.GetArrayElementAtIndex(i);
                var index = i;
                var entry = new VisualElement();
                entry.style.marginBottom = 2;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;

                var name = new TextField { tooltip = "Animator parameter, e.g. v2/JawOpen." };
                name.style.width = Length.Percent(36);
                name.style.flexShrink = 0;
                name.BindProperty(element.FindPropertyRelative(nameof(CustomForward.parameter)));
                row.Add(name);
                row.Add(new Label("="));

                var expression = new TextField { tooltip = "Tracking keys, Voice, Viseme_*, numbers, + - * / abs() min() max() clamp(). Result clamped to -1..1." };
                expression.style.flexGrow = 1;
                expression.style.flexShrink = 1;
                expression.BindProperty(element.FindPropertyRelative(nameof(CustomForward.expression)));
                row.Add(expression);

                // Where the cursor was, so inserted inputs land there; -1 means the end.
                var cursor = -1;
                expression.RegisterCallback<KeyUpEvent>(_ => cursor = expression.cursorIndex);
                expression.RegisterCallback<PointerUpEvent>(_ => cursor = expression.cursorIndex);
                expression.RegisterCallback<FocusOutEvent>(_ => cursor = expression.cursorIndex);

                var icon = new Image();
                icon.style.width = 16;
                icon.style.height = 16;
                row.Add(icon);

                var pick = new Button { text = "+", tooltip = "Insert an input" };
                pick.clicked += () => new InputPicker(input => cursor = Insert(expression, cursor, input, input.Length, true)).Show(pick.worldBound);
                row.Add(pick);

                var functions = new Button { text = "ƒ", tooltip = "Insert a function or operator" };
                functions.clicked += () => FunctionMenu((text, caret, isValue) => cursor = Insert(expression, cursor, text, caret, isValue))
                    .DropDown(functions.worldBound);
                row.Add(functions);

                var remove = new Button(() => Remove(index)) { text = "✕", tooltip = "Remove" };
                row.Add(remove);
                entry.Add(row);

                var message = new Label();
                message.style.whiteSpace = WhiteSpace.Normal;
                message.style.marginLeft = 4;
                entry.Add(message);

                _states.Add((icon, message));
                _rows.Add(entry);
            }
        }

        /// <summary>
        /// Inserts text at the cursor (or the end) and returns the new cursor. A value that would directly follow
        /// another value gets " + " in front, so the expression stays valid.
        /// </summary>
        static int Insert(TextField field, int cursor, string text, int caret, bool isValue)
        {
            var value = field.value ?? "";
            var at = cursor < 0 || cursor > value.Length ? value.Length : cursor;
            var before = value.Substring(0, at).TrimEnd();
            var joiner = "";
            if (isValue && before.Length > 0)
            {
                var last = before[before.Length - 1];
                if (char.IsLetterOrDigit(last) || last == '_' || last == '.' || last == ')') joiner = " + ";
                else if (last != '(' && !char.IsWhiteSpace(value[at - 1])) joiner = " ";
            }
            field.value = value.Substring(0, at) + joiner + text + value.Substring(at);
            var next = at + joiner.Length + caret;
            field.schedule.Execute(() =>
            {
                field.Focus();
                field.SelectRange(next, next);
            });
            return next;
        }

        delegate void InsertText(string text, int caret, bool isValue);

        static GenericMenu FunctionMenu(InsertText insert)
        {
            var menu = new GenericMenu();
            void Item(string label, string text, int caret, bool isValue) => menu.AddItem(new GUIContent(label), false, () => insert(text, caret, isValue));
            Item("abs(x)", "abs()", 4, true);
            Item("min(a, b)", "min(, )", 4, true);
            Item("max(a, b)", "max(, )", 4, true);
            Item("clamp(x, low, high)", "clamp(, 0, 1)", 6, true);
            menu.AddSeparator("");
            Item("( )", "()", 1, true);
            Item("+", " + ", 3, false);
            Item("-", " - ", 3, false);
            Item("*", " * ", 3, false);
            Item("/", " / ", 3, false);
            return menu;
        }
    }
}
