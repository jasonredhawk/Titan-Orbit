using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Finds the closest enemy ship or planetary-defense turret for a homing rocket.
    /// Asteroids, moons, and planets are never acquired — they can still collide and take
    /// damage when a rocket flies into them, but they are not lock points.
    /// <para>
    /// Server <c>BulletSimulationSystem</c> and client <c>BulletVfxDriver</c> share this
    /// so the cosmetic tracer turns toward the same class of target. Client callers must
    /// skip when <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> is true.
    /// </para>
    /// Map size comes from the caller (<c>MapStateSingleton</c>) — never hard-coded.
    /// </summary>
    public static class RocketHomingTargeting
    {
        /// <summary>
        /// Picks the nearest valid lock. Returns false when nothing is in range (fly straight).
        /// </summary>
        /// <param name="em">Server or client EntityManager.</param>
        /// <param name="from">Rocket logical position.</param>
        /// <param name="ownerTeam">Shooter team — same-team / unassigned hulls are ignored.</param>
        /// <param name="ownerNetworkId">Shooter id — the firing ship is never targeted.</param>
        /// <param name="acquireRange">Toroidal search radius. Missing/0 uses the catalog default (~50), never whole-map.</param>
        /// <param name="mapW">Toroidal width.</param>
        /// <param name="mapH">Toroidal height.</param>
        /// <param name="targetPos">Winning lock position (XZ).</param>
        public static bool TryFindClosestTarget(
            EntityManager em,
            float3 from,
            byte ownerTeam,
            int ownerNetworkId,
            float acquireRange,
            float mapW,
            float mapH,
            out float3 targetPos)
        {
            targetPos = from;
            if (!em.World.IsCreated || !ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return false;

            // --- Unknown shooter team ---
            // [TITAN-ORBIT] Team.None would treat every hull/pad as hostile and look like
            // the rocket is diving at nearby rocks (miners / home pads in the belt).
            if (ownerTeam == (byte)TeamId.None)
                return false;

            // --- Join Team Crash!!! ---
            // [TITAN-ORBIT] Client ship + planet gathers during Instantiates are unsafe.
            // Server worlds leave these false. Prefer the helpers (never Settling alone).
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;
            bool skipTurrets = ClientJoinSettleCache.ShouldSkipMapBodyQueries;

            from.y = 0f;
            float bestDist = float.MaxValue;
            bool found = false;

            // --- Enemy ships only ---
            // [TITAN-ORBIT] AsteroidTag / PlanetTag / moon hulls are excluded even if a
            // bad bake ever stacked those with ShipTag. Dead / awaiting-team hulls are not locks.
            using (var query = em.CreateEntityQuery(
                       ComponentType.ReadOnly<ShipTag>(),
                       ComponentType.ReadOnly<ShipState>(),
                       ComponentType.ReadOnly<LocalTransform>(),
                       ComponentType.ReadOnly<GhostOwner>(),
                       ComponentType.Exclude<AsteroidTag>(),
                       ComponentType.Exclude<PlanetTag>(),
                       ComponentType.Exclude<PlanetGemMoonColliderTag>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            using (var states = query.ToComponentDataArray<ShipState>(Allocator.Temp))
            using (var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp))
            using (var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (!IsEnemyShipLock(em, entities[i], states[i], owners[i], ownerTeam, ownerNetworkId))
                        continue;

                    float3 pos = transforms[i].Position;
                    pos.y = 0f;
                    ConsiderLock(from, pos, acquireRange, mapW, mapH, ref bestDist, ref targetPos, ref found);
                }
            }

            // --- Enemy turrets only (derived pad spheres — not the planet / moon / rock) ---
            if (skipTurrets)
                return found;

            using (var query = em.CreateEntityQuery(
                       ComponentType.ReadOnly<PlanetTag>(),
                       ComponentType.ReadOnly<PlanetState>(),
                       ComponentType.ReadOnly<LocalTransform>(),
                       ComponentType.ReadOnly<PlanetaryDefenseSlotElement>(),
                       ComponentType.Exclude<AsteroidTag>()))
            using (var planets = query.ToEntityArray(Allocator.Temp))
            {
                var scratch = new System.Collections.Generic.List<PlanetaryDefenseHitTarget>(16);
                PlanetaryDefenseHitScan.RebuildTargets(
                    em, planets, mapW, mapH, null, null, scratch);
                for (int i = 0; i < scratch.Count; i++)
                {
                    var pad = scratch[i];
                    if (!IsEnemyTeam(ownerTeam, pad.Team))
                        continue;

                    float3 pos = pad.Position;
                    pos.y = 0f;
                    ConsiderLock(from, pos, acquireRange, mapW, mapH, ref bestDist, ref targetPos, ref found);
                }
            }

            return found;
        }

        /// <summary>
        /// True for a living enemy hull. Rejects map bodies that must never be followed
        /// (asteroids, planets, gem-moon colliders) even if they somehow carry ShipTag.
        /// </summary>
        static bool IsEnemyShipLock(
            EntityManager em,
            Entity entity,
            in ShipState ship,
            in GhostOwner owner,
            byte ownerTeam,
            int ownerNetworkId)
        {
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return false;
            if (ownerNetworkId > 0 && owner.NetworkId == ownerNetworkId)
                return false;
            if (!IsEnemyTeam(ownerTeam, (byte)ship.Team))
                return false;

            // --- Collision-only bodies ---
            // [TITAN-ORBIT] Rockets may hit these and deal damage; they are never seek targets.
            if (em.HasComponent<AsteroidTag>(entity) ||
                em.HasComponent<PlanetTag>(entity) ||
                em.HasComponent<PlanetGemMoonColliderTag>(entity) ||
                em.HasComponent<PlanetGemMoonState>(entity))
                return false;

            return true;
        }

        /// <summary>Opposing playable teams only — Team.None is never a lock.</summary>
        static bool IsEnemyTeam(byte ownerTeam, byte targetTeam)
        {
            if (ownerTeam == (byte)TeamId.None || targetTeam == (byte)TeamId.None)
                return false;
            return ownerTeam != targetTeam;
        }

        /// <summary>Keeps the closest in-range candidate (toroidal XZ).</summary>
        static void ConsiderLock(
            float3 from,
            float3 pos,
            float acquireRange,
            float mapW,
            float mapH,
            ref float bestDist,
            ref float3 targetPos,
            ref bool found)
        {
            float dist = ToroidalMapEcs.ToroidalDistance(from, pos, mapW, mapH);
            if (!RocketHomingLogic.IsInAcquireRange(dist, acquireRange))
                return;
            if (dist >= bestDist)
                return;

            bestDist = dist;
            targetPos = pos;
            found = true;
        }
    }
}
