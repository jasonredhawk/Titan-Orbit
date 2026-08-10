#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// Points MPPM Player 2 at the shipped Windows client build profile for multi-editor play mode testing.
    /// </summary>
    public static class MppmBuildProfileSetup
    {
        public const string ClientProfilePath = "Assets/Settings/Build Profiles/TitanOrbitMppmClient.asset";

        [MenuItem("Titan Orbit/Create MPPM Client Build Profile")]
        public static void CreateMppmClientBuildProfile()
        {
            // --- Create instance ---
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

        /// <summary>Opens MPPM scenarios window with step-by-step two-player instructions.</summary>
        internal static void ShowAssignProfileDialog(BuildProfile profile)
        {
            // --- ShowAssignProfileDialog ---
            EditorUtility.DisplayDialog(
                "Titan Orbit — MPPM two-player setup",
                "1. Stop Play on ALL instances.\n" +
                "2. Window > Play Mode > Scenarios.\n" +
                "3. Enable your scenario with one Additional Editor Instance (Player 2).\n" +
                "4. Confirm Main Editor + Player 2 Multiplayer Role → Client (not Server).\n" +
                "5. Press Play from the Main Editor only.\n" +
                "6. Both: Join game → GCE Relay (or Local host/client for LAN).\n\n" +
                "Player 2 console MUST show buildSubTarget=Player (or Editor), never Server.\n" +
                "If you see 'MPPM clone is using a Dedicated SERVER build', fix Role → Client and restart.",
                "OK");

            if (!EditorApplication.ExecuteMenuItem("Window/Play Mode/Scenarios"))
                EditorApplication.ExecuteMenuItem("Window/Multiplayer/Play Mode");
        }
    }
}
#endif
