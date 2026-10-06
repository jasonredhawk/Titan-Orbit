using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Per-asteroid-proxy cache for territory tint: stores the original SgtPlanet material
    /// and the last applied <see cref="TeamId"/> so <see cref="WorldBodyVisualApplier.ApplyAsteroidTerritoryTint"/>
    /// can restore the neutral rock or skip a repeat write. Presentation only.
    /// </summary>
    public sealed class AsteroidTerritoryTintCache : MonoBehaviour
    {
        /// <summary>True after the first read of the prefab material and its base color.</summary>
        public bool HasOriginal;

        /// <summary>Shared Barren material before any team instance is assigned.</summary>
        public Material OriginalMaterial;

        /// <summary>Neutral SgtPlanet color before any team lerp.</summary>
        public Color OriginalColor = Color.gray;

        /// <summary>Last team written onto the draw material.</summary>
        public TeamId AppliedTeam = TeamId.None;
    }
}
