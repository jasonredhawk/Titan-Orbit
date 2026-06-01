using UnityEditor;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>Fallback drawer when a <see cref="BulletBankAbility"/> is shown outside <see cref="BulletBankProfileDrawer"/>.</summary>
    [CustomPropertyDrawer(typeof(BulletBankAbility))]
    public class BulletBankAbilityDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!property.isExpanded)
                return EditorGUIUtility.singleLineHeight;
            return EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing
                   + BulletBankAbilityEditorUI.GetHeight(property);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            float line = EditorGUIUtility.singleLineHeight;
            property.isExpanded = EditorGUI.Foldout(
                new Rect(position.x, position.y, position.width, line),
                property.isExpanded,
                label,
                true);

            if (property.isExpanded)
            {
                float y = position.y + line + EditorGUIUtility.standardVerticalSpacing;
                float h = BulletBankAbilityEditorUI.GetHeight(property);
                BulletBankAbilityEditorUI.Draw(new Rect(position.x, y, position.width, h), property);
            }

            EditorGUI.EndProperty();
        }
    }
}
