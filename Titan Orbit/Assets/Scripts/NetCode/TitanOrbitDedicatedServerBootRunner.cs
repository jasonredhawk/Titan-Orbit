using System;
using TitanOrbit.Diagnostics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [NETCODE] Ensures dedicated-server boot runs after scene load and NetCode worlds exist.
    /// Creates <see cref="TitanOrbitSessionManager"/> if missing and calls
    /// <see cref="TitanOrbitSessionManager.EnsureDedicatedBootStarted"/>. GCE/Linux headless entry path.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class TitanOrbitDedicatedServerBootRunner : MonoBehaviour
    {
        static bool s_Created;

        /// <summary>
        /// [UNITY] AfterSceneLoad — spawns persistent boot runner on dedicated server processes only.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            // --- AfterSceneLoad ---
            if (!TitanOrbitDedicatedServerAutoBoot.IsDedicatedServerProcess())
                return;

            if (s_Created)
                return;

            s_Created = true;
            var go = new GameObject(nameof(TitanOrbitDedicatedServerBootRunner));
            DontDestroyOnLoad(go);
            go.AddComponent<TitanOrbitDedicatedServerBootRunner>();
            DedicatedServerFileLog.Append("boot", "BootRunner scheduled AfterSceneLoad.");
            Debug.Log("[TitanOrbitDedicatedServerBootRunner] Scheduled dedicated boot runner.");
        }

        /// <summary>Start — ensure session manager exists, then kick dedicated Relay + lobby boot.</summary>
        void Start()
        {
            // --- Unity lifecycle ---
            DontDestroyOnLoad(gameObject);
            DedicatedServerFileLog.Append("boot", "BootRunner Start — ensuring session manager + boot.");
            Debug.Log("[TitanOrbitDedicatedServerBootRunner] Start — triggering dedicated boot.");

            var session = TitanOrbitSessionManager.Instance;
            if (session == null)
            {
                var root = new GameObject("NceGameRoot");
                DontDestroyOnLoad(root);
                session = root.AddComponent<TitanOrbitSessionManager>();
                Debug.Log("[TitanOrbitDedicatedServerBootRunner] Created NceGameRoot + TitanOrbitSessionManager.");
            }

            session.EnsureDedicatedBootStarted();
        }

        // Server ECS tick: TitanOrbitSessionManager.Update() on UNITY_SERVER builds.

        void OnApplicationQuit()
        {
            // --- OnApplicationQuit ---
            if (TitanOrbitSessionManager.Instance != null &&
                !string.IsNullOrWhiteSpace(TitanOrbitSessionManager.Instance.CurrentLobbyId))
            {
                _ = TitanOrbitSessionManager.Instance.CloseLobbyForNewJoinersAsync(
                    TitanOrbitSessionManager.Instance.CurrentLobbyId,
                    "process_exit");
            }
        }
    }
}
