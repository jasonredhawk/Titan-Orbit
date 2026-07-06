using System;
using TitanOrbit.Diagnostics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Ensures dedicated-server boot runs after the scene and NetCode worlds are live.
    /// Mirrors legacy <c>DedicatedMatchServerBootstrap.Init</c> timing without relying on scene object order.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class TitanOrbitDedicatedServerBootRunner : MonoBehaviour
    {
        static bool s_Created;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
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

        void Start()
        {
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

#if UNITY_SERVER
        void Update()
        {
            var server = ClientServerBootstrap.ServerWorld;
            if (server != null && server.IsCreated)
                server.Update();
        }
#endif

        void OnApplicationQuit()
        {
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
