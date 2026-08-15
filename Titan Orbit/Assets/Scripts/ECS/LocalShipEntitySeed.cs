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
        /// <para>
        /// [TITAN-ORBIT] With Domain Reload disabled this can stay non-null across Play Mode
        /// while the entity is already destroyed — prefer <see cref="HasLiveOwnedShipSeed"/> /
        /// <see cref="PruneStale"/> before trusting this for spawn decisions.
        /// </para>
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

        /// <summary>
        /// Clears seed on domain reload. Also use <see cref="BeforeSceneLoad"/> — Editor may run
        /// with Domain Reload disabled so SubsystemRegistration does not re-fire each Play.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsSubsystem() => Clear();

        /// <summary>
        /// [UNITY] Runs every Play Mode enter even when Domain Reload is disabled
        /// (Editor.log: "Entering Playmode with Reload Domain disabled").
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetStaticsBeforeSceneLoad() => Clear();

        /// <summary>
        /// True when a seeded/pending handle still exists in <paramref name="em"/> with ShipTag.
        /// Prunes stale handles left over from a prior Play Mode (Domain Reload off).
        /// </summary>
        /// <param name="em">Client EntityManager for Exists checks.</param>
        public static bool HasLiveOwnedShipSeed(EntityManager em)
        {
            PruneStale(em);
            // GhostReceive can Instantiates one frame before GhostOwner.NetworkId is readable.
            // Promote that deferred handle without a ship gather (safe during Join Team suppress).
            TryResolveDeferredOwnership(em);
            return HasOwnedShipSeed;
        }

        /// <summary>
        /// Owned-ship entity even while team suppress is on (pending Instantiates under Join Team).
        /// Does not promote pending → SeededShip and does not require Confirm.
        /// </summary>
        /// <param name="em">Client EntityManager.</param>
        /// <param name="ship">Pending or live seed, or Null.</param>
        /// <returns>True when a live ShipTag handle exists.</returns>
        public static bool TryGetOwnedShipEntityUnchecked(EntityManager em, out Entity ship)
        {
            PruneStale(em);
            ship = SeededShip != Entity.Null ? SeededShip : s_PendingOwnedShip;
            if (ship == Entity.Null || !em.Exists(ship) || !em.HasComponent<ShipTag>(ship))
            {
                ship = Entity.Null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Drops SeededShip / pending / unresolved handles that no longer exist in this world.
        /// Safe to call every frame from ClientWorld systems.
        /// </summary>
        /// <param name="em">Client EntityManager.</param>
        public static void PruneStale(EntityManager em)
        {
            if (SeededShip != Entity.Null &&
                (!em.Exists(SeededShip) || !em.HasComponent<ShipTag>(SeededShip)))
                SeededShip = Entity.Null;

            if (s_PendingOwnedShip != Entity.Null &&
                (!em.Exists(s_PendingOwnedShip) || !em.HasComponent<ShipTag>(s_PendingOwnedShip)))
                s_PendingOwnedShip = Entity.Null;

            if (s_UnresolvedOwnershipShip != Entity.Null &&
                (!em.Exists(s_UnresolvedOwnershipShip) ||
                 !em.HasComponent<ShipTag>(s_UnresolvedOwnershipShip)))
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

            // --- Ownership may lag Instantiates by a frame ---
            // [TITAN-ORBIT] Two races (Editor.log 2026-08-10):
            // 1) local NetworkId not ready yet
            // 2) GhostOwner.NetworkId still 0 on the first Instantiates frame (snapshot lag).
            //    Dropping the entity forever left hasSeed=false while InstantiatesSession
            //    already counted the hull.
            if (!TryIsLocallyOwnedShip(em, entity, out bool localIdReady, out int ownerId))
            {
                if (em.HasComponent<GhostOwner>(entity) &&
                    (!localIdReady || ownerId == 0))
                {
                    // Retry from TryGetSeededShip / TryRecover once NetworkId + owner resolve.
                    s_UnresolvedOwnershipShip = entity;
                    Debug.Log(
                        "[LocalShipEntitySeed] Ship Instantiates before ownership ready — deferred " +
                        $"(localIdReady={localIdReady}, ownerId={ownerId}).");
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

        /// <summary>
        /// Accepts a known-local hull without re-reading <see cref="GhostOwner"/>.
        /// Leftover helper — Join Team no longer Instantiates a client predicted ship.
        /// </summary>
        /// <param name="entity">ClientWorld ship entity that this player owns.</param>
        public static void ForceAcceptOwnedShip(Entity entity)
        {
            if (entity == Entity.Null)
                return;
            AcceptOwnedShip(entity);
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

            if (!TryIsLocallyOwnedShip(
                    em, s_UnresolvedOwnershipShip, out bool localIdReady, out int ownerId))
            {
                // Still waiting for local id or GhostOwner.NetworkId to leave 0.
                if (!localIdReady || ownerId == 0)
                    return;

                // Local id ready, owner set, but not our ship — drop.
                s_UnresolvedOwnershipShip = Entity.Null;
                return;
            }

            AcceptOwnedShip(s_UnresolvedOwnershipShip);
        }

        /// <summary>
        /// Ownership check with ready / owner-id outs so Instantiates can defer when NetworkId
        /// or GhostOwner is still settling.
        /// </summary>
        /// <param name="em">Client EntityManager.</param>
        /// <param name="entity">Candidate ship.</param>
        /// <param name="localIdReady">True when this machine's NetworkId is readable (&gt; 0).</param>
        /// <param name="ownerId">GhostOwner.NetworkId on the entity (0 if missing).</param>
        /// <returns>True when the entity is owned by the local NetworkId.</returns>
        static bool TryIsLocallyOwnedShip(
            EntityManager em,
            Entity entity,
            out bool localIdReady,
            out int ownerId)
        {
            localIdReady = false;
            ownerId = 0;
            if (!em.HasComponent<GhostOwner>(entity))
                return false;

            ownerId = em.GetComponentData<GhostOwner>(entity).NetworkId;
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
