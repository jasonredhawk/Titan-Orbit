#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>Points MPPM Player 2 at the shipped Windows client build profile.</summary>
    public static class MppmBuildProfileSetup
    {
        public const string ClientProfilePath = "Assets/Settings/Build Profiles/TitanOrbitMppmClient.asset";

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

        internal static void ShowAssignProfileDialog(BuildProfile profile)
        {
            EditorUtility.DisplayDialog(
                "Titan Orbit — MPPM two-player setup",
                "1. Stop Play on ALL instances.\n" +
                "2. Window > Play Mode > Scenarios.\n" +
                "3. Enable your scenario with one Additional Editor Instance (Player 2).\n" +
                "4. Set Player 2 Multiplayer Role → Client.\n" +
                "5. Press Play from the Main Editor only.\n" +
                "6. On the Main Editor Game tab: click Play on the menu, then pick a team.\n" +
                "7. On Player 2: click Join on any team (same team allowed for testing).\n\n" +
                "Player 2 console should show buildSubTarget=Player, not Server.",
                "OK");

            if (!EditorApplication.ExecuteMenuItem("Window/Play Mode/Scenarios"))
                EditorApplication.ExecuteMenuItem("Window/Multiplayer/Play Mode");
        }
    }
}
#endif
