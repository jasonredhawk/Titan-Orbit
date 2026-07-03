using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class StarshipGhostAuthoring : MonoBehaviour
    {
        public float EngineThrust = 40f;
        public float MaxSpeed = 35f;
        public float RotationSpeed = 180f;
        public float BrakeDeceleration = 25f;
        public float Mass = 5f;
        public float RecoilDecayPerSecond = 6f;

        [Header("Weapon (level-1 cannon defaults)")]
        public float FireRate = 2f;
        public float BulletSpeed = 20f;
        public float BulletDamage = 8f;
        public float BulletLifetime = 3f;
        public float BulletMaxDistance = 200f;
        public float MuzzleOffset = 2f;
        public float BulletScale = 1f;

        [Header("Collision (optional bake for headless server)")]
        [Tooltip("Chassis prefab used to bake module BoxColliders into the ship ghost. Leave empty to rely on client hull sync.")]
        public GameObject collisionChassisPrefab;

        class Baker : Baker<StarshipGhostAuthoring>
        {
            public override void Bake(StarshipGhostAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new ShipTag());
                AddComponent(entity, new ShipState
                {
                    Health = 100f,
                    MaxHealth = 100f,
                    ShipLevel = 1,
                    GemCapacity = 50f,
                    CurrentEnergy = 50f,
                    MaxEnergy = 50f,
                    PeopleCapacity = 10,
                    AwaitingTeamSelection = true,
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
                AddComponent(entity, new ShipInput());
                AddComponent(entity, new ShipKinematics());
                BakeWeaponMounts(authoring, entity);
                BakeWingTractorBeams(authoring, entity);
                BakeHullColliders(authoring, entity);
            }

            void BakeHullColliders(StarshipGhostAuthoring authoring, Entity shipEntity)
            {
                var hull = AddBuffer<ShipHullColliderElement>(shipEntity);
                if (authoring.collisionChassisPrefab == null)
                    return;

#if UNITY_EDITOR
                var temp = Object.Instantiate(authoring.collisionChassisPrefab);
                try
                {
                    foreach (var element in ShipHullColliderBakeUtility.CollectFromHierarchy(temp.transform))
                        hull.Add(element);
                }
                finally
                {
                    Object.DestroyImmediate(temp);
                }
#endif
            }

            void BakeWeaponMounts(StarshipGhostAuthoring authoring, Entity shipEntity)
            {
                var mounts = AddBuffer<ShipWeaponMountElement>(shipEntity);
                var mountAuthorings = authoring.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);
                for (int i = 0; i < mountAuthorings.Length; i++)
                {
                    var mount = mountAuthorings[i];
                    if (mount == null || mount.transform == authoring.transform)
                        continue;

                    var t = mount.transform;
                    mounts.Add(new ShipWeaponMountElement
                    {
                        LocalPosition = t.localPosition,
                        LocalRotation = t.localRotation,
                        DirectionAngleDeg = mount.DirectionAngleDeg,
                        CannonIndex = mount.CannonIndex,
                    });
                }

                if (mounts.Length == 0)
                {
                    foreach (var t in authoring.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == authoring.transform || !t.name.Contains("Weapon"))
                            continue;

                        mounts.Add(new ShipWeaponMountElement
                        {
                            LocalPosition = t.localPosition,
                            LocalRotation = t.localRotation,
                            DirectionAngleDeg = 0f,
                            CannonIndex = mounts.Length,
                        });
                    }
                }

                if (mounts.Length == 0)
                {
                    mounts.Add(new ShipWeaponMountElement
                    {
                        LocalPosition = new float3(0f, 0f, authoring.MuzzleOffset),
                        LocalRotation = quaternion.identity,
                        DirectionAngleDeg = 0f,
                        CannonIndex = 0,
                    });
                }
            }

            void BakeWingTractorBeams(StarshipGhostAuthoring authoring, Entity shipEntity)
            {
                var wings = AddBuffer<ShipWingTractorBeamElement>(shipEntity);
                var wingAuthorings = authoring.GetComponentsInChildren<ShipWingTractorBeamAuthoring>(true);
                for (int i = 0; i < wingAuthorings.Length; i++)
                {
                    var wing = wingAuthorings[i];
                    if (wing == null || wing.transform == authoring.transform)
                        continue;

                    var t = wing.transform;
                    wings.Add(new ShipWingTractorBeamElement
                    {
                        LocalPosition = t.localPosition,
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
                        if (t == authoring.transform || !t.name.Contains("Wing"))
                            continue;
                        if (t.name.Contains("Weapon"))
                            continue;

                        wings.Add(new ShipWingTractorBeamElement
                        {
                            LocalPosition = t.localPosition,
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
