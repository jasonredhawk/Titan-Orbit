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
        /// <summary>
        /// [TITAN-ORBIT] Current hull points. Hitting zero alone does not kill — death requires
        /// hull and <see cref="CurrentGems"/> both depleted (<see cref="TitanOrbit.Simulation.ShipDamageLogic"/>).
        /// </summary>
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

        /// <summary>
        /// [TITAN-ORBIT] Which <see cref="Data.PlanetShipFamilyConfig"/> list index this ship is flying
        /// (0 = AstroEagle home family). Set to the docked planet's <c>ShipFamilyConfigIndex</c> when
        /// buying a hull at a captured neutral so stats / visuals / Orbit Menu match that family's tree —
        /// not always AstroEagle. Ghosted so clients resolve the same chassis as the server.
        /// </summary>
        [GhostField] public byte ShipFamilyConfigIndex;

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

        /// <summary>
        /// [TITAN-ORBIT] True after hull and cargo are both empty; movement and weapons disabled until respawn.
        /// </summary>
        [GhostField] public bool IsDead;

        /// <summary>[TITAN-ORBIT] True at spawn until RequestTeamCommand assigns a team.</summary>
        [GhostField] public bool AwaitingTeamSelection;
    }

    /// <summary>
    /// [ECS/DOTS] Motor tuning derived from chassis stats + capacity tax. Not ghost-serialized —
    /// recomputed on server and client by <see cref="ShipStatApplyLogic"/> when level, branch,
    /// attributes, or equipment change. Read by movement job.
    /// <para>
    /// [TITAN-ORBIT] MaxSpeed / EngineThrust / RotationSpeed already include the empty-hold
    /// capacity tax from <see cref="TitanOrbit.Data.ShipMobilityResolution"/> (gem/people capacity).
    /// </para>
    /// </summary>
    public struct ShipMotorConfig : IComponentData
    {
        /// <summary>[PHYSICS] Engine force in Newtons (acceleration = thrust / mass). Capacity-taxed.</summary>
        public float EngineThrust;

        /// <summary>[TITAN-ORBIT] Top speed cap in world units per second. Capacity-taxed.</summary>
        public float MaxSpeed;

        /// <summary>[TITAN-ORBIT] Turn rate in degrees per second toward aim point. Capacity-taxed.</summary>
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

        /// <summary>
        /// [TITAN-ORBIT] Summed family ramming power at the current ship level. Written by
        /// <see cref="ShipStatApplyLogic"/>; read by asteroid ram/grind damage on the server.
        /// </summary>
        public float RammingPower;
    }

    /// <summary>
    /// [ECS/DOTS] Cannon stats for <see cref="BulletSimulationSystem"/>. Applied from chassis data;
    /// not individually ghost-serialized (clients infer from replicated ship level).
    /// </summary>
    public struct ShipWeaponConfig : IComponentData
    {
        /// <summary>
        /// [TITAN-ORBIT] Default max travel distance before a bullet expires with no impact VFX.
        /// Matches the original NGO-era design (~30 world units); bank range multipliers scale this.
        /// </summary>
        public const float DefaultBulletMaxDistance = 30f;

        /// <summary>[TITAN-ORBIT] Minimum seconds between shots (HUD / fallback — live fire uses per-mount FireRate).</summary>
        public float FireRate;

        /// <summary>[TITAN-ORBIT] Bullet speed in world units per second.</summary>
        public float BulletSpeed;

        /// <summary>
        /// [TITAN-ORBIT] Average damage per bullet across mounts (HUD / fallback).
        /// Live shots read each <see cref="ShipWeaponMountElement.FirePower"/>.
        /// </summary>
        public float BulletDamage;

        /// <summary>
        /// [TITAN-ORBIT] Average energy per barrel (HUD / fallback). Live spend uses each
        /// mount’s firePower via <see cref="ShipWeaponFireLogic"/>.
        /// </summary>
        public float EnergyCostPerShot;

        /// <summary>
        /// [UNITY] Bullet lifetime in seconds. Prefer distance cull; keep lifetime ≥ distance/speed
        /// so MaxDistance is the primary kill when the shot misses.
        /// </summary>
        public float BulletLifetime;

        /// <summary>
        /// [TITAN-ORBIT] Max travel distance before the bullet is removed with no hit/impact.
        /// Default <see cref="DefaultBulletMaxDistance"/> (~30).
        /// </summary>
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

    /// <summary>
    /// [ECS/DOTS] Energy-queue cursor for low-energy multi-cannon fire (server sim).
    /// Per-barrel cooldowns live on <see cref="ShipWeaponMountElement.FireCooldown"/>.
    /// <see cref="NextMountIndex"/> is which mount may spend energy when a full volley is not affordable.
    /// </summary>
    public struct ShipWeaponState : IComponentData
    {
        /// <summary>[LEGACY] Unused — prefer per-mount <see cref="ShipWeaponMountElement.FireCooldown"/>.</summary>
        public float FireCooldown;

        /// <summary>
        /// [TITAN-ORBIT] Energy-queue index for round-robin drip. When the shared pool cannot
        /// cover every mount at once, only this barrel may fire; after it shoots the cursor
        /// advances 0→1→2→…→0. Reset to 0 after a full same-tick volley.
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
