using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Player-facing world names for planets. Client presentation only — world labels, gem-moon
    /// titles, and minimap hover tips all resolve through <see cref="PlanetShipFamilyConfig.GetPlanetDisplayName"/>.
    /// <para>
    /// [TITAN-ORBIT] Ship families keep their own labels (Astro Eagle, Cosmic Shark, …) on hulls
    /// and upgrade trees. Planets are places, so each body gets a proper world name. Names are
    /// deterministic from the already-ghosted <c>PlanetState.PlanetId</c>: every client shows the
    /// same string without serializing a name field. Homes use the team id (1–5). Neutrals use
    /// ids 100, 101, … from <c>MapLayoutBlueprint</c>.
    /// </para>
    /// Paired with <see cref="PlanetShipFamilyConfig"/> (optional per-family override) and
    /// <see cref="Game.PlanetWorldStatsLabel"/>.
    /// </summary>
    public static class PlanetDisplayNames
    {
        /// <summary>
        /// First neutral <c>PlanetId</c>. Must stay in sync with
        /// <c>MapLayoutBlueprint</c> (<c>nextNeutralPlanetId = 100</c>).
        /// </summary>
        public const int NeutralPlanetIdBase = 100;

        /// <summary>
        /// Team capital names, index 0 = Team A (Red). Five slots match <see cref="TeamId.TeamA"/>
        /// through <see cref="TeamId.TeamE"/>. All homes still fly Astro Eagle ships — the name
        /// identifies the world, not the hull family.
        /// </summary>
        static readonly string[] HomeWorldNames =
        {
            "Helios",
            "Thalassa",
            "Viridia",
            "Solara",
            "Nyxara"
        };

        /// <summary>
        /// Neutral world names, index 0 = planet id 100. Longer than the default 12-neutral map
        /// so a raised <c>maxNeutralPlanets</c> still gets unique titles before wrapping.
        /// Coined and star-catalog names on purpose — none match a ship family.
        /// </summary>
        static readonly string[] NeutralWorldNames =
        {
            "Aetheris",
            "Vesperia",
            "Lumenor",
            "Kryos",
            "Pyrrhon",
            "Nereida",
            "Caelum",
            "Ardentis",
            "Halcyon",
            "Eclipta",
            "Novara",
            "Orryx",
            "Velara",
            "Cinderis",
            "Nadiris",
            "Zenithar",
            "Umbraxis",
            "Luminara",
            "Coriolis",
            "Aphelion",
            "Callisto",
            "Rhea",
            "Oberon",
            "Icarus",
            "Electra",
            "Maia",
            "Merope",
            "Bellatrix",
            "Hadar",
            "Acrux",
            "Mintaka",
            "Algol",
            "Rigel",
            "Vega",
            "Altair",
            "Deneb"
        };

        /// <summary>
        /// Resolves the world name for a planet from its stable id.
        /// Called from <see cref="PlanetShipFamilyConfig.GetPlanetDisplayName"/> when the family
        /// row has no designer <c>planetName</c> override.
        /// </summary>
        /// <param name="planetId">Ghosted <c>PlanetState.PlanetId</c> (homes = team id, neutrals ≥ 100).</param>
        /// <param name="isHomePlanet">True for team spawn worlds — picks a capital name, not the neutral list.</param>
        /// <returns>Display name, or empty when both catalogs are somehow empty.</returns>
        public static string Resolve(int planetId, bool isHomePlanet)
        {
            // --- Home capitals ---
            // [TITAN-ORBIT] Every team home shares the Astro Eagle family. Naming by team id
            // keeps "Helios" and "Thalassa" distinct instead of five worlds called the same thing.
            if (isHomePlanet)
                return ResolveHomeName(planetId);

            // --- Neutral worlds ---
            return ResolveNeutralName(planetId);
        }

        /// <summary>
        /// Capital name for a team home. <paramref name="planetId"/> is the team byte
        /// (<see cref="TeamId.TeamA"/> = 1). Unknown or zero ids fall back to Team A's name.
        /// </summary>
        /// <param name="planetId">Home <c>PlanetId</c> (team id at spawn).</param>
        /// <returns>One entry from <see cref="HomeWorldNames"/>.</returns>
        static string ResolveHomeName(int planetId)
        {
            // --- Map team id → list index ---
            if (HomeWorldNames == null || HomeWorldNames.Length == 0)
                return string.Empty;

            int index = planetId >= (int)TeamId.TeamA && planetId <= (int)TeamId.TeamE
                ? planetId - (int)TeamId.TeamA
                : 0;
            index = Mathf.Clamp(index, 0, HomeWorldNames.Length - 1);
            return HomeWorldNames[index];
        }

        /// <summary>
        /// Neutral name from planet id 100, 101, … wrapping if the map rolls more neutrals
        /// than the catalog. Ids below the neutral base (legacy lookups) still pick a stable slot.
        /// </summary>
        /// <param name="planetId">Neutral <c>PlanetId</c>.</param>
        /// <returns>One entry from <see cref="NeutralWorldNames"/>.</returns>
        static string ResolveNeutralName(int planetId)
        {
            // --- Stable ordinal ---
            if (NeutralWorldNames == null || NeutralWorldNames.Length == 0)
                return string.Empty;

            int ordinal = planetId >= NeutralPlanetIdBase
                ? planetId - NeutralPlanetIdBase
                : Mathf.Abs(planetId);
            int index = ordinal % NeutralWorldNames.Length;
            return NeutralWorldNames[index];
        }
    }
}
