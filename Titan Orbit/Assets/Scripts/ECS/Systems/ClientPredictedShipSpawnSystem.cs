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
    /// Editor.log 2026-08-10: predicted Instantiates worked on the first Play after compile, then
    /// every later Play (Domain Reload disabled) skipped it — stale
    /// <see cref="LocalShipEntitySeed"/> / flow statics blocked <see cref="Request"/>. Always arm
    /// Pending; prune live seed before skipping Instantiates.
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

        /// <summary>Domain reload.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsSubsystem() => Clear();

        /// <summary>Every Play Mode enter (Domain Reload may be off).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetStaticsBeforeSceneLoad() => Clear();
    }

    /// <summary>
    /// ClientSimulation: drains <see cref="ClientPredictedShipSpawnRequest"/> and Instantiates
    /// the GhostCollection ship prefab with PredictedGhostSpawnRequest so classification can
    /// adopt the server ghost when its snapshot finally arrives.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TeamChoiceResultClientSystem))]
    public partial struct ClientPredictedShipSpawnSystem : ISystem
    {
        /// <summary>Requires GhostCollection so the ship prefab index is valid.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// Instantiates one predicted ship when a TeamChoice request is pending and no live seed exists.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;

            // --- Drop handles from a previous Play Mode (Domain Reload off) ---
            LocalShipEntitySeed.PruneStale(em);

            if (!ClientPredictedShipSpawnRequest.Pending)
                return;

            // --- Live hull already present (GhostReceive or prior predicted Instantiates) ---
            if (LocalShipEntitySeed.HasLiveOwnedShipSeed(em))
            {
                ClientPredictedShipSpawnRequest.Clear();
                return;
            }

            int networkId = ClientPredictedShipSpawnRequest.NetworkId;
            TeamId team = ClientPredictedShipSpawnRequest.Team;
            bool hasSpawnPos = ClientPredictedShipSpawnRequest.HasSpawnPos;
            float3 spawnPos = ClientPredictedShipSpawnRequest.SpawnPos;
            ClientPredictedShipSpawnRequest.Clear();

            if (networkId <= 0 || team == TeamId.None)
                return;

            // --- Resolve client GhostCollection ship prefab ---
            if (!TryResolveClientShipPrefab(em, out Entity shipPrefab))
            {
                Debug.LogError(
                    "[ClientPredictedShipSpawn] No ShipTag prefab in ClientWorld GhostCollection — " +
                    "cannot predicted-spawn hull after TeamChoice.");
                return;
            }

            // --- Spawn pose: server hint, else home ring on client map ---
            if (!hasSpawnPos)
            {
                int hz = 0;
                if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                    hz = tickRate.SimulationTickRate;
                double orbitElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                    ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                    : SystemAPI.Time.ElapsedTime;
                spawnPos = ShipHomeSpawnLogic.FindHomeSpawnPosition(em, team, orbitElapsed);
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
                return;
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

            // --- Seed presentation even while post–TeamChoice suppress is on ---
            LocalShipEntitySeed.NotifyShipInstantiated(em, ship);

            Debug.Log(
                $"[ClientPredictedShipSpawn] Predicted hull Instantiates for networkId={networkId} " +
                $"team={team} at {spawnPos} (awaits GhostSpawn classification when server snapshot arrives).");
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
    }
}
