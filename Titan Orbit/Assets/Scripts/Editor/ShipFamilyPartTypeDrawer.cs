#if UNITY_EDITOR
using System;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Popup of canonical Part Type groups for <see cref="ShipFamilyPartTypeAttribute"/>
    /// string fields (Name Mappings and Part Profiles).
    /// </summary>
    [CustomPropertyDrawer(typeof(ShipFamilyPartTypeAttribute))]
    public sealed class ShipFamilyPartTypeDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            var attr = (ShipFamilyPartTypeAttribute)attribute;
            string[] choices = ShipFamilyPartTypes.GetInspectorChoices(attr.IncludeUnmappedAndIgnore);
            property.stringValue = DrawPopup(position, label, property.stringValue, choices);
        }

        /// <summary>IMGUI popup used by the property drawer and suggestion review.</summary>
        public static string DrawPopup(Rect position, GUIContent label, string current, string[] choices)
        {
            string[] options = BuildOptions(current, choices);
            int index = IndexOfChoice(options, current);
            int next = EditorGUI.Popup(position, label, index, ToGuiContents(options));
            return options[Mathf.Clamp(next, 0, options.Length - 1)];
        }

        /// <summary>Layout-mode popup for EditorGUILayout screens (Cursor suggestion review).</summary>
        public static string DrawPopupLayout(string label, string current, bool includeUnmappedAndIgnore = true)
        {
            string[] choices = ShipFamilyPartTypes.GetInspectorChoices(includeUnmappedAndIgnore);
            string[] options = BuildOptions(current, choices);
            int index = IndexOfChoice(options, current);
            int next = EditorGUILayout.Popup(label, index, options);
            return options[Mathf.Clamp(next, 0, options.Length - 1)];
        }

        static string[] BuildOptions(string current, string[] choices)
        {
            if (choices == null || choices.Length == 0)
                return new[] { string.IsNullOrEmpty(current) ? ShipFamilyPartTypes.Unmapped : current };

            if (string.IsNullOrWhiteSpace(current) || IndexOfChoice(choices, current) >= 0)
                return choices;

            // Keep a legacy / custom label selectable so the drawer does not silently rewrite it.
            var expanded = new string[choices.Length + 1];
            expanded[0] = current.Trim();
            Array.Copy(choices, 0, expanded, 1, choices.Length);
            return expanded;
        }

        static int IndexOfChoice(string[] options, string current)
        {
            if (options == null || options.Length == 0)
                return 0;
            if (string.IsNullOrWhiteSpace(current))
            {
                int unmapped = IndexOfChoice(options, ShipFamilyPartTypes.Unmapped);
                return unmapped >= 0 ? unmapped : 0;
            }

            for (int i = 0; i < options.Length; i++)
            {
                if (string.Equals(options[i], current, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return 0;
        }

        static GUIContent[] ToGuiContents(string[] options)
        {
            var contents = new GUIContent[options.Length];
            for (int i = 0; i < options.Length; i++)
                contents[i] = new GUIContent(options[i]);
            return contents;
        }
    }
}
#endif
