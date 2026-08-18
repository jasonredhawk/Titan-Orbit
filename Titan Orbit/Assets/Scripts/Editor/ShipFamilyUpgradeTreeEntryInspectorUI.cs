using UnityEditor;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Shared layout/draw logic for <see cref="ShipFamilyChassisTierEntry"/> list rows.
    /// Used by <see cref="ShipFamilyDefinitionEditor"/> ReorderableList (not a PropertyDrawer).
    /// </summary>
    public static class ShipFamilyUpgradeTreeEntryInspectorUI
    {
        private const float LabelWidthRatio = 0.38f;
        private const float ValueFieldWidthRatio = 0.28f;
        private const float BottomPadding = 4f;
        private const int StatRowCount = 12;
        private static readonly Color MissingStatRowBackground = new Color(1f, 0.22f, 0.22f, 0.18f);
        private static readonly Color MissingStatText = new Color(1f, 0.42f, 0.42f, 1f);

        private static readonly string[] StatLabels =
        {
            "Fire Power", "Bullet Speed", "Fire Rate", "Ram Power",
            "Health Cap", "Health Reg",
            "Energy Cap", "Energy Regen",
            "Move Speed", "Turn Speed",
            "Gem Cap", "Troop Cap"
        };

        public static float GetHeight(SerializedProperty element, bool isExpanded)
        {
            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing;
            float height = line;

            if (!isExpanded)
                return height + BottomPadding;

            height += gap + GetExpandedBodyHeight(element, line, gap);
            return height + BottomPadding;
        }

        public static void Draw(
            Rect position,
            SerializedProperty element,
            GUIContent label,
            ShipFamilyDefinition familyDefinition)
        {
            EditorGUI.BeginProperty(position, label, element);

            float y = position.y;
            float width = position.width;
            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing;

            SerializedProperty chassisIdProp = element.FindPropertyRelative("chassisId");
            string chassisId = chassisIdProp.stringValue ?? string.Empty;
            string header = string.IsNullOrWhiteSpace(chassisId) ? label.text : chassisId;

            SerializedProperty prefabProp = element.FindPropertyRelative("prefab");
            GameObject prefab = prefabProp.objectReferenceValue as GameObject;
            int minHomePlanetLevel = GetMinHomePlanetLevel(element);
            bool hasPreview = TryGetStatPreview(familyDefinition, prefab, minHomePlanetLevel, out ShipFamilyUpgradeTreeStatScanner.UpgradeTreeStatPreview preview);
            bool hasMissingStats = hasPreview && HasMissingStats(preview);
            if (hasMissingStats)
                header += "  ΓÇö missing components";

            bool expanded = element.isExpanded;
            Color previousContentColor = GUI.contentColor;
            if (hasMissingStats)
                GUI.contentColor = MissingStatText;
            expanded = EditorGUI.Foldout(new Rect(position.x, y, width, line), expanded, header, true);
            GUI.contentColor = previousContentColor;
            element.isExpanded = expanded;
            y += line;

            if (!expanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            y += gap;
            y = DrawStandardProperty(new Rect(position.x, y, width, line), element.FindPropertyRelative("chassisId"), gap);
            y = DrawStandardProperty(new Rect(position.x, y, width, line), element.FindPropertyRelative("upgradeTreeShipName"), gap);
            y = DrawStandardProperty(new Rect(position.x, y, width, line), element.FindPropertyRelative("prefab"), gap);

            float menuPreviewHeight = EditorGUI.GetPropertyHeight(element.FindPropertyRelative("menuPreviewSprite"), true);
            y = DrawPropertyBlock(new Rect(position.x, y, width, menuPreviewHeight), element.FindPropertyRelative("menuPreviewSprite"), gap);

            float teamPreviewHeight = EditorGUI.GetPropertyHeight(element.FindPropertyRelative("teamMenuPreviewSprites"), true);
            y = DrawPropertyBlock(new Rect(position.x, y, width, teamPreviewHeight), element.FindPropertyRelative("teamMenuPreviewSprites"), gap);

            y = DrawStandardProperty(new Rect(position.x, y, width, line), element.FindPropertyRelative("minHomePlanetLevel"), gap);
            y = DrawStandardProperty(new Rect(position.x, y, width, line), element.FindPropertyRelative("lockedInUpgradeTree"), gap);

            y += gap;
            y = DrawPowerScoreBreakdown(new Rect(position.x, y, width, line), element, familyDefinition, line, gap);

            EditorGUI.EndProperty();
        }

        private static float GetExpandedBodyHeight(SerializedProperty element, float line, float gap)
        {
            float height = (line + gap) * 3; // chassisId, upgradeTreeShipName, prefab
            height += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("menuPreviewSprite"), true) + gap;
            height += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("teamMenuPreviewSprites"), true) + gap;
            height += (line + gap) * 2; // minHomePlanetLevel, lockedInUpgradeTree
            height += gap; // before breakdown
            height += line + gap; // breakdown title
            height += line + gap; // min/max headers
            height += gap;
            height += (line + gap); // power score total row
            height += (line + gap) * StatRowCount;
            if (ElementMayShowMissingStatsHelp(element, out bool hasMissing))
                height += hasMissing ? line + gap : 0f;
            return height;
        }

        private static bool ElementMayShowMissingStatsHelp(SerializedProperty element, out bool hasMissingStats)
        {
            hasMissingStats = false;
            SerializedProperty prefabProp = element.FindPropertyRelative("prefab");
            GameObject prefab = prefabProp.objectReferenceValue as GameObject;
            if (prefab == null)
                return false;

            ShipFamilyDefinition def = GetFamilyDefinitionFromElement(element);
            int minHomePlanetLevel = GetMinHomePlanetLevel(element);
            if (!TryGetStatPreview(def, prefab, minHomePlanetLevel, out ShipFamilyUpgradeTreeStatScanner.UpgradeTreeStatPreview preview))
                return false;

            hasMissingStats = HasMissingStats(preview);
            return hasMissingStats;
        }

        private static ShipFamilyDefinition GetFamilyDefinitionFromElement(SerializedProperty element)
        {
            SerializedObject owner = element.serializedObject;
            return owner?.targetObject as ShipFamilyDefinition;
        }

        private static float DrawPowerScoreBreakdown(
            Rect rect,
            SerializedProperty element,
            ShipFamilyDefinition familyDefinition,
            float line,
            float gap)
        {
            float y = rect.y;
            float width = rect.width;

            EditorGUI.LabelField(new Rect(rect.x, y, width, line), "Power Score Breakdown", EditorStyles.boldLabel);
            y += line + gap;

            float labelWidth = width * LabelWidthRatio;
            float valueWidth = width * ValueFieldWidthRatio;
            float valueGap = width - labelWidth - valueWidth * 2f;

            EditorGUI.LabelField(new Rect(rect.x + labelWidth, y, valueWidth, line), "Min", EditorStyles.miniLabel);
            EditorGUI.LabelField(new Rect(rect.x + labelWidth + valueWidth + valueGap, y, valueWidth, line), "Max", EditorStyles.miniLabel);
            y += line + gap;

            SerializedProperty prefabProp = element.FindPropertyRelative("prefab");
            GameObject prefab = prefabProp.objectReferenceValue as GameObject;
            SerializedProperty powerScoreProp = element.FindPropertyRelative("powerScore");
            int minHomePlanetLevel = GetMinHomePlanetLevel(element);

            bool hasPreview = TryGetStatPreview(familyDefinition, prefab, minHomePlanetLevel, out ShipFamilyUpgradeTreeStatScanner.UpgradeTreeStatPreview preview);
            bool hasMissingStats = hasPreview && HasMissingStats(preview);

            if (hasPreview)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    y = DrawStatRow(
                        new Rect(rect.x, y, width, line),
                        "Power Score Total",
                        preview.powerScoreTotal,
                        labelWidth,
                        valueWidth,
                        valueGap,
                        gap,
                        EditorStyles.boldLabel);
                }
            }
            else
            {
                float storedMin = powerScoreProp != null ? powerScoreProp.floatValue : 0f;
                using (new EditorGUI.DisabledScope(true))
                {
                    y = DrawStatRow(
                        new Rect(rect.x, y, width, line),
                        "Power Score Total",
                        storedMin,
                        storedMin,
                        labelWidth,
                        valueWidth,
                        valueGap,
                        gap,
                        EditorStyles.boldLabel);
                }
            }

            if (!hasPreview)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    for (int i = 0; i < StatRowCount; i++)
                    {
                        y = DrawStatRow(new Rect(rect.x, y, width, line), StatLabels[i], 0f, 0f, labelWidth, valueWidth, valueGap, gap);
                    }
                }

                if (prefab == null)
                {
                    EditorGUI.LabelField(
                        new Rect(rect.x, y, width, line),
                        "Assign a prefab to preview stats.",
                        EditorStyles.miniLabel);
                    y += line + gap;
                }

                return y;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                ShipFamilyUpgradeTreeStatScanner.StatMinMax[] ranges = GetStatRanges(preview);
                for (int i = 0; i < StatRowCount; i++)
                {
                    y = DrawStatRow(
                        new Rect(rect.x, y, width, line),
                        StatLabels[i],
                        ranges[i],
                        labelWidth,
                        valueWidth,
                        valueGap,
                        gap);
                }
            }

            if (hasMissingStats)
            {
                Color previousHelpColor = GUI.contentColor;
                GUI.contentColor = MissingStatText;
                EditorGUI.LabelField(
                    new Rect(rect.x, y, width, line),
                    "Red rows: base stat is 0 ΓÇö prefab is likely missing parts (e.g. tail/fin for Turn Speed).",
                    EditorStyles.miniLabel);
                GUI.contentColor = previousHelpColor;
                y += line + gap;
            }

            return y;
        }

        private static int GetMinHomePlanetLevel(SerializedProperty element)
        {
            SerializedProperty prop = element.FindPropertyRelative("minHomePlanetLevel");
            return prop != null ? prop.intValue : 1;
        }

        private static bool TryGetStatPreview(
            ShipFamilyDefinition familyDefinition,
            GameObject prefab,
            int minHomePlanetLevel,
            out ShipFamilyUpgradeTreeStatScanner.UpgradeTreeStatPreview preview)
        {
            preview = default;
            return familyDefinition != null
                && prefab != null
                && !string.IsNullOrWhiteSpace(familyDefinition.familyId)
                && ShipFamilyUpgradeTreeStatScanner.TryGetUpgradeTreeStatPreview(
                    prefab,
                    familyDefinition,
                    familyDefinition.familyId,
                    minHomePlanetLevel,
                    out preview);
        }

        private static bool HasMissingStats(ShipFamilyUpgradeTreeStatScanner.UpgradeTreeStatPreview preview)
        {
            ShipFamilyUpgradeTreeStatScanner.StatMinMax[] ranges = GetStatRanges(preview);
            for (int i = 0; i < ranges.Length; i++)
            {
                if (IsMissingStat(ranges[i].min))
                    return true;
            }

            return false;
        }

        private static bool IsMissingStat(float baseValue)
        {
            return baseValue <= 0f;
        }

        private static ShipFamilyUpgradeTreeStatScanner.StatMinMax[] GetStatRanges(
            ShipFamilyUpgradeTreeStatScanner.UpgradeTreeStatPreview preview)
        {
            return new[]
            {
                preview.firePower,
                preview.bulletSpeed,
                preview.fireRate,
                preview.ramPower,
                preview.healthCap,
                preview.healthRegen,
                preview.energyCap,
                preview.energyRegen,
                preview.moveSpeed,
                preview.turnSpeed,
                preview.gemCap,
                preview.peopleCap
            };
        }

        private static float DrawStatRow(
            Rect rect,
            string label,
            ShipFamilyUpgradeTreeStatScanner.StatMinMax range,
            float labelWidth,
            float valueWidth,
            float valueGap,
            float gap,
            GUIStyle labelStyle = null)
        {
            return DrawStatRow(rect, label, range.min, range.max, labelWidth, valueWidth, valueGap, gap, labelStyle);
        }

        private static float DrawStatRow(
            Rect rect,
            string label,
            ShipFamilyUpgradeTreeStatScanner.StatMinMax range,
            float labelWidth,
            float valueWidth,
            float valueGap,
            float gap)
        {
            return DrawStatRow(rect, label, range.min, range.max, labelWidth, valueWidth, valueGap, gap, null);
        }

        private static float DrawStatRow(
            Rect rect,
            string label,
            float minValue,
            float maxValue,
            float labelWidth,
            float valueWidth,
            float valueGap,
            float gap,
            GUIStyle labelStyle)
        {
            bool missing = IsMissingStat(minValue);
            if (missing)
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, rect.height), MissingStatRowBackground);

            Color previousContentColor = GUI.contentColor;
            if (missing)
                GUI.contentColor = MissingStatText;

            var style = labelStyle ?? EditorStyles.label;
            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelWidth, rect.height), label, style);
            EditorGUI.FloatField(new Rect(rect.x + labelWidth, rect.y, valueWidth, rect.height), minValue);
            EditorGUI.FloatField(new Rect(rect.x + labelWidth + valueWidth + valueGap, rect.y, valueWidth, rect.height), maxValue);

            GUI.contentColor = previousContentColor;
            return rect.y + rect.height + gap;
        }

        private static float DrawStatRow(
            Rect rect,
            string label,
            float minValue,
            float maxValue,
            float labelWidth,
            float valueWidth,
            float valueGap,
            float gap)
        {
            return DrawStatRow(rect, label, minValue, maxValue, labelWidth, valueWidth, valueGap, gap, null);
        }

        private static float DrawStandardProperty(Rect rect, SerializedProperty prop, float gap)
        {
            EditorGUI.PropertyField(rect, prop, true);
            return rect.y + rect.height + gap;
        }

        private static float DrawPropertyBlock(Rect rect, SerializedProperty prop, float gap)
        {
            EditorGUI.PropertyField(rect, prop, true);
            return rect.y + rect.height + gap;
        }
    }
}
