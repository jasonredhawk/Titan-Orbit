using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// MonoBehaviour authoring component on ship ghost prefabs. The Baker converts this GameObject
    /// hierarchy into an ECS entity with ShipTag, motor/weapon/vitals components, weapon mount buffers,
    /// wing tractor beam buffers, and a placeholder Unity Physics sphere collider replaced at runtime
    /// by <see cref="ShipHullColliderLogic"/> from the chassis visual prefab. Baked into SubScenes
    /// for NetCode ghost replication. Paired with StarshipGhost prefab variants under Assets/Prefabs/Ships/.
    /// </summary>
    public class StarshipGhostAuthoring : MonoBehaviour
    {
        [Header("Motor (level-1 defaults — ShipStatApplyLogic overwrites from chassis)")]
        public float EngineThrust = 40f;
        public float MaxSpeed = 35f;
        public float RotationSpeed = 180f;
        public float BrakeDeceleration = 7f;
        public float Mass = 1f;
        public float RecoilDecayPerSecond = 6f;

        [Header("Weapon (level-1 cannon defaults)")]
        public float FireRate = 2f;
        public float BulletSpeed = 20f;
        public float BulletDamage = 8f;
        public float BulletLifetime = 2f;
        public float BulletMaxDistance = 30f;
        public float MuzzleOffset = 2f;
        public float BulletScale = 1f;

        class Baker : Baker<StarshipGhostAuthoring>
        {
            public override void Bake(StarshipGhostAuthoring authoring)
            {
                // [ECS/DOTS] GetEntity registers this GameObject as a baked entity in the SubScene.
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                // --- Core ship components ---
                AddComponent(entity, new ShipTag());
                AddComponent(entity, new ShipState
                {
                    Health = 100f,
                    MaxHealth = 100f,
                    ShipLevel = 1,
                    BranchIndex = 0,
                    GemCapacity = 50f,
                    CurrentEnergy = 50f,
                    MaxEnergy = 50f,
                    PeopleCapacity = 10,
                    // [TITAN-ORBIT] New ships wait for RequestTeamCommand before movement is enabled.
                    AwaitingTeamSelection = true,
                });
                // [NETCODE] ShipLoadoutState MUST be baked — GhostFields (incl. RuntimeBulletIndex)
                // do not replicate when the component is only added at runtime by
                // ShipEnsureComponentsSystem. B-key bullet cycle depends on this ghosting.
                AddComponent(entity, new ShipLoadoutState
                {
                    RocketCount = 0,
                    MineCount = 0,
                    RuntimeBulletIndex = 0,
                    BranchIndex = 0,
                    ChassisIndex = 0,
                });
                AddComponent(entity, new ShipMotorConfig
                {
                    EngineThrust = authoring.EngineThrust,
                    MaxSpeed = authoring.MaxSpeed,
                    RotationSpeed = authoring.RotationSpeed,
                    BrakeDeceleration = authoring.BrakeDeceleration,
                    Mass = authoring.Mass,
                    RecoilDecayPerSecond = authoring.RecoilDecayPerSecond,
                });
                AddComponent(entity, new ShipWeaponConfig
                {
                    FireRate = authoring.FireRate,
                    BulletSpeed = authoring.BulletSpeed,
                    BulletDamage = authoring.BulletDamage,
                    EnergyCostPerShot = authoring.BulletDamage,
                    BulletLifetime = authoring.BulletLifetime,
                    BulletMaxDistance = authoring.BulletMaxDistance,
                    MuzzleOffset = authoring.MuzzleOffset,
                    BulletScale = authoring.BulletScale,
                    ReferenceBulletDamage = authoring.BulletDamage,
                    ReferenceBulletSpeed = authoring.BulletSpeed,
                });
                AddComponent(entity, new ShipVitalsConfig
                {
                    HealthRegenPerSecond = 6f,
                    EnergyRegenPerSecond = 5f,
                    HealthRegenDelayAfterDamage = 0.35f,
                });
                AddComponent(entity, new ShipVitalsState());
                AddComponent(entity, new ShipAttributeUpgradeState());
                AddComponent(entity, new ShipWeaponState());
                AddComponent(entity, new ShipOrbitState());
                AddComponent(entity, new ShipMoonDockState());
                AddComponent(entity, new ShipDepositIntent());
                // [NETCODE] Server bumps BeatSequence each deposit chunk; clients play SFX/UI from it.
                AddComponent(entity, new ShipDepositFeedback());
                // [NETCODE] ShipInput is IInputComponentData — replicated from owner client each tick.
                AddComponent(entity, new ShipInput());
                AddComponent(entity, new ShipKinematics());
                BakeWeaponMounts(authoring, entity);
                BakeWingTractorBeams(authoring, entity);
                BakeShipPhysicsBody(entity, authoring.Mass);
            }

            /// <summary>
            /// Bakes a fully dynamic Unity Physics sphere collider. Thrust and brakes are applied by
            /// <see cref="ShipPhysicsDriveSystem"/> via impulses and <see cref="PhysicsDamping"/>.
            /// </summary>
            void BakeShipPhysicsBody(Entity shipEntity, float mass)
            {
                float radius = BodyCollisionMath.GetShipHullRadiusWorld(1f);
                var material = Unity.Physics.Material.Default;
                material.Restitution = 0.15f;
                material.Friction = 0.05f;

                var collider = Unity.Physics.SphereCollider.Create(
                    new SphereGeometry { Center = float3.zero, Radius = radius },
                    TitanOrbitPhysicsLayers.Ship,
                    material);
                AddBlobAsset(ref collider, out _);
                AddComponent(shipEntity, new PhysicsCollider { Value = collider });
                AddSharedComponent(shipEntity, new PhysicsWorldIndex(0));
                AddComponent(shipEntity, PhysicsVelocity.Zero);

                var physicsMass = PhysicsMass.CreateDynamic(collider.Value.MassProperties, math.max(0.5f, mass));
                AddComponent(shipEntity, physicsMass);
                AddComponent(shipEntity, new PhysicsGravityFactor { Value = 0f });
                AddComponent(shipEntity, new PhysicsDamping { Linear = 0.15f, Angular = 2f });
            }

            /// <summary>
            /// Collects child ShipWeaponMountAuthoring transforms into a DynamicBuffer for
            /// multi-cannon ships. Falls back to "Weapon" named children. Empty buffer = unarmed
            /// (no synthetic centerline muzzle).
            /// </summary>
            void BakeWeaponMounts(StarshipGhostAuthoring authoring, Entity shipEntity)
            {
                var mounts = AddBuffer<ShipWeaponMountElement>(shipEntity);
                Transform hullRoot = authoring.transform;
                var mountAuthorings = authoring.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);
                for (int i = 0; i < mountAuthorings.Length; i++)
                {
                    var mount = mountAuthorings[i];
                    if (mount == null || mount.transform == hullRoot)
                        continue;

                    ShipChassisPrefabBakeUtility.GetHullRootLocalPose(
                        hullRoot, mount.transform, out float3 localPos, out quaternion localRot);
                    mounts.Add(new ShipWeaponMountElement
                    {
                        LocalPosition = localPos,
                        LocalRotation = ShipChassisPrefabBakeUtility.ToPlanarYawLocalRotation(localRot),
                        DirectionAngleDeg = mount.DirectionAngleDeg,
                        CannonIndex = mount.CannonIndex,
                    });
                }

                if (mounts.Length == 0)
                {
                    foreach (var t in authoring.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == hullRoot || !ShipChassisPrefabBakeUtility.LooksLikeWeaponChildForBake(t))
                            continue;

                        ShipChassisPrefabBakeUtility.GetHullRootLocalPose(
                            hullRoot, t, out float3 localPos, out quaternion localRot);
                        mounts.Add(new ShipWeaponMountElement
                        {
                            LocalPosition = localPos,
                            LocalRotation = ShipChassisPrefabBakeUtility.ToPlanarYawLocalRotation(localRot),
                            DirectionAngleDeg = 0f,
                            CannonIndex = mounts.Length,
                        });
                    }
                }
                else
                {
                    // [TITAN-ORBIT] Prefabs often leave every CannonIndex at 0 — uniquify so
                    // round-robin slots stay paired with the same live barrel.
                    EnsureUniqueBakedCannonIndices(mounts);
                }
                // [TITAN-ORBIT] Intentionally no MuzzleOffset fallback — unarmed ships stay empty.
            }

            /// <summary>
            /// Rewrites all-equal CannonIndex values to 0..N-1 in buffer order.
            /// </summary>
            static void EnsureUniqueBakedCannonIndices(DynamicBuffer<ShipWeaponMountElement> mounts)
            {
                if (mounts.Length <= 1)
                    return;

                bool allSame = true;
                int first = mounts[0].CannonIndex;
                for (int i = 1; i < mounts.Length; i++)
                {
                    if (mounts[i].CannonIndex != first)
                    {
                        allSame = false;
                        break;
                    }
                }

                if (!allSame)
                    return;

                for (int i = 0; i < mounts.Length; i++)
                {
                    var m = mounts[i];
                    m.CannonIndex = i;
                    mounts[i] = m;
                }
            }

            /// <summary>
            /// Collects wing tractor beam child authorings for gem collection gameplay.
            /// Stores hull-root-local unscaled offsets (not immediate-parent localPosition).
            /// </summary>
            void BakeWingTractorBeams(StarshipGhostAuthoring authoring, Entity shipEntity)
            {
                var wings = AddBuffer<ShipWingTractorBeamElement>(shipEntity);
                Transform hullRoot = authoring.transform;
                var wingAuthorings = authoring.GetComponentsInChildren<ShipWingTractorBeamAuthoring>(true);
                for (int i = 0; i < wingAuthorings.Length; i++)
                {
                    var wing = wingAuthorings[i];
                    if (wing == null || wing.transform == hullRoot)
                        continue;

                    // [TITAN-ORBIT] Same hull-root rule as BakeWeaponMounts — nested wings on upgrade
                    // chassis were baking parent-local offsets and drawing beams outside the hull.
                    ShipChassisPrefabBakeUtility.GetHullRootLocalPose(
                        hullRoot, wing.transform, out float3 localPos, out _);
                    wings.Add(new ShipWingTractorBeamElement
                    {
                        LocalPosition = localPos,
                        TractorBeamDistance = wing.tractorBeamDistance,
                        TractorBeamDistancePerLevel = wing.tractorBeamDistancePerLevel,
                        TractorBeamPower = wing.tractorBeamPower,
                        TractorBeamPowerPerLevel = wing.tractorBeamPowerPerLevel,
                        MaxGems = wing.maxGems,
                        MaxGemsPerLevel = wing.maxGemsPerLevel,
                    });
                }

                if (wings.Length == 0)
                {
                    foreach (var t in authoring.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == hullRoot || !t.name.Contains("Wing"))
                            continue;
                        if (t.name.Contains("Weapon"))
                            continue;

                        ShipChassisPrefabBakeUtility.GetHullRootLocalPose(
                            hullRoot, t, out float3 localPos, out _);
                        wings.Add(new ShipWingTractorBeamElement
                        {
                            LocalPosition = localPos,
                            TractorBeamDistance = 3f,
                            TractorBeamDistancePerLevel = 0.75f,
                            TractorBeamPower = 4f,
                            TractorBeamPowerPerLevel = 1f,
                            MaxGems = 8f,
                            MaxGemsPerLevel = 2f,
                        });
                    }
                }
            }
        }
    }
}
