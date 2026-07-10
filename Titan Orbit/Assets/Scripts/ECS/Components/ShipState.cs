using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Core replicated ship vitals and economy state. Ghost — a networked entity copy replicated
    /// to all clients (NetCode term). Fields marked [GhostField] serialize over the network.
    /// Read by movement, combat, HUD, and orbit systems; written by server sim and RPC handlers.
    /// </summary>
    public struct ShipState : IComponentData
    {
        /// <summary>Current hull points; lethal at zero (server sets IsDead).</summary>
        [GhostField] public float Health;
        /// <summary>Maximum hull from chassis stats + upgrades.</summary>
        [GhostField] public float MaxHealth;
        /// <summary>Team assignment; None until player picks a team at spawn.</summary>
        [GhostField] public TeamId Team;
        /// <summary>Upgrade ladder level (1 = starter chassis).</summary>
        [GhostField] public int ShipLevel;
        /// <summary>Gems currently stored in the ship cargo hold.</summary>
        [GhostField] public float CurrentGems;
        /// <summary>Maximum gem cargo from chassis + wing tractor stats.</summary>
        [GhostField] public float GemCapacity;
        /// <summary>Weapon energy pool; shots consume this (regen in ShipVitalsRegenSystem).</summary>
        [GhostField] public float CurrentEnergy;
        [GhostField] public float MaxEnergy;
        /// <summary>Population units aboard (people transport gameplay).</summary>
        [GhostField] public int CurrentPeople;
        [GhostField] public int PeopleCapacity;
        /// <summary>True after lethal damage; movement and weapons disabled until respawn.</summary>
        [GhostField] public bool IsDead;
        /// <summary>True at spawn until RequestTeamCommand assigns a team.</summary>
        [GhostField] public bool AwaitingTeamSelection;
    }

    /// <summary>
    /// Motor tuning derived from chassis stats. Not ghost-serialized — recomputed server-side
    /// by <see cref="ShipStatApplyLogic"/> when level or branch changes. Read by movement job.
    /// </summary>
    public struct ShipMotorConfig : IComponentData
    {
        /// <summary>Engine force in Newtons (acceleration = thrust / mass).</summary>
        public float EngineThrust;
        /// <summary>Top speed cap in world units per second.</summary>
        public float MaxSpeed;
        /// <summary>Turn rate in degrees per second toward aim point.</summary>
        public float RotationSpeed;
        /// <summary>Space-brake deceleration magnitude.</summary>
        public float BrakeDeceleration;
        /// <summary>Fallback hull mass when <see cref="HullMassReference"/> is unset (legacy baseMass).</summary>
        public float Mass;
        /// <summary>How fast excess speed (recoil) bleeds off per second.</summary>
        public float RecoilDecayPerSecond;
        /// <summary>Chassis component mass × hull mass scale (excludes HP bulk and gems).</summary>
        public float HullMassReference;
        /// <summary>Level-1 max health used to soften movement mass at higher ship levels.</summary>
        public float ChassisReferenceHealth;
    }

    /// <summary>
    /// Cannon stats for <see cref="BulletSimulationSystem"/>. Applied from chassis data;
    /// not individually ghost-serialized (clients infer from replicated ship level).
    /// </summary>
    public struct ShipWeaponConfig : IComponentData
    {
        public float FireRate;
        public float BulletSpeed;
        public float BulletDamage;
        /// <summary>Energy spent per shot (legacy: equals fire power / bullet damage).</summary>
        public float EnergyCostPerShot;
        public float BulletLifetime;
        public float BulletMaxDistance;
        /// <summary>Fallback muzzle offset when no weapon mount buffer exists.</summary>
        public float MuzzleOffset;
        /// <summary>Authored cannon bullet scale (WeaponConfig.cannons[].bulletScale).</summary>
        public float BulletScale;
        /// <summary>Level-1 baseline used to derive upgrade VFX growth from current damage/speed.</summary>
        public float ReferenceBulletDamage;
        public float ReferenceBulletSpeed;
    }

    /// <summary>
    /// Regen rates from ship-family stats; applied server-side each tick by
    /// <see cref="ShipVitalsRegenSystem"/>.
    /// </summary>
    public struct ShipVitalsConfig : IComponentData
    {
        public float HealthRegenPerSecond;
        public float EnergyRegenPerSecond;
        /// <summary>Seconds after hull damage before health regen resumes.</summary>
        public float HealthRegenDelayAfterDamage;
    }

    /// <summary>Server-only timestamp for health regen delay tracking.</summary>
    public struct ShipVitalsState : IComponentData
    {
        public double LastHullDamageTime;
    }

    /// <summary>Per-ship weapon cooldown and round-robin mount index (server sim).</summary>
    public struct ShipWeaponState : IComponentData
    {
        public float FireCooldown;
        public int NextMountIndex;
    }

    /// <summary>
    /// Gameplay-readable velocity mirror of physics linear velocity. Ghost-serialized for
    /// remote interpolation and HUD. Kept in sync by <see cref="ShipMovementBurstLogic"/>.
    /// </summary>
    public struct ShipKinematics : IComponentData
    {
        [GhostField(Quantization = 1000)]
        public float3 Velocity;
    }

    /// <summary>Marker — entity is a player or AI starship (used in queries across all ship systems).</summary>
    public struct ShipTag : IComponentData { }

    /// <summary>
    /// Client-only tag on the connection-owned ship. Used by input and presentation systems
    /// to find "my ship" without scanning GhostOwner every frame in MonoBehaviour code.
    /// </summary>
    public struct LocalPlayerShipTag : IComponentData { }
}
