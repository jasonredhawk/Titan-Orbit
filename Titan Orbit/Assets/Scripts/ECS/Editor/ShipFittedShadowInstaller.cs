using TitanOrbit.Game;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// Pins the ship-fitted shadow feature onto the PC and mobile URP renderers and keeps a
    /// reference to the apply shader so player builds do not strip it.
    /// </summary>
    [InitializeOnLoad]
    static class ShipFittedShadowInstaller
    {
        const string ShaderPath = "Assets/Shaders/ShipFittedShadowApply.shader";

        static readonly string[] RendererPaths =
        {
            "Assets/Settings/PC_Renderer.asset",
            "Assets/Settings/Mobile_Renderer.asset"
        };

        static ShipFittedShadowInstaller()
        {
            EditorApplication.delayCall += InstallPersistent;
        }

        [MenuItem("TitanOrbit/Rendering/Install Ship Fitted Shadow")]
        static void InstallPersistent()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
                return;

            bool changed = false;
            for (int i = 0; i < RendererPaths.Length; i++)
                changed |= Ensure(RendererPaths[i], shader);

            if (changed)
                AssetDatabase.SaveAssets();
        }

        static bool Ensure(string rendererPath, Shader shader)
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (data == null)
                return false;

            if (data.TryGetRendererFeature<ShipFittedShadowFeature>(out ShipFittedShadowFeature existing))
            {
                var existingSerialized = new SerializedObject(existing);
                SerializedProperty shaderProp = existingSerialized.FindProperty("applyShader");
                if (shaderProp == null || shaderProp.objectReferenceValue == shader)
                    return false;

                shaderProp.objectReferenceValue = shader;
                existingSerialized.ApplyModifiedPropertiesWithoutUndo();
                return true;
            }

            var feature = ScriptableObject.CreateInstance<ShipFittedShadowFeature>();
            feature.name = "Ship Fitted Shadow";
            feature.AssignApplyShader(shader);
            AssetDatabase.AddObjectToAsset(feature, data);

            var serialized = new SerializedObject(data);
            SerializedProperty features = serialized.FindProperty("m_RendererFeatures");
            SerializedProperty map = serialized.FindProperty("m_RendererFeatureMap");
            int index = features.arraySize;
            features.arraySize = index + 1;
            features.GetArrayElementAtIndex(index).objectReferenceValue = feature;
            map.arraySize = features.arraySize;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(data);
            return true;
        }
    }
}
