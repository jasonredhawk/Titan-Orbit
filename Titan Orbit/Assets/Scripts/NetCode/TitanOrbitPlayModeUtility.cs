using System;
using System.IO;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [EDITOR] Helpers for Unity Multiplayer Play Mode (MPPM) additional editor instances.
    /// Detects virtual-project clones and warns when server build subtarget mismatches host.
    /// </summary>
    public static class TitanOrbitPlayModeUtility
    {
        const string ServerBuildSubtargetWarning =
            "[TitanOrbitPlayMode] MPPM clone is using a Dedicated SERVER build (buildSubTarget=Server). " +
            "NetCode ghost schemas will not match the main Editor host.\n\n" +
            "Fix: In Window > Play Mode > Scenarios, set the additional instance Multiplayer Role to Client " +
            "(not Server). Then Play from the Main Editor only.";

        /// <summary>True when launched via MPPM --virtual-project-clone (not main editor).</summary>
        public static bool IsMppmAdditionalEditorInstance()
        {
#if UNITY_EDITOR
            // --- MPPM passes --virtual-project-clone to additional editors ---
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "--virtual-project-clone")
                    return true;
            }
#endif
            return false;
        }

        /// <summary>True when MPPM launched this clone with a Dedicated Server build subtarget.</summary>
        public static bool UsesServerBuildSubtarget()
        {
            // --- Parse Unity command line for standalone build role ---
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-standaloneBuildSubtarget" &&
                    string.Equals(args[i + 1], "Server", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Logs error when MPPM clone uses Dedicated Server build (ghost schema mismatch).</summary>
        public static void WarnIfMppmServerBuildClone()
        {
#if UNITY_EDITOR
            // #region agent log
            // H51: prove whether Player 2 still launches with Server subtarget after SystemData Client patch.
            try
            {
                bool clone = IsMppmAdditionalEditorInstance();
                string sub = GetMppmBuildSubtarget();
                int player = GetMppmPlayerNumber();
                string line =
                    "{\"sessionId\":\"6b87b4\",\"runId\":\"basics65\",\"hypothesisId\":\"H51\"," +
                    "\"location\":\"TitanOrbitPlayModeUtility.WarnIfMppmServerBuildClone\"," +
                    "\"message\":\"mppm bootstrap build role\"," +
                    "\"data\":{\"mppmClone\":" + (clone ? "true" : "false") +
                    ",\"mppmPlayer\":" + player +
                    ",\"buildSub\":\"" + sub + "\"" +
                    ",\"serverSub\":" + (UsesServerBuildSubtarget() ? "true" : "false") + "}," +
                    "\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n";
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
                {
                    if (Directory.Exists(Path.Combine(dir, ".git")))
                    {
                        File.AppendAllText(Path.Combine(dir, "debug-6b87b4.log"), line);
                        break;
                    }
                    string parent = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(parent) || parent == dir)
                        break;
                    dir = parent;
                }
            }
            catch
            {
                // ignore debug I/O
            }
            // #endregion

            if (IsMppmAdditionalEditorInstance() && UsesServerBuildSubtarget())
                Debug.LogError(ServerBuildSubtargetWarning);
#endif
        }

        public static string GetMppmBuildSubtarget()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-standaloneBuildSubtarget")
                    return args[i + 1];
            }

            return "Editor";
        }

        /// <summary>1 for main editor; 2+ for MPPM Player N clones (from -name "Player N").</summary>
        public static int GetMppmPlayerNumber()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "-name")
                    continue;

                string name = args[i + 1];
                const string prefix = "Player ";
                if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                    int.TryParse(name.Substring(prefix.Length), out int number) &&
                    number > 0)
                    return number;
            }

            return 1;
        }

        public static TeamId GetSuggestedTeamForMppmPlayer()
        {
            // --- Round-robin TeamA/B/C across MPPM Player 2, 3, … ---
            int playerNumber = GetMppmPlayerNumber();
            int teamIndex = (playerNumber - 1) % 3;
            return teamIndex switch
            {
                0 => TeamId.TeamA,
                1 => TeamId.TeamB,
                _ => TeamId.TeamC,
            };
        }
    }
}
