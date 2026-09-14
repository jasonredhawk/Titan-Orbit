#if UNITY_EDITOR
using System.IO;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Team Color1 stays on this asset. Accent presets are separate assets so
    /// designers can author Color2 / Color3 / Glow without touching team identity.
    /// </summary>
    [CustomEditor(typeof(TeamColor1Palette))]
    public sealed class TeamColor1PaletteEditor : UnityEditor.Editor
    {
        const string PresetFolder = "Assets/Resources/ShipAccentPresets";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Color1 is the team read and players cannot change it. " +
                "Create accent presets for Color2 / Color3 / Glow — keep those neutrals " +
                "so a red team cannot look blue.",
                MessageType.Info);

            if (!GUILayout.Button("Create Accent Preset"))
                return;

            var palette = (TeamColor1Palette)target;
            if (!AssetDatabase.IsValidFolder(PresetFolder))
            {
                Directory.CreateDirectory(PresetFolder);
                AssetDatabase.Refresh();
            }

            var preset = CreateInstance<ShipAccentPreset>();
            preset.displayName = "New Preset";
            string path = AssetDatabase.GenerateUniqueAssetPath(PresetFolder + "/AccentPreset_New.asset");
            AssetDatabase.CreateAsset(preset, path);

            Undo.RecordObject(palette, "Create Accent Preset");
            if (palette.accentPresets == null)
                palette.accentPresets = new System.Collections.Generic.List<ShipAccentPreset>();
            palette.accentPresets.Add(preset);
            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
            Selection.activeObject = preset;
        }
    }
}
#endif
