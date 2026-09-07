#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// Points MPPM Player 2 at the shipped Windows client build profile for multi-editor play mode testing.
    /// Aborts Play when the Main Editor is still on Dedicated Server — MPPM copies that subtarget
    /// onto clones even when Player 2's Role dropdown is Client, which mismatches NetCode ghosts.
    /// </summary>
    [InitializeOnLoad]
    public static class MppmBuildProfileSetup
    {
        public const string ClientProfilePath = "Assets/Settings/Build Profiles/TitanOrbitMppmClient.asset";

        const string ServerSubtargetPlayWarning =
            "The Main Editor is still on Dedicated Server (often leftover after a Linux headless build).\n\n" +
            "MPPM copies -standaloneBuildSubtarget Server onto Player 2 even when that clone's " +
            "Multiplayer Role is Client. UNITY_SERVER is then defined in the clone, NetCode ghost " +
            "schemas do not match, and both windows snap / hitch.\n\n" +
            "Fix: File > Build Profiles → Windows Player (not Dedicated Server, not WebGL).\n" +
            "Then Window > Play Mode > Scenarios: toggle Player 2 off and on so the clone relaunches.\n" +
            "Play from the Main Editor only.\n\n" +
            "Or use Titan Orbit > Switch Editor To Windows Player (MPPM).";

        static MppmBuildProfileSetup()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
                return;
            if (!IsDedicatedServerBuildTargetActive())
                return;
            if (!IsMppmAdditionalEditorEnabled())
                return;

            EditorApplication.isPlaying = false;
            Debug.LogError("[TitanOrbitPlayMode] " + ServerSubtargetPlayWarning);
            EditorApplication.delayCall += () =>
                EditorUtility.DisplayDialog(
                    "Titan Orbit — cannot Play MPPM on Dedicated Server",
                    ServerSubtargetPlayWarning,
                    "OK");
        }

        [MenuItem("Titan Orbit/Create MPPM Client Build Profile")]
        public static void CreateMppmClientBuildProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ClientProfilePath);
            if (profile == null)
            {
                EditorUtility.DisplayDialog(
                    "Titan Orbit — MPPM client profile missing",
                    "Expected asset at:\n" + ClientProfilePath + "\n\n" +
                    "Restore it from git or reimport the project.",
                    "OK");
                return;
            }

            Debug.Log("[MppmBuildProfileSetup] Using Windows client profile: " + ClientProfilePath);
            EditorGUIUtility.PingObject(profile);
            ShowAssignProfileDialog(profile);
        }

        [MenuItem("Titan Orbit/Switch Editor To Windows Player (MPPM)")]
        public static void SwitchEditorToWindowsPlayerMenu()
        {
            if (IsWindowsClientPlayerActive())
            {
                EditorUtility.DisplayDialog(
                    "Titan Orbit — already Windows Player",
                    "The Main Editor is already on Windows Player.\n\n" +
                    "If Player 2 was launched while Dedicated Server was active, toggle Player 2 " +
                    "off and on in Window > Play Mode > Scenarios so the clone relaunches, then " +
                    "Play from the Main Editor only.",
                    "OK");
                return;
            }

            TrySwitchEditorToWindowsPlayer();
            if (IsWindowsClientPlayerActive())
                return;

            EditorUtility.DisplayDialog(
                "Titan Orbit — switching to Windows Player",
                "Wait for the platform switch to finish (scripts recompile).\n\n" +
                "Then Window > Play Mode > Scenarios: toggle Player 2 off and on so the clone " +
                "relaunches without -standaloneBuildSubtarget Server. Play from the Main Editor only.",
                "OK");
        }

        /// <summary>
        /// Switches the active Editor target to Windows Player when leftover Dedicated Server
        /// would poison MPPM clones. Returns false when a platform switch was started (domain reload).
        /// </summary>
        public static bool TrySwitchEditorToWindowsPlayer()
        {
            if (IsWindowsClientPlayerActive())
                return true;

            Debug.Log(
                "[MppmBuildProfileSetup] Switching Editor off Dedicated Server " +
                $"({EditorUserBuildSettings.activeBuildTarget} / {EditorUserBuildSettings.standaloneBuildSubtarget}) " +
                "→ Windows Player so MPPM clones do not launch with UNITY_SERVER.");

            // Named profile alone does not leave Linux Dedicated Server — SwitchActiveBuildTarget does.
            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ClientProfilePath);
            if (profile != null)
                BuildProfile.SetActiveBuildProfile(profile);

            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                NamedBuildTarget.Standalone,
                BuildTarget.StandaloneWindows64);
            return switched && IsWindowsClientPlayerActive();
        }

        public static bool IsWindowsClientPlayerActive()
        {
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64
                   && EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Player;
        }

        public static bool IsDedicatedServerBuildTargetActive()
        {
            return EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server;
        }

        /// <summary>True when Library/VP/SystemData.json has an enabled additional Editor clone.</summary>
        public static bool IsMppmAdditionalEditorEnabled()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string systemDataPath = Path.Combine(projectRoot, "Library", "VP", "SystemData.json");
            if (!File.Exists(systemDataPath))
                return false;

            string json = File.ReadAllText(systemDataPath);
            if (!json.Contains("\"IsMppmActive\": true"))
                return false;

            // Player 2's own Active flag — Main Editor also has "Active": true in the same file.
            return System.Text.RegularExpressions.Regex.IsMatch(
                json,
                "\"Name\"\\s*:\\s*\"Player 2\"[\\s\\S]{0,400}?\"Active\"\\s*:\\s*true");
        }

        /// <summary>Opens MPPM scenarios window with step-by-step two-player instructions.</summary>
        internal static void ShowAssignProfileDialog(BuildProfile profile)
        {
            EditorUtility.DisplayDialog(
                "Titan Orbit — MPPM two-player setup",
                "1. Stop Play on ALL instances.\n" +
                "2. File > Build Profiles → Windows Player (not Dedicated Server, not WebGL).\n" +
                "3. Window > Play Mode > Scenarios — enable one Additional Editor (Player 2).\n" +
                "4. Confirm Player 2 Multiplayer Role → Client (not Server).\n" +
                "5. If Player 2 was launched while Dedicated Server was active, toggle it off and on.\n" +
                "6. Press Play from the Main Editor only.\n" +
                "7. Main: Local play. Player 2: Local client (or Join).\n\n" +
                "Player 2 console MUST show buildSubTarget=Player (or Editor), never Server.\n" +
                "A leftover Linux Dedicated Server target on the Main Editor is enough to break this.",
                "OK");

            if (!EditorApplication.ExecuteMenuItem("Window/Play Mode/Scenarios"))
                EditorApplication.ExecuteMenuItem("Window/Multiplayer/Play Mode");
        }
    }
}
#endif
