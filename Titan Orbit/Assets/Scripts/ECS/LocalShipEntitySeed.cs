using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Seeds the local ship entity the frame GhostSpawn Instantiates it.
    /// <para>
    /// After Join Team, Settling stays OFF but <see cref="ClientJoinSettleCache.GhostSpawnBacklog"/>
    /// is true while the ship Instantiates. Bridge lookup is gated — without a seed, presentation
    /// kept <c>hasPose=false</c> at the origin then snapped when the first pose arrived.
    /// </para>
    /// <para>
    /// Race: ship Instantiates while team-suppress is still true → old seed code returned without
    /// storing → suppress cleared later but seed stayed empty → many frames of no pose → camera jump.
    /// Fix: always record a pending owned ship on Instantiates; promote to the live seed once
    /// suppress is off.
    /// </para>
    /// <para>
    /// Recovery: if Instantiates-hook ownership failed (local NetworkId not ready yet) or the seed
    /// was cleared, <see cref="TryRecoverOwnedShip"/> may run a tiny ship query once Instantiates
    /// are idle — never while <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>.
    /// </para>
    /// </summary>
    public static class LocalShipEntitySeed
    {
        /// <summary>Live seed used by ShipVisualSync (only while team control is allowed).</summary>
        public static Entity SeededShip { get; private set; }

        /// <summary>
        /// True when Instantiates already recorded a locally owned ship (seeded or pending).
        /// Used by presentation / spawn-wait UI while ship gathers are still gated.
        /// </summary>
        public static bool HasOwnedShipSeed =>
            SeededShip != Entity.Null || s_PendingOwnedShip != Entity.Null;

        /// <summary>
        /// Owned ship seen at Instantiates even if Join Team confirm had not latched yet.
        /// Promoted into <see cref="SeededShip"/> when suppress clears.
        /// </summary>
        static Entity s_PendingOwnedShip;

        /// <summary>
        /// Ship Instantiates seen before local NetworkId was readable — ownership rechecked later.
        /// </summary>
        static Entity s_UnresolvedOwnershipShip;

        /// <summary>Clears seed on domain reload / leave match.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            SeededShip = Entity.Null;
            s_PendingOwnedShip = Entity.Null;
            s_UnresolvedOwnershipShip = Entity.Null;
        }

        /// <summary>
        /// Called from the GhostSpawn Instantiates hook when a ship ghost finishes Instantiates.
        /// No ship gathers — only inspects this one entity + a tiny NetworkId connection query.
        /// </summary>
        /// <param name="em">Client EntityManager.</param>
        /// <param name="entity">Ship entity that just Instantiated.</param>
        public static void NotifyShipInstantiated(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity))
                return;
            if (!em.HasComponent<ShipTag>(entity))
                return;

            // --- Ownership may lag Instantiates by a frame (local NetworkId not ready) ---
            if (!TryIsLocallyOwnedShip(em, entity, out bool localIdReady))
            {
                if (!localIdReady && em.HasComponent<GhostOwner>(entity))
                {
                    // Retry from TryGetSeededShip / TryRecover once NetworkId exists.
                    s_UnresolvedOwnershipShip = entity;
                    Debug.Log(
                        "[LocalShipEntitySeed] Ship Instantiates before local NetworkId — " +
                        "deferred ownership check.");
                }
                return;
            }

            AcceptOwnedShip(entity);
        }

        /// <summary>
        /// True when a seeded/pending owned ship exists and team control is allowed.
        /// Promotes <see cref="s_PendingOwnedShip"/> once suppress clears. Also retries deferred
        /// ownership when Instantiates raced ahead of NetworkId.
        /// </summary>
        public static bool TryGetSeededShip(EntityManager em, out Entity shipEntity)
        {
            shipEntity = Entity.Null;

            // --- Do not drive camera before Join Team / resume ---
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            // --- Retry Instantiates that arrived before NetworkId was readable ---
            TryResolveDeferredOwnership(em);

            // --- Promote pending Instantiates that happened under suppress ---
            if (SeededShip == Entity.Null &&
                s_PendingOwnedShip != Entity.Null &&
                em.Exists(s_PendingOwnedShip) &&
                em.HasComponent<ShipTag>(s_PendingOwnedShip))
            {
                SeededShip = s_PendingOwnedShip;
            }

            shipEntity = SeededShip;
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
            {
                shipEntity = Entity.Null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Tiny owned-ship gather used only when Instantiates are idle and TeamChoice already
        /// confirmed — recovers spawn-wait if the Instantiates-hook seed was missed.
        /// Must not run while <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>.
        /// </summary>
        /// <param name="em">Client EntityManager.</param>
        /// <returns>True when a seed was recovered or already present.</returns>
        public static bool TryRecoverOwnedShip(EntityManager em)
        {
            if (HasOwnedShipSeed)
                return TryGetSeededShip(em, out _);

            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

            // [TITAN-ORBIT] Never ToEntityArray ships during Settling / GhostSpawnBacklog /
            // post–TeamChoice hold — that is the Crash!!! window.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            TryResolveDeferredOwnership(em);
            if (HasOwnedShipSeed)
                return TryGetSeededShip(em, out _);

            int localId = ReadLocalNetworkId(em);
            if (localId <= 0)
                return false;

            // --- Tiny ship set only (not map bodies) ---
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var ships = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);

            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != localId)
                    continue;
                if (!em.Exists(ships[i]))
                    continue;

                AcceptOwnedShip(ships[i]);
                Debug.Log(
                    "[LocalShipEntitySeed] Recovered owned ship seed after TeamChoice " +
                    $"(networkId={localId}, entity={ships[i].Index}).");
                return true;
            }

            return false;
        }

        /// <summary>Clears seed when the local ship is confirmed gone.</summary>
        public static void Clear()
        {
            SeededShip = Entity.Null;
            s_PendingOwnedShip = Entity.Null;
            s_UnresolvedOwnershipShip = Entity.Null;
        }

        /// <summary>Records ownership match and arms the short Instantiates hold.</summary>
        static void AcceptOwnedShip(Entity entity)
        {
            s_PendingOwnedShip = entity;
            s_UnresolvedOwnershipShip = Entity.Null;
            if (!ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                SeededShip = entity;

            // --- Arm ship Instantiates hold (not map Instantiates) ---
            // [TITAN-ORBIT] Placeholder is often gone the same frame Instantiates succeeds. Without
            // a short hold, ship WithEntityAccess / EnsureShipProxies fail-open → Crash!!!.
            ClientJoinSettleCache.ArmPostShipInstantiateHold();
        }

        /// <summary>Promotes <see cref="s_UnresolvedOwnershipShip"/> once NetworkId is ready.</summary>
        static void TryResolveDeferredOwnership(EntityManager em)
        {
            if (s_UnresolvedOwnershipShip == Entity.Null)
                return;
            if (!em.Exists(s_UnresolvedOwnershipShip) ||
                !em.HasComponent<ShipTag>(s_UnresolvedOwnershipShip))
            {
                s_UnresolvedOwnershipShip = Entity.Null;
                return;
            }

            if (!TryIsLocallyOwnedShip(em, s_UnresolvedOwnershipShip, out bool localIdReady))
            {
                if (!localIdReady)
                    return;
                // Local id ready but not our ship — drop.
                s_UnresolvedOwnershipShip = Entity.Null;
                return;
            }

            AcceptOwnedShip(s_UnresolvedOwnershipShip);
        }

        /// <summary>
        /// Ownership check with a ready flag so Instantiates can defer when NetworkId is missing.
        /// </summary>
        static bool TryIsLocallyOwnedShip(EntityManager em, Entity entity, out bool localIdReady)
        {
            localIdReady = false;
            if (!em.HasComponent<GhostOwner>(entity))
                return false;

            int ownerId = em.GetComponentData<GhostOwner>(entity).NetworkId;
            int localId = ReadLocalNetworkId(em);
            localIdReady = localId > 0;
            return localIdReady && ownerId == localId;
        }

        /// <summary>Local client's NetworkId from the in-game connection entity (tiny query).</summary>
        static int ReadLocalNetworkId(EntityManager em)
        {
            using var ids = em.CreateEntityQuery(
                    typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : -1;
        }
    }
}
