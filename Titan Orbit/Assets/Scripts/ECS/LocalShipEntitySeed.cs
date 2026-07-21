using System;
using System.IO;
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
    /// kept <c>hasPose=false</c> at the origin then snapped ~113m (debug-604d3d POSE_GAINED).
    /// </para>
    /// <para>
    /// Race (604d3d frame 903→949): ship Instantiates while team-suppress is still true → old seed
    /// code returned without storing → suppress cleared later but seed stayed empty → 35 frames of
    /// no pose → !!CAM_JUMP + !!RETILE. Fix: always record a pending owned ship on Instantiates;
    /// promote to the live seed once suppress is off.
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
            {
                // #region agent log
                WriteSeedLog("SHIP_INSTANTIATE_NOT_OWNED", entity, em);
                // #endregion
                return;
            }

            // --- Always remember ownership match (even during team suppress) ---
            // [TITAN-ORBIT] Suppress may still be true the Instantiates frame of TeamChoiceResult;
            // discarding here caused the 113m POSE_GAINED blink (debug-604d3d).
            s_PendingOwnedShip = entity;
            bool suppress = ClientTeamFlowState.ShouldSuppressLocalPlayerControl();
            if (!suppress)
                SeededShip = entity;

            // #region agent log
            WriteSeedLog(
                suppress ? "SHIP_INSTANTIATE_PENDING" : "SHIP_INSTANTIATE_SEEDED",
                entity,
                em);
            // #endregion
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
                // #region agent log
                WriteSeedLog("SHIP_SEED_PROMOTED", SeededShip, em);
                // #endregion
            }

            shipEntity = SeededShip;
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
            {
                shipEntity = Entity.Null;
                return false;
            }

            if (!em.HasComponent<ShipTag>(shipEntity))
            {
                shipEntity = Entity.Null;
                return false;
            }

            return true;
        }

        /// <summary>Clears the seed (despawn / leave).</summary>
        public static void Clear()
        {
            SeededShip = Entity.Null;
            s_PendingOwnedShip = Entity.Null;
        }

        /// <summary>True when this ship ghost is owned by the local connection.</summary>
        static bool IsLocallyOwnedShip(EntityManager em, Entity entity)
        {
            // --- Path 1: NetCode enableable local-owner flag (must be enabled) ---
            if (em.HasComponent<GhostOwnerIsLocal>(entity) &&
                em.IsComponentEnabled<GhostOwnerIsLocal>(entity))
                return true;

            // --- Path 2: GhostOwner.NetworkId matches our connection ---
            if (!em.HasComponent<GhostOwner>(entity))
                return false;

            int localId = ReadLocalNetworkId(em);
            if (localId <= 0)
                return false;

            return em.GetComponentData<GhostOwner>(entity).NetworkId == localId;
        }

        /// <summary>
        /// Tiny connection query only — not a ship gather. Safe during GhostSpawnBacklog.
        /// </summary>
        static int ReadLocalNetworkId(EntityManager em)
        {
            using var ids = em.CreateEntityQuery(
                    typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : -1;
        }

        // #region agent log
        /// <summary>NDJSON for debug-604d3d — hypothesis F (seed race / POSE_GAINED jump).</summary>
        static void WriteSeedLog(string message, Entity entity, EntityManager em)
        {
            try
            {
                int localId = ReadLocalNetworkId(em);
                int ownerId = em.HasComponent<GhostOwner>(entity)
                    ? em.GetComponentData<GhostOwner>(entity).NetworkId
                    : -1;
                bool suppress = ClientTeamFlowState.ShouldSuppressLocalPlayerControl();
                string path = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", "..", "debug-604d3d.log"));
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line =
                    "{\"sessionId\":\"604d3d\",\"hypothesisId\":\"F\",\"location\":\"LocalShipEntitySeed\"," +
                    "\"message\":\"" + message + "\",\"data\":{" +
                    "\"shipIndex\":" + entity.Index +
                    ",\"localId\":" + localId +
                    ",\"ownerId\":" + ownerId +
                    ",\"suppress\":" + (suppress ? "true" : "false") +
                    ",\"backlog\":" + (ClientJoinSettleCache.GhostSpawnBacklog ? "true" : "false") +
                    ",\"frame\":" + Time.frameCount +
                    "},\"timestamp\":" + ts + ",\"runId\":\"post-fix\"}\n";
                File.AppendAllText(path, line);
            }
            catch
            {
                // Diagnostic only.
            }
        }
        // #endregion
    }
}
