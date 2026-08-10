using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Shared sprites for world-space stat labels (moon gems/shield, defense pad gem cost).
    /// Loads the same CleanFlatIcon assets used by <see cref="GemMoonWorldStatsLabel"/>.
    /// Editor Play Mode uses AssetDatabase; player builds expect the sprites to already be
    /// referenced (or null if the asset was never cached this session).
    /// </summary>
    public static class WorldStatLabelIcons
    {
        const string GemIconPath =
            "Assets/CleanFlatIcon/png_128/icon_line/icon_line_store/icon_line_store_25.png";
        const string ShieldIconPath =
            "Assets/CleanFlatIcon/png_128/icon/icon_shield/icon_shield_20.png";

        static Sprite _gem;
        static Sprite _shield;

        /// <summary>Red gem / crystal icon next to gem counts (moon + defense pads).</summary>
        public static Sprite Gem => Load(ref _gem, GemIconPath);

        /// <summary>Shield icon next to matrix-shield counts on the gem moon.</summary>
        public static Sprite Shield => Load(ref _shield, ShieldIconPath);

        /// <summary>Loads once and caches; Editor-only AssetDatabase path (same as moon labels).</summary>
        static Sprite Load(ref Sprite cache, string assetPath)
        {
            if (cache != null)
                return cache;

#if UNITY_EDITOR
            cache = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
#endif
            return cache;
        }
    }
}
