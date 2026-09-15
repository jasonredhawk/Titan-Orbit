using TitanOrbit.ECS;
using TitanOrbit.Services;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client helper: when the local player owns Orbit Unlocked, automatically claim the
    /// match +1 loadout slot so they never see WATCH AD / UNLOCK.
    /// <para>
    /// Uses the existing <see cref="ClaimRewardedBonusSlotCommand"/> RPC (or a Local Host
    /// write). No new ghost field. Throttled — not a per-tick ship gather. Skips while
    /// <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> unless the seeded
    /// local hull is already known.
    /// </para>
    /// Dedicated server does not run this component. Server still trusts the RPC in v1,
    /// same as the rewarded-ad path.
    /// </summary>
    public sealed class TitanOrbitOrbitUnlockedBonusSlotGrant : MonoBehaviour
    {
        const string HostObjectName = "TitanOrbitOrbitUnlockedGrant";
        const float CheckIntervalSeconds = 0.5f;
        const float RetrySeconds = 2f;

        float _nextCheckUnscaled;
        Entity _grantedShip;
        Entity _attemptedShip;
        float _attemptedAtUnscaled;

        /// <summary>
        /// [UNITY] After the first scene loads, spawn a DontDestroyOnLoad host.
        /// Independent of the IAP services object so attach order cannot miss.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureHost()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#endif
            if (FindFirstObjectByType<TitanOrbitOrbitUnlockedBonusSlotGrant>() != null)
                return;

            var go = new GameObject(HostObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<TitanOrbitOrbitUnlockedBonusSlotGrant>();
        }

        /// <summary>
        /// Throttled check. Cheap when not in a match or when the entitlement is off.
        /// </summary>
        void Update()
        {
            if (Time.unscaledTime < _nextCheckUnscaled)
                return;
            _nextCheckUnscaled = Time.unscaledTime + CheckIntervalSeconds;
            TryGrantIfNeeded();
        }

        /// <summary>
        /// If owned and the local ship still has 0 bonus slots, fire one claim.
        /// Retries after a short wait if the ghost has not applied yet.
        /// </summary>
        void TryGrantIfNeeded()
        {
            if (!TitanOrbitEntitlements.IsOrbitUnlockedOwned)
                return;
            if (!EcsGameBridge.IsNetworkInGame())
            {
                _grantedShip = Entity.Null;
                _attemptedShip = Entity.Null;
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            // --- Resolve local hull without a full ship gather ---
            // [TITAN-ORBIT] TryGetLocalShipEntityTagged uses LocalPlayerShipTag or the
            // seeded entity during join Instantiates — never ToEntityArray while gated.
            if (!EcsGameBridge.TryGetLocalShipEntityTagged(world, out Entity ship) ||
                ship == Entity.Null)
                return;

            var em = world.EntityManager;
            if (!em.Exists(ship) || !em.HasComponent<ShipLoadoutState>(ship))
                return;

            var loadout = em.GetComponentData<ShipLoadoutState>(ship);
            if (loadout.LoadoutBonusSlots > 0)
            {
                _grantedShip = ship;
                return;
            }

            if (_grantedShip == ship)
                return;

            // Already sent a claim for this hull — wait for the ghost before retrying.
            if (_attemptedShip == ship &&
                Time.unscaledTime - _attemptedAtUnscaled < RetrySeconds)
                return;

            ClaimBonusSlot();
            _attemptedShip = ship;
            _attemptedAtUnscaled = Time.unscaledTime;
        }

        /// <summary>
        /// Same write path as the orbit-menu UNLOCK button: Local Host mutates
        /// ServerWorld immediately; a dedicated client sends an RPC.
        /// </summary>
        static void ClaimBonusSlot()
        {
            // --- Local Host ---
            // [NETCODE] Editor / listen-server owns ServerWorld — skip the RPC hop.
            if (EcsGameBridge.IsLocalHost())
            {
                var server = EcsGameBridge.ServerWorld;
                if (server == null || !server.IsCreated)
                    return;

                int networkId = EcsGameBridge.GetLocalNetworkId();
                if (networkId <= 0)
                    return;

                MoonOrbitStoreSystem.TryClaimRewardedBonusSlotForNetworkId(
                    server.EntityManager, networkId, out _);
                return;
            }

            // --- Dedicated client ---
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new ClaimRewardedBonusSlotCommand());
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}
