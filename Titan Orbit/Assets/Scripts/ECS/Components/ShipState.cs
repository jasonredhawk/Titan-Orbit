using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Core replicated ship vitals and economy state. Ghost — a networked entity copy
    /// replicated to all clients. Fields marked [GhostField] serialize over the network. Read by
    /// movement, combat, HUD, and orbit systems; written by server sim and RPC handlers.
    /// </summary>
    public struct ShipState : IComponentData
    {
        // --- Type members ---
        /// <summary>[TITAN-ORBIT] Current hull points; lethal at zero (server sets IsDead).</summary>
        [GhostField] public float Health;

        /// <summary>[TITAN-ORBIT] Maximum hull from chassis stats + attribute upgrades.</summary>
        [GhostField] public float MaxHealth;

        /// <summary>[TITAN-ORBIT] Team assignment; None until player picks a team at spawn.</summary>
        [GhostField] public TeamId Team;

        /// <summary>[TITAN-ORBIT] Upgrade ladder level (1 = starter chassis).</summary>
        [GhostField] public int ShipLevel;

        /// <summary>
        /// [TITAN-ORBIT] Upgrade-tree branch within <see cref="ShipLevel"/> (0-based).
        /// Ghosted on ShipState so clients rebuild the correct hull after moon-store purchases —
        /// <see cref="ShipLoadoutState.BranchIndex"/> alone was not baked on older ship ghosts and
        /// did not reliably replicate, so every client stayed on branch 0.
        /// </summary>
        [GhostField] public int BranchIndex;

        /// <summary>[TITAN-ORBIT] Gems currently stored in the ship cargo hold.</summary>
        [GhostField] public float CurrentGems;

        /// <summary>[TITAN-ORBIT] Maximum gem cargo from chassis + wing tractor stats.</summary>
        [GhostField] public float GemCapacity;

        /// <summary>[TITAN-ORBIT] Weapon energy pool; shots consume this (regen in ShipVitalsRegenSystem).</summary>
        [GhostField] public float CurrentEnergy;

        /// <summary>[TITAN-ORBIT] Maximum energy from chassis stats.</summary>
        [GhostField] public float MaxEnergy;

        /// <summary>[TITAN-ORBIT] Population units aboard (people transport gameplay).</summary>
        [GhostField] public int CurrentPeople;

        /// <summary>[TITAN-ORBIT] Maximum population cargo capacity.</summary>
        [GhostField] public int PeopleCapacity;

        /// <summary>[TITAN-ORBIT] True after lethal damage; movement and weapons disabled until respawn.</summary>
        [GhostField] public bool IsDead;

        /// <summary>[TITAN-ORBIT] True at spawn until RequestTeamCommand assigns a team.</summary>
        [GhostField] public bool AwaitingTeamSelection;
    }

    /// <summary>
    /// [ECS/DOTS] Motor tuning derived from chassis stats. Not ghost-serialized — recomputed server-side
    /// by <see cref="ShipStatApplyLogic"/> when level or branch changes. Read by movement job.
    /// </summary>
    public struct ShipMotorConfig : IComponentData
    {
        /// <summary>[PHYSICS] Engine force in Newtons (acceleration = thrust / mass).</summary>
        public float EngineThrust;

        /// <summary>[TITAN-ORBIT] Top speed cap in world units per second.</summary>
        public float MaxSpeed;

        /// <summary>[TITAN-ORBIT] Turn rate in degrees per second toward aim point.</summary>
        public float RotationSpeed;

        /// <summary>[TITAN-ORBIT] Space-brake deceleration magnitude.</summary>
        public float BrakeDeceleration;

        /// <summary>[PHYSICS] Fallback hull mass when HullMassReference is unset.</summary>
        public float Mass;

        /// <summary>[TITAN-ORBIT] How fast excess speed (recoil) bleeds off per second.</summary>
        public float RecoilDecayPerSecond;

        /// <summary>[PHYSICS] Chassis component mass × hull mass scale (excludes HP bulk and gems).</summary>
        public float HullMassReference;

        /// <summary>[TITAN-ORBIT] Level-1 max health used to soften movement mass at higher ship levels.</summary>
        public float ChassisReferenceHealth;
    }

    /// <summary>
    /// [ECS/DOTS] Cannon stats for <see cref="BulletSimulationSystem"/>. Applied from chassis data;
    /// not individually ghost-serialized (clients infer from replicated ship level).
    /// </summary>
    public struct ShipWeaponConfig : IComponentData
    {
        /// <summary>[TITAN-ORBIT] Minimum seconds between shots.</summary>
        public float FireRate;

        /// <summary>[TITAN-ORBIT] Bullet speed in world units per second.</summary>
        public float BulletSpeed;

        /// <summary>[TITAN-ORBIT] Damage per bullet on hit.</summary>
        public float BulletDamage;

        /// <summary>[TITAN-ORBIT] Energy spent per shot.</summary>
        public float EnergyCostPerShot;

        /// <summary>[UNITY] Bullet lifetime in seconds.</summary>
        public float BulletLifetime;

        /// <summary>[TITAN-ORBIT] Maximum bullet travel distance.</summary>
        public float BulletMaxDistance;

        /// <summary>[TITAN-ORBIT] Fallback muzzle offset when no weapon mount buffer exists.</summary>
        public float MuzzleOffset;

        /// <summary>[TITAN-ORBIT] Authored cannon bullet scale from WeaponConfig.</summary>
        public float BulletScale;

        /// <summary>[TITAN-ORBIT] Level-1 baseline damage for upgrade VFX growth.</summary>
        public float ReferenceBulletDamage;

        /// <summary>[TITAN-ORBIT] Level-1 baseline speed for upgrade VFX growth.</summary>
        public float ReferenceBulletSpeed;
    }

    /// <summary>
    /// [ECS/DOTS] Regen rates from ship-family stats; applied server-side each tick by
    /// <see cref="ShipVitalsRegenSystem"/>.
    /// </summary>
    public struct ShipVitalsConfig : IComponentData
    {
        /// <summary>[TITAN-ORBIT] Hull regen per second when not in damage delay.</summary>
        public float HealthRegenPerSecond;

        /// <summary>[TITAN-ORBIT] Energy regen per second.</summary>
        public float EnergyRegenPerSecond;

        /// <summary>[TITAN-ORBIT] Seconds after hull damage before health regen resumes.</summary>
        public float HealthRegenDelayAfterDamage;
    }

    /// <summary>
    /// [ECS/DOTS] Server-only timestamp for health regen delay tracking.
    /// </summary>
    public struct ShipVitalsState : IComponentData
    {
        /// <summary>[UNITY] Server world ElapsedTime of last hull damage.</summary>
        public double LastHullDamageTime;
    }

    /// <summary>[ECS/DOTS] Per-ship weapon cooldown (server sim). NextMountIndex kept for compatibility.</summary>
    public struct ShipWeaponState : IComponentData
    {
        /// <summary>[UNITY] Seconds until next volley is allowed.</summary>
        public float FireCooldown;

        /// <summary>
        /// [LEGACY] Formerly round-robin mount index. Multi-cannon ships now fire a full volley
        /// each tick (<see cref="BulletSimulationSystem"/>); this field is cleared to 0 after fire.
        /// </summary>
        public int NextMountIndex;
    }

    /// <summary>
    /// Gameplay-readable velocity mirror of physics linear velocity. Ghost-serialized for
    /// remote interpolation and HUD. Synced by <see cref="ShipKinematicsSyncSystem"/> after physics.
    /// </summary>
    public struct ShipKinematics : IComponentData
    {
        /// <summary>[ECS/DOTS] Linear velocity; quantized for network bandwidth.</summary>
        [GhostField(Quantization = 1000)]
        public float3 Velocity;
    }

    /// <summary>[ECS/DOTS] Marker — entity is a player or AI starship (used in queries across all ship systems).</summary>
    public struct ShipTag : IComponentData { }

    /// <summary>
    /// [NETCODE] Client-only tag on the connection-owned ship. Used by input and presentation systems
    /// to find "my ship" without scanning GhostOwner every frame in MonoBehaviour code.
    /// </summary>
    public struct LocalPlayerShipTag : IComponentData { }
}
