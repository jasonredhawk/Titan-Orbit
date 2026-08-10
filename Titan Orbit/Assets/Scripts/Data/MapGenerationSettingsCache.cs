namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Runtime pointer to the active <see cref="MapGenerationSettings"/> ScriptableObject.
    /// Set by <see cref="Game.MapGenerationSettingsLoader"/> at boot; read by
    /// <see cref="ECS.MapGenerationLogic"/> and editor tools. Null until loader runs —
    /// server map bootstrap should fall back to baked SubScene defaults. Client and server
    /// both read the same asset reference after boot so procedural rolls match.
    /// </summary>
    public static class MapGenerationSettingsCache
    {
        /// <summary>
        /// Current map-gen asset (planet counts, spacing, asteroid density). [UNITY] ScriptableObject
        /// reference — not replicated; server authority bakes rolled values into ECS singletons.
        /// </summary>
        public static MapGenerationSettings Settings { get; set; }
    }
}
