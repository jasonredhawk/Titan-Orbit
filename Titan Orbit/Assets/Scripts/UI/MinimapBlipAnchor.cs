using TitanOrbit.Core;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>Minimap icon category for ECS-synced world blips.</summary>
    public enum MinimapBlipKind
    {
        // --- World entity categories for minimap icons ---
        Ship,
        Planet,
        HomePlanet,
        Asteroid,
        GemMoon,
    }

    /// <summary>
    /// Hidden world-space anchor used as a blip key for ECS entities on the minimap.
    /// <see cref="MinimapEcsEntitySync"/> creates/updates these from ghost state; blip
    /// renderers read Kind, Team, and body stats for icon shape and color. [HYBRID] bridge
    /// between ECS entities and UGUI/minimap presentation — does not drive simulation.
    /// </summary>
    public sealed class MinimapBlipAnchor : MonoBehaviour
    {
        // --- Blip classification ---
        /// <summary>Which minimap sprite/layout to use.</summary>
        public MinimapBlipKind Kind;

        /// <summary>Team tint for ships and owned planets.</summary>
        public TeamId Team;

        /// <summary>True when ship ghost is in death state.</summary>
        public bool IsDead;

        /// <summary>True for the local player's ship blip.</summary>
        public bool IsLocalPlayer;

        /// <summary>True when player has not picked a team yet.</summary>
        public bool AwaitingTeamSelection;

        // --- Planet / body stats for label and scale ---
        /// <summary>Planet level for ring/label display.</summary>
        public int PlanetLevel;

        /// <summary>Rounded population for planet blip label.</summary>
        public int Population;

        /// <summary>World body radius scale for blip size.</summary>
        public float BodySize;

        /// <summary>Stable planet id for connection lines UI.</summary>
        public int PlanetId;

        /// <summary>True when asteroid is depleted.</summary>
        public bool IsDestroyed;

        /// <summary>Gem moon shield visual radius for blip scale.</summary>
        public float MoonVisualSize;

        /// <summary>True for team home world planets.</summary>
        public bool IsHomePlanet;

        /// <summary>ECS entity this blip tracks — used for add/remove sync.</summary>
        public Entity SourceEntity;
    }

}
