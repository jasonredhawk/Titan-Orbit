#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// First-run editor prompt on SampleScene — offers to run NetCodeGameSetup if SubScene is missing.
    /// Uses EditorPrefs so the dialog shows only once per machine.
    /// </summary>
    [InitializeOnLoad]
    static class NetCodeGameSetupPrompt
    {
        const string PrefKey = "TitanOrbit.NetCodeGameSetupDone";

        static NetCodeGameSetupPrompt()
        {
            EditorApplication.delayCall += TryPromptSetup;
        }

        static void TryPromptSetup()
        {
            if (EditorPrefs.GetBool(PrefKey, false))
                return;
            if (EditorSceneManager.GetActiveScene().name != "SampleScene")
                return;
            if (UnityEngine.Object.FindAnyObjectByType<Unity.Scenes.SubScene>() != null)
            {
                EditorPrefs.SetBool(PrefKey, true);
                return;
            }

            if (EditorUtility.DisplayDialog(
                    "Titan Orbit NetCode Setup",
                    "This scene is not wired for Netcode for Entities yet.\n\nRun 'Titan Orbit → Setup NetCode Game (Full)' now?",
                    "Setup Now",
                    "Later"))
            {
                NetCodeGameSetup.SetupActiveScene();
                EditorPrefs.SetBool(PrefKey, true);
            }
        }
    }
}
#endif
