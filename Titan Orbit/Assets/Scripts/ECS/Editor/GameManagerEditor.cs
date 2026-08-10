#if UNITY_EDITOR
using TitanOrbit.Core;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// Custom Inspector for <see cref="GameManager"/>.
    /// Adds a Test / Production toolbar that applies the same NetCode PlayMode prefs and Local play
    /// UI flag as Titan Orbit → Configure Multiplayer For Local Play / Dedicated Server — so you
    /// do not need to dig through the menu every time you switch workflows.
    /// [EDITOR] only — not compiled into player or dedicated server builds.
    /// </summary>
    [CustomEditor(typeof(GameManager))]
    public class GameManagerEditor : UnityEditor.Editor
    {
        /// <summary>Labels for the two-mode toolbar (index matches <see cref="EditorMultiplayerMode"/>).</summary>
        static readonly string[] ModeToolbarLabels = { "Test", "Production" };

        /// <summary>Serialized mirror of <c>GameManager.editorMultiplayerMode</c>.</summary>
        SerializedProperty _editorMultiplayerModeProp;

        /// <summary>
        /// [UNITY] OnEnable — cache the mode property and sync the enum from live PlayMode Tools
        /// so the toolbar matches prefs even if you last changed mode via the Titan Orbit menu.
        /// </summary>
        void OnEnable()
        {
            // --- Cache serialized fields ---
            _editorMultiplayerModeProp = serializedObject.FindProperty("editorMultiplayerMode");

            // --- Sync from live Editor prefs (menu may have changed them) ---
            SyncModePropertyFromPrefs();
        }

        /// <summary>
        /// [UNITY] Draws the Test / Production toolbar first, then the rest of GameManager
        /// (debug flags, stutter isolator, etc.).
        /// </summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // --- Multiplayer mode toolbar ---
            DrawMultiplayerModeSection();

            EditorGUILayout.Space(8f);

            // --- Remaining GameManager fields (skip script + mode; mode drawn above) ---
            // [UNITY] DrawPropertiesExcluding keeps default Inspector behavior for debug toggles.
            DrawPropertiesExcluding(serializedObject, "m_Script", "editorMultiplayerMode");

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Draws the help box + Test | Production toolbar and applies NetCode prefs when the
        /// selection changes.
        /// </summary>
        void DrawMultiplayerModeSection()
        {
            EditorGUILayout.LabelField("Multiplayer Mode (Editor)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Test — Client & Server worlds, Local play menu buttons (local host / LAN).\n" +
                "Production — Client-only Editor, UGS/Relay join to a dedicated server " +
                "(hides Local play; same as Configure Multiplayer For Dedicated Server).\n\n" +
                "Stop Play before switching. Restart Play after changing mode.",
                MessageType.Info);

            if (_editorMultiplayerModeProp == null)
                return;

            // --- Toolbar ---
            // [STANDARD] GUILayout.Toolbar returns the selected index; we map it to the enum.
            EditorGUI.BeginChangeCheck();
            int selected = GUILayout.Toolbar(
                _editorMultiplayerModeProp.enumValueIndex,
                ModeToolbarLabels,
                GUILayout.Height(28f));

            if (!EditorGUI.EndChangeCheck())
                return;

            // --- Persist choice on the scene component ---
            _editorMultiplayerModeProp.enumValueIndex = selected;
            serializedObject.ApplyModifiedProperties();

            // --- Apply NetCode + Local play UI (same as Titan Orbit menus, no dialog) ---
            // [TITAN-ORBIT] NetCodeGameSetup owns the prefs + MPPM role patching.
            if (selected == (int)EditorMultiplayerMode.Test)
                NetCodeGameSetup.ApplyTestMode();
            else
                NetCodeGameSetup.ApplyProductionMode();

            // Mark the GameManager scene object dirty so the enum survives save.
            EditorUtility.SetDirty(target);
        }

        /// <summary>
        /// Writes <c>editorMultiplayerMode</c> from <see cref="NetCodeGameSetup.IsCurrentModeTest"/>
        /// without re-applying prefs (avoids fighting the Titan Orbit menu).
        /// </summary>
        void SyncModePropertyFromPrefs()
        {
            if (_editorMultiplayerModeProp == null)
                return;

            // --- Infer Test vs Production from PlayMode Tools + Resources config ---
            int inferred = NetCodeGameSetup.IsCurrentModeTest()
                ? (int)EditorMultiplayerMode.Test
                : (int)EditorMultiplayerMode.Production;

            if (_editorMultiplayerModeProp.enumValueIndex == inferred)
                return;

            serializedObject.Update();
            _editorMultiplayerModeProp.enumValueIndex = inferred;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
