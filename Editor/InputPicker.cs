using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>
    /// A searchable list of the inputs an expression can read: tracking keys by face region, Voice and the visemes.
    /// Follows Pawlygon VRC Face Tracking's input picker (MIT, see THIRD_PARTY_NOTICES.md).
    /// </summary>
    internal sealed class InputPicker : AdvancedDropdown
    {
        readonly Action<string> _pick;
        readonly List<string> _values = new List<string>();
        int _groups;

        public InputPicker(Action<string> pick) : base(new AdvancedDropdownState())
        {
            _pick = pick;
            minimumSize = new Vector2(280, 360);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            _values.Clear();
            _groups = 0;
            var root = Group("Inputs");
            var regions = new Dictionary<string, AdvancedDropdownItem>();
            foreach (var key in Enum.GetNames(typeof(VRCAvatarDescriptor.FaceTrackingKey)).Where(NativeExpression.TrackingKeys.Contains))
            {
                var region = Region(key);
                if (!regions.TryGetValue(region, out var group))
                {
                    regions[region] = group = Group(region);
                    root.AddChild(group);
                }
                group.AddChild(Item(key));
            }
            root.AddChild(Item(NativeExpression.Voice));
            var visemes = Group("Visemes");
            foreach (var viseme in NativeExpression.Visemes) visemes.AddChild(Item(viseme));
            root.AddChild(visemes);
            return root;
        }

        static string Region(string key)
        {
            if (key.StartsWith("EyeLook", StringComparison.Ordinal)) return "Eye Gaze";
            if (key.StartsWith("Eye", StringComparison.Ordinal)) return "Eyes";
            if (key.StartsWith("Brow", StringComparison.Ordinal)) return "Brows";
            if (key.StartsWith("Nose", StringComparison.Ordinal) || key.StartsWith("Nasal", StringComparison.Ordinal)) return "Nose";
            if (key.StartsWith("Cheek", StringComparison.Ordinal)) return "Cheeks";
            if (key.StartsWith("Jaw", StringComparison.Ordinal) || key == "MouthClosed") return "Jaw";
            if (key.StartsWith("Lip", StringComparison.Ordinal)) return "Lips";
            if (key.StartsWith("Mouth", StringComparison.Ordinal)) return "Mouth";
            if (key.StartsWith("Tongue", StringComparison.Ordinal)) return "Tongue";
            return "Throat & Neck";
        }

        // Groups get negative ids so they never collide with an input's index.
        AdvancedDropdownItem Group(string label) => new AdvancedDropdownItem(label) { id = -1 - ++_groups };

        AdvancedDropdownItem Item(string value)
        {
            _values.Add(value);
            return new AdvancedDropdownItem(value) { id = _values.Count - 1 };
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (item.id >= 0 && item.id < _values.Count) _pick(_values[item.id]);
        }
    }
}
