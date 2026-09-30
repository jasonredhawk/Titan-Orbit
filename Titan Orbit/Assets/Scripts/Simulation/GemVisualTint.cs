using TitanOrbit.Data;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Crystal colour on a loose gem. Pickup, tractor, and deposit ignore tint — any ship
    /// may scoop any tint. Tint is a teaching colour only:
    /// <list type="bullet">
    /// <item><see cref="Standard"/> — ordinary mined / destroy leftover (<c>AsteroidSettings.DefaultGemColor</c>).</item>
    /// <item><see cref="TerritoryBonus"/> — yellow extra yield from a friendly triangle.</item>
    /// <item><see cref="MinerCommander"/> — blue extra yield from that team's top miner (5%).</item>
    /// </list>
    /// Packed into <c>GemSpawnRecipe.Flags</c> and the gem RPC <c>IsBonusGem</c> byte so
    /// client hydrate matches the server Instantiates without a gem ghost.
    /// </summary>
    public enum GemVisualTint : byte
    {
        /// <summary>Ordinary crystal (base mine / destroy leftover). Colour is <c>AsteroidSettings.DefaultGemColor</c>.</summary>
        Standard = 0,

        /// <summary>Yellow — triangle territory bonus. Separate spawn from the red base.</summary>
        TerritoryBonus = 1,

        /// <summary>
        /// Blue — top-miner command bonus. Never mixed into the yellow burst so players
        /// can see both extras at once on the same rock.
        /// </summary>
        MinerCommander = 2,
    }

    /// <summary>
    /// Fallback RGB for primitive spheres when the pooled gem material is missing.
    /// Matches <c>GemVisualApplier</c> shared tints so a pool-miss still reads correctly.
    /// </summary>
    public static class GemVisualTintColors
    {
        /// <summary>
        /// Ordinary crystal — <see cref="AsteroidSettings.DefaultGemColor"/> (built-in red when unset).
        /// Same family as the pooled standard material.
        /// </summary>
        public static UnityEngine.Color Standard =>
            AsteroidSettingsCache.ResolveOrDefault().ResolveDefaultGemColor();

        /// <summary>Yellow — triangle bonus (NGO bonusGemTintColor).</summary>
        public static readonly UnityEngine.Color TerritoryBonus = new UnityEngine.Color(1f, 0.9f, 0.15f, 0.55f);

        /// <summary>Ice blue — top-miner 5% so it cannot be mistaken for yellow triangles.</summary>
        public static readonly UnityEngine.Color MinerCommander = new UnityEngine.Color(0.25f, 0.55f, 1f, 0.55f);

        /// <summary>Opaque fallback for CreateLitMaterial (alpha is ignored there).</summary>
        public static UnityEngine.Color FallbackLit(GemVisualTint tint)
        {
            switch (tint)
            {
                case GemVisualTint.TerritoryBonus:
                    return new UnityEngine.Color(1f, 0.9f, 0.15f, 1f);
                case GemVisualTint.MinerCommander:
                    return new UnityEngine.Color(0.25f, 0.55f, 1f, 1f);
                default:
                    var standard = Standard;
                    standard.a = 1f;
                    return standard;
            }
        }
    }
}
