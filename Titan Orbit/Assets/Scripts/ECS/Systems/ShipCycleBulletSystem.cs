using TitanOrbit.Data;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// B-key bullet bank cycle. When the owner presses CycleBullet (<c>ShipInput.CycleBullet</c>),
    /// increments <see cref="ShipLoadoutState.RuntimeBulletIndex"/> modulo
    /// <see cref="BulletVfxBank.CategoryCount"/> (wraps to 0 after the last category).
    /// <para>
    /// Runs on <b>server</b> (authoritative GhostField write) and <b>client prediction</b> so local
    /// tracers / floating names update immediately. Requires <see cref="ShipLoadoutState"/> baked
    /// on the ship ghost — runtime-added GhostFields do not replicate.
    /// </para>
    /// World: ServerSimulation + ClientSimulation. Before <see cref="BulletSimulationSystem"/> on server.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipCycleBulletSystem : ISystem
    {
        /// <summary>Cached category count from Resources bank (0 = bank missing).</summary>
        int _categoryCount;

        /// <summary>Load bank size once — categories do not change at runtime.</summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Cache bank size ---
            // [TITAN-ORBIT] BulletVfxBank is a ScriptableObject; safe to read on main thread in OnCreate.
            var bank = BulletVfxBank.LoadDefault();
            _categoryCount = bank != null ? bank.CategoryCount : 0;
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// For each simulated ship with CycleBullet pressed this tick, advance RuntimeBulletIndex.
        /// Client uses <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/> so rollback/resim
        /// does not increment the index multiple times for one press.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Guard ---
            if (_categoryCount < 1)
                return;

            // --- One-shot guard (client prediction) ---
            // [NETCODE] Partial ticks / rollback re-run this system; only apply once per full tick.
            // [NETCODE] World.IsServer — NetCode extension; client must one-shot-guard prediction.
            if (!state.World.IsServer())
            {
                // [TITAN-ORBIT] TeamChoice Instantiates — skip client ship Query until safe.
                if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                    return;

                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                if (!networkTime.IsFirstTimeFullyPredictingTick)
                    return;
            }

            // --- Cycle pass ---
            foreach (var (input, loadout) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRW<ShipLoadoutState>>()
                         .WithAll<ShipTag, Simulate>())
            {
                // [NETCODE] InputEvent.IsSet — true on the tick the client called Set().
                if (!input.ValueRO.CycleBullet.IsSet)
                    continue;

                int current = loadout.ValueRO.RuntimeBulletIndex;
                // Negative / stale → start at 0; otherwise wrap (current+1) % count.
                int next = current < 0 ? 0 : (current + 1) % _categoryCount;
                loadout.ValueRW.RuntimeBulletIndex = next;
            }
        }
    }
}
