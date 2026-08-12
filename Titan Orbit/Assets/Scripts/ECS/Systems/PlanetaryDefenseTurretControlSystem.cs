using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: enter planetary defense turret via RPC, exit on RMB thrust, and force-eject when
    /// the pad is destroyed / wiped / ship dies. Stows the same ship ghost (no turret ghosts).
    /// <para>
    /// World: ServerSimulation. Local Host also calls
    /// <see cref="TryEnterForNetworkId"/> directly because SendRpc on ServerWorld never becomes
    /// <see cref="ReceiveRpcCommandRequest"/> (same pattern as moon-orbit store).
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetaryDefenseSlotSyncSystem))]
    public partial class PlanetaryDefenseTurretControlSystem : SystemBase
    {
        PlanetShipFamilyConfig _familyConfig;
        bool _familyResolved;

        /// <summary>Require map + planets for enter/exit pose math.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<PlanetTag>();
            RequireForUpdate<MapStateSingleton>();
        }

        /// <summary>Process enter RPCs, thrust exits, and force ejects each tick.</summary>
        protected override void OnUpdate()
        {
            EnsureFamilyConfig();

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            float mapW = map.MapWidth;
            float mapH = map.MapHeight;
            var defaultConfig = PlanetaryDefenseConfig.LoadDefault();
            var em = EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Enter RPCs ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<EnterPlanetaryDefenseTurretCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(em, req.ValueRO.SourceConnection);
                TryEnterForNetworkId(
                    em, networkId, cmd.ValueRO.PlanetId, cmd.ValueRO.SlotIndex,
                    mapW, mapH, _familyConfig, defaultConfig);
                ecb.DestroyEntity(entity);
            }

            // --- Controlling ships: thrust exit + force eject ---
            foreach (var (control, input, shipEntity) in SystemAPI
                         .Query<RefRO<ShipTurretControlState>, RefRO<ShipInput>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!control.ValueRO.IsControlling)
                    continue;

                if (PlanetaryDefenseTurretControlLogic.ShouldForceEject(em, shipEntity) ||
                    input.ValueRO.Thrust)
                {
                    PlanetaryDefenseTurretControlLogic.ExitTurret(em, shipEntity, mapW, mapH);
                }
                else
                {
                    // Keep hull frozen at the pad while occupied (prediction may nudge slightly).
                    if (em.HasComponent<Unity.Physics.PhysicsVelocity>(shipEntity))
                        em.SetComponentData(shipEntity, Unity.Physics.PhysicsVelocity.Zero);
                }
            }

            // --- Stale occupancy (disconnect / destroyed ship left OccupiedByNetworkId set) ---
            ClearOrphanOccupancy(em);

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// Frees pad occupancy when no living ship with that NetworkId is still controlling it.
        /// Covers disconnects where the ship entity is destroyed before ExitTurret runs.
        /// </summary>
        static void ClearOrphanOccupancy(EntityManager em)
        {
            using var planetQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetaryDefenseSlotElement>());
            using var planets = planetQuery.ToEntityArray(Allocator.Temp);

            for (int p = 0; p < planets.Length; p++)
            {
                Entity planetEntity = planets[p];
                if (!em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                for (int i = 0; i < buffer.Length; i++)
                {
                    var slot = buffer[i];
                    if (slot.OccupiedByNetworkId == 0)
                        continue;

                    if (IsOccupancyClaimedByLivingShip(em, slot.OccupiedByNetworkId, planetEntity, i))
                        continue;

                    slot.OccupiedByNetworkId = 0;
                    buffer[i] = slot;
                }
            }
        }

        /// <summary>
        /// True when a living ship with <paramref name="networkId"/> still claims this pad.
        /// </summary>
        static bool IsOccupancyClaimedByLivingShip(
            EntityManager em,
            int networkId,
            Entity planetEntity,
            int slotIndex)
        {
            if (!TryGetOwnedShip(em, networkId, out Entity shipEntity))
                return false;
            if (!em.HasComponent<ShipTurretControlState>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<PlanetState>(planetEntity))
                return false;

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.IsDead)
                return false;

            var control = em.GetComponentData<ShipTurretControlState>(shipEntity);
            if (!control.IsControlling || control.SlotIndex != slotIndex)
                return false;

            var planet = em.GetComponentData<PlanetState>(planetEntity);
            return control.PlanetId == planet.PlanetId;
        }

        /// <summary>
        /// Public helper for Local Host (direct server write) and the RPC path.
        /// </summary>
        public static bool TryEnterForNetworkId(
            EntityManager em,
            int networkId,
            int planetId,
            byte slotIndex,
            float mapW,
            float mapH,
            PlanetShipFamilyConfig familyConfig,
            PlanetaryDefenseConfig defaultConfig)
        {
            if (!TryGetOwnedShip(em, networkId, out Entity shipEntity))
                return false;

            return PlanetaryDefenseTurretControlLogic.TryEnterTurret(
                em, shipEntity, networkId, planetId, slotIndex,
                mapW, mapH, familyConfig, defaultConfig);
        }

        /// <summary>Reads NetworkId from the connection that sent the enter RPC.</summary>
        static int GetSenderNetworkId(EntityManager em, Entity connection)
        {
            if (connection == Entity.Null || !em.HasComponent<NetworkId>(connection))
                return -1;
            return em.GetComponentData<NetworkId>(connection).Value;
        }

        /// <summary>Finds the ship ghost owned by this client's GhostOwner.NetworkId.</summary>
        static bool TryGetOwnedShip(EntityManager em, int networkId, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (networkId <= 0)
                return false;

            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                shipEntity = entities[i];
                return true;
            }

            return false;
        }

        void EnsureFamilyConfig()
        {
            if (_familyResolved)
                return;
            _familyConfig = UnityEngine.Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            _familyResolved = true;
        }
    }
}
