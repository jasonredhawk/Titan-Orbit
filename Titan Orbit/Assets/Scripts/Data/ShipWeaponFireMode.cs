namespace TitanOrbit.Data
{
    /// <summary>
    /// Hull-wide multi-mount fire policy for a <see cref="ShipFamilyDefinition"/>.
    /// Authored on the family asset (Bullets header) and copied into
    /// <c>ShipWeaponConfig.FireMode</c> by <c>ShipStatApplyLogic</c>.
    /// <para>
    /// [TITAN-ORBIT] Live fire is <see cref="EnergyHybrid"/>. A pool that covers
    /// every armed projectile volleys those ready barrels. A short pool cycles
    /// one arsenal square at a time. The other values stay so existing family
    /// assets keep a stable serialized byte.
    /// </para>
    /// </summary>
    public enum ShipWeaponFireMode : byte
    {
        /// <summary>
        /// Default. Volley when the pool covers every armed projectile;
        /// otherwise cycle one arsenal square at a time.
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
