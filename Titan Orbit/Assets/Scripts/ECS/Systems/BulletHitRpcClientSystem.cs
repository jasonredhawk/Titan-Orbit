using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: receives <see cref="BulletHitRpc"/> and feeds <see cref="BulletVfxBridge"/> so
    /// <c>BulletVfxDriver</c> can play impact VFX and destroy the matching tracer.
    /// <para>
    /// [TITAN-ORBIT] Also applies <see cref="BulletHitRpc.AsteroidHealthAfter"/> onto seed-hydrated
    /// local asteroids (not ghost-relevant). Sequence 0 (ram/grind) uses body-radius matching so
    /// a packed neighbor is not culled instead of the rock the server damaged.
    /// Planetary-defense remaining HP is applied the same way via
    /// <see cref="PlanetaryDefenseClientHealthSync"/> — a client store filled from HitRpc,
    /// not from the planet ghost buffer (layout channel: level, occupancy, MaxHealth).
    /// </para>
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSpawnRpcClientSystem))]
    public partial struct BulletHitRpcClientSystem : ISystem
    {
        /// <summary>Scratch for asteroid HitRpc apply (blittable).</summary>
        struct PendingAsteroidHit
        {
            public float3 HitPosition;
            public float AsteroidHealthAfter;
            /// <summary>0 = ram/grind (body-radius match); non-zero = bullet (hit-sphere match).</summary>
            public uint Sequence;
        }

        /// <summary>Re-queues broadcast hit RPCs into the VFX bridge and syncs local asteroid HP.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var destroyEcb = new EntityCommandBuffer(Allocator.Temp);
            var asteroidHits = new NativeList<PendingAsteroidHit>(8, Allocator.Temp);

            // --- Phase 1: copy / enqueue (no asteroid structural changes yet) ---
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<BulletHitRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var r = rpc.ValueRO;
                float3 hit = r.HitPosition;
                hit.y = 0f;

                BulletVfxBridge.EnqueueHit(new BulletVfxBridge.HitRequest
                {
                    Sequence = r.Sequence,
                    HitPosition = hit,
                    Damage = r.Damage,
                    OwnerTeam = r.OwnerTeam,
                    OwnerNetworkId = r.OwnerNetworkId,
                    MountIndex = r.MountIndex,
                    BankIndex = r.BankIndex,
                    ScaleMultiplier = r.ScaleMultiplier > 0f ? r.ScaleMultiplier : 1f,
                    AsteroidHealthAfter = r.AsteroidHealthAfter,
                    PlanetaryDefensePlanetId = r.PlanetaryDefensePlanetId,
                    PlanetaryDefenseSlotIndex = r.PlanetaryDefenseSlotIndex,
                    PlanetaryDefenseHealthAfter = r.PlanetaryDefenseHealthAfter,
                });

                if (r.AsteroidHealthAfter >= 0f)
                {
                    asteroidHits.Add(new PendingAsteroidHit
                    {
                        HitPosition = hit,
                        AsteroidHealthAfter = r.AsteroidHealthAfter,
                        Sequence = r.Sequence,
                    });
                }

                // --- Turret combat HP (HitRpc channel, not planet ghost Health) ---
                // [TITAN-ORBIT] Same phase as asteroid writes. Broadcast RPC — every client
                // applies remaining HP so the bar stays injured after you stop firing.
                if (r.PlanetaryDefensePlanetId > 0)
                {
                    PlanetaryDefenseClientHealthSync.ApplyHitRpc(
                        em,
                        r.PlanetaryDefensePlanetId,
                        r.PlanetaryDefenseSlotIndex,
                        r.PlanetaryDefenseHealthAfter);
                }

                destroyEcb.DestroyEntity(entity);
            }

            destroyEcb.Playback(em);
            destroyEcb.Dispose();

            // --- Phase 2: apply HP for living rocks only ---
            // Kill frames (HealthAfter <= 0) must NOT cull here. Surface-fit / ram residual
            // picks a packed neighbor, then DestroyRpc culls the real rock — two client hides,
            // one server kill, invisible hull. DestroyRpc matches the server center.
            for (int i = 0; i < asteroidHits.Length; i++)
            {
                var hit = asteroidHits[i];
                if (hit.AsteroidHealthAfter <= 0.01f)
                    continue;

                if (hit.Sequence == 0)
                {
                    ClientLocalAsteroidCombatSync.ApplyRamHitAtPosition(
                        em, hit.HitPosition, hit.AsteroidHealthAfter);
                }
                else
                {
                    ClientLocalAsteroidCombatSync.ApplyHitAtPosition(
                        em, hit.HitPosition, hit.AsteroidHealthAfter);
                }
            }

            asteroidHits.Dispose();
        }
    }
}
