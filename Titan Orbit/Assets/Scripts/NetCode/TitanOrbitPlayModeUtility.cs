using System;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>Helpers for Unity Multiplayer Play Mode (MPPM) additional editor instances.</summary>
    public static class TitanOrbitPlayModeUtility
    {
        const string ServerBuildSubtargetWarning =
            "[TitanOrbitPlayMode] MPPM clone is using a Dedicated SERVER build (buildSubTarget=Server). " +
            "NetCode ghost schemas will not match the main Editor host.\n\n" +
            "Fix: In Window > Play Mode > Scenarios, set the additional instance Multiplayer Role to Client " +
            "(not Server). Then Play from the Main Editor only.";

        public static bool IsMppmAdditionalEditorInstance()
        {
#if UNITY_EDITOR
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
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-standaloneBuildSubtarget" &&
                    string.Equals(args[i + 1], "Server", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

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

        public static TeamId GetSuggestedTeamForMppmPlayer()
        {
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
