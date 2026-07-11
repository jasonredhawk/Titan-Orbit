using UnityEngine;
using UnityEditor;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Converts all UltimateSpaceshipsCreator materials and prefabs from Built-in render pipeline to URP.
    /// Fixes purple materials caused by incompatible Built-in Standard/Skybox shaders.
    /// </summary>
    public static class UltimateSpaceshipsCreatorURPFix
    {
        const string RootFolder = "Assets/UltimateSpaceshipsCreator";

        /// <summary>
        /// Call via: Unity -batchmode -projectPath "..." -executeMethod TitanOrbit.Editor.UltimateSpaceshipsCreatorURPFix.FixAllMaterialsForURP -quit
        /// </summary>
        [MenuItem("Titan Orbit/Fix All UltimateSpaceshipsCreator Materials for URP")]
        public static void FixAllMaterialsForURP()
        {
            // --- FixAllMaterialsForURP ---
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Universal Render Pipeline/Simple Lit");
            Shader urpSkybox = Shader.Find("Universal Render Pipeline/Skybox/Cubemap")
                ?? Shader.Find("Skybox/Cubemap");

            if (urpLit == null)
            {
                Debug.LogError("URP Lit shader not found. Ensure Universal RP is installed.");
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { RootFolder });
            int standardFixed = 0;
            int skyboxFixed = 0;
            int skipped = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;

                string shaderName = mat.shader.name;

                // Already URP
                if (shaderName.Contains("Universal Render Pipeline"))
                {
                    skipped++;
                    continue;
                }

                // Built-in Skybox -> URP Skybox
                if (shaderName.Contains("Skybox") && urpSkybox != null)
                {
                    ConvertSkyboxToURP(mat, urpSkybox);
                    EditorUtility.SetDirty(mat);
                    skyboxFixed++;
                    continue;
                }

                // Built-in Standard -> URP Lit
                if (shaderName.Contains("Standard") || shaderName == "Standard")
                {
                    ConvertStandardToURPLit(mat, urpLit);
                    EditorUtility.SetDirty(mat);
                    standardFixed++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[UltimateSpaceshipsCreator URP Fix] Converted {standardFixed} Standard materials to URP Lit, {skyboxFixed} Skybox materials to URP Skybox. Skipped {skipped} (already URP). Total materials: {guids.Length}.");
        }

        static void ConvertStandardToURPLit(Material mat, Shader urpLit)
        {
            // --- ConvertStandardToURPLit ---
            Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            Color baseColor = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            Texture bumpMap = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
            Texture metallicGlossMap = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
            Texture emissionMap = mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null;
            Color emissionColor = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.black;
            float smoothness = mat.HasProperty("_Glossiness") ? mat.GetFloat("_Glossiness") : 0.5f;
            float metallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;

            mat.shader = urpLit;

            if (mainTex != null) mat.SetTexture("_BaseMap", mainTex);
            mat.SetColor("_BaseColor", baseColor);
            if (bumpMap != null) mat.SetTexture("_BumpMap", bumpMap);
            if (metallicGlossMap != null) mat.SetTexture("_MetallicGlossMap", metallicGlossMap);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);

            if (emissionMap != null)
            {
                mat.SetTexture("_EmissionMap", emissionMap);
                mat.SetColor("_EmissionColor", emissionColor);
                mat.EnableKeyword("_EMISSION");
            }
        }

        static void ConvertSkyboxToURP(Material mat, Shader urpSkybox)
        {
            // --- ConvertSkyboxToURP ---
            // Built-in Skybox/Cubemap uses _Tex; URP Skybox/Cubemap uses _Tex as well
            Texture tex = mat.HasProperty("_Tex") ? mat.GetTexture("_Tex") : null;
            Color tint = mat.HasProperty("_Tint") ? mat.GetColor("_Tint") : new Color(0.5f, 0.5f, 0.5f, 0.5f);
            float exposure = mat.HasProperty("_Exposure") ? mat.GetFloat("_Exposure") : 1f;
            float rotation = mat.HasProperty("_Rotation") ? mat.GetFloat("_Rotation") : 0f;

            mat.shader = urpSkybox;
            if (tex != null) mat.SetTexture("_Tex", tex);
            if (mat.HasProperty("_Tint")) mat.SetColor("_Tint", tint);
            if (mat.HasProperty("_Exposure")) mat.SetFloat("_Exposure", exposure);
            if (mat.HasProperty("_Rotation")) mat.SetFloat("_Rotation", rotation);
        }
    }
}
