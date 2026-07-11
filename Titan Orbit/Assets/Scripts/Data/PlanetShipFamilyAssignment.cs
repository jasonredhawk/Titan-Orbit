namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Constants for procedural planet generation: which <see cref="PlanetShipFamilyConfig"/> list index
    /// each planet receives. Home planets always get index 0 (AstroEagle starter family); neutral and
    /// captured planets cycle through the remaining eleven USC families. Written at planet spawn by
    /// <see cref="ECS.MapGenerationLogic"/>; read by orbit station UI and chassis unlock resolution.
    /// Shared client/server — indices must stay stable across builds.
    /// </summary>
    public static class PlanetShipFamilyAssignment
    {
        /// <summary>[TITAN-ORBIT] PlanetShipFamilyConfig list index for team home planets (AstroEagle).</summary>
        public const byte HomeFamilyConfigIndex = 0;

        /// <summary>[TITAN-ORBIT] Count of non-home families in PlanetShipFamilyConfig (Cosmic Shark through Strider Ox).</summary>
        public const int NonHomeFamilySlotCount = 11;
    }
}
