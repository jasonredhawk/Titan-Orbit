using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Safety net that adds missing runtime ship components at load time. Authoring should bake
    /// everything via <see cref="Authoring.StarshipGhostAuthoring"/>, but this system ensures
    /// older prefabs and runtime-spawned ships never hit null-component errors in motor hot paths.
    /// Runs in <see cref="SimulationSystemGroup"/> (variable step) before the predicted fixed-step motor.
    /// Uses EntityCommandBuffer so structural changes don't invalidate parallel queries mid-frame.
    /// <para>
    /// [ECS/DOTS] Do not UpdateBefore systems in <see cref="PredictedFixedStepSimulationSystemGroup"/> —
    /// those are a different group instance and Unity logs invalid-attribute warnings every world create.
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipEnsureComponentsSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Client: ship WithEntityAccess during GhostSpawn Instantiates Crash!!!
            // (TeamChoiceResult — Settling OFF, backlog ON). Server always runs. Local Host shares
            // ClientJoinSettleCache statics — must gate with IsClient(), not the flag alone.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Kinematics mirror (velocity for gameplay reads) ---
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipKinematics>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipKinematics());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<PhysicsDamping>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new PhysicsDamping { Linear = 0.15f, Angular = 2f });

            // --- Weapon defaults (overwritten by ShipStatApplyLogic when chassis applies) ---
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipWeaponConfig>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new ShipWeaponConfig
                {
                    FireRate = 2f,
                    BulletSpeed = 20f,
                    BulletDamage = 8f,
                    EnergyCostPerShot = 8f,
                    BulletLifetime = 2f,
                    BulletMaxDistance = ShipWeaponConfig.DefaultBulletMaxDistance,
                    MuzzleOffset = 2f,
                    BulletScale = 1f,
                    ReferenceBulletDamage = 8f,
                    ReferenceBulletSpeed = 20f,
                });
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipVitalsConfig>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new ShipVitalsConfig
                {
                    HealthRegenPerSecond = 6f,
                    EnergyRegenPerSecond = 5f,
                    HealthRegenDelayAfterDamage = 0.35f,
                });
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipVitalsState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipVitalsState());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipWeaponState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipWeaponState());

            // --- Weapon mounts and wing tractor beams (DynamicBuffer for multi-mount ships) ---
            // [TITAN-ORBIT] Empty mount buffer = intentional unarmed ship — never inject a fake muzzle.
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>().WithEntityAccess())
            {
                if (!state.EntityManager.HasBuffer<ShipWeaponMountElement>(entity))
                    ecb.AddBuffer<ShipWeaponMountElement>(entity);

                if (!state.EntityManager.HasBuffer<ShipWingTractorBeamElement>(entity))
                    ecb.AddBuffer<ShipWingTractorBeamElement>(entity);
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipOrbitState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipOrbitState());

            // --- Friendly-triangle speed sticky latch (not ghosted; motor recomputes each tick) ---
            // [TITAN-ORBIT] Without this, IJobEntity ShipPhysicsDriveJob skips ships missing the
            // component and territory boost never applies on older prefabs.
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipTerritoryBoostLatch>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new ShipTerritoryBoostLatch
                {
                    LatchedMult = 1f,
                    HoldUntilElapsed = -1.0,
                });
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipMoonDockState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipMoonDockState());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipDepositIntent>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipDepositIntent());

            // --- Ghosted deposit beat feedback (SFX / Orbit Menu follow BeatSequence) ---
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipDepositFeedback>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipDepositFeedback());

            // --- Server-only deposit metronome timer (not ghosted) ---
            // [TITAN-ORBIT] Clients present from ShipDepositFeedback; only the server sim needs Accum.
            if (state.World.IsServer())
            {
                foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                             .WithNone<ShipDepositBeatTimer>()
                             .WithEntityAccess())
                    ecb.AddComponent(entity, new ShipDepositBeatTimer());
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipLoadoutState());

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>().WithEntityAccess())
            {
                if (!state.EntityManager.HasBuffer<EquippedEquipmentElement>(entity))
                    ecb.AddBuffer<EquippedEquipmentElement>(entity);
            }

            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                         .WithNone<ShipPeopleTransferState>()
                         .WithEntityAccess())
                ecb.AddComponent(entity, new ShipPeopleTransferState());

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
