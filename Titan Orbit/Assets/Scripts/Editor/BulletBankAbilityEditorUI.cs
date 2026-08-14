using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>Shared inspector UI for one <see cref="BulletBankAbility"/> serialized element.</summary>
    internal static class BulletBankAbilityEditorUI
    {
        private const float Spacing = 2f;

        private static readonly BulletBankAbilityType[] TypePopupValues;
        private static readonly string[] TypePopupLabels;

        static BulletBankAbilityEditorUI()
        {
            // --- BulletBankAbilityEditorUI ---
            // Filter by *name*, not enum equality. ElectricShockRotationLock aliases
            // ElectricShockDisable (same int 0); `t == ElectricShockRotationLock` would hide both.
            var values = new List<BulletBankAbilityType>();
            var labels = new List<string>();
            foreach (string name in Enum.GetNames(typeof(BulletBankAbilityType)))
            {
                if (string.Equals(name, nameof(BulletBankAbilityType.ElectricShockRotationLock), StringComparison.Ordinal))
                    continue;
                var field = typeof(BulletBankAbilityType).GetField(name);
                if (field != null && Attribute.IsDefined(field, typeof(ObsoleteAttribute)))
                    continue;
                var t = (BulletBankAbilityType)Enum.Parse(typeof(BulletBankAbilityType), name);
                values.Add(t);
                labels.Add(TypeDisplayName(t, name));
            }

            TypePopupValues = values.ToArray();
            TypePopupLabels = labels.ToArray();
        }

        static string TypeDisplayName(BulletBankAbilityType type, string enumName)
        {
            if (type == BulletBankAbilityType.ElectricShockDisable)
                return "Electric Shock";
            return ObjectNames.NicifyVariableName(enumName);
        }

        public static BulletBankAbilityType ReadType(SerializedProperty abilityProperty)
        {
            // --- ReadType ---
            var typeProp = abilityProperty.FindPropertyRelative("type");
            var raw = (BulletBankAbilityType)typeProp.intValue;
            if (raw == BulletBankAbilityType.ElectricShockRotationLock)
                raw = BulletBankAbilityType.ElectricShockDisable;
            return raw;
        }

        public static float GetHeight(SerializedProperty abilityProperty)
        {
            // --- Compute value ---
            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing + Spacing;
            int rows = 1 + CountVisibleFields(ReadType(abilityProperty));
            return rows * line + (rows - 1) * gap;
        }

        public static void Draw(Rect rect, SerializedProperty abilityProperty)
        {
            // --- Draw ---
            if (abilityProperty == null) return;

            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing + Spacing;
            float y = rect.y;

            var typeProp = abilityProperty.FindPropertyRelative("type");
            var magnitudeProp = abilityProperty.FindPropertyRelative("magnitude");
            var magnitudePerProp = abilityProperty.FindPropertyRelative("magnitudePerExtra");
            var durationProp = abilityProperty.FindPropertyRelative("duration");
            var durationPerProp = abilityProperty.FindPropertyRelative("durationPerExtra");
            var tickIntervalProp = abilityProperty.FindPropertyRelative("tickInterval");
            var tickIntervalPerProp = abilityProperty.FindPropertyRelative("tickIntervalPerExtra");
            var radiusProp = abilityProperty.FindPropertyRelative("radius");
            var radiusPerProp = abilityProperty.FindPropertyRelative("radiusPerExtra");
            var energyDrainProp = abilityProperty.FindPropertyRelative("energyDrain");
            var energyDrainPerProp = abilityProperty.FindPropertyRelative("energyDrainPerExtra");
            var damageTargetProp = abilityProperty.FindPropertyRelative("damageTarget");

            DrawTypePopup(new Rect(rect.x, y, rect.width, line), typeProp);
            y += line + gap;

            var type = ReadType(abilityProperty);

            switch (type)
            {
                case BulletBankAbilityType.ElectricShockDisable:
                    DrawPrimaryPerExtra(new Rect(rect.x, y, rect.width, line), durationProp, durationPerProp, "Stun Duration (sec)");
                    break;

                case BulletBankAbilityType.BurnOverTime:
                    y = DrawPrimaryPerExtraRow(rect, y, line, gap, magnitudeProp, magnitudePerProp, "Damage Per Second");
                    y = DrawPrimaryPerExtraRow(rect, y, line, gap, durationProp, durationPerProp, "Burn Duration (sec)");
                    y = DrawPrimaryPerExtraRow(rect, y, line, gap, tickIntervalProp, tickIntervalPerProp, "Tick Interval (sec)");
                    DrawPrimaryPerExtra(new Rect(rect.x, y, rect.width, line), radiusProp, radiusPerProp, "Extra Range (burn only)");
                    break;

                case BulletBankAbilityType.HealFriendly:
                    DrawPrimaryPerExtra(new Rect(rect.x, y, rect.width, line), magnitudeProp, magnitudePerProp, "Heal Per Hit");
                    break;

                case BulletBankAbilityType.ConcussivePush:
                    y = DrawPrimaryPerExtraRow(rect, y, line, gap, magnitudeProp, magnitudePerProp, "Push Force");
                    DrawPrimaryPerExtra(new Rect(rect.x, y, rect.width, line), radiusProp, radiusPerProp, "Blast Radius");
                    break;

                case BulletBankAbilityType.GravityPull:
                    y = DrawPrimaryPerExtraRow(rect, y, line, gap, radiusProp, radiusPerProp, "Pull Radius");
                    y = DrawPrimaryPerExtraRow(rect, y, line, gap, magnitudeProp, magnitudePerProp, "Pull Force");
                    DrawPrimaryPerExtra(new Rect(rect.x, y, rect.width, line), durationProp, durationPerProp, "Field Duration (sec)");
                    break;

                case BulletBankAbilityType.DamageMultiplier:
                    y = DrawLabeledRow(rect, y, line, gap, damageTargetProp, "Damage Target");
                    DrawPrimaryPerExtra(new Rect(rect.x, y, rect.width, line), magnitudeProp, magnitudePerProp, "Damage Multiplier");
                    break;

                case BulletBankAbilityType.DamageMultiplierVsAsteroid:
                case BulletBankAbilityType.DamageMultiplierVsShip:
                case BulletBankAbilityType.DamageMultiplierVsGemMoon:
                case BulletBankAbilityType.DamageMultiplierVsGem:
                    DrawPrimaryPerExtra(new Rect(rect.x, y, rect.width, line), magnitudeProp, magnitudePerProp, "Damage Multiplier");
                    break;

                case BulletBankAbilityType.StretchLengthInFlight:
                    y = DrawPrimaryPerExtraRow(rect, y, line, gap, radiusProp, radiusPerProp, "Start Length (×)");
                    DrawPrimaryPerExtra(new Rect(rect.x, y, rect.width, line), magnitudeProp, magnitudePerProp, "End Length (×)");
                    break;
            }

            float energyY = rect.y + CountVisibleFields(type) * (line + gap);
            DrawPrimaryPerExtra(
                new Rect(rect.x, energyY, rect.width, line),
                energyDrainProp,
                energyDrainPerProp,
                "Energy Drain");
        }

        private static void DrawTypePopup(Rect rect, SerializedProperty typeProp)
        {
            // --- DrawTypePopup ---
            int current = typeProp.intValue;
            int selected = EditorGUI.IntPopup(rect, "Type", current, TypePopupLabels, GetTypePopupInts());
            if (selected != current)
                typeProp.intValue = selected;
        }

        private static int[] GetTypePopupInts()
        {
            // --- Compute value ---
            var ints = new int[TypePopupValues.Length];
            for (int i = 0; i < TypePopupValues.Length; i++)
                ints[i] = (int)TypePopupValues[i];
            return ints;
        }

        private static int CountVisibleFields(BulletBankAbilityType type)
        {
            // --- CountVisibleFields ---
            return type switch
            {
                BulletBankAbilityType.ElectricShockDisable => 2,
                BulletBankAbilityType.BurnOverTime => 5,
                BulletBankAbilityType.HealFriendly => 2,
                BulletBankAbilityType.ConcussivePush => 3,
                BulletBankAbilityType.GravityPull => 4,
                BulletBankAbilityType.DamageMultiplier => 3,
                BulletBankAbilityType.DamageMultiplierVsAsteroid => 2,
                BulletBankAbilityType.DamageMultiplierVsShip => 2,
                BulletBankAbilityType.DamageMultiplierVsGemMoon => 2,
                BulletBankAbilityType.DamageMultiplierVsGem => 2,
                BulletBankAbilityType.StretchLengthInFlight => 3,
                _ => 2,
            };
        }

        private static float DrawLabeledRow(Rect block, float y, float line, float gap, SerializedProperty prop, string label)
        {
            DrawLabeled(new Rect(block.x, y, block.width, line), prop, label);
            return y + line + gap;
        }

        private static float DrawPrimaryPerExtraRow(
            Rect block, float y, float line, float gap,
            SerializedProperty primary, SerializedProperty perExtra, string label)
        {
            DrawPrimaryPerExtra(new Rect(block.x, y, block.width, line), primary, perExtra, label);
            return y + line + gap;
        }

        private static void DrawLabeled(Rect rect, SerializedProperty prop, string label)
        {
            // --- DrawLabeled ---
            float labelW = rect.width * 0.44f;
            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelW, rect.height), label);
            EditorGUI.PropertyField(new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height), prop, GUIContent.none);
        }

        private static void DrawPrimaryPerExtra(
            Rect rect, SerializedProperty primary, SerializedProperty perExtra, string label)
        {
            float labelW = Mathf.Min(160f, rect.width * 0.34f);
            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelW, rect.height), label);

            float restX = rect.x + labelW;
            float restW = rect.width - labelW;
            float primaryW = restW * 0.38f;
            EditorGUI.PropertyField(new Rect(restX, rect.y, primaryW, rect.height), primary, GUIContent.none);

            float perLabelW = 64f;
            float perX = restX + primaryW + 6f;
            EditorGUI.LabelField(new Rect(perX, rect.y, perLabelW, rect.height), "Per Extra");
            float perFieldX = perX + perLabelW;
            EditorGUI.PropertyField(
                new Rect(perFieldX, rect.y, rect.xMax - perFieldX, rect.height),
                perExtra,
                GUIContent.none);
        }
    }
}
