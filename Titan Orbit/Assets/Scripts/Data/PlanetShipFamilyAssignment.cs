namespace TitanOrbit.Data
{
    /// <summary>Ship-family config indices assigned when planets are generated.</summary>
    public static class PlanetShipFamilyAssignment
    {
        /// <summary>PlanetShipFamilyConfig index for home planets (AstroEagle).</summary>
        public const byte HomeFamilyConfigIndex = 0;

        /// <summary>Non-home families in PlanetShipFamilyConfig (Cosmic Shark through Strider Ox).</summary>
        public const int NonHomeFamilySlotCount = 11;
    }
}
