namespace TitanOrbit.Data
{
    /// <summary>
    /// Hull-wide multi-mount fire policy for a <see cref="ShipFamilyDefinition"/>.
    /// Authored on the family asset (Bullets header) and copied into
    /// <c>ShipWeaponConfig.FireMode</c> by <c>ShipStatApplyLogic</c>.
    /// <para>
    /// [TITAN-ORBIT] Live fire does not branch on this enum. Every barrel waits
    /// <c>1 / fireRate</c>, then fires if the hull pool can pay that shot.
    /// The values stay so existing family assets keep a stable serialized byte.
    /// </para>
    /// </summary>
    public enum ShipWeaponFireMode : byte
    {
        /// <summary>
        /// Default serialized value. Ready delay plus a per-shot energy check.
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
