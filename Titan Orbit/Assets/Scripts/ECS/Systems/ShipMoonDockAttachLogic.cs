using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One-shot moon-dock re-pin for hull/chassis swaps while fully landed.
    /// <para>
    /// [TITAN-ORBIT] Upgrade-tree purchases rebuild the physics collider and mass while the ship
    /// is nested in the kinematic moon. Physics can shove the hull out before the next drive
    /// tick's <see cref="ShipPhysicsDriveLogic"/> attach runs. Call this immediately after
    /// collider apply so the new hull keeps the same angular pose around the moon.
    /// </para>
    /// Server-only gather of the docked planet (never call under client TransformQuarantine —
    /// planet <c>ToEntityArray</c> Crash!!!). Paired with <see cref="ShipPhysicsDriveLogic"/>.
    /// </summary>
    public static class ShipMoonDockAttachLogic
    {
        /// <summary>
        /// If the ship is fully moon-docked, rewrite pose + velocity to the moon surface contact
        /// (same side of the moon as before). No-op when not fully landed or planet missing.
        /// </summary>
        /// <param name="em">Server EntityManager.</param>
        /// <param name="shipEntity">Ship that just received a new hull collider / chassis.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="elapsedSeconds">Shared moon orbit clock (ServerTick seconds).</param>
        /// <returns>True when attach was applied.</returns>
        public static bool TryReattachFullyDockedShip(
            EntityManager em,
            Entity shipEntity,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            // --- Guard: fully landed only ---
            if (!em.Exists(shipEntity) ||
                !em.HasComponent<ShipMoonDockState>(shipEntity) ||
                !em.HasComponent<LocalTransform>(shipEntity))
                return false;

            var moonDock = em.GetComponentData<ShipMoonDockState>(shipEntity);
            if (moonDock.MoonPlanetId == 0 ||
                moonDock.LandingProgress < GemEconomyConstants.MoonLandingCompleteThreshold)
                return false;

            // --- Resolve the single docked planet (tiny query — not a full map-body gather) ---
            if (!TryFindPlanetSnapshot(em, moonDock.MoonPlanetId, out PlanetMotorSnapshot snapshot))
                return false;

            var transform = em.GetComponentData<LocalTransform>(shipEntity);
            var physicsVelocity = em.HasComponent<PhysicsVelocity>(shipEntity)
                ? em.GetComponentData<PhysicsVelocity>(shipEntity)
                : default;

            // Shared pin math with the per-tick motor path.
            ShipPhysicsDriveLogic.ApplyMoonDockAttach(
                moonDock.MoonPlanetId,
                ref transform,
                ref physicsVelocity,
                snapshot,
                mapW,
                mapH,
                elapsedSeconds);

            em.SetComponentData(shipEntity, transform);
            if (em.HasComponent<PhysicsVelocity>(shipEntity))
                em.SetComponentData(shipEntity, physicsVelocity);
            if (em.HasComponent<ShipKinematics>(shipEntity))
            {
                em.SetComponentData(shipEntity, new ShipKinematics
                {
                    Velocity = physicsVelocity.Linear,
                });
            }

            return true;
        }

        /// <summary>
        /// Finds one planet by <see cref="PlanetState.PlanetId"/> and builds a motor snapshot.
        /// </summary>
        static bool TryFindPlanetSnapshot(EntityManager em, int planetId, out PlanetMotorSnapshot snapshot)
        {
            snapshot = default;
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var planet = em.GetComponentData<PlanetState>(entity);
                if (planet.PlanetId != planetId)
                    continue;

                var transform = em.GetComponentData<LocalTransform>(entity);
                var moon = em.HasComponent<PlanetGemMoonState>(entity)
                    ? em.GetComponentData<PlanetGemMoonState>(entity)
                    : default;
                float planetSize = math.max(0.25f, transform.Scale);
                snapshot = new PlanetMotorSnapshot
                {
                    Planet = planet,
                    Moon = moon,
                    Transform = transform,
                    ShieldOuterRadiusWorld = PlanetGemMoonMath.GetMoonShieldOuterRadiusWorld(
                        planetSize,
                        planet.IsHomePlanet),
                    MoonBodyRadiusWorld = PlanetGemMoonMath.GetMoonBodyRadiusWorld(
                        planetSize,
                        planet.IsHomePlanet),
                };
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads map size from <see cref="MapStateSingleton"/> (1000×1000 fallback).
        /// </summary>
        public static void GetMapSize(EntityManager em, out float mapW, out float mapH)
        {
            mapW = 1000f;
            mapH = 1000f;
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
            if (query.TryGetSingleton<MapStateSingleton>(out var map))
            {
                mapW = math.max(100f, map.MapWidth);
                mapH = math.max(100f, map.MapHeight);
            }
        }
    }
}
