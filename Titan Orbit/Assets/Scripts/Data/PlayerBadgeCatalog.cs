using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Sprite lookup for player profile badges. One asset at <c>Resources/PlayerBadgeCatalog</c>
    /// so menu and nameplates share the same ids. Entries are keyed by the number in
    /// <c>Badge (N).png</c> (not array index) so reordering files does not rematch saved picks.
    /// Rebuilt by Titan Orbit → Data → Rebuild Player Badge Catalog.
    /// </summary>
    [CreateAssetMenu(
        fileName = "PlayerBadgeCatalog",
        menuName = "Titan Orbit/Player Badge Catalog",
        order = 70)]
    public class PlayerBadgeCatalog : ScriptableObject
    {
        /// <summary>Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        public const string ResourcesLoadName = "PlayerBadgeCatalog";

        /// <summary>One fantasy-badge sprite plus its stable filename id.</summary>
        [Serializable]
        public struct Entry
        {
            [Tooltip("Number from Badge (N).png. 0 is reserved for none.")]
            public int badgeId;

            [Tooltip("UI / world sprite for this id.")]
            public Sprite sprite;
        }

        [Tooltip("All selectable badges, sorted by badgeId.")]
        public Entry[] entries = Array.Empty<Entry>();

        static PlayerBadgeCatalog _cached;

        /// <summary>Loads the Resources catalog once per domain.</summary>
        public static PlayerBadgeCatalog LoadDefault()
        {
            if (_cached != null)
                return _cached;
            _cached = Resources.Load<PlayerBadgeCatalog>(ResourcesLoadName);
            return _cached;
        }

        /// <summary>
        /// Looks up the sprite for a filename-stable badge id.
        /// </summary>
        /// <param name="badgeId">Number from Badge (N).png. 0 / unknown → false.</param>
        /// <param name="sprite">Catalog sprite when found.</param>
        /// <returns>True when this id has a non-null sprite.</returns>
        public bool TryGetSprite(int badgeId, out Sprite sprite)
        {
            sprite = null;
            if (badgeId <= 0 || entries == null || entries.Length == 0)
                return false;

            // Binary search — editor rebuild writes entries sorted by badgeId.
            int lo = 0;
            int hi = entries.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int id = entries[mid].badgeId;
                if (id == badgeId)
                {
                    sprite = entries[mid].sprite;
                    return sprite != null;
                }

                if (id < badgeId)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }

            return false;
        }

        /// <summary>Convenience load + lookup. Returns null when the catalog or sprite is missing.</summary>
        public static Sprite FindSprite(int badgeId)
        {
            PlayerBadgeCatalog catalog = LoadDefault();
            if (catalog == null)
                return null;
            return catalog.TryGetSprite(badgeId, out Sprite sprite) ? sprite : null;
        }
    }
}
