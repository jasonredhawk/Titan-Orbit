using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    public struct ShipState : IComponentData
    {
        [GhostField] public float Health;
        [GhostField] public float MaxHealth;
        [GhostField] public TeamId Team;
        [GhostField] public int ShipLevel;
        [GhostField] public float CurrentGems;
        [GhostField] public float GemCapacity;
        [GhostField] public float CurrentEnergy;
        [GhostField] public float MaxEnergy;
        [GhostField] public int CurrentPeople;
        [GhostField] public int PeopleCapacity;
        [GhostField] public bool IsDead;
        [GhostField] public bool AwaitingTeamSelection;
    }

    public struct ShipMotorConfig : IComponentData
    {
        public float EngineThrust;
        public float MaxSpeed;
        public float RotationSpeed;
        public float BrakeDeceleration;
        public float Mass;
        public float RecoilDecayPerSecond;
    }

    public struct ShipWeaponConfig : IComponentData
    {
        public float FireRate;
        public float BulletSpeed;
        public float BulletDamage;
        public float BulletLifetime;
        public float BulletMaxDistance;
        public float MuzzleOffset;
        /// <summary>Authored cannon bullet scale (WeaponConfig.cannons[].bulletScale).</summary>
        public float BulletScale;
        /// <summary>Level-1 baseline used to derive upgrade VFX growth from current damage/speed.</summary>
        public float ReferenceBulletDamage;
        public float ReferenceBulletSpeed;
    }

    public struct ShipWeaponState : IComponentData
    {
        public float FireCooldown;
        public int NextMountIndex;
    }

    public struct ShipKinematics : IComponentData
    {
        public float3 Velocity;
    }

    public struct ShipTag : IComponentData { }

    public struct LocalPlayerShipTag : IComponentData { }
}
