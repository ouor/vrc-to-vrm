using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace VrcToVrm
{
    /// <summary>Popup with a search field; used for meshes with hundreds of blendshapes.</summary>
    public class SearchableDropdown : AdvancedDropdown
    {
        public static string NoneLabel => L.Tr("ui.none");

        readonly string _title;
        readonly IList<string> _items;
        readonly Action<string> _onSelected;

        SearchableDropdown(string title, IList<string> items, Action<string> onSelected) : base(new AdvancedDropdownState())
        {
            _title = title;
            _items = items;
            _onSelected = onSelected;
            minimumSize = new Vector2(260, 320);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem(_title);
            root.AddChild(new AdvancedDropdownItem(NoneLabel) { id = -1 });
            for (int i = 0; i < _items.Count; i++) root.AddChild(new AdvancedDropdownItem(_items[i]) { id = i });
            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item) => _onSelected(item.id < 0 ? null : _items[item.id]);

        /// <summary>Draws a labeled popup button; the value changes asynchronously through onSelected.</summary>
        public static void Field(GUIContent label, string current, IList<string> items, Action<string> onSelected, params GUILayoutOption[] options)
        {
            var rect = EditorGUILayout.GetControlRect(options.Length > 0 ? options : new[] { GUILayout.MinWidth(80) });
            rect = EditorGUI.PrefixLabel(rect, label);
            var shown = string.IsNullOrEmpty(current) ? NoneLabel : current;
            if (EditorGUI.DropdownButton(rect, new GUIContent(shown, shown), FocusType.Keyboard))
                new SearchableDropdown(label.text, items, onSelected).Show(rect);
        }
    }
}
