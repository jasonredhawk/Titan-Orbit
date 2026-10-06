using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Team-colored Archanor <c>LaserStatic</c> prefabs for MEGA cannon beams.
    /// One asset at <c>Resources/CannonLaserVfx</c>. Client presentation only.
    /// </summary>
    [CreateAssetMenu(
        fileName = "CannonLaserVfx",
        menuName = "Titan Orbit/Cannon Laser Vfx")]
    public class CannonLaserVfxSettings : ScriptableObject
    {
        /// <summary>Resources path used at runtime (<c>Resources.Load</c>).</summary>
        public const string ResourcesPath = "CannonLaserVfx";

        /// <summary>[UNITY] Sole asset path for Editor fallback loads.</summary>
        public const string ResourcesAssetPath = "Assets/Resources/CannonLaserVfx.asset";

        [Header("Archanor LaserStatic (Combat/Beams/StaticBeam/Laser)")]
        [Tooltip("Team A — LaserStaticRed.")]
        public GameObject Red;

        [Tooltip("Team B — LaserStaticBlue.")]
        public GameObject Blue;

        [Tooltip("Team C — LaserStaticGreen.")]
        public GameObject Green;

        [Tooltip("Team D — LaserStaticOrange.")]
        public GameObject Orange;

        [Tooltip("Team E — LaserStaticPurple.")]
        public GameObject Purple;

        static CannonLaserVfxSettings _cached;

        /// <summary>Loads the Resources asset once per domain.</summary>
        public static CannonLaserVfxSettings LoadDefault()
        {
            if (_cached != null)
                return _cached;

            _cached = Resources.Load<CannonLaserVfxSettings>(ResourcesPath);
#if UNITY_EDITOR
            if (_cached == null)
                _cached = UnityEditor.AssetDatabase.LoadAssetAtPath<CannonLaserVfxSettings>(ResourcesAssetPath);
#endif
            return _cached;
        }

        /// <summary>LaserStatic prefab for <paramref name="team"/> (Red if unset).</summary>
        public GameObject GetPrefabForTeam(TeamId team)
        {
            GameObject picked;
            switch (team)
            {
                case TeamId.TeamB:
                    picked = Blue;
                    break;
                case TeamId.TeamC:
                    picked = Green;
                    break;
                case TeamId.TeamD:
                    picked = Orange;
                    break;
                case TeamId.TeamE:
                    picked = Purple;
                    break;
                default:
                    picked = Red;
                    break;
            }

            if (picked != null)
                return picked;
            if (Red != null)
                return Red;
            if (Blue != null)
                return Blue;
            return Green != null ? Green : Orange;
        }
    }
}
