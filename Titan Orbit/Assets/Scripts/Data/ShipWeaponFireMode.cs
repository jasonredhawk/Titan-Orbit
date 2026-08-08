namespace TitanOrbit.Data
{
    /// <summary>
    /// Hull-wide multi-mount fire policy for a <see cref="ShipFamilyDefinition"/>.
    /// Authored on the family asset (Bullets header), copied into
    /// <c>ShipWeaponConfig.FireMode</c> by <c>ShipStatApplyLogic</c>, and consumed by
    /// <c>ShipWeaponFireLogic.TryPlanFire</c> on both server bullets and client tracers.
    /// <para>
    /// [TITAN-ORBIT] Sequencing is ship-level (shared energy pool + mount cursor), not per-weapon
    /// component row — multi-barrel hulls share one policy for every mount.
    /// </para>
    /// </summary>
    public enum ShipWeaponFireMode : byte
    {
        /// <summary>
        /// [TITAN-ORBIT] Default / legacy feel: full volley when energy covers every ready barrel;
        /// otherwise round-robin on <c>ShipWeaponState.NextMountIndex</c>.
        /// </summary>
        EnergyHybrid = 0,

        /// <summary>
        /// [TITAN-ORBIT] Only fire when every mount is off cooldown and the pool can pay the sum
        /// of all firePowers at once. No single-barrel drip while waiting.
        /// </summary>
        AlwaysFireTogether = 1,

        /// <summary>
        /// [TITAN-ORBIT] Never volley. Cycle one mount at a time (0→1→2→…→0), waiting until the
        /// shared pool covers the next barrel’s firePower.
        /// </summary>
        AlwaysRoundRobin = 2,
    }
}
