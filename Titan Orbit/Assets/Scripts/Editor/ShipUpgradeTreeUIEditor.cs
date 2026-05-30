using TitanOrbit.Data;
using TitanOrbit.UI;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    [CustomEditor(typeof(ShipUpgradeTreeUI))]
    public class ShipUpgradeTreeUIEditor : UnityEditor.Editor
    {
        private SerializedProperty _previewFamilyProp;
        private ShipFamilyDefinition _lastPreviewFamily;

        private void OnEnable()
        {
            _previewFamilyProp = serializedObject.FindProperty("previewFamily");
            _lastPreviewFamily = ((ShipUpgradeTreeUI)target).PreviewFamily;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawPropertiesExcluding(serializedObject, "m_Script");

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Editor preview", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(_previewFamilyProp);
            bool familyChanged = EditorGUI.EndChangeCheck();

            serializedObject.ApplyModifiedProperties();

            var tree = (ShipUpgradeTreeUI)target;
            if (familyChanged || tree.PreviewFamily != _lastPreviewFamily)
            {
                _lastPreviewFamily = tree.PreviewFamily;
                RefreshPreview(tree, markDirty: true);
            }

            if (GUILayout.Button("Refresh preview (nodes + connectors)"))
                RefreshPreview(tree, markDirty: true);

            EditorGUILayout.HelpBox(
                "Assign Preview Family, then click Refresh to preview nodes in the editor. " +
                "Runtime builds nodes dynamically from ShipUpgradeTreeNode.prefab. " +
                "Re-run Titan Orbit → UI → Create Ship Upgrade Tree Prefab to regenerate shells.",
                MessageType.Info);
        }

        private static void RefreshPreview(ShipUpgradeTreeUI tree, bool markDirty)
        {
            if (tree == null || Application.isPlaying)
                return;

            tree.EditorPreviewFromFamily(tree.PreviewFamily);
            if (markDirty)
                EditorUtility.SetDirty(tree);
        }
    }
}
