using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Formerly copied live hull GO weapon transforms into <see cref="ShipWeaponMountElement"/> each
    /// server frame. That path is intentionally disabled.
    /// <para>
    /// [TITAN-ORBIT] Visual bank lives on <c>BankPivot</c> (client cosmetic). ECS ship rotation is
    /// yaw-only. Syncing banked GO → hull-root locals, then resolving with unbanked
    /// <see cref="Unity.Transforms.LocalTransform"/> lifted muzzles above the real barrels.
    /// </para>
    /// <para>
    /// Sim mounts stay catalog/bake unbanked locals. Local bullet VFX read live weapon
    /// <see cref="UnityEngine.Transform"/> poses via <see cref="BulletMuzzlePresentation"/>.
    /// System kept so update-order attributes and asmdef wiring stay stable.
    /// </para>
    /// </summary>
    // [ECS/DOTS] ShipPhysicsDriveSystem is in PredictedFixedStepSimulationSystemGroup — cannot
    // UpdateAfter that type from SimulationSystemGroup (Unity ignores + warns).
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ShipWeaponMountSyncSystem : SystemBase
    {
        /// <summary>
        /// No-op: do not overwrite mount buffers from banked hybrid GOs.
        /// </summary>
        protected override void OnUpdate()
        {
            // Intentionally empty — see type summary (bank ≠ ECS yaw).
        }
    }
}
