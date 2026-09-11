using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Custom Inspector row for one Part Profile on <see cref="ShipFamilyPartCalcProfileSet"/>.
    /// Shows only ability fields allowed by <c>defaultCategories</c> under Base At Version 1 and
    /// Per Version Increment (same field allowlist as ShipFamilyDefinition component rows).
    /// Authored 0 on *PerLevel is kept (use the ProfileSet fill button for empty-field defaults).
    /// </summary>
    [CustomPropertyDrawer(typeof(ShipFamilyPartCalcProfile))]
    public sealed class ShipFamilyPartCalcProfileDrawer : PropertyDrawer
    {
        const float LabelWidthRatio = 0.52f;

        /// <summary>Computes drawer height from expanded state + category-filtered field counts.</summary>
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing;

            if (!property.isExpanded)
                return line;

            float height = line + gap;

            // partType + defaultCategories + perLevelFractionOverride
            height += line + gap;
            SerializedProperty categoriesProp = property.FindPropertyRelative("defaultCategories");
            height += EditorGUI.GetPropertyHeight(categoriesProp, true) + gap;
            height += line + gap;

            string partType = property.FindPropertyRelative("partType")?.stringValue ?? ShipFamilyPartTypes.Hull;
            var categories = ReadCategories(categoriesProp);
            if (categories.Count == 0)
                categories = ShipFamilyComponentPartKey.InferDefaultStatCategories(partType);

            string[] fields = ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(categories, partType);
            // Two blocks (Base At Version 1, Per Version Increment): header + fields each.
            height += GetStatsBlockHeight(fields, line, gap) * 2f;
            height += gap;
            return height;
        }

        /// <summary>Draws partType, categories, fraction override, then filtered base / increment stats.</summary>
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing;
            float y = position.y;
            float width = position.width;

            // --- Foldout header (shows partType in the label) ---
            SerializedProperty partTypeProp = property.FindPropertyRelative("partType");
            string partType = partTypeProp != null ? partTypeProp.stringValue : "Part";
            var header = new GUIContent(string.IsNullOrEmpty(label.text) ? partType : $"{label.text}: {partType}");
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

            // --- Identity / categories ---
            EditorGUI.PropertyField(new Rect(position.x, y, width, line), partTypeProp);
            y += line + gap;

            SerializedProperty categoriesProp = property.FindPropertyRelative("defaultCategories");
            float catHeight = EditorGUI.GetPropertyHeight(categoriesProp, true);
            EditorGUI.PropertyField(new Rect(position.x, y, width, catHeight), categoriesProp, true);
            y += catHeight + gap;

            SerializedProperty fracProp = property.FindPropertyRelative("perLevelFractionOverride");
            EditorGUI.PropertyField(new Rect(position.x, y, width, line), fracProp);
            y += line + gap;

            // --- Resolve allowlist from categories (or inferred defaults) ---
            partType = partTypeProp.stringValue;
            var categories = ReadCategories(categoriesProp);
            if (categories.Count == 0)
            {
                categories = ShipFamilyComponentPartKey.InferDefaultStatCategories(partType);
                EditorGUI.HelpBox(
                    new Rect(position.x, y, width, line * 2f),
                    "No Default Categories — showing inferred fields for this part type. Add categories to filter.",
                    MessageType.Info);
                y += line * 2f + gap;
            }

            string[] fields = ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(categories, partType);

            // --- Zero fields outside the allowlist so hidden categories stay cleared ---
            SerializedProperty baseProp = property.FindPropertyRelative("baseAtVersion1");
            SerializedProperty incrProp = property.FindPropertyRelative("perVersionIncrement");
            FilterStatsToAllowed(baseProp, fields);
            FilterStatsToAllowed(incrProp, fields);

            y = DrawStatsBlock(
                new Rect(position.x, y, width, 0f),
                "Base At Version 1",
                baseProp,
                fields,
                line,
                gap);
            y = DrawStatsBlock(
                new Rect(position.x, y, width, 0f),
                "Per Version Increment",
                incrProp,
                fields,
                line,
                gap);

            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        /// <summary>Draws a labeled block of category-allowed float fields.</summary>
        static float DrawStatsBlock(
            Rect rect,
            string title,
            SerializedProperty statsProp,
            string[] fields,
            float line,
            float gap)
        {
            float y = rect.y;
            float width = rect.width;

            EditorGUI.LabelField(new Rect(rect.x, y, width, line), title, EditorStyles.boldLabel);
            y += line + gap;

            if (statsProp == null || fields == null)
                return y;

            for (int i = 0; i < fields.Length; i++)
            {
                SerializedProperty field = statsProp.FindPropertyRelative(fields[i]);
                if (field == null)
                    continue;

                float labelWidth = width * LabelWidthRatio;
                var labelRect = new Rect(rect.x, y, labelWidth, line);
                var fieldRect = new Rect(rect.x + labelWidth, y, width - labelWidth, line);
                EditorGUI.LabelField(labelRect, field.displayName);
                EditorGUI.BeginChangeCheck();
                float value = EditorGUI.FloatField(fieldRect, field.floatValue);
                if (EditorGUI.EndChangeCheck())
                    field.floatValue = value;
                y += line + gap;
            }

            y += gap;
            return y;
        }

        static float GetStatsBlockHeight(string[] fields, float line, float gap)
        {
            float height = line + gap; // title
            int count = fields != null ? fields.Length : 0;
            height += (line + gap) * count;
            height += gap;
            return height;
        }

        static List<ShipComponentStatCategory> ReadCategories(SerializedProperty categoriesProp)
        {
            var categories = new List<ShipComponentStatCategory>();
            if (categoriesProp == null || !categoriesProp.isArray)
                return categories;

            for (int i = 0; i < categoriesProp.arraySize; i++)
            {
                SerializedProperty item = categoriesProp.GetArrayElementAtIndex(i);
                categories.Add((ShipComponentStatCategory)item.enumValueIndex);
            }

            return categories;
        }

        /// <summary>Zeros float fields not in the category allowlist (hidden categories stay cleared).</summary>
        static void FilterStatsToAllowed(SerializedProperty statsProp, string[] allowedFields)
        {
            if (statsProp == null || allowedFields == null)
                return;

            var allowed = new HashSet<string>(allowedFields, StringComparer.Ordinal);
            SerializedProperty child = statsProp.Copy();
            SerializedProperty end = child.GetEndProperty();
            if (!child.NextVisible(true))
                return;

            while (!SerializedProperty.EqualContents(child, end))
            {
                if (child.propertyType == SerializedPropertyType.Float
                    && !allowed.Contains(child.name))
                {
                    child.floatValue = 0f;
                }

                if (!child.NextVisible(false))
                    break;
            }
        }
    }
}
