namespace TitanOrbit.Data
{
    /// <summary>
    /// High-level ship role tag for upgrade-tree branching and legacy <see cref="ShipData"/> assets.
    /// Designers pick focus when authoring families; UI may tint nodes by role. Not replicated
    /// as a standalone ghost field — chassis choice encodes role at runtime via equipped components.
    /// </summary>
    public enum ShipFocusType
    {
        // --- Chassis role branches ---
        /// <summary>[TITAN-ORBIT] Combat-oriented chassis branch (weapons, speed).</summary>
        Fighter,

        /// <summary>[TITAN-ORBIT] Mining and gem capacity emphasis.</summary>
        Miner,

        /// <summary>[TITAN-ORBIT] People transport and cargo emphasis.</summary>
        Transport,
    }
}
