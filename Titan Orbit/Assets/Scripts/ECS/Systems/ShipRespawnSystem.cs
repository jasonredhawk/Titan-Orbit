using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only death aftermath: eliminate ships whose team owns no planets, and respawn
    /// a dead hull only after a valid <see cref="RequestRespawnPlanetCommand"/>.
    /// <para>
    /// [TITAN-ORBIT] The 10s <see cref="RespawnDelaySeconds"/> is a minimum wait — the ship
    /// does <b>not</b> auto-teleport when the timer elapses. The player must click a friendly
    /// planet on the expanded minimap. Spawn sits inside that planet's decorative rings,
    /// outside the gem-moon dock disc (see <see cref="ShipHomeSpawnLogic"/>).
    /// </para>
    /// Triggered by death bookkeeping in <see cref="ShipDeathRecordingSystem"/> (adds
    /// <see cref="ShipDeathState"/>). Runs after <see cref="BulletSimulationSystem"/> so
    /// death is fully processed first.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    public partial struct ShipRespawnSystem : ISystem
    {
        /// <summary>Seconds after death before the player may request a respawn planet.</summary>
        public const float RespawnDelaySeconds = 10f;

        /// <summary>Process elimination, then inbound respawn-planet RPCs.</summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Clocks ---
            // Death timer still uses World.Time; moon exclusion needs ServerTick orbit clock.
            float now = (float)SystemAPI.Time.ElapsedTime;
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double orbitElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Elimination: dead + zero friendly planets = out of the match ---
            // Alive ships keep flying even if the last planet just flipped. Elimination is
            // a death-time rule so a last-stand dogfight is still possible.
            MarkEliminatedShips(ref state, em);

            // --- Respawn RPCs (click on expanded minimap after the 10s beat) ---
            foreach (var (cmd, req, rpcEntity) in SystemAPI
                         .Query<RefRO<RequestRespawnPlanetCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                TryRespawnFromRpc(
                    ref state,
                    em,
                    req.ValueRO.SourceConnection,
                    cmd.ValueRO.PlanetId,
                    now,
                    orbitElapsed);
                ecb.DestroyEntity(rpcEntity);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// Sets <see cref="ShipState.IsEliminated"/> on every dead ship whose team owns no planets.
        /// Runs every tick so a last planet captured while they wait to click still ends the life.
        /// </summary>
        /// <param name="state">
        /// [ECS/DOTS] Required by <c>SystemAPI.Query</c> source-gen so this helper can complete
        /// query dependencies (SGSG0002).
        /// </param>
        void MarkEliminatedShips(ref SystemState state, EntityManager em)
        {
            foreach (var shipState in SystemAPI
                         .Query<RefRW<ShipState>>()
                         .WithAll<ShipTag, ShipDeathState>())
            {
                if (!shipState.ValueRO.IsDead || shipState.ValueRO.IsEliminated)
                    continue;
                if (shipState.ValueRO.Team == TeamId.None)
                    continue;
                if (ShipHomeSpawnLogic.TeamOwnsAnyPlanet(em, shipState.ValueRO.Team))
                    continue;

                shipState.ValueRW.IsEliminated = true;
            }
        }

        /// <summary>
        /// Validates the sender, timer, and planet ownership, then respawns the hull.
        /// Invalid requests are ignored so the client can click another friendly world.
        /// </summary>
        void TryRespawnFromRpc(
            ref SystemState state,
            EntityManager em,
            Entity connection,
            int planetId,
            float now,
            double orbitElapsed)
        {
            // --- Resolve sender ---
            if (connection == Entity.Null || !em.Exists(connection) || !em.HasComponent<NetworkId>(connection))
                return;
            int networkId = em.GetComponentData<NetworkId>(connection).Value;
            if (networkId <= 0)
                return;

            if (!TryFindShipForNetworkId(ref state, networkId, out Entity ship))
                return;
            if (!em.HasComponent<ShipState>(ship) || !em.HasComponent<ShipDeathState>(ship))
                return;

            var shipState = em.GetComponentData<ShipState>(ship);
            if (!shipState.IsDead || shipState.IsEliminated)
                return;

            var death = em.GetComponentData<ShipDeathState>(ship);
            if (now < death.RespawnAtTime)
                return;

            // --- Friendly planet still owned by this team? ---
            if (!ShipHomeSpawnLogic.TryGetFriendlyPlanetPose(
                    em, planetId, shipState.Team, out _, out _, out _, out _))
                return;

            if (!ShipHomeSpawnLogic.TryFindPlanetSpawnPosition(em, planetId, orbitElapsed, out float3 spawnPos))
                return;

            // --- MEGA: restore L6 while still IsDead so clients do not flash the MEGA at spawn ---
            if (em.HasComponent<MegaShipState>(ship)
                && em.GetComponentData<MegaShipState>(ship).IsMega)
            {
                MegaShipStatApplyLogic.RestorePreviousHull(em, ship);
                shipState = em.GetComponentData<ShipState>(ship);
            }

            var kinematics = em.HasComponent<ShipKinematics>(ship)
                ? em.GetComponentData<ShipKinematics>(ship)
                : default;
            var orbitState = em.HasComponent<ShipOrbitState>(ship)
                ? em.GetComponentData<ShipOrbitState>(ship)
                : default;
            var territoryLatch = em.HasComponent<ShipTerritoryBoostLatch>(ship)
                ? em.GetComponentData<ShipTerritoryBoostLatch>(ship)
                : default;
            var transform = em.HasComponent<LocalTransform>(ship)
                ? em.GetComponentData<LocalTransform>(ship)
                : LocalTransform.FromPosition(spawnPos);

            RespawnShip(
                ref shipState,
                ref kinematics,
                ref orbitState,
                ref territoryLatch,
                ref transform,
                spawnPos);

            em.SetComponentData(ship, shipState);
            if (em.HasComponent<ShipKinematics>(ship))
                em.SetComponentData(ship, kinematics);
            if (em.HasComponent<ShipOrbitState>(ship))
                em.SetComponentData(ship, orbitState);
            if (em.HasComponent<ShipTerritoryBoostLatch>(ship))
                em.SetComponentData(ship, territoryLatch);
            if (em.HasComponent<LocalTransform>(ship))
                em.SetComponentData(ship, transform);
            if (em.HasComponent<PhysicsVelocity>(ship))
                em.SetComponentData(ship, PhysicsVelocity.Zero);

            // --- Clear kill attribution only (match stats stay match-long) ---
            if (em.HasComponent<ShipCombatAttribution>(ship))
            {
                em.SetComponentData(ship, new ShipCombatAttribution
                {
                    LastDamagerNetworkId = 0,
                    LastDamageServerTime = 0f,
                    LastImpulseXZ = float2.zero,
                    LastImpulsePower = 0f,
                });
            }

            if (em.HasComponent<ShipDeathVfxState>(ship))
                em.SetComponentData(ship, new ShipDeathVfxState { Packed = 0 });

            em.RemoveComponent<ShipDeathState>(ship);
        }

        /// <summary>Finds the ship ghost owned by this network id (<see cref="GhostOwner"/>).</summary>
        bool TryFindShipForNetworkId(ref SystemState state, int networkId, out Entity ship)
        {
            ship = Entity.Null;
            foreach (var (owner, entity) in SystemAPI.Query<RefRO<GhostOwner>>()
                         .WithAll<ShipTag, ShipState>()
                         .WithEntityAccess())
            {
                if (owner.ValueRO.NetworkId != networkId)
                    continue;
                ship = entity;
                return true;
            }

            return false;
        }

        /// <summary>Restores ship to full vitals at spawn position with zero velocity.</summary>
        static void RespawnShip(
            ref ShipState ship,
            ref ShipKinematics kinematics,
            ref ShipOrbitState orbit,
            ref ShipTerritoryBoostLatch territoryLatch,
            ref LocalTransform transform,
            float3 spawnPos)
        {
            transform.Position = spawnPos;
            transform.Rotation = quaternion.identity;

            ship.Health = ship.MaxHealth;
            ship.CurrentGems = 0f;
            ship.CurrentPeople = 0;
            ship.CurrentEnergy = ship.MaxEnergy;
            ship.IsDead = false;
            ship.IsEliminated = false;
            ship.OverdriveLockout = false;
            kinematics.Velocity = float3.zero;
            orbit.OrbitPlanetId = 0;
            orbit.InOrbitRing = false;
            orbit.UsingOrbitMotor = false;
            orbit.OrbitLocked = false;
            orbit.IsTransferringPeople = false;
            // [TITAN-ORBIT] Drop sticky triangle boost so respawn does not keep a latched mult.
            ShipPhysicsDriveLogic.ClearTerritoryBoostLatch(ref territoryLatch);

            LogRespawn(ship.Team, spawnPos);
        }

        [Unity.Burst.BurstDiscard]
        static void LogRespawn(TeamId team, float3 position)
        {
            UnityEngine.Debug.Log($"[ShipRespawnSystem] Respawned {team} ship at {position}.");
        }
    }
}
