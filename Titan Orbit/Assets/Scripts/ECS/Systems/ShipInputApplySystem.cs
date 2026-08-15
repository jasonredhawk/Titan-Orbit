using TitanOrbit.Core;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    // Pipeline: ShipInputApplySystem → ShipClientPredictedPhysicsDriveSystem → PhysicsSystemGroup → …
    /// <summary>
    /// Copies the latest player input from <see cref="ShipPendingInput"/> onto the local ship
    /// ghost during <see cref="GhostInputSystemGroup"/> — before prediction runs this tick.
    /// [NETCODE] Client-side instancy (Starblast pillar 1): the local ship executes
    /// <see cref="ShipClientPredictedPhysicsDriveSystem"/> on the current tick immediately;
    /// server reconciliation is silent via NetCode rollback/resim. Dedicated server reads
    /// replicated <see cref="ShipInput"/> ghost commands instead. Paired with
    /// <see cref="Game.ShipInputBridge"/>.
    /// </summary>
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipInputApplySystem : ISystem
    {
        /// <summary>[NETCODE] Wait until the client connection is in-game before applying input.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// Write pending keyboard/mouse onto the owner ghost, then clear one-shot latches
        /// (CycleBullet) so the next Unity frame does not re-send the same B press forever.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
            if (!ShipPendingInput.HasValue)
                return;

            // [TITAN-ORBIT] TeamChoice / ship Instantiates hold only — not map Instantiates trickle.
            // Gating on ShouldSkipShipEntityQueries froze thrust after Join Team while asteroids
            // kept GhostSpawnBacklog true (loading-complete proxy-ready exit).
            if (ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            var cmd = ShipPendingInput.Latest;

            // --- Map Instantiates trickle: write seeded hull only (no ship Query) ---
            // [TITAN-ORBIT] ShouldSkipShipEntityQueries stays true while placeholders drain;
            // Query/WithEntityAccess is still unsafe — SetComponentData on the known seed is OK.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                Entity seeded = Entity.Null;
                if (!LocalShipEntitySeed.TryGetSeededShip(state.EntityManager, out seeded) ||
                    seeded == Entity.Null)
                {
                    LocalShipEntitySeed.TryGetOwnedShipEntityUnchecked(state.EntityManager, out seeded);
                }

                if (seeded != Entity.Null &&
                    state.EntityManager.HasComponent<ShipInput>(seeded))
                {
                    state.EntityManager.SetComponentData(seeded, cmd);
                    if (cmd.CycleBullet.IsSet)
                        ShipPendingInput.ConsumeCycleBulletLatch();
                    if (cmd.FireRocket.IsSet)
                        ShipPendingInput.ConsumeFireRocketLatch();
                    if (cmd.PlaceMine.IsSet)
                        ShipPendingInput.ConsumePlaceMineLatch();
                }

                return;
            }

            // [NETCODE] GhostOwnerIsLocal — NetCode's tag for the connection-owned ghost.
            foreach (var input in SystemAPI.Query<RefRW<ShipInput>>().WithAll<ShipTag, GhostOwnerIsLocal>())
                input.ValueRW = cmd;

            // [TITAN-ORBIT] Fallback tag added by LocalPlayerTagSystem for hybrid host paths.
            foreach (var input in SystemAPI.Query<RefRW<ShipInput>>().WithAll<ShipTag, LocalPlayerShipTag>())
                input.ValueRW = cmd;

            // --- Seeded hull when GhostOwnerIsLocal has not enabled yet ---
            // [TITAN-ORBIT] GhostReceive Instantiates the owner ship; GhostOwnerIsLocal can lag
            // one GhostUpdate. Write the same command onto the known seed so the motor has input
            // even if both queries above matched nothing.
            if (LocalShipEntitySeed.TryGetOwnedShipEntityUnchecked(state.EntityManager, out var seededHull) &&
                seededHull != Entity.Null &&
                state.EntityManager.HasComponent<ShipInput>(seededHull))
            {
                state.EntityManager.SetComponentData(seededHull, cmd);
            }

            // --- Consume one-shots after copy ---
            // [TITAN-ORBIT] Latch survives across Unity Updates until this apply runs; clear now
            // so the next BuildInput / Set does not keep CycleBullet.IsSet for many ticks.
            if (cmd.CycleBullet.IsSet)
                ShipPendingInput.ConsumeCycleBulletLatch();
            if (cmd.FireRocket.IsSet)
                ShipPendingInput.ConsumeFireRocketLatch();
            if (cmd.PlaceMine.IsSet)
                ShipPendingInput.ConsumePlaceMineLatch();
        }
    }
}
