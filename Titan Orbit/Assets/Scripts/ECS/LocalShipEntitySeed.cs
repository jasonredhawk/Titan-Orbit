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
    /// </summary>
    public static class LocalShipEntitySeed
    {
        /// <summary>Live seed used by ShipVisualSync (only while team control is allowed).</summary>
        public static Entity SeededShip { get; private set; }

        /// <summary>
        /// Owned ship seen at Instantiates even if Join Team confirm had not latched yet.
        /// Promoted into <see cref="SeededShip"/> when suppress clears.
        /// </summary>
        static Entity s_PendingOwnedShip;

        /// <summary>Clears seed on domain reload / leave match.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            SeededShip = Entity.Null;
            s_PendingOwnedShip = Entity.Null;
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

            if (!IsLocallyOwnedShip(em, entity))
                return;

            // --- Always remember ownership match (even during team suppress) ---
            // [TITAN-ORBIT] Suppress may still be true the Instantiates frame of TeamChoiceResult;
            // discarding here caused a late POSE_GAINED camera jump.
            s_PendingOwnedShip = entity;
            if (!ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                SeededShip = entity;
        }

        /// <summary>
        /// True when a seeded/pending owned ship exists and team control is allowed.
        /// Promotes <see cref="s_PendingOwnedShip"/> once suppress clears.
        /// </summary>
        public static bool TryGetSeededShip(EntityManager em, out Entity shipEntity)
        {
            shipEntity = Entity.Null;

            // --- Do not drive camera before Join Team / resume ---
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;

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

        /// <summary>Clears seed when the local ship is confirmed gone.</summary>
        public static void Clear()
        {
            SeededShip = Entity.Null;
            s_PendingOwnedShip = Entity.Null;
        }

        /// <summary>True when this ship ghost belongs to the local NetworkId connection.</summary>
        static bool IsLocallyOwnedShip(EntityManager em, Entity entity)
        {
            if (!em.HasComponent<GhostOwner>(entity))
                return false;

            int ownerId = em.GetComponentData<GhostOwner>(entity).NetworkId;
            int localId = ReadLocalNetworkId(em);
            return localId > 0 && ownerId == localId;
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
