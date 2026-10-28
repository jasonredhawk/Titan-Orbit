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

            // Foldout, identity fields, extra part-type rows, status line.
            int rows = 10 + AdditionalTypeRowCount(property);
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
            SerializedProperty extraTypesProp = property.FindPropertyRelative("additionalPartTypes");
            string discoveredName = nameProp != null ? nameProp.stringValue : "Part";
            string partType = partTypeProp != null ? partTypeProp.stringValue : string.Empty;
            string typeLabel = BuildTypeLabel(partType, extraTypesProp);
            string header = string.IsNullOrEmpty(typeLabel)
                ? discoveredName
                : discoveredName + "  (" + typeLabel + ")";

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
            y = DrawAdditionalPartTypes(position, y, width, line, gap, partTypeProp, extraTypesProp);
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("contributesAbilityStats"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("enablePropulsionVfx"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("propulsionVfxScale"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("seenInFamilies"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("notes"));
            y = DrawProperty(position, y, width, line, gap, property.FindPropertyRelative("includeInPopulate"));

            SerializedProperty hasStatsProp = property.FindPropertyRelative("hasBaseStats");
            bool hasStats = hasStatsProp != null && hasStatsProp.boolValue;
            string[] fields = AuthoringFields(property);

            bool hasExtraTypes = extraTypesProp != null && extraTypesProp.isArray && extraTypesProp.arraySize > 0;
            string status = fields.Length == 0
                ? "No ability stats (cosmetic, Unmapped, or Ignore)."
                : hasExtraTypes
                    ? hasStats
                        ? "Combined stats for this part. Replace copies every listed Part Profile onto this row."
                        : "Extra part types are listed. Replace copies those Part Profiles onto this row."
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

        static int AdditionalTypeRowCount(SerializedProperty property)
        {
            // Section label, one popup per extra type, and the Add button.
            SerializedProperty extra = property.FindPropertyRelative("additionalPartTypes");
            if (extra == null || !extra.isArray)
                return 0;
            return 2 + extra.arraySize;
        }

        static string BuildTypeLabel(string partType, SerializedProperty extraTypesProp)
        {
            if (extraTypesProp == null || !extraTypesProp.isArray || extraTypesProp.arraySize == 0)
                return partType ?? string.Empty;

            string label = partType ?? string.Empty;
            for (int i = 0; i < extraTypesProp.arraySize; i++)
            {
                string extra = extraTypesProp.GetArrayElementAtIndex(i).stringValue;
                if (string.IsNullOrWhiteSpace(extra))
                    continue;
                label = string.IsNullOrEmpty(label) ? extra : label + " + " + extra;
            }

            return label;
        }

        static float DrawAdditionalPartTypes(
            Rect position,
            float y,
            float width,
            float line,
            float gap,
            SerializedProperty partTypeProp,
            SerializedProperty extraTypesProp)
        {
            if (extraTypesProp == null || !extraTypesProp.isArray)
                return y;

            var label = new GUIContent(
                "Additional Part Types",
                "Extra Part Profiles whose stats are added onto this part. Part Type above still picks the mesh-scale group.");
            EditorGUI.LabelField(new Rect(position.x, y, width, line), label);
            y += line + gap;

            string primary = partTypeProp != null ? partTypeProp.stringValue : string.Empty;
            int removeIndex = -1;
            for (int i = 0; i < extraTypesProp.arraySize; i++)
            {
                SerializedProperty element = extraTypesProp.GetArrayElementAtIndex(i);
                y = DrawAdditionalTypeRow(
                    position, y, width, line, gap, extraTypesProp, element, i, primary, ref removeIndex);
            }

            if (removeIndex >= 0)
                extraTypesProp.DeleteArrayElementAtIndex(removeIndex);

            Rect addRect = EditorGUI.IndentedRect(new Rect(position.x, y, width, line));
            int previousIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            if (GUI.Button(addRect, "Add Part Type"))
            {
                int index = extraTypesProp.arraySize;
                extraTypesProp.InsertArrayElementAtIndex(index);
                extraTypesProp.GetArrayElementAtIndex(index).stringValue =
                    PickUnusedPartType(primary, extraTypesProp);
            }

            EditorGUI.indentLevel = previousIndent;
            return y + line + gap;
        }

        static float DrawAdditionalTypeRow(
            Rect position,
            float y,
            float width,
            float line,
            float gap,
            SerializedProperty extraTypesProp,
            SerializedProperty element,
            int index,
            string primary,
            ref int removeIndex)
        {
            Rect row = EditorGUI.IndentedRect(new Rect(position.x, y, width, line));
            const float removeWidth = 22f;
            var popupRect = new Rect(row.x, row.y, Mathf.Max(40f, row.width - removeWidth - 4f), row.height);
            var removeRect = new Rect(popupRect.xMax + 4f, row.y, removeWidth, row.height);

            int previousIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            string current = element.stringValue;
            string next = ShipFamilyPartTypeDrawer.DrawPopup(
                popupRect,
                GUIContent.none,
                current,
                ShipFamilyPartTypes.AdditionalInspectorChoices);
            if (!string.Equals(next, current, StringComparison.Ordinal)
                && !IsPartTypeAlreadyUsed(primary, extraTypesProp, index, next))
                element.stringValue = next;

            if (GUI.Button(removeRect, "X"))
                removeIndex = index;
            EditorGUI.indentLevel = previousIndent;
            return y + line + gap;
        }

        static bool IsPartTypeAlreadyUsed(
            string primary,
            SerializedProperty extraTypesProp,
            int skipIndex,
            string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return true;
            if (string.Equals(primary, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
            if (extraTypesProp == null || !extraTypesProp.isArray)
                return false;
            for (int i = 0; i < extraTypesProp.arraySize; i++)
            {
                if (i == skipIndex)
                    continue;
                string other = extraTypesProp.GetArrayElementAtIndex(i).stringValue;
                if (string.Equals(other, candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static string PickUnusedPartType(string primary, SerializedProperty extraTypesProp)
        {
            if (string.Equals(primary, ShipFamilyPartTypes.Engine, StringComparison.OrdinalIgnoreCase)
                && !IsPartTypeAlreadyUsed(primary, extraTypesProp, -1, ShipFamilyPartTypes.Thruster))
                return ShipFamilyPartTypes.Thruster;
            if (!IsPartTypeAlreadyUsed(primary, extraTypesProp, -1, ShipFamilyPartTypes.Tail))
                return ShipFamilyPartTypes.Tail;

            string[] choices = ShipFamilyPartTypes.AdditionalInspectorChoices;
            for (int i = 0; i < choices.Length; i++)
            {
                if (!IsPartTypeAlreadyUsed(primary, extraTypesProp, -1, choices[i]))
                    return choices[i];
            }

            return ShipFamilyPartTypes.Hull;
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
            bool primaryContributes = ShipFamilyPartTypes.IsContributingProfile(partType);
            SerializedProperty extraTypesProp = property.FindPropertyRelative("additionalPartTypes");
            bool extraContributes = false;
            if (extraTypesProp != null && extraTypesProp.isArray)
            {
                for (int i = 0; i < extraTypesProp.arraySize; i++)
                {
                    if (ShipFamilyPartTypes.IsContributingProfile(extraTypesProp.GetArrayElementAtIndex(i).stringValue))
                    {
                        extraContributes = true;
                        break;
                    }
                }
            }

            if (!primaryContributes && !extraContributes)
                return Array.Empty<string>();

            string componentId = nameProp != null ? nameProp.stringValue : partType;
            List<ShipComponentStatCategory> categories = primaryContributes
                ? ShipFamilyComponentPartKey.InferDefaultStatCategoriesForPartType(partType, componentId)
                : new List<ShipComponentStatCategory>();
            if (categories == null)
                categories = new List<ShipComponentStatCategory>();
            if (extraTypesProp != null && extraTypesProp.isArray)
            {
                for (int i = 0; i < extraTypesProp.arraySize; i++)
                    ShipFamilyComponentPartKey.UnionCategoriesForPartType(
                        categories, extraTypesProp.GetArrayElementAtIndex(i).stringValue);
            }

            var extra = new List<string>(extraTypesProp != null ? extraTypesProp.arraySize : 0);
            if (extraTypesProp != null && extraTypesProp.isArray)
            {
                for (int i = 0; i < extraTypesProp.arraySize; i++)
                    extra.Add(extraTypesProp.GetArrayElementAtIndex(i).stringValue);
            }

            return ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(categories, componentId, extra);
        }
    }
}
