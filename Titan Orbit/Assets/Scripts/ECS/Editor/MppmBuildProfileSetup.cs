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
            string profileName = profile != null ? profile.name : "TitanOrbitMppmClient";
            EditorUtility.DisplayDialog(
                "Titan Orbit — assign MPPM Player 2 build profile",
                "Client build profile: " + profileName + "\n\n" +
                "Required (fixes GhostReceiveSystem bit-count errors):\n\n" +
                "1. Stop Play on ALL instances.\n" +
                "2. Window > Play Mode > Scenarios.\n" +
                "3. Select your scenario.\n" +
                "4. Under Additional Editor Instances, select Player 2.\n" +
                "5. Build Profile → '" + profileName + "' (Windows Standalone CLIENT).\n" +
                "   Never use Linux Dedicated Server or Windows Dedicated Server.\n" +
                "6. Save, then Play from the Main Editor only.\n\n" +
                "Player 2 console should show buildSubTarget=Player, not Server.",
                "OK");

            if (!EditorApplication.ExecuteMenuItem("Window/Play Mode/Scenarios"))
                EditorApplication.ExecuteMenuItem("Window/Multiplayer/Play Mode");
        }
    }
}
#endif
