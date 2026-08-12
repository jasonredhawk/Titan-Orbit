using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Queues a ClientWorld predicted ship Instantiates after TeamChoice succeeds.
    /// <para>
    /// [NETCODE] OwnerPredicted hulls may be Instantiated on the client with
    /// <see cref="PredictedGhostSpawnRequest"/> (disabled on the prefab).
    /// <see cref="PredictedGhostSpawnSystem"/> initializes them (ghostId=-1) and
    /// <see cref="GhostSpawnClassificationSystem"/> matches the server snapshot later.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Debug 1af271 / Editor.log 2026-08-10: server assigns ghostId and runs
    /// people transports, but ClientWorld InstantiatesSession stays at map meta-N with
    /// placeholders=0 — GhostReceive never sees the hull. Without a client predicted Instantiates,
    /// Join Team soft-locks on “Spawning your ship…”. Instantiating on ClientWorld (not ServerWorld)
    /// with PredictedGhostSpawnRequest avoids the invalid-ghost / NetworkTime freeze of the old
    /// LocalHostPredictedShipSpawn hack.
    /// </para>
    /// <para>
    /// Editor.log 2026-08-12: after waiting on Join Team, Request stayed Pending for 240 frames
    /// with no Instantiates — <see cref="ClientPredictedShipSpawnSystem"/> was gated on
    /// RequireForUpdate and often ran a full frame after Local Host Result. Drain is now a static
    /// <see cref="TryDrainPending"/> callable from Init (deferred Confirm) and from Result apply.
    /// </para>
    /// </summary>
    public static class ClientPredictedShipSpawnRequest
    {
        /// <summary>True when a TeamChoice success asked for a predicted hull Instantiates.</summary>
        public static bool Pending { get; private set; }

        /// <summary>Local NetworkId that will own the hull.</summary>
        public static int NetworkId { get; private set; }

        /// <summary>Team assigned by the server.</summary>
        public static TeamId Team { get; private set; }

        /// <summary>
        /// Preferred spawn pose (server Local Host). When zero, the client system finds home ring.
        /// </summary>
        public static float3 SpawnPos { get; private set; }

        /// <summary>True when <see cref="SpawnPos"/> came from the server spawn.</summary>
        public static bool HasSpawnPos { get; private set; }

        /// <summary>
        /// Last predicted hull Instantiates from this request path (not a gather — one handle).
        /// Blocks duplicate Instantiates when seed was pruned after ghost classification.
        /// </summary>
        static Entity s_LastPredictedHull;

        /// <summary>
        /// Arms a one-shot ClientWorld Instantiates after TeamChoice success.
        /// Safe to call from ServerWorld Local Host mirror or ClientWorld RPC handler.
        /// Always sets Pending — do not gate on <see cref="LocalShipEntitySeed.HasOwnedShipSeed"/>
        /// (stale handles across Domain-Reload-off Play Modes blocked spawn forever).
        /// </summary>
        /// <param name="networkId">Owning NetCode id.</param>
        /// <param name="team">Assigned team.</param>
        /// <param name="spawnPos">Server spawn when known; ignore when <paramref name="hasSpawnPos"/> is false.</param>
        /// <param name="hasSpawnPos">True when <paramref name="spawnPos"/> is authoritative.</param>
        public static void Request(int networkId, TeamId team, float3 spawnPos, bool hasSpawnPos)
        {
            if (networkId <= 0 || team == TeamId.None)
                return;

            // --- Prefer keeping an authoritative spawn pose ---
            // [TITAN-ORBIT] Local Host arms hasSpawnPos=True first; TeamChoiceResult / DeferredConfirm
            // later re-arm with false. Do not clobber a good pose while still Pending.
            if (Pending && HasSpawnPos && !hasSpawnPos && networkId == NetworkId && team == Team)
            {
                Debug.Log(
                    "[ClientPredictedShipSpawn] Request ignored (keep pending server spawn pose) " +
                    $"networkId={networkId} team={team}.");
                return;
            }

            Pending = true;
            NetworkId = networkId;
            Team = team;
            SpawnPos = spawnPos;
            HasSpawnPos = hasSpawnPos;
            Debug.Log(
                $"[ClientPredictedShipSpawn] Request armed networkId={networkId} team={team} " +
                $"hasSpawnPos={hasSpawnPos}.");
        }

        /// <summary>Clears the queue (consumed, domain reload, leave match).</summary>
        public static void Clear()
        {
            Pending = false;
            NetworkId = 0;
            Team = TeamId.None;
            SpawnPos = float3.zero;
            HasSpawnPos = false;
        }

        /// <summary>
        /// Drops static handles across Play Mode (Domain Reload off).
        /// Does not clear a live Pending request mid-match.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetStaticsBeforeSceneLoad()
        {
            Clear();
            s_LastPredictedHull = Entity.Null;
        }

        /// <summary>
        /// Instantiates the pending predicted hull on <paramref name="em"/> if armed.
        /// Safe to call from Init, Simulation, or Local Host Result apply (ClientWorld EM only).
        /// </summary>
        /// <param name="em">ClientWorld EntityManager.</param>
        /// <returns>True when a hull was Instantiated or a live seed already existed.</returns>
        public static bool TryDrainPending(EntityManager em)
        {
            // --- Nothing queued ---
            if (!Pending)
                return false;

            // --- Drop handles from a previous Play Mode (Domain Reload off) ---
            LocalShipEntitySeed.PruneStale(em);
            if (s_LastPredictedHull != Entity.Null &&
                (!em.Exists(s_LastPredictedHull) || !em.HasComponent<ShipTag>(s_LastPredictedHull)))
                s_LastPredictedHull = Entity.Null;

            // --- Live hull already present (GhostReceive or prior predicted Instantiates) ---
            if (LocalShipEntitySeed.HasLiveOwnedShipSeed(em))
            {
                Clear();
                return true;
            }

            // --- Same-session predicted hull still alive (seed pruned after classification) ---
            // [TITAN-ORBIT] Without this, DeferredConfirm re-armed Instantiates at (0,0,0) while
            // the good ring hull still existed — player stuck off-map / unable to drive the real ship.
            if (s_LastPredictedHull != Entity.Null)
            {
                LocalShipEntitySeed.ForceAcceptOwnedShip(s_LastPredictedHull);
                Clear();
                Debug.Log(
                    "[ClientPredictedShipSpawn] Re-seeded existing predicted hull — skipped duplicate Instantiates.");
                return true;
            }

            int networkId = NetworkId;
            TeamId team = Team;
            bool hasSpawnPos = HasSpawnPos;
            float3 spawnPos = SpawnPos;
            // Clear before Instantiates so a throw cannot leave Pending stuck forever.
            Clear();

            if (networkId <= 0 || team == TeamId.None)
                return false;

            // --- Resolve client GhostCollection ship prefab ---
            if (!TryResolveClientShipPrefab(em, out Entity shipPrefab))
            {
                Debug.LogError(
                    "[ClientPredictedShipSpawn] No ShipTag prefab in ClientWorld GhostCollection — " +
                    "cannot predicted-spawn hull after TeamChoice.");
                return false;
            }

            // --- Spawn pose: server hint, else home ring on client map ---
            if (!hasSpawnPos)
            {
                double orbitElapsed = 0.0;
                using (var timeQ = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>()))
                {
                    if (!timeQ.IsEmptyIgnoreFilter)
                    {
                        int hz = 0;
                        using (var rateQ = em.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>()))
                        {
                            if (!rateQ.IsEmptyIgnoreFilter)
                                hz = rateQ.GetSingleton<ClientServerTickRate>().SimulationTickRate;
                        }

                        orbitElapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(
                            timeQ.GetSingleton<NetworkTime>(), hz, includeTickFraction: false);
                    }
                }

                spawnPos = ShipHomeSpawnLogic.FindHomeSpawnPosition(em, team, orbitElapsed);

                // --- Never Instantiates at unresolved origin ---
                // [TITAN-ORBIT] Home ghosts may not be ready yet. Re-arm Pending and retry next tick
                // instead of spawning a second hull at (0,0,0) that steals CommandTarget.
                if (math.lengthsq(spawnPos) < 0.0001f)
                {
                    Debug.LogWarning(
                        "[ClientPredictedShipSpawn] Home ring pose not ready — re-arming Pending " +
                        $"(networkId={networkId} team={team}).");
                    Request(networkId, team, float3.zero, hasSpawnPos: false);
                    return false;
                }
            }

            // --- Instantiates on ClientWorld only (PredictedGhostSpawnRequest stays disabled) ---
            // [NETCODE] Client predicted prefabs bake PredictedGhostSpawnRequest disabled.
            // PredictedGhostSpawnSystem consumes WithDisabled&lt;PredictedGhostSpawnRequest&gt;.
            Entity ship = em.Instantiate(shipPrefab);

            if (!em.HasComponent<PredictedGhostSpawnRequest>(ship))
            {
                Debug.LogError(
                    "[ClientPredictedShipSpawn] Ship prefab lacks PredictedGhostSpawnRequest — " +
                    "destroying Instantiates (would log invalid ghostId==0). Check OwnerPredicted bake.");
                em.DestroyEntity(ship);
                return false;
            }

            if (em.HasComponent<GhostOwner>(ship))
                em.SetComponentData(ship, new GhostOwner { NetworkId = networkId });
            else
                em.AddComponentData(ship, new GhostOwner { NetworkId = networkId });

            em.SetComponentData(ship, LocalTransform.FromPosition(spawnPos));

            if (em.HasComponent<ShipState>(ship))
            {
                var shipState = em.GetComponentData<ShipState>(ship);
                shipState.Team = team;
                shipState.Health = 100f;
                shipState.MaxHealth = 100f;
                shipState.ShipLevel = 1;
                shipState.GemCapacity = 50f;
                shipState.CurrentEnergy = 50f;
                shipState.MaxEnergy = 50f;
                shipState.PeopleCapacity = 10;
                shipState.AwaitingTeamSelection = false;
                em.SetComponentData(ship, shipState);
            }

            // --- Seed with known NetworkId (do not trust GhostOwner readback) ---
            // [TITAN-ORBIT] Editor.log 2026-08-12: NotifyShipInstantiated saw ownerId=0 right after
            // SetComponentData(NetworkId=1) — seed never latched, Confirm waited 240 frames empty.
            LocalShipEntitySeed.ForceAcceptOwnedShip(ship);
            s_LastPredictedHull = ship;

            Debug.Log(
                $"[ClientPredictedShipSpawn] Predicted hull Instantiates for networkId={networkId} " +
                $"team={team} at {spawnPos} (awaits GhostSpawn classification when server snapshot arrives).");
            return true;
        }

        /// <summary>
        /// Finds the ClientWorld GhostCollection prefab with <see cref="ShipTag"/>.
        /// Prefers GamePrefabs.Ship when present in this world.
        /// </summary>
        static bool TryResolveClientShipPrefab(EntityManager em, out Entity shipPrefab)
        {
            shipPrefab = Entity.Null;

            Entity gamePrefabsShip = Entity.Null;
            using (var gpQ = em.CreateEntityQuery(ComponentType.ReadOnly<GamePrefabs>()))
            {
                if (!gpQ.IsEmptyIgnoreFilter)
                {
                    var prefs = gpQ.GetSingleton<GamePrefabs>();
                    if (prefs.Ship != Entity.Null && em.Exists(prefs.Ship))
                        gamePrefabsShip = prefs.Ship;
                }
            }

            using var collectionQuery = em.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
            if (collectionQuery.IsEmptyIgnoreFilter)
            {
                shipPrefab = gamePrefabsShip;
                return shipPrefab != Entity.Null;
            }

            Entity collectionEntity = collectionQuery.GetSingletonEntity();
            if (!em.HasBuffer<GhostCollectionPrefab>(collectionEntity))
            {
                shipPrefab = gamePrefabsShip;
                return shipPrefab != Entity.Null;
            }

            GhostType targetType = default;
            bool hasTargetType = gamePrefabsShip != Entity.Null && em.HasComponent<GhostType>(gamePrefabsShip);
            if (hasTargetType)
                targetType = em.GetComponentData<GhostType>(gamePrefabsShip);

            var buffer = em.GetBuffer<GhostCollectionPrefab>(collectionEntity, isReadOnly: true);
            Entity shipTagFallback = Entity.Null;
            for (int i = 0; i < buffer.Length; i++)
            {
                Entity candidate = buffer[i].GhostPrefab;
                if (candidate == Entity.Null || !em.Exists(candidate))
                    continue;

                if (gamePrefabsShip != Entity.Null && candidate == gamePrefabsShip)
                {
                    shipPrefab = candidate;
                    return true;
                }

                if (hasTargetType &&
                    em.HasComponent<GhostType>(candidate) &&
                    em.GetComponentData<GhostType>(candidate) == targetType)
                {
                    shipPrefab = candidate;
                    return true;
                }

                if (shipTagFallback == Entity.Null && em.HasComponent<ShipTag>(candidate))
                    shipTagFallback = candidate;
            }

            if (shipTagFallback != Entity.Null)
            {
                shipPrefab = shipTagFallback;
                return true;
            }

            shipPrefab = gamePrefabsShip;
            return shipPrefab != Entity.Null;
        }

        /// <summary>Domain reload — also drops last predicted hull handle.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsSubsystem()
        {
            Clear();
            s_LastPredictedHull = Entity.Null;
        }
    }

    /// <summary>
    /// ClientSimulation: drains <see cref="ClientPredictedShipSpawnRequest"/> each sim tick.
    /// Prefer <see cref="ClientPredictedShipSpawnRequest.TryDrainPending"/> from Init / Result too —
    /// Local Host Result is applied on ServerWorld after ClientSimulation may already have run.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TeamChoiceResultClientSystem))]
    public partial struct ClientPredictedShipSpawnSystem : ISystem
    {
        /// <summary>
        /// No RequireForUpdate — a missing GhostCollection singleton used to disable this system
        /// entirely while Request stayed Pending (Join Team late-click hang).
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
        }

        /// <summary>
        /// Instantiates one predicted ship when a TeamChoice request is pending and no live seed exists.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            ClientPredictedShipSpawnRequest.TryDrainPending(state.EntityManager);
        }
    }
}
