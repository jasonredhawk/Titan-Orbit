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
    /// local asteroids (not ghost-relevant). Without this, HitRpc only hid the hybrid mesh while
    /// the ECS rock stayed at full HP — phantom collision until the delayed respawn RPC.
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
                    });
                }

                destroyEcb.DestroyEntity(entity);
            }

            destroyEcb.Playback(em);
            destroyEcb.Dispose();

            // --- Phase 2: apply HP; kill frames DestroyEntity immediately ---
            // Prefer AsteroidDestroyedRpc for authoritative teardown (mining/ram too).
            // HitRpc still updates mid-fight HP and is a belt-and-suspenders kill path.
            for (int i = 0; i < asteroidHits.Length; i++)
            {
                var hit = asteroidHits[i];
                ClientLocalAsteroidCombatSync.ApplyHitAtPosition(
                    em, hit.HitPosition, hit.AsteroidHealthAfter);
            }

            asteroidHits.Dispose();
        }
    }
}
