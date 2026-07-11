using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Custom Inspector drawer for <see cref="BulletBankProfile"/> — foldout for stat
    /// modifiers plus reorderable abilities list. Cached per property path; cleared on assembly reload.
    /// Used on <see cref="BulletVfxBank"/> category rows and any serialized profile field.
    /// </summary>
    [CustomPropertyDrawer(typeof(BulletBankProfile))]
    public class BulletBankProfileDrawer : PropertyDrawer
    {
        /// <summary>One ReorderableList per serialized property path (supports multiple profiles on one asset).</summary>
        private static readonly Dictionary<string, ReorderableList> AbilityLists = new();

        static BulletBankProfileDrawer()
        {
            // [UNITY] Domain reload would leave stale SerializedProperty refs in the cache.
            AssemblyReloadEvents.beforeAssemblyReload += ClearAbilityListCache;
        }

        /// <summary>Clears cached reorderable lists before script recompile.</summary>
        private static void ClearAbilityListCache()
        {
            AbilityLists.Clear();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!property.isExpanded)
                return EditorGUIUtility.singleLineHeight;

            float h = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            SerializedProperty statMods = property.FindPropertyRelative("statModifiers");
            h += EditorGUI.GetPropertyHeight(statMods, true);
            h += EditorGUIUtility.standardVerticalSpacing;
            ReorderableList list = GetAbilityList(property);
            if (list != null)
                h += list.GetHeight();
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
            if (list != null)
            {
                float listH = list.GetHeight();
                list.DoList(new Rect(position.x, y, position.width, listH));
            }

            EditorGUI.EndProperty();
        }

        private static ReorderableList GetAbilityList(SerializedProperty profileProperty)
        {
            // --- Compute value ---
            if (profileProperty == null || profileProperty.serializedObject == null)
                return null;

            UnityEngine.Object target = profileProperty.serializedObject.targetObject;
            if (target == null)
                return null;

            string key = target.GetInstanceID() + ":" + profileProperty.propertyPath;
            if (AbilityLists.TryGetValue(key, out ReorderableList existing))
            {
                if (IsAbilityListValid(existing, profileProperty))
                    return existing;
                AbilityLists.Remove(key);
            }

            SerializedProperty abilities = profileProperty.FindPropertyRelative("abilities");
            if (abilities == null)
                return null;

            var list = new ReorderableList(profileProperty.serializedObject, abilities, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Abilities"),
                elementHeightCallback = index =>
                {
                    if (abilities == null || !IsSerializedPropertyAlive(abilities))
                        return EditorGUIUtility.singleLineHeight + 4f;
                    if (index < 0 || index >= abilities.arraySize)
                        return EditorGUIUtility.singleLineHeight + 4f;

                    SerializedProperty element = abilities.GetArrayElementAtIndex(index);
                    return BulletBankAbilityEditorUI.GetHeight(element) + 4f;
                },
                drawElementCallback = (rect, index, active, focused) =>
                {
                    if (abilities == null || !IsSerializedPropertyAlive(abilities))
                        return;
                    if (index < 0 || index >= abilities.arraySize)
                        return;

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

        private static bool IsAbilityListValid(ReorderableList list, SerializedProperty profileProperty)
        {
            // --- IsAbilityListValid ---
            if (list?.serializedProperty == null || profileProperty == null)
                return false;

            if (!IsSerializedPropertyAlive(list.serializedProperty))
                return false;

            return list.serializedProperty.serializedObject == profileProperty.serializedObject;
        }

        private static bool IsSerializedPropertyAlive(SerializedProperty property)
        {
            // --- IsSerializedPropertyAlive ---
            if (property == null)
                return false;

            try
            {
                SerializedObject serializedObject = property.serializedObject;
                return serializedObject != null && serializedObject.targetObject != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
