using System;
using System.IO;
using TitanOrbit.NetCode;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor.WebGL
{
    /// <summary>
    /// [EDITOR] Instant WebGL client checks — no player build. Validates the connection-entity
    /// recipe and keeps WebGL Burst AOT off so Receive's Burst IJobEntity does not WASM-OOB
    /// on the first tick that has a NetworkStreamConnection.
    /// </summary>
    public static class TitanOrbitWebGlClientGuard
    {
        const string BurstAotPath = "ProjectSettings/BurstAotSettings_WebGL.json";

        [MenuItem("TitanOrbit/WebGL/Validate Connect Entity Recipe")]
        public static void ValidateConnectEntityRecipe()
        {
            var world = new World("WebGlConnectRecipeTest", WorldFlags.None);
            try
            {
                var em = world.EntityManager;
                Entity ent = TitanOrbitWebGlClientConnect.CreateConnectionEntity(
                    em, default, ConnectionState.State.Connecting);
                if (!TitanOrbitWebGlClientConnect.TryDescribeMissing(em, ent, out string missing))
                {
                    EditorUtility.DisplayDialog(
                        "WebGL connect recipe",
                        "FAILED — missing " + missing + ". Do not WebGL-rebuild until this passes.",
                        "OK");
                    Debug.LogError("[TitanOrbitWebGlClientGuard] Connect recipe failed: " + missing);
                    return;
                }

                Debug.Log("[TitanOrbitWebGlClientGuard] Connect recipe OK — NSC + Ack + stream buffers + query count=1.");
                EditorUtility.DisplayDialog(
                    "WebGL connect recipe",
                    "Passed. Editor check only — WASM join still needs one WebGL player build after a real fix (Burst AOT off).",
                    "OK");
            }
            finally
            {
                world.Dispose();
            }
        }

        [MenuItem("TitanOrbit/WebGL/Validate DynamicAssemblyList")]
        public static void ValidateDynamicAssemblyList()
        {
            int found = 0;
            int excluded = 0;
            string firstName = "";
            NativeList<SystemTypeIndex> all = DefaultWorldInitialization.GetAllSystemTypeIndices(
                WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation);
            for (int i = 0; i < all.Length; i++)
            {
                string systemName = TypeManager.GetSystemName(all[i]).ToString();
                if (!TitanOrbitWebGlSystemFilter.IsDynamicAssemblyListSystem(systemName))
                    continue;
                found++;
                if (string.IsNullOrEmpty(firstName))
                    firstName = systemName;
                if (TitanOrbitWebGlSystemFilter.IsExcluded(systemName))
                    excluded++;
            }

            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string json =
                "{\"sessionId\":\"1a7cd0\",\"runId\":\"editor-dal\",\"hypothesisId\":\"H-DAL-A\"," +
                "\"location\":\"TitanOrbitWebGlClientGuard.ValidateDynamicAssemblyList\"," +
                "\"message\":\"dal-system-scan\"," +
                "\"data\":{\"found\":" + found +
                ",\"excludedOnWebGl\":" + excluded +
                ",\"name\":\"" + EscapeJson(firstName) + "\"}," +
                "\"timestamp\":" + ts + "}\n";
            // #region agent log
            string logPath = GetDebugLogPath();
            try
            {
                File.AppendAllText(logPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TitanOrbitWebGlClientGuard] Could not write debug log: " + ex.Message);
            }
            // #endregion

            Debug.Log("[TitanOrbitWebGlClientGuard] DynamicAssemblyList systems found=" + found +
                      " excludedOnWebGl=" + excluded + " name=" + firstName +
                      (found > 0 && excluded == found
                          ? " — WebGL client stays DAL=0; server/Windows stay DAL=1."
                          : " — unexpected, inspect before a WebGL rebuild."));
        }

        static string GetDebugLogPath()
        {
            string assets = Application.dataPath;
            string project = Path.GetDirectoryName(assets);
            string repo = Path.GetDirectoryName(project);
            return Path.Combine(repo ?? project ?? "", "debug-1a7cd0.log");
        }

        static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        [MenuItem("TitanOrbit/WebGL/Ensure Burst Off For WebGL")]
        public static void EnsureBurstOffForWebGl()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot))
                return;

            string path = Path.Combine(projectRoot, BurstAotPath);
            string json =
                "{\n" +
                "  \"MonoBehaviour\": {\n" +
                "    \"Version\": 4,\n" +
                "    \"EnableBurstCompilation\": false,\n" +
                "    \"EnableOptimisations\": true,\n" +
                "    \"EnableSafetyChecks\": false,\n" +
                "    \"EnableDebugInAllBuilds\": false,\n" +
                "    \"UsePlatformSDKLinker\": false,\n" +
                "    \"CpuMinTargetX32\": 0,\n" +
                "    \"CpuMaxTargetX32\": 0,\n" +
                "    \"CpuMinTargetX64\": 0,\n" +
                "    \"CpuMaxTargetX64\": 0,\n" +
                "    \"OptimizeFor\": 0\n" +
                "  }\n" +
                "}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? projectRoot);
            File.WriteAllText(path, json);
            Debug.Log("[TitanOrbitWebGlClientGuard] Wrote " + BurstAotPath + " (EnableBurstCompilation=false).");
        }

        [InitializeOnLoadMethod]
        static void OnEditorLoad()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot))
                return;
            string path = Path.Combine(projectRoot, BurstAotPath);
            if (!File.Exists(path) || !File.ReadAllText(path).Contains("\"EnableBurstCompilation\": false"))
                EnsureBurstOffForWebGl();
        }
    }
}
