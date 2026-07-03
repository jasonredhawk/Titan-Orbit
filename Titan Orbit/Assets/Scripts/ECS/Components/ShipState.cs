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
        /// <summary>Fallback hull mass when <see cref="HullMassReference"/> is unset (legacy baseMass).</summary>
        public float Mass;
        public float RecoilDecayPerSecond;
        /// <summary>Chassis component mass × hull mass scale (excludes HP bulk and gems).</summary>
        public float HullMassReference;
        /// <summary>Level-1 max health used to soften movement mass at higher ship levels.</summary>
        public float ChassisReferenceHealth;
    }

    public struct ShipWeaponConfig : IComponentData
    {
        public float FireRate;
        public float BulletSpeed;
        public float BulletDamage;
        /// <summary>Energy spent per shot (legacy: equals fire power / bullet damage).</summary>
        public float EnergyCostPerShot;
        public float BulletLifetime;
        public float BulletMaxDistance;
        public float MuzzleOffset;
        /// <summary>Authored cannon bullet scale (WeaponConfig.cannons[].bulletScale).</summary>
        public float BulletScale;
        /// <summary>Level-1 baseline used to derive upgrade VFX growth from current damage/speed.</summary>
        public float ReferenceBulletDamage;
        public float ReferenceBulletSpeed;
    }

    /// <summary>Regen rates from ship-family stats; applied server-side each tick.</summary>
    public struct ShipVitalsConfig : IComponentData
    {
        public float HealthRegenPerSecond;
        public float EnergyRegenPerSecond;
        public float HealthRegenDelayAfterDamage;
    }

    public struct ShipVitalsState : IComponentData
    {
        public double LastHullDamageTime;
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
