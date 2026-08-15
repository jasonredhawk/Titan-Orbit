using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared enter / exit / query helpers for planetary defense turret possession.
    /// Used by the server control system (RPC + thrust exit + force eject) and mirrored by
    /// client UI for eligibility checks. Turrets are not ghosts — possession lives on the ship
    /// (<see cref="ShipTurretControlState"/>) plus slot occupancy on the planet buffer.
    /// </summary>
    public static class PlanetaryDefenseTurretControlLogic
    {
        /// <summary>
        /// True when this EntityManager is a client world and Instantiates/join gates forbid gathers.
        /// Server worlds (including Local Host ServerWorld) always return false so eject/enter
        /// cannot be blocked by the client's Join Team Instantiates latch.
        /// </summary>
        static bool ShouldRefuseClientGathers(EntityManager em)
        {
            // --- Server authority must never be gated by client Instantiates ---
            var world = em.World;
            if (world != null && world.IsServer())
                return false;

            return ClientJoinSettleCache.ShouldSkipShipEntityQueries ||
                   ClientJoinSettleCache.ShouldSkipMapBodyQueries;
        }

        /// <summary>
        /// True when this ship ghost is currently stowed in a planetary defense pad.
        /// Used by moon dock, drones, nameplates, and other systems that must ignore occupied hulls.
        /// </summary>
        public static bool IsControllingTurret(EntityManager em, Entity shipEntity)
        {
            return shipEntity != Entity.Null &&
                   em.Exists(shipEntity) &&
                   em.HasComponent<ShipTurretControlState>(shipEntity) &&
                   em.GetComponentData<ShipTurretControlState>(shipEntity).IsControlling;
        }

        /// <summary>
        /// Clears control state on the ship and frees the slot occupancy when it still points
        /// at this NetworkId. Restores the hull at the pad world position with zero velocity.
        /// </summary>
        /// <param name="em">Server (or Local Host) EntityManager.</param>
        /// <param name="shipEntity">Ship ghost being ejected.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        public static void ExitTurret(
            EntityManager em,
            Entity shipEntity,
            float mapW,
            float mapH)
        {
            // --- Join-crash gates (client Instantiates only) ---
            if (ShouldRefuseClientGathers(em))
                return;

            // --- Guard ---
            if (shipEntity == Entity.Null || !em.Exists(shipEntity))
                return;
            if (!em.HasComponent<ShipTurretControlState>(shipEntity))
                return;

            var control = em.GetComponentData<ShipTurretControlState>(shipEntity);
            if (!control.IsControlling)
            {
                // Ensure cleared even if a half-written state exists.
                em.SetComponentData(shipEntity, default(ShipTurretControlState));
                return;
            }

            int networkId = 0;
            if (em.HasComponent<GhostOwner>(shipEntity))
                networkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;

            // --- Resolve pad pose for restore ---
            float3 restorePos = em.HasComponent<LocalTransform>(shipEntity)
                ? em.GetComponentData<LocalTransform>(shipEntity).Position
                : float3.zero;
            restorePos.y = PlanetaryDefenseMath.FixedY;

            if (TryFindPlanetById(em, control.PlanetId, out Entity planetEntity) &&
                em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity) &&
                em.HasComponent<LocalTransform>(planetEntity) &&
                em.HasComponent<PlanetState>(planetEntity))
            {
                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                int slotIndex = control.SlotIndex;
                if (slotIndex >= 0 && slotIndex < buffer.Length)
                {
                    var slot = buffer[slotIndex];
                    // Free occupancy only if we still own it (another path may have wiped the slot).
                    if (slot.OccupiedByNetworkId == networkId || slot.OccupiedByNetworkId == 0)
                    {
                        slot.OccupiedByNetworkId = 0;
                        buffer[slotIndex] = slot;
                    }

                    var planet = em.GetComponentData<PlanetState>(planetEntity);
                    var xf = em.GetComponentData<LocalTransform>(planetEntity);
                    restorePos = PlanetaryDefenseMath.GetSlotWorldPositionNear(
                        restorePos,
                        xf.Position,
                        math.max(0.25f, xf.Scale),
                        planet.PlanetLevel,
                        slotIndex,
                        buffer.Length,
                        mapW,
                        mapH);
                    restorePos.y = PlanetaryDefenseMath.FixedY;
                }
            }

            // --- Stow restore: pose + zero velocity ---
            if (em.HasComponent<LocalTransform>(shipEntity))
            {
                var xf = em.GetComponentData<LocalTransform>(shipEntity);
                xf.Position = restorePos;
                em.SetComponentData(shipEntity, xf);
            }

            if (em.HasComponent<PhysicsVelocity>(shipEntity))
                em.SetComponentData(shipEntity, PhysicsVelocity.Zero);

            if (em.HasComponent<ShipKinematics>(shipEntity))
            {
                em.SetComponentData(shipEntity, new ShipKinematics { Velocity = float3.zero });
            }

            em.SetComponentData(shipEntity, default(ShipTurretControlState));
        }

        /// <summary>
        /// Attempts to enter a built friendly pad. Returns false when validation fails
        /// (wrong team, empty pad, occupied, out of zone, already controlling, etc.).
        /// </summary>
        public static bool TryEnterTurret(
            EntityManager em,
            Entity shipEntity,
            int networkId,
            int planetId,
            byte slotIndex,
            float mapW,
            float mapH,
            PlanetShipFamilyConfig familyConfig,
            PlanetaryDefenseConfig defaultConfig)
        {
            // --- Join-crash gates (client Instantiates only; ServerWorld always proceeds) ---
            if (ShouldRefuseClientGathers(em))
                return false;

            // --- Ship guards ---
            if (shipEntity == Entity.Null || networkId <= 0 || planetId <= 0)
                return false;
            if (!em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<LocalTransform>(shipEntity) ||
                !em.HasComponent<ShipTurretControlState>(shipEntity))
                return false;

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
                return false;

            var control = em.GetComponentData<ShipTurretControlState>(shipEntity);
            if (control.IsControlling)
                return false;

            if (!TryFindPlanetById(em, planetId, out Entity planetEntity))
                return false;
            if (!em.HasComponent<PlanetState>(planetEntity) ||
                !em.HasComponent<LocalTransform>(planetEntity) ||
                !em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                return false;

            var planet = em.GetComponentData<PlanetState>(planetEntity);
            if (planet.Ownership == TeamId.None || planet.Ownership != ship.Team)
                return false;

            var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
            if (slotIndex >= buffer.Length)
                return false;

            var slot = buffer[slotIndex];
            // [TITAN-ORBIT] Only built turrets — empty pads cannot be entered.
            if (slot.TurretLevel == 0 || slot.Health <= 0f)
                return false;
            if (slot.OccupiedByNetworkId != 0)
                return false;

            var config = PlanetaryDefenseConfig.ResolveForFamily(
                familyConfig, planet.ShipFamilyConfigIndex);
            if (config == null)
                config = defaultConfig;

            float3 shipPos = em.GetComponentData<LocalTransform>(shipEntity).Position;
            shipPos.y = PlanetaryDefenseMath.FixedY;
            var planetXf = em.GetComponentData<LocalTransform>(planetEntity);
            float3 padPos = PlanetaryDefenseMath.GetSlotWorldPositionNear(
                shipPos,
                planetXf.Position,
                math.max(0.25f, planetXf.Scale),
                planet.PlanetLevel,
                slotIndex,
                buffer.Length,
                mapW,
                mapH);
            padPos.y = PlanetaryDefenseMath.FixedY;

            float zoneR = math.max(0.25f, config.depositZoneRadius);
            float3 delta = ToroidalMapEcs.ShortestOffsetXZ(shipPos, padPos, mapW, mapH);
            float distSq = math.lengthsq(new float3(delta.x, 0f, delta.z));
            if (distSq > zoneR * zoneR)
                return false;

            // --- Occupy + stow ---
            slot.OccupiedByNetworkId = networkId;
            buffer[slotIndex] = slot;

            em.SetComponentData(shipEntity, new ShipTurretControlState
            {
                IsControlling = true,
                PlanetId = planetId,
                SlotIndex = slotIndex,
            });

            // --- Clear moon dock so a passing gem-moon cannot open Orbit Menu on the pad ---
            // [TITAN-ORBIT] Stow parks the hull at the pad; home moons often sweep that zone.
            if (em.HasComponent<ShipMoonDockState>(shipEntity))
                em.SetComponentData(shipEntity, default(ShipMoonDockState));

            if (em.HasComponent<LocalTransform>(shipEntity))
            {
                var xf = em.GetComponentData<LocalTransform>(shipEntity);
                xf.Position = padPos;
                em.SetComponentData(shipEntity, xf);
            }

            if (em.HasComponent<PhysicsVelocity>(shipEntity))
                em.SetComponentData(shipEntity, PhysicsVelocity.Zero);

            if (em.HasComponent<ShipKinematics>(shipEntity))
                em.SetComponentData(shipEntity, new ShipKinematics { Velocity = float3.zero });

            return true;
        }

        /// <summary>
        /// True when the ship's control state is invalid (destroyed pad, wrong occupant, wiped).
        /// Caller should <see cref="ExitTurret"/>.
        /// </summary>
        public static bool ShouldForceEject(EntityManager em, Entity shipEntity)
        {
            if (ShouldRefuseClientGathers(em))
                return false;

            if (!em.HasComponent<ShipTurretControlState>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity))
                return false;

            var control = em.GetComponentData<ShipTurretControlState>(shipEntity);
            if (!control.IsControlling)
                return false;

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return true;

            if (!TryFindPlanetById(em, control.PlanetId, out Entity planetEntity) ||
                !em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity) ||
                !em.HasComponent<PlanetState>(planetEntity))
                return true;

            var planet = em.GetComponentData<PlanetState>(planetEntity);
            if (planet.Ownership == TeamId.None || planet.Ownership != ship.Team)
                return true;

            var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
            int slotIndex = control.SlotIndex;
            if (slotIndex < 0 || slotIndex >= buffer.Length)
                return true;

            var slot = buffer[slotIndex];
            // Destroyed / empty pad, or occupancy lost (wipe / stolen clear).
            if (slot.TurretLevel == 0 || slot.Health <= 0f)
                return true;

            int networkId = 0;
            if (em.HasComponent<GhostOwner>(shipEntity))
                networkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            if (slot.OccupiedByNetworkId != networkId)
                return true;

            return false;
        }

        /// <summary>Finds a planet entity by stable <see cref="PlanetState.PlanetId"/>.</summary>
        public static bool TryFindPlanetById(EntityManager em, int planetId, out Entity planetEntity)
        {
            planetEntity = Entity.Null;
            // Map-body gather — skip during client Instantiates (server worlds always proceed).
            if (ShouldRefuseClientGathers(em))
                return false;
            if (planetId <= 0)
                return false;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>());
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                planetEntity = entities[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the closest built, free, friendly pad whose deposit zone contains
        /// <paramref name="shipPos"/>. Used by client Take Control eligibility.
        /// </summary>
        public static bool TryFindClosestEnterableSlotInZone(
            EntityManager em,
            TeamId shipTeam,
            float3 shipPos,
            float mapW,
            float mapH,
            PlanetShipFamilyConfig familyConfig,
            PlanetaryDefenseConfig defaultConfig,
            out int planetId,
            out byte slotIndex,
            out float3 padWorldPos,
            out PlanetaryDefenseConfig config)
        {
            planetId = 0;
            slotIndex = 0;
            padWorldPos = float3.zero;
            config = defaultConfig;
            // Client UI eligibility — refuse ship + map gathers during Join Team Instantiates.
            if (ShouldRefuseClientGathers(em))
                return false;

            float bestDistSq = float.MaxValue;
            bool found = false;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<PlanetaryDefenseSlotElement>());
            using var entities = query.ToEntityArray(Allocator.Temp);

            for (int p = 0; p < entities.Length; p++)
            {
                Entity planetEntity = entities[p];
                var planet = em.GetComponentData<PlanetState>(planetEntity);
                if (planet.Ownership == TeamId.None || planet.Ownership != shipTeam)
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                if (buffer.Length == 0)
                    continue;

                var resolved = PlanetaryDefenseConfig.ResolveForFamily(
                    familyConfig, planet.ShipFamilyConfigIndex);
                float zoneR = math.max(0.25f, resolved.depositZoneRadius);
                float zoneRSq = zoneR * zoneR;

                var planetXf = em.GetComponentData<LocalTransform>(planetEntity);
                float3 planetPos = planetXf.Position;
                float planetSize = math.max(0.25f, planetXf.Scale);

                for (int i = 0; i < buffer.Length; i++)
                {
                    var slot = buffer[i];
                    // Enterable = built + healthy + free.
                    if (slot.TurretLevel == 0 || slot.Health <= 0f || slot.OccupiedByNetworkId != 0)
                        continue;

                    float3 slotPos = PlanetaryDefenseMath.GetSlotWorldPositionNear(
                        shipPos, planetPos, planetSize, planet.PlanetLevel,
                        i, buffer.Length, mapW, mapH);
                    float3 delta = ToroidalMapEcs.ShortestOffsetXZ(shipPos, slotPos, mapW, mapH);
                    float distSq = math.lengthsq(new float3(delta.x, 0f, delta.z));
                    if (distSq > zoneRSq || distSq >= bestDistSq)
                        continue;

                    bestDistSq = distSq;
                    planetId = planet.PlanetId;
                    slotIndex = (byte)i;
                    padWorldPos = slotPos;
                    padWorldPos.y = PlanetaryDefenseMath.FixedY;
                    config = resolved;
                    found = true;
                }
            }

            return found;
        }
    }
}
