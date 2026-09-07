#if UNITY_EDITOR
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Popup of live <see cref="BulletVfxBank"/> category names for
    /// <see cref="BulletVfxBankCategoryAttribute"/> int fields.
    /// </summary>
    [CustomPropertyDrawer(typeof(BulletVfxBankCategoryAttribute))]
    public class BulletVfxBankCategoryDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            var attr = (BulletVfxBankCategoryAttribute)attribute;
            var bank = BulletVfxBank.LoadDefault();
            int count = bank != null ? bank.CategoryCount : 0;
            int extra = attr.IncludeInheritOption ? 1 : 0;
            if (count + extra < 1)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            var contents = new GUIContent[count + extra];
            var values = new int[count + extra];
            int i = 0;
            if (attr.IncludeInheritOption)
            {
                contents[0] = new GUIContent(attr.InheritLabel);
                values[0] = -1;
                i = 1;
            }

            for (int c = 0; c < count; c++, i++)
            {
                string name = bank.GetCategoryName(c);
                contents[i] = new GUIContent(
                    string.IsNullOrEmpty(name) ? $"Bank {c}" : $"{c}: {name}");
                values[i] = c;
            }

            EditorGUI.IntPopup(position, property, contents, values, label);
        }
    }
}
#endif
