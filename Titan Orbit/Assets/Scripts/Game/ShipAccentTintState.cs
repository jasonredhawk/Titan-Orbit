using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Remembers the shared Colorize team materials on a ship proxy so accent retints
    /// can re-instance from the assets, not from a previous cached copy.
    /// <para>
    /// Without this, a second slider move would <c>SetColor</c> on an already-tinted
    /// instance and we could not return to the baked palette on Reset.
    /// </para>
    /// </summary>
    public sealed class ShipAccentTintState : MonoBehaviour
    {
        /// <summary>Per-renderer shared team materials captured after the team swap.</summary>
        public Renderer[] Renderers;

        /// <summary>Parallel to <see cref="Renderers"/> — the Colorize assets, never cache instances.</summary>
        public Material[][] BaseSharedMaterials;

        /// <summary>Last applied <see cref="ECS.ShipAccentColors.CacheKey"/>, or 0 before the first tint.</summary>
        public int LastAccentKey;
    }
}
