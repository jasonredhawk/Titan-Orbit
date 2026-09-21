using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Applies arsenal HUD clicks to the ghosted <see cref="ShipWeaponArmState"/> mask.
    /// Runs in predicted simulation on client and server so a tap mutes a barrel
    /// on the same tick local fire planning reads the mask.
    /// <para>
    /// [NETCODE] Client uses <see cref="NetworkTime.IsFirstTimeFullyPredictingTick"/>
    /// so rollback / resim does not apply the same press twice (same rule as
    /// <see cref="ShipCycleBulletSystem"/>).
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Mode is an explicit set (arm or mute), not a toggle. Toggling
    /// on both client prediction and server from a stale snapshot would flip twice
    /// and leave the gun in the wrong state.
    /// </para>
    /// World: ServerSimulation and ClientSimulation. Group: PredictedSimulationSystemGroup.
    /// </summary>
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(ShipCycleBulletSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipWeaponArmSystem : ISystem
    {
        /// <summary>[NETCODE] Wait until the connection is in-game before reading ship input.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// For each simulated ship with SetWeaponArm this tick, write the mount or
        /// kind bit onto <see cref="ShipWeaponArmState"/>.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!state.World.IsServer())
            {
                // [TITAN-ORBIT] Join Team Instantiates — skip ship WithEntityAccess.
                if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                    return;

                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                if (!networkTime.IsFirstTimeFullyPredictingTick)
                    return;
            }

            foreach (var (input, arm, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRW<ShipWeaponArmState>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithEntityAccess())
            {
                if (!input.ValueRO.SetWeaponArm.IsSet)
                    continue;

                byte mode = input.ValueRO.WeaponArmMode;
                int index = input.ValueRO.WeaponArmIndex;
                bool enabled = input.ValueRO.WeaponArmEnabled != 0;
                var next = arm.ValueRO;

                // --- Kind: mute / arm every barrel of that class ---
                // [TITAN-ORBIT] "all missiles off, leave lasers" is one click on the
                // LASER / MISSILE header. Regular family barrels are all kind 0 (gun).
                if (mode == ShipWeaponArmState.ModeKind)
                {
                    if (index < 0 || index > 255)
                        continue;
                    if (!state.EntityManager.HasBuffer<ShipWeaponMountElement>(entity))
                        continue;
                    var mounts = state.EntityManager.GetBuffer<ShipWeaponMountElement>(entity);
                    ShipWeaponArmState.SetKind(ref next, mounts, (byte)index, enabled);
                    arm.ValueRW = next;
                    continue;
                }

                // --- Mount: one barrel ---
                if (index < 0 || index >= ShipWeaponArmState.MaxTrackedMounts)
                    continue;
                if (state.EntityManager.HasBuffer<ShipWeaponMountElement>(entity))
                {
                    int mountCount = state.EntityManager.GetBuffer<ShipWeaponMountElement>(entity).Length;
                    if (index >= mountCount)
                        continue;
                }

                ShipWeaponArmState.SetMount(ref next, index, enabled);
                arm.ValueRW = next;
            }
        }
    }
}
