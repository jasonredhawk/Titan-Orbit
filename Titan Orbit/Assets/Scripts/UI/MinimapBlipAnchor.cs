using TitanOrbit.Core;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.UI
{
    public enum MinimapBlipKind
    {
        Ship,
        Planet,
        HomePlanet,
        Asteroid,
        GemMoon,
    }

    /// <summary>Hidden world-space anchor used as a blip key for ECS entities on the minimap.</summary>
    public sealed class MinimapBlipAnchor : MonoBehaviour
    {
        public MinimapBlipKind Kind;
        public TeamId Team;
        public bool IsDead;
        public bool IsLocalPlayer;
        public bool AwaitingTeamSelection;
        public int PlanetLevel;
        public int Population;
        public float BodySize;
        public int PlanetId;
        public bool IsDestroyed;
        public float MoonVisualSize;
        public bool IsHomePlanet;
        public Entity SourceEntity;
    }

}
