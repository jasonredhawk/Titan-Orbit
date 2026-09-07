using System;
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
            "[TitanOrbitPlayMode] This extra Editor was launched with -standaloneBuildSubtarget Server. " +
            "UNITY_SERVER is defined, so NetCode ghost schemas will not match the main Editor.\n\n" +
            "Player 2's Scenarios Multiplayer Role can already be Client — this flag is copied from " +
            "the Main Editor build platform, not from that dropdown.\n\n" +
            "Fix: File > Build Profiles → Windows Player (not Dedicated Server, not WebGL). " +
            "Then in Window > Play Mode > Scenarios, toggle Player 2 off and on so the clone relaunches. " +
            "Play from the Main Editor only.";

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

        /// <summary>
        /// PlayerPrefs key unique to this Editor instance. MPPM clones share the same registry
        /// store as the main Editor — without a suffix, Player 2 typing a name overwrites Player 1.
        /// </summary>
        public static string GetInstancePlayerPrefsKey(string baseKey)
        {
            if (string.IsNullOrEmpty(baseKey))
                return baseKey;

#if UNITY_EDITOR
            if (IsMppmAdditionalEditorInstance())
                return baseKey + "_P" + GetMppmPlayerNumber();
#endif
            return baseKey;
        }

        /// <summary>Default Main Menu name for this instance ("Player 2" on the MPPM clone).</summary>
        public static string GetInstanceDefaultDisplayName(string fallback)
        {
#if UNITY_EDITOR
            if (IsMppmAdditionalEditorInstance())
                return "Player " + GetMppmPlayerNumber();
#endif
            return fallback;
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
