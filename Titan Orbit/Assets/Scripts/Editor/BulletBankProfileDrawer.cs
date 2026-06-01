using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    [CustomPropertyDrawer(typeof(BulletBankProfile))]
    public class BulletBankProfileDrawer : PropertyDrawer
    {
        private static readonly Dictionary<string, ReorderableList> AbilityLists = new();

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!property.isExpanded)
                return EditorGUIUtility.singleLineHeight;

            float h = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            SerializedProperty statMods = property.FindPropertyRelative("statModifiers");
            h += EditorGUI.GetPropertyHeight(statMods, true);
            h += EditorGUIUtility.standardVerticalSpacing;
            h += GetAbilityList(property).GetHeight();
            return h;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            float line = EditorGUIUtility.singleLineHeight;
            var foldoutRect = new Rect(position.x, position.y, position.width, line);
            property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

            if (!property.isExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            float y = position.y + line + EditorGUIUtility.standardVerticalSpacing;
            SerializedProperty statMods = property.FindPropertyRelative("statModifiers");
            float statH = EditorGUI.GetPropertyHeight(statMods, true);
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, statH), statMods, true);
            y += statH + EditorGUIUtility.standardVerticalSpacing;

            ReorderableList list = GetAbilityList(property);
            float listH = list.GetHeight();
            list.DoList(new Rect(position.x, y, position.width, listH));

            EditorGUI.EndProperty();
        }

        private static ReorderableList GetAbilityList(SerializedProperty profileProperty)
        {
            string key = profileProperty.propertyPath;
            if (AbilityLists.TryGetValue(key, out ReorderableList existing) && existing.serializedProperty != null)
                return existing;

            SerializedProperty abilities = profileProperty.FindPropertyRelative("abilities");
            var list = new ReorderableList(profileProperty.serializedObject, abilities, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Abilities"),
                elementHeightCallback = index =>
                {
                    SerializedProperty element = abilities.GetArrayElementAtIndex(index);
                    return BulletBankAbilityEditorUI.GetHeight(element) + 4f;
                },
                drawElementCallback = (rect, index, active, focused) =>
                {
                    SerializedProperty element = abilities.GetArrayElementAtIndex(index);
                    rect.y += 2f;
                    rect.height -= 4f;
                    EditorGUI.BeginChangeCheck();
                    BulletBankAbilityEditorUI.Draw(rect, element);
                    if (EditorGUI.EndChangeCheck())
                        element.serializedObject.ApplyModifiedProperties();
                },
                drawElementBackgroundCallback = (rect, index, active, focused) =>
                {
                    if (!active && !focused) return;
                    EditorGUI.DrawRect(rect, new Color(1f, 1f, 1f, active ? 0.08f : 0.04f));
                },
            };

            AbilityLists[key] = list;
            return list;
        }
    }
}
