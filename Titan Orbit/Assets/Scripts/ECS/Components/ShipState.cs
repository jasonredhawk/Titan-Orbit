using TitanOrbit.Core;
using TitanOrbit.Data;
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
        /// Clamp (not Interpolate): floating damage reads this field. Interpolated Health
        /// shredded 2-HP turret hits into sub-1 fragments that PollShips dropped.
        /// </summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public float Health;

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

        /// <summary>[TITAN-ORBIT] Troop units aboard (troop transport gameplay).</summary>
        [GhostField] public int CurrentPeople;

        /// <summary>[TITAN-ORBIT] Troop cap aboard this hull.</summary>
        [GhostField] public int PeopleCapacity;

        /// <summary>
        /// [TITAN-ORBIT] True after hull and cargo are both empty; movement and weapons disabled until respawn.
        /// </summary>
        [GhostField] public bool IsDead;

        /// <summary>[TITAN-ORBIT] True at spawn until RequestTeamCommand assigns a team.</summary>
        [GhostField] public bool AwaitingTeamSelection;

        /// <summary>
        /// [TITAN-ORBIT] OVERDRIVE energy lockout (predicted ghost field — client + server share it).
        /// Set when energy hits 0; cleared when Shift is released or energy reaches ≥25% MaxEnergy.
        /// Burst is active only while Shift+Thrust, energy &gt; 0, and this is false.
        /// Replaces the old non-ghosted Engaged latch that desynced bloom/speed.
        /// </summary>
        [GhostField] public bool OverdriveLockout;
    }

    /// <summary>
    /// [ECS/DOTS] Motor tuning derived from chassis stats + capacity tax. Not ghost-serialized —
    /// recomputed on server and client by <see cref="ShipStatApplyLogic"/> when level, branch,
    /// attributes, or equipment change. Read by movement job.
    /// <para>
    /// [TITAN-ORBIT] MaxSpeed / EngineThrust / RotationSpeed are <b>untaxed</b> chassis baselines.
    /// Drive applies live subtractive mass tax from current gems/people + ComponentSize
    /// (<see cref="TitanOrbit.Data.ShipMobilityResolution"/>).
    /// EngineThrust stores acceleration (world units/s²), not force — no ×10 visibility, no F/m.
    /// ThrustEnergyDrainPerSecond is absolute OVERDRIVE energy/sec = sum over engines of
    /// ExtraSpeedEnergyDrain. OverdriveEnergyDrainMultiplier stays 1
    /// (rate already baked). Normal RMB thrust does not spend energy.
    /// </para>
    /// </summary>
    public struct ShipMotorConfig : IComponentData
    {
        /// <summary>
        /// [TITAN-ORBIT] Untaxed acceleration (world units/s²). Drive subtracts totalMass × AccelWeightPerMass.
        /// Field name kept for ghost/serialization stability.
        /// </summary>
        public float EngineThrust;

        /// <summary>[TITAN-ORBIT] Untaxed top speed (world units/s). Drive applies live mass tax.</summary>
        public float MaxSpeed;

        /// <summary>[TITAN-ORBIT] Untaxed turn rate (°/s) toward aim. Drive applies live mass tax.</summary>
        public float RotationSpeed;

        /// <summary>[TITAN-ORBIT] Space-brake deceleration magnitude.</summary>
        public float BrakeDeceleration;

        /// <summary>[PHYSICS] Fallback hull mass when HullMassReference is unset.</summary>
        public float Mass;

        /// <summary>[TITAN-ORBIT] How fast excess speed (recoil) bleeds off per second.</summary>
        public float RecoilDecayPerSecond;

        /// <summary>[PHYSICS] Chassis ComponentSize × hull scale (excludes HP bulk and gems). Feeds mass tax.</summary>
        public float HullMassReference;

        /// <summary>[TITAN-ORBIT] Level-1 max health used to soften movement mass at higher ship levels.</summary>
        public float ChassisReferenceHealth;

        /// <summary>
        /// [TITAN-ORBIT] Summed family ramming power at the current ship level. Written by
        /// <see cref="ShipStatApplyLogic"/>; read by asteroid ram/grind damage on the server.
        /// </summary>
        public float RammingPower;

        /// <summary>
        /// [TITAN-ORBIT] Absolute energy spend per second while OVERDRIVE burst is active.
        /// Sum over engines of ExtraSpeedEnergyDrain (e.g. 2 = spend 2 energy/sec).
        /// Normal RMB thrust does not spend energy.
        /// </summary>
        public float ThrustEnergyDrainPerSecond;

        /// <summary>
        /// [TITAN-ORBIT] MaxSpeed × this while OVERDRIVE burst is active
        /// (<see cref="ShipOverdriveTuning.IsBurstActive"/>).
        /// Baked from engine ExtraSpeedPercent × family extraSpeedPercentMul (1 + p).
        /// </summary>
        public float OverdriveSpeedMultiplier;

        /// <summary>
        /// [TITAN-ORBIT] EngineThrust × this while OVERDRIVE burst is active (matches speed mul).
        /// </summary>
        public float OverdriveThrustMultiplier;

        /// <summary>
        /// [TITAN-ORBIT] Kept at 1 — absolute OD drain is already in
        /// <see cref="ThrustEnergyDrainPerSecond"/>. Legacy mul path left for ghost/bake safety.
        /// </summary>
        public float OverdriveEnergyDrainMultiplier;

        /// <summary>
        /// 1 = MEGA hull: skip cargo / ComponentSize mass tax on speed, accel, and turn.
        /// Not ghosted — written by chassis apply on server and client.
        /// </summary>
        public byte SkipMassTax;
    }

    /// <summary>
    /// [TITAN-ORBIT] Code fallbacks + shared OVERDRIVE lockout / burst rules for motor, HUD, and scale.
    /// Live speed/drain come from engines × family bonuses baked onto <see cref="ShipMotorConfig"/>.
    /// Formula: speed = 1 + p; drain/sec = ExtraSpeedEnergyDrain (defaults p=0.75, drain=2).
    /// <para>
    /// Lockout hysteresis (one place for sim + presentation):
    /// Shift up → clear lockout; energy ≤ 0 → lockout; energy ≥ 25% MaxEnergy → clear lockout.
    /// Burst = Shift ∧ Thrust ∧ ¬orbit ∧ energy &gt; 0 ∧ ¬lockout.
    /// </para>
    /// </summary>
    public static class ShipOverdriveTuning
    {
        /// <summary>Fallback MaxSpeed multiplier for +75% (1.75).</summary>
        public static float SpeedMultiplier =>
            1f + ShipFamilyOverdriveAbility.DefaultExtraSpeedPercent;

        /// <summary>Fallback EngineThrust multiplier (same as speed).</summary>
        public static float ThrustMultiplier => SpeedMultiplier;

        /// <summary>Fallback absolute OD drain/sec (ExtraSpeedEnergyDrain default = 2).</summary>
        public static float DefaultEnergyDrainPerSecond =>
            ShipFamilyOverdriveAbility.DefaultExtraSpeedEnergyDrain;

        /// <summary>Legacy name — always 1; absolute drain lives on ThrustEnergyDrainPerSecond.</summary>
        public static float EnergyDrainMultiplier => 1f;

        /// <summary>
        /// Fraction of MaxEnergy required to clear lockout / (re)start OVERDRIVE after empty.
        /// </summary>
        public const float OverdriveEngageEnergyFraction = 0.25f;

        /// <summary>Absolute floor so tiny MaxEnergy pools still get hysteresis.</summary>
        public const float OverdriveEngageEnergyAbsoluteMin = 1f;

        /// <summary>Energy required to clear lockout and allow a new burst.</summary>
        public static float EngageEnergyThreshold(float maxEnergy) =>
            math.max(OverdriveEngageEnergyAbsoluteMin, maxEnergy * OverdriveEngageEnergyFraction);

        /// <summary>
        /// Updates <paramref name="lockout"/> from Shift + energy (call every fixed motor tick).
        /// Does not require thrust — lockout clears at ≥25% while Shift is held so the next
        /// thrust frame can burst immediately.
        /// </summary>
        /// <param name="shiftHeld"><see cref="ShipInput.Overdrive"/> (Shift alone).</param>
        /// <param name="currentEnergy">Current energy pool (may be mid-drain this tick).</param>
        /// <param name="maxEnergy">Ship MaxEnergy.</param>
        /// <param name="lockout"><see cref="ShipState.OverdriveLockout"/>.</param>
        public static void StepLockout(
            bool shiftHeld,
            float currentEnergy,
            float maxEnergy,
            ref bool lockout)
        {
            // --- Shift released: always allow a fresh engage later ---
            if (!shiftHeld)
            {
                lockout = false;
                return;
            }

            // --- Empty pool: block until regen hits the engage floor ---
            if (currentEnergy <= 0f)
            {
                lockout = true;
                return;
            }

            // --- Regen (or still above floor): clear lockout so burst can run ---
            if (currentEnergy >= EngageEnergyThreshold(maxEnergy))
                lockout = false;
        }

        /// <summary>
        /// True when OVERDRIVE burst should apply speed/thrust/drain and thruster bloom.
        /// Call <see cref="StepLockout"/> first in the motor so lockout matches this tick's energy.
        /// </summary>
        /// <param name="shiftHeld">Shift held.</param>
        /// <param name="thrustHeld">RMB thrust held.</param>
        /// <param name="useOrbit">Passive orbit motor owns this tick.</param>
        /// <param name="currentEnergy">Energy after lockout step (before or after drain — see motor).</param>
        /// <param name="lockout">Current <see cref="ShipState.OverdriveLockout"/>.</param>
        public static bool IsBurstActive(
            bool shiftHeld,
            bool thrustHeld,
            bool useOrbit,
            float currentEnergy,
            bool lockout)
        {
            return shiftHeld
                && thrustHeld
                && !useOrbit
                && currentEnergy > 0f
                && !lockout;
        }

        /// <summary>Resolves speed mul from motor, falling back to code default when unset (≤ 0).</summary>
        public static float ResolveSpeedMultiplier(in ShipMotorConfig motor) =>
            motor.OverdriveSpeedMultiplier > 0.01f
                ? motor.OverdriveSpeedMultiplier
                : SpeedMultiplier;

        /// <summary>Resolves thrust mul from motor, falling back to code default when unset (≤ 0).</summary>
        public static float ResolveThrustMultiplier(in ShipMotorConfig motor) =>
            motor.OverdriveThrustMultiplier > 0.01f
                ? motor.OverdriveThrustMultiplier
                : ThrustMultiplier;

        /// <summary>Resolves overdrive drain mul from motor (always ~1; absolute rate is on ThrustEnergyDrainPerSecond).</summary>
        public static float ResolveEnergyDrainMultiplier(in ShipMotorConfig motor) =>
            motor.OverdriveEnergyDrainMultiplier > 0.01f
                ? motor.OverdriveEnergyDrainMultiplier
                : EnergyDrainMultiplier;
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
        /// Written from family <c>bulletRange</c> (ship-level scaled + <c>bulletRangeMul</c>);
        /// falls back to <see cref="DefaultBulletMaxDistance"/> (~30) when authored range is zero.
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

        /// <summary>
        /// [TITAN-ORBIT] Hull-wide fire policy from <c>ShipFamilyDefinition.weaponFireMode</c>.
        /// Not ghosted — written by <see cref="ShipStatApplyLogic"/> on server and client apply paths.
        /// Consumed by <see cref="ShipWeaponFireLogic.TryPlanFire"/>.
        /// </summary>
        public ShipWeaponFireMode FireMode;
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
    /// [ECS/DOTS] Energy-queue cursor for multi-cannon fire (server sim).
    /// Per-barrel cooldowns live on <see cref="ShipWeaponMountElement.FireCooldown"/>.
    /// <see cref="NextMountIndex"/> is which mount may spend energy under Energy Hybrid (when a
    /// full volley is not affordable) or Always Round-Robin. Unused while Always Fire Together waits.
    /// </summary>
    public struct ShipWeaponState : IComponentData
    {
        /// <summary>[LEGACY] Unused — prefer per-mount <see cref="ShipWeaponMountElement.FireCooldown"/>.</summary>
        public float FireCooldown;

        /// <summary>
        /// [TITAN-ORBIT] Energy-queue index for round-robin drip. When
        /// <see cref="ShipWeaponConfig.FireMode"/> allows drip and the shared pool cannot cover
        /// every mount at once, only this barrel may fire; after it shoots the cursor advances
        /// 0→1→2→…→0. Reset to 0 after a full same-tick volley.
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
