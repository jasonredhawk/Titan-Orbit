namespace TitanOrbit.Data
{
    /// <summary>
    /// Constants for procedural planet generation: which <see cref="PlanetShipFamilyConfig"/> list index
    /// each planet receives. Home planets always get index 0 (AstroEagle); captured planets cycle through
    /// the remaining eleven families. Written at planet spawn, read by orbit station and chassis resolution.
    /// </summary>
    public static class PlanetShipFamilyAssignment
    {
        /// <summary>PlanetShipFamilyConfig index for home planets (AstroEagle).</summary>
        public const byte HomeFamilyConfigIndex = 0;

        /// <summary>Non-home families in PlanetShipFamilyConfig (Cosmic Shark through Strider Ox).</summary>
        public const int NonHomeFamilySlotCount = 11;
    }
}
