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
            SerializedProperty statCategoriesProp = element.FindPropertyRelative("statCategories");
            SerializedProperty statsProp = element.FindPropertyRelative("stats");
            SerializedProperty bulletPrefabIndexProp = element.FindPropertyRelative("bulletPrefabIndex");
            SerializedProperty enableVfxProp = element.FindPropertyRelative("enablePropulsionVfx");
            SerializedProperty vfxScaleProp = element.FindPropertyRelative("propulsionVfxScale");
            SerializedProperty menuPreviewSpriteProp = element.FindPropertyRelative("menuPreviewSprite");
            SerializedProperty theatricalMenuPreviewSpriteProp = element.FindPropertyRelative("theatricalMenuPreviewSprite");
            SerializedProperty teamMenuPreviewSpritesProp = element.FindPropertyRelative("teamMenuPreviewSprites");
            SerializedProperty teamTheatricalMenuPreviewSpritesProp = element.FindPropertyRelative("teamTheatricalMenuPreviewSprites");
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

            float statCategoriesHeight = EditorGUI.GetPropertyHeight(statCategoriesProp, true);
            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(
                new Rect(position.x, y, width, statCategoriesHeight),
                statCategoriesProp,
                true);
            y += statCategoriesHeight + gap;
            componentId = componentIdProp.stringValue ?? string.Empty;
            var statCategories = ReadStatCategories(statCategoriesProp);
            if (EditorGUI.EndChangeCheck())
            {
                FilterStatsProperty(statsProp, statCategories, componentId);
            }

            EditorGUI.LabelField(new Rect(position.x, y, width, line), "Stats", EditorStyles.boldLabel);
            y += line + gap;

            y = DrawStatsByCategory(new Rect(position.x, y, width, line), statsProp, statCategories, componentId, line, gap);

            // [TITAN-ORBIT] Stack weight is meta (not category-gated) — always editable on every part row.
            y = DrawExtraStackWeight(new Rect(position.x, y, width, line), statsProp, componentId, line, gap);

            if (ShipFamilyComponentPartKey.ShouldShowBulletPrefabIndex(statCategories, componentId)
                && bulletPrefabIndexProp != null)
                y = DrawStandardProperty(new Rect(position.x, y, width, line), bulletPrefabIndexProp, gap);

            if (enableVfxProp != null)
                y = DrawStandardProperty(new Rect(position.x, y, width, line), enableVfxProp, gap);
            if (vfxScaleProp != null)
                y = DrawStandardProperty(new Rect(position.x, y, width, line), vfxScaleProp, gap);

            float menuPreviewHeight = EditorGUI.GetPropertyHeight(menuPreviewSpriteProp, true);
            y = DrawPropertyBlock(new Rect(position.x, y, width, menuPreviewHeight), menuPreviewSpriteProp, gap);
            if (theatricalMenuPreviewSpriteProp != null)
            {
                float th = EditorGUI.GetPropertyHeight(theatricalMenuPreviewSpriteProp, true);
                y = DrawPropertyBlock(new Rect(position.x, y, width, th), theatricalMenuPreviewSpriteProp, gap);
            }

            float teamPreviewHeight = EditorGUI.GetPropertyHeight(teamMenuPreviewSpritesProp, true);
            y = DrawPropertyBlock(new Rect(position.x, y, width, teamPreviewHeight), teamMenuPreviewSpritesProp, gap);
            if (teamTheatricalMenuPreviewSpritesProp != null)
            {
                float tth = EditorGUI.GetPropertyHeight(teamTheatricalMenuPreviewSpritesProp, true);
                DrawPropertyBlock(new Rect(position.x, y, width, tth), teamTheatricalMenuPreviewSpritesProp, gap);
            }

            EditorGUI.EndProperty();
        }

        private static float DrawPropertyBlock(Rect rect, SerializedProperty prop, float gap)
        {
            EditorGUI.PropertyField(rect, prop, true);
            return rect.y + rect.height + gap;
        }

        private static float GetExpandedBodyHeight(SerializedProperty element, float line, float gap)
        {
            float height = 0f;

            height += line + gap; // componentId
            height += line + gap; // displayName

            SerializedProperty statCategoriesProp = element.FindPropertyRelative("statCategories");
            height += EditorGUI.GetPropertyHeight(statCategoriesProp, true) + gap;
            height += line + gap; // Stats label

            string componentId = element.FindPropertyRelative("componentId").stringValue ?? string.Empty;
            var statCategories = ReadStatCategories(statCategoriesProp);
            height += GetStatsByCategoryHeight(statCategories, componentId, line, gap);
            height += line + (line + gap); // Stack header + Extra Stack Weight

            if (ShipFamilyComponentPartKey.ShouldShowBulletPrefabIndex(statCategories, componentId))
                height += line + gap; // bulletPrefabIndex

            height += line + gap; // enablePropulsionVfx
            height += line + gap; // propulsionVfxScale

            height += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("menuPreviewSprite"), true) + gap;
            var thProp = element.FindPropertyRelative("theatricalMenuPreviewSprite");
            if (thProp != null)
                height += EditorGUI.GetPropertyHeight(thProp, true) + gap;
            height += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("teamMenuPreviewSprites"), true) + gap;
            var tthProp = element.FindPropertyRelative("teamTheatricalMenuPreviewSprites");
            if (tthProp != null)
                height += EditorGUI.GetPropertyHeight(tthProp, true) + gap;

            return height;
        }

        private static float DrawStatsByCategory(
            Rect rect,
            SerializedProperty statsProp,
            List<ShipComponentStatCategory> statCategories,
            string componentId,
            float line,
            float gap)
        {
            float y = rect.y;
            float width = rect.width;

            if (statCategories == null || statCategories.Count == 0)
                return y;

            for (int c = 0; c < statCategories.Count; c++)
            {
                ShipComponentStatCategory category = statCategories[c];
                EditorGUI.LabelField(new Rect(rect.x, y, width, line), category.ToString(), EditorStyles.miniBoldLabel);
                y += line;

                string[] fields = ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(category, componentId);
                for (int i = 0; i < fields.Length; i++)
                {
                    SerializedProperty field = statsProp.FindPropertyRelative(fields[i]);
                    if (field == null)
                        continue;
                    y = DrawFloatProperty(new Rect(rect.x, y, width, line), field, gap);
                }

                y += gap;
            }

            return y;
        }

        /// <summary>
        /// Draws <c>extraStackWeight</c> under Stats. Primary in a pool contributes 100%;
        /// each extra uses this fraction of its own stats (engines/thrusters default 0.1).
        /// </summary>
        private static float DrawExtraStackWeight(
            Rect rect,
            SerializedProperty statsProp,
            string componentId,
            float line,
            float gap)
        {
            if (statsProp == null)
                return rect.y;

            SerializedProperty weightProp = statsProp.FindPropertyRelative("extraStackWeight");
            if (weightProp == null)
                return rect.y;

            float y = rect.y;
            float width = rect.width;

            EditorGUI.LabelField(new Rect(rect.x, y, width, line), "Stack", EditorStyles.miniBoldLabel);
            y += line;

            // Seed a visible suggested value when still unset so designers see 0.1 / 1.0 in the field.
            if (weightProp.floatValue <= 0.0001f)
            {
                weightProp.floatValue =
                    ShipComponentStackAggregation.GetSuggestedExtraStackWeight(componentId);
            }

            float labelWidth = width * LabelWidthRatio;
            var labelRect = new Rect(rect.x, y, labelWidth, line);
            var fieldRect = new Rect(rect.x + labelWidth, y, width - labelWidth, line);
            EditorGUI.LabelField(
                labelRect,
                new GUIContent(
                    "Extra Stack Weight",
                    "When multiple parts share a pool: primary = 100%; each extra adds this fraction of ITS stats. " +
                    "1 = full sum; Engines/Thrusters = 0.1."));
            EditorGUI.BeginChangeCheck();
            float value = EditorGUI.FloatField(fieldRect, weightProp.floatValue);
            if (EditorGUI.EndChangeCheck())
                weightProp.floatValue = Mathf.Max(0f, value);

            return y + line + gap;
        }

        private static float GetStatsByCategoryHeight(
            List<ShipComponentStatCategory> statCategories,
            string componentId,
            float line,
            float gap)
        {
            if (statCategories == null || statCategories.Count == 0)
                return 0f;

            float height = 0f;
            for (int c = 0; c < statCategories.Count; c++)
            {
                height += line; // category header
                string[] fields = ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(statCategories[c], componentId);
                height += (line + gap) * fields.Length;
                height += gap;
            }

            return height;
        }

        private static List<ShipComponentStatCategory> ReadStatCategories(SerializedProperty statCategoriesProp)
        {
            var categories = new List<ShipComponentStatCategory>();
            if (statCategoriesProp == null || !statCategoriesProp.isArray)
                return categories;

            for (int i = 0; i < statCategoriesProp.arraySize; i++)
            {
                SerializedProperty item = statCategoriesProp.GetArrayElementAtIndex(i);
                categories.Add((ShipComponentStatCategory)item.enumValueIndex);
            }

            return categories;
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

        private static void FilterStatsProperty(
            SerializedProperty statsProp,
            List<ShipComponentStatCategory> categories,
            string componentId)
        {
            if (statsProp == null)
                return;

            var allowed = new HashSet<string>(
                ShipFamilyComponentPartKey.GetAuthoringStatFieldNames(categories, componentId),
                StringComparer.Ordinal);

            SerializedProperty child = statsProp.Copy();
            SerializedProperty end = child.GetEndProperty();
            child.NextVisible(true);
            while (!SerializedProperty.EqualContents(child, end))
            {
                // [TITAN-ORBIT] Never clear stack weight — it is not category-gated.
                if (child.propertyType == SerializedPropertyType.Float
                    && !allowed.Contains(child.name)
                    && !string.Equals(child.name, "extraStackWeight", StringComparison.Ordinal))
                {
                    child.floatValue = 0f;
                }

                if (!child.NextVisible(false))
                    break;
            }
        }
    }
}
