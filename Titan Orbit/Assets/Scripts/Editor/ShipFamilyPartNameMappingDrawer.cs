using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Inspector row for one <see cref="ShipFamilyPartNameMapping"/>.
    /// Shows the part's own base stats (the fields its part type authors) so HyperFalcon_Cockpit
    /// can be edited without changing every other cockpit.
    /// </summary>
    [CustomPropertyDrawer(typeof(ShipFamilyPartNameMapping))]
    public sealed class ShipFamilyPartNameMappingDrawer : PropertyDrawer
    {
        const float LabelWidthRatio = 0.52f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing;
            if (!property.isExpanded)
                return line;

            // Foldout, eight identity fields, status line.
            int rows = 10;
            string[] fields = AuthoringFields(property);
            if (fields.Length > 0)
                rows += 2 + fields.Length; // header, stat rows, replace button
            return rows * (line + gap);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing;
            float y = position.y;
            float width = position.width;

            SerializedProperty nameProp = property.FindPropertyRelative("discoveredName");
            SerializedProperty partTypeProp = property.FindPropertyRelative("partType");
            string discoveredName = nameProp != null ? nameProp.stringValue : "Part";
            string partType = partTypeProp != null ? partTypeProp.stringValue : string.Empty;
            string header = string.IsNullOrEmpty(partType)
                ? discoveredName
                : discoveredName + "  (" + partType + ")";

            property.isExpanded = EditorGUI.Foldout(
                new Rect(position.x, y, width, line),
                property.isExpanded,
                header,
                true);
            y += line + gap;

            if (!property.isExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            EditorGUI.indentLevel++;
            y = DrawProperty(position, y, width, line, gap, nameProp);
            y = DrawProperty(position, y, width, line, gap, partTypeProp);
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("contributesAbilityStats"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("enablePropulsionVfx"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("propulsionVfxScale"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("seenInFamilies"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("notes"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("includeInPopulate"));

            SerializedProperty hasStatsProp = property.FindPropertyRelative("hasBaseStats");
            bool hasStats = hasStatsProp != null && hasStatsProp.boolValue;
            string[] fields = AuthoringFields(property);

            string status = fields.Length == 0
                ? "No ability stats (cosmetic, Unmapped, or Ignore)."
                : hasStats
                    ? "Master stats for this part only. Other parts of the same type are unchanged."
                    : "No per-part stats yet. Apply to a ship, or copy from the Part Profile.";
            EditorGUI.LabelField(new Rect(position.x, y, width, line), status);
            y += line + gap;

            if (fields.Length > 0)
            {
                EditorGUI.LabelField(new Rect(position.x, y, width, line), "Base Stats", EditorStyles.boldLabel);
                y += line + gap;

                SerializedProperty statsProp = property.FindPropertyRelative("baseStats");
                y = DrawStatFields(position, y, width, line, gap, statsProp, hasStatsProp, fields);

                if (GUI.Button(new Rect(position.x, y, width, line), "Replace With Part Profile Stats"))
                    CopyFromPartProfile(property);
            }

            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        static float DrawProperty(Rect position, float y, float width, float line, float gap, SerializedProperty prop)
        {
            if (prop == null)
                return y;
            EditorGUI.PropertyField(new Rect(position.x, y, width, line), prop);
            return y + line + gap;
        }

        static float DrawStatFields(
            Rect position,
            float y,
            float width,
            float line,
            float gap,
            SerializedProperty statsProp,
            SerializedProperty hasStatsProp,
            string[] fields)
        {
            if (statsProp == null || fields == null)
                return y;

            for (int i = 0; i < fields.Length; i++)
            {
                SerializedProperty field = statsProp.FindPropertyRelative(fields[i]);
                if (field == null)
                    continue;

                float labelWidth = width * LabelWidthRatio;
                EditorGUI.LabelField(new Rect(position.x, y, labelWidth, line), field.displayName);
                EditorGUI.BeginChangeCheck();
                float value = EditorGUI.FloatField(new Rect(position.x + labelWidth, y, width - labelWidth, line), field.floatValue);
                if (EditorGUI.EndChangeCheck())
                {
                    field.floatValue = value;
                    if (hasStatsProp != null)
                        hasStatsProp.boolValue = true;
                }

                y += line + gap;
            }

            return y;
        }

        /// <summary>Overwrites this row from its Part Profile and marks the block as the master.</summary>
        static void CopyFromPartProfile(SerializedProperty property)
        {
            var set = property.serializedObject.targetObject as ShipFamilyPartCalcProfileSet;
            SerializedProperty nameProp = property.FindPropertyRelative("discoveredName");
            if (set == null || nameProp == null)
                return;

            string discoveredName = nameProp.stringValue;
            property.serializedObject.ApplyModifiedProperties();
            Undo.RecordObject(set, "Copy Part Profile Onto Name Mapping");
            if (!set.SeedNameMappingBaseStats(discoveredName, overwrite: true))
            {
                EditorUtility.DisplayDialog(
                    "Name Mapping",
                    "Could not copy Part Profile stats onto \"" + discoveredName + "\". " +
                    "The row needs a real part type and contributesAbilityStats on.",
                    "OK");
                return;
            }

            EditorUtility.SetDirty(set);
            property.serializedObject.Update();
        }

        /// <summary>Category-filtered field names for this row, or empty when the part authors nothing.</summary>
        static string[] AuthoringFields(SerializedProperty property)
        {
            SerializedProperty contributesProp = property.FindPropertyRelative("contributesAbilityStats");
            SerializedProperty partTypeProp = property.FindPropertyRelative("partType");
            SerializedProperty nameProp = property.FindPropertyRelative("discoveredName");
            if (contributesProp == null || !contributesProp.boolValue || partTypeProp == null)
                return Array.Empty<string>();

            string partType = partTypeProp.stringValue;
            if (string.IsNullOrWhiteSpace(partType)
                || string.Equals(partType, ShipFamilyPartTypes.Unmapped, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, ShipFamilyPartTypes.Ignore, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();

            string componentId = nameProp != null ? nameProp.stringValue : partType;
            List<ShipComponentStatCategory> categories =
                ShipFamilyComponentPartKey.InferDefaultStatCategoriesForPartType(partType, componentId);
            return ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(categories, componentId);
        }
    }
}
