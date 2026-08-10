namespace TitanOrbit.UI
{
    /// <summary>
    /// Minimap ping kinds placed by the player on attack/defend radial menus. Serialized on marker
    /// prefabs and used by <see cref="MinimapController"/> for color and icon selection. Team
    /// coordination feature — not authoritative sim state.
    /// </summary>
    public enum MinimapMarkerKind
    {
        // --- Team ping kinds (radial menu) ---
        /// <summary>Defensive priority marker (hold this area).</summary>
        Defend,

        /// <summary>Offensive priority marker (push this target).</summary>
        Attack,
    }
}
