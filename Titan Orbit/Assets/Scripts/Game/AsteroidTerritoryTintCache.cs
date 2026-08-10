using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Per-asteroid-proxy cache for territory tint: stores the original SgtPlanet color and
    /// the last applied <see cref="TeamId"/> so <see cref="WorldBodyVisualApplier.ApplyAsteroidTerritoryTint"/>
    /// can restore / skip redundant MaterialPropertyBlock writes. Presentation only.
    /// </summary>
    public sealed class AsteroidTerritoryTintCache : MonoBehaviour
    {
        /// <summary>True after the first read of the prefab/material base color.</summary>
        public bool HasOriginal;

        /// <summary>Neutral SgtPlanet color before any team lerp.</summary>
        public Color OriginalColor = Color.gray;

        /// <summary>Last team written to the property block (avoids per-frame SetColor).</summary>
        public TeamId AppliedTeam = TeamId.None;
    }
}
