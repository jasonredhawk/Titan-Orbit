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
            var values = new List<BulletBankAbilityType>();
            var labels = new List<string>();
            foreach (BulletBankAbilityType t in Enum.GetValues(typeof(BulletBankAbilityType)))
            {
                if (t == BulletBankAbilityType.ElectricShockRotationLock)
                    continue;
                var name = t.ToString();
                var field = typeof(BulletBankAbilityType).GetField(name);
                if (field != null && Attribute.IsDefined(field, typeof(ObsoleteAttribute)))
                    continue;
                values.Add(t);
                labels.Add(ObjectNames.NicifyVariableName(name));
            }

            TypePopupValues = values.ToArray();
            TypePopupLabels = labels.ToArray();
        }

        public static BulletBankAbilityType ReadType(SerializedProperty abilityProperty)
        {
            var typeProp = abilityProperty.FindPropertyRelative("type");
            var raw = (BulletBankAbilityType)typeProp.intValue;
            if (raw == BulletBankAbilityType.ElectricShockRotationLock)
                raw = BulletBankAbilityType.ElectricShockDisable;
            return raw;
        }

        public static float GetHeight(SerializedProperty abilityProperty)
        {
            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing + Spacing;
            int rows = 1 + CountVisibleFields(ReadType(abilityProperty));
            return rows * line + (rows - 1) * gap;
        }

        public static void Draw(Rect rect, SerializedProperty abilityProperty)
        {
            if (abilityProperty == null) return;

            float line = EditorGUIUtility.singleLineHeight;
            float gap = EditorGUIUtility.standardVerticalSpacing + Spacing;
            float y = rect.y;

            var typeProp = abilityProperty.FindPropertyRelative("type");
            var magnitudeProp = abilityProperty.FindPropertyRelative("magnitude");
            var durationProp = abilityProperty.FindPropertyRelative("duration");
            var tickIntervalProp = abilityProperty.FindPropertyRelative("tickInterval");
            var radiusProp = abilityProperty.FindPropertyRelative("radius");
            var damageTargetProp = abilityProperty.FindPropertyRelative("damageTarget");

            DrawTypePopup(new Rect(rect.x, y, rect.width, line), typeProp);
            y += line + gap;

            var type = ReadType(abilityProperty);

            switch (type)
            {
                case BulletBankAbilityType.ElectricShockDisable:
                    DrawLabeled(new Rect(rect.x, y, rect.width, line), durationProp, "Stun Duration (sec)");
                    break;

                case BulletBankAbilityType.BurnOverTime:
                    y = DrawLabeledRow(rect, y, line, gap, magnitudeProp, "Damage Per Second");
                    y = DrawLabeledRow(rect, y, line, gap, durationProp, "Burn Duration (sec)");
                    y = DrawLabeledRow(rect, y, line, gap, tickIntervalProp, "Tick Interval (sec)");
                    DrawLabeled(new Rect(rect.x, y, rect.width, line), radiusProp, "Extra Range (burn only)");
                    break;

                case BulletBankAbilityType.HealFriendly:
                    DrawLabeled(new Rect(rect.x, y, rect.width, line), magnitudeProp, "Heal Per Hit");
                    break;

                case BulletBankAbilityType.ConcussivePush:
                    DrawLabeled(new Rect(rect.x, y, rect.width, line), magnitudeProp, "Push Force");
                    break;

                case BulletBankAbilityType.GravityPull:
                    y = DrawLabeledRow(rect, y, line, gap, radiusProp, "Pull Radius");
                    y = DrawLabeledRow(rect, y, line, gap, magnitudeProp, "Pull Force");
                    DrawLabeled(new Rect(rect.x, y, rect.width, line), durationProp, "Field Duration (sec)");
                    break;

                case BulletBankAbilityType.DamageMultiplier:
                    y = DrawLabeledRow(rect, y, line, gap, damageTargetProp, "Damage Target");
                    DrawLabeled(new Rect(rect.x, y, rect.width, line), magnitudeProp, "Damage Multiplier");
                    break;

                case BulletBankAbilityType.DamageMultiplierVsAsteroid:
                case BulletBankAbilityType.DamageMultiplierVsShip:
                case BulletBankAbilityType.DamageMultiplierVsGemMoon:
                case BulletBankAbilityType.DamageMultiplierVsGem:
                    DrawLabeled(new Rect(rect.x, y, rect.width, line), magnitudeProp, "Damage Multiplier");
                    break;

                case BulletBankAbilityType.StretchLengthInFlight:
                    y = DrawLabeledRow(rect, y, line, gap, radiusProp, "Start Length (×)");
                    DrawLabeled(new Rect(rect.x, y, rect.width, line), magnitudeProp, "End Length (×)");
                    break;
            }
        }

        private static void DrawTypePopup(Rect rect, SerializedProperty typeProp)
        {
            int current = typeProp.intValue;
            int selected = EditorGUI.IntPopup(rect, "Type", current, TypePopupLabels, GetTypePopupInts());
            if (selected != current)
                typeProp.intValue = selected;
        }

        private static int[] GetTypePopupInts()
        {
            var ints = new int[TypePopupValues.Length];
            for (int i = 0; i < TypePopupValues.Length; i++)
                ints[i] = (int)TypePopupValues[i];
            return ints;
        }

        private static int CountVisibleFields(BulletBankAbilityType type)
        {
            return type switch
            {
                BulletBankAbilityType.ElectricShockDisable => 1,
                BulletBankAbilityType.BurnOverTime => 4,
                BulletBankAbilityType.HealFriendly => 1,
                BulletBankAbilityType.ConcussivePush => 1,
                BulletBankAbilityType.GravityPull => 3,
                BulletBankAbilityType.DamageMultiplier => 2,
                BulletBankAbilityType.DamageMultiplierVsAsteroid => 1,
                BulletBankAbilityType.DamageMultiplierVsShip => 1,
                BulletBankAbilityType.DamageMultiplierVsGemMoon => 1,
                BulletBankAbilityType.DamageMultiplierVsGem => 1,
                BulletBankAbilityType.StretchLengthInFlight => 2,
                _ => 1,
            };
        }

        private static float DrawLabeledRow(Rect block, float y, float line, float gap, SerializedProperty prop, string label)
        {
            DrawLabeled(new Rect(block.x, y, block.width, line), prop, label);
            return y + line + gap;
        }

        private static void DrawLabeled(Rect rect, SerializedProperty prop, string label)
        {
            float labelW = rect.width * 0.44f;
            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelW, rect.height), label);
            EditorGUI.PropertyField(new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height), prop, GUIContent.none);
        }
    }
}
