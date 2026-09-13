using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Single source of truth for client thruster jet flames.
    /// One asset at <c>Assets/Resources/ThrusterVfxBank.asset</c> lists every family
    /// prefab (and optional team-color variants). Placement and size are computed
    /// from the live thruster component — this asset does not store offsets,
    /// eulers, insets, or idle scales. Presentation only.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ThrusterVfxBank",
        menuName = "Titan Orbit/Thruster VFX Bank",
        order = 48)]
    public class ThrusterVfxBank : ScriptableObject
    {
        public const string ResourcesLoadName = "ThrusterVfxBank";
        public const string ResourcesAssetPath = "Assets/Resources/ThrusterVfxBank.asset";

        /// <summary>
        /// Shared starter flame (Modular / V2). Families no longer own a unique jet —
        /// the player picks one of the four types in Customize Ship.
        /// </summary>
        public const string DefaultFamilyId = "AstroEagle";

        /// <summary>Classic V1, Modular V2, Heavy V3, Soft. Index 1 is the default.</summary>
        public const int StyleCount = 4;

        /// <summary>Modular (V2 / AstroEagle signature).</summary>
        public const int DefaultStyleIndex = 1;

        static readonly string[] StyleDisplayNames = { "Classic", "Modular", "Heavy", "Soft" };
        static readonly string[] StyleFamilyIds = { "SpaceExcalibur", "AstroEagle", "CosmicShark", "LightFox" };

        /// <summary>
        /// Child name for Instantiated jet instances so stash/restore can strip them
        /// instead of baking a live flame into the original part.
        /// </summary>
        public const string JetInstanceName = "_ThrusterJetVfx";

        /// <summary>One team-color slot in <see cref="Entry.colorPrefabs"/>.</summary>
        [Serializable]
        public class ColorPrefab
        {
            public string colorName = "Blue";
            public GameObject prefab;
        }

        /// <summary>One family row — prefab only. Pose comes from the mount at runtime.</summary>
        [Serializable]
        public class Entry
        {
            public string familyId;
            public string displayName;

            [Tooltip("Signature flame when the mount name has no color match.")]
            public GameObject prefab;

            [Tooltip("Optional Blue / Green / Purple / Red / Yellow variants of this family's style.")]
            public List<ColorPrefab> colorPrefabs = new List<ColorPrefab>();

            public GameObject ResolvePrefab(string mountName)
            {
                if (colorPrefabs != null && colorPrefabs.Count > 0)
                {
                    string color = ExtractColorNameFromText(mountName);
                    if (!string.IsNullOrEmpty(color))
                    {
                        for (int i = 0; i < colorPrefabs.Count; i++)
                        {
                            ColorPrefab slot = colorPrefabs[i];
                            if (slot == null || slot.prefab == null || string.IsNullOrEmpty(slot.colorName))
                                continue;
                            if (string.Equals(slot.colorName, color, StringComparison.OrdinalIgnoreCase))
                                return slot.prefab;
                        }
                    }
                }

                return prefab;
            }
        }

        [Tooltip("Family rows hold the four JetFlame types (V1 / V2 / V3 / Soft) via signature prefabs.")]
        public List<Entry> entries = new List<Entry>();

        /// <summary>Client debug cycle index (T-key). Not serialized; not a ghost field.</summary>
        public static int DebugCycleIndex;

        static ThrusterVfxBank s_Cached;
        static readonly string[] ColorNames = { "Blue", "Green", "Orange", "Purple", "Red", "Yellow" };

        public int EntryCount => entries != null ? entries.Count : 0;

        public static ThrusterVfxBank LoadDefault()
        {
            if (s_Cached != null)
                return s_Cached;
            s_Cached = Resources.Load<ThrusterVfxBank>(ResourcesLoadName);
            return s_Cached;
        }

        public Entry GetEntry(int index)
        {
            if (entries == null || entries.Count == 0)
                return null;
            int i = ((index % entries.Count) + entries.Count) % entries.Count;
            return entries[i];
        }

        public Entry GetEntryByFamilyId(string familyId)
        {
            if (entries == null || string.IsNullOrWhiteSpace(familyId))
                return null;

            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e != null && string.Equals(e.familyId, familyId, StringComparison.OrdinalIgnoreCase))
                    return e;
            }

            return null;
        }

        /// <summary>Modular V2 row, or the first authored entry if that id is missing.</summary>
        public Entry GetDefaultEntry()
        {
            return GetStyleEntry(DefaultStyleIndex) ?? GetEntry(0);
        }

        /// <summary>Wraps 0..3 for the four JetFlame types.</summary>
        public static int WrapStyleIndex(int index)
        {
            int i = index % StyleCount;
            return i < 0 ? i + StyleCount : i;
        }

        /// <summary>Signature row for Classic / Modular / Heavy / Soft.</summary>
        public Entry GetStyleEntry(int styleIndex)
        {
            int i = WrapStyleIndex(styleIndex);
            Entry fromFamily = GetEntryByFamilyId(StyleFamilyIds[i]);
            return fromFamily ?? GetEntryByFamilyId(DefaultFamilyId) ?? GetEntry(0);
        }

        public static string GetStyleDisplayName(int styleIndex)
        {
            return StyleDisplayNames[WrapStyleIndex(styleIndex)];
        }

        public string GetDisplayName(int index) => GetStyleDisplayName(index);

        /// <summary>Authored JetFlame prefab name for the T-key label (no Clone suffix).</summary>
        public string GetThrusterPrefabDisplayName(int index)
        {
            Entry e = GetStyleEntry(index);
            if (e == null || e.prefab == null)
                return string.Empty;
            return e.prefab.name.Replace("(Clone)", string.Empty).Trim();
        }

        /// <summary>Advances <see cref="DebugCycleIndex"/> across the four types.</summary>
        public int CycleDebugIndex()
        {
            DebugCycleIndex = WrapStyleIndex(DebugCycleIndex + 1);
            return DebugCycleIndex;
        }

        public static string ExtractColorNameFromText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            for (int i = 0; i < ColorNames.Length; i++)
            {
                string color = ColorNames[i];
                if (value.IndexOf(color, StringComparison.OrdinalIgnoreCase) >= 0)
                    return color;
            }

            return null;
        }
    }
}
