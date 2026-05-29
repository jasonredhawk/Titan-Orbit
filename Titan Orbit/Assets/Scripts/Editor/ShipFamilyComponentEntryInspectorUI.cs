using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Shared layout/draw logic for <see cref="ShipFamilyComponentEntry"/> list rows.
    /// Used by <see cref="ShipFamilyDefinitionEditor"/> ReorderableList (not a PropertyDrawer).
    /// </summary>
    public static class ShipFamilyComponentEntryInspectorUI
    {
        private const float LabelWidthRatio = 0.52f;
        private const float BottomPadding = 4f;

        public static float GetHeight(SerializedProperty element)
        {
            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing;
            float height = line;

            if (!element.isExpanded)
                return height + BottomPadding;

            height += gap + GetExpandedBodyHeight(element, line, gap);
            return height + BottomPadding;
        }

        public static void Draw(Rect position, SerializedProperty element, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, element);

            float y = position.y;
            float width = position.width;
            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing;

            SerializedProperty componentIdProp = element.FindPropertyRelative("componentId");
            SerializedProperty displayNameProp = element.FindPropertyRelative("displayName");
            SerializedProperty statCategoryProp = element.FindPropertyRelative("statCategory");
            SerializedProperty statsProp = element.FindPropertyRelative("stats");
            SerializedProperty bulletPrefabIndexProp = element.FindPropertyRelative("bulletPrefabIndex");
            string componentId = componentIdProp.stringValue ?? string.Empty;

            string header = string.IsNullOrWhiteSpace(componentId)
                ? label.text
                : componentId;

            element.isExpanded = EditorGUI.Foldout(
                new Rect(position.x, y, width, line),
                element.isExpanded,
                header,
                true);
            y += line;

            if (!element.isExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            y += gap;

            y = DrawStandardProperty(new Rect(position.x, y, width, line), componentIdProp, gap);
            y = DrawStandardProperty(new Rect(position.x, y, width, line), displayNameProp, gap);

            EditorGUI.BeginChangeCheck();
            y = DrawStandardProperty(new Rect(position.x, y, width, line), statCategoryProp, gap);
            componentId = componentIdProp.stringValue ?? string.Empty;
            var statCategory = (ShipComponentStatCategory)statCategoryProp.enumValueIndex;
            if (EditorGUI.EndChangeCheck())
            {
                FilterStatsProperty(statsProp, statCategory, componentId);
            }

            EditorGUI.LabelField(new Rect(position.x, y, width, line), "Stats", EditorStyles.boldLabel);
            y += line + gap;

            string[] fields = ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(statCategory, componentId);
            for (int i = 0; i < fields.Length; i++)
            {
                SerializedProperty field = statsProp.FindPropertyRelative(fields[i]);
                if (field == null)
                    continue;
                y = DrawFloatProperty(new Rect(position.x, y, width, line), field, gap);
            }

            if (ShipFamilyComponentPartKey.ShouldShowBulletPrefabIndex(statCategory, componentId))
                DrawStandardProperty(new Rect(position.x, y, width, line), bulletPrefabIndexProp, gap);

            EditorGUI.EndProperty();
        }

        private static float GetExpandedBodyHeight(SerializedProperty element, float line, float gap)
        {
            float height = 0f;

            height += line + gap; // componentId
            height += line + gap; // displayName
            height += line + gap; // statCategory
            height += line + gap; // Stats label

            string componentId = element.FindPropertyRelative("componentId").stringValue ?? string.Empty;
            var statCategory = (ShipComponentStatCategory)element.FindPropertyRelative("statCategory").enumValueIndex;
            string[] fields = ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(statCategory, componentId);
            height += (line + gap) * fields.Length;

            if (ShipFamilyComponentPartKey.ShouldShowBulletPrefabIndex(statCategory, componentId))
                height += line + gap; // bulletPrefabIndex

            return height;
        }

        private static float DrawStandardProperty(Rect rect, SerializedProperty prop, float gap)
        {
            EditorGUI.PropertyField(rect, prop, false);
            return rect.y + rect.height + gap;
        }

        private static float DrawFloatProperty(Rect rect, SerializedProperty prop, float gap)
        {
            float labelWidth = rect.width * LabelWidthRatio;
            var labelRect = new Rect(rect.x, rect.y, labelWidth, rect.height);
            var fieldRect = new Rect(rect.x + labelWidth, rect.y, rect.width - labelWidth, rect.height);
            EditorGUI.LabelField(labelRect, prop.displayName);
            EditorGUI.BeginChangeCheck();
            float value = EditorGUI.FloatField(fieldRect, value: prop.floatValue);
            if (EditorGUI.EndChangeCheck())
                prop.floatValue = value;
            return rect.y + rect.height + gap;
        }

        private static void FilterStatsProperty(SerializedProperty statsProp, ShipComponentStatCategory category, string componentId)
        {
            if (statsProp == null)
                return;

            var allowed = new HashSet<string>(
                ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(category, componentId),
                StringComparer.Ordinal);

            SerializedProperty child = statsProp.Copy();
            SerializedProperty end = child.GetEndProperty();
            child.NextVisible(true);
            while (!SerializedProperty.EqualContents(child, end))
            {
                if (child.propertyType == SerializedPropertyType.Float && !allowed.Contains(child.name))
                    child.floatValue = 0f;
                if (!child.NextVisible(false))
                    break;
            }
        }
    }
}
