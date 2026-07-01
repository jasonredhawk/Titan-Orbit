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
                    BulletLifetime = authoring.BulletLifetime,
                    BulletMaxDistance = authoring.BulletMaxDistance,
                    MuzzleOffset = authoring.MuzzleOffset,
                });
                AddComponent(entity, new ShipWeaponState());
                AddComponent(entity, new ShipInput());
                AddComponent(entity, new ShipKinematics());
                BakeWeaponMounts(authoring, entity);
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
        }
    }
}
