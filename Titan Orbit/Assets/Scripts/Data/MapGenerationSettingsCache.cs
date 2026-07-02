namespace TitanOrbit.Data
{
    /// <summary>Runtime reference to the active map generation settings asset (set by MapGenerationSettingsLoader).</summary>
    public static class MapGenerationSettingsCache
    {
        public static MapGenerationSettings Settings { get; set; }
    }
}
