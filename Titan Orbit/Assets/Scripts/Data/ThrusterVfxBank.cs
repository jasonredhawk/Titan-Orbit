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

        /// <summary>Ribbon V1, Default V2, Heavy V3, Soft. Index 1 is Default.</summary>
        public const int StyleCount = 4;

        /// <summary>Default (V2 / AstroEagle signature).</summary>
        public const int DefaultStyleIndex = 1;

        static readonly string[] StyleDisplayNames = { "Ribbon", "Default", "Heavy", "Soft" };

        /// <summary>UI cycle: Default first, then Ribbon / Heavy / Soft. Stored indices stay 0–3.</summary>
        static readonly int[] StyleUiOrder = { DefaultStyleIndex, 0, 2, 3 };

        /// <summary>Ribbon = V1, Modular = V2, Heavy = V3, Soft = Soft folder.</summary>
        public const string JetFlameFolder =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Interactive/JetFlame";

        static readonly string[] StyleEditorPaths =
        {
            JetFlameFolder + "/V1/ModularJetFlame.prefab",
            JetFlameFolder + "/V2/ModularJetFlame2.prefab",
            JetFlameFolder + "/V3/ModularJetFlame3.prefab",
            JetFlameFolder + "/Soft/JetFlameSoftRed.prefab",
        };

        static readonly string[] StyleResourceNames =
        {
            "ModularJetFlame",
            "ModularJetFlame2",
            "ModularJetFlame3",
            "JetFlameSoftRed",
        };

        /// <summary>Ribbon / Modular / Heavy / Soft color-variant paths in the JetFlame folders.</summary>
        static readonly string[] StyleColorEditorFormats =
        {
            JetFlameFolder + "/V1/{0}JetFlame.prefab",
            JetFlameFolder + "/V2/{0}JetFlame2.prefab",
            JetFlameFolder + "/V3/{0}JetFlame3.prefab",
            JetFlameFolder + "/Soft/JetFlameSoft{0}.prefab",
        };

        static readonly string[] StyleColorResourceFormats =
        {
            "{0}JetFlame",
            "{0}JetFlame2",
            "{0}JetFlame3",
            "JetFlameSoft{0}",
        };

        /// <summary>Authored Archanor colors. There is no black flame — dark picks snap here.</summary>
        public static readonly string[] FlameColorNames = { "Blue", "Green", "Purple", "Red", "Yellow" };

        static readonly Color[] FlameDisplayColors =
        {
            new Color(0.22f, 0.48f, 1.00f, 1f),
            new Color(0.20f, 0.88f, 0.28f, 1f),
            new Color(0.72f, 0.28f, 0.95f, 1f),
            new Color(0.98f, 0.18f, 0.16f, 1f),
            new Color(1.00f, 0.86f, 0.18f, 1f),
        };

        static readonly float[] FlameHues = { 0.60f, 0.33f, 0.80f, 0.00f, 0.14f };

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

        [Tooltip("Ribbon V1, Modular V2, Heavy V3, Soft. Used before family-row lookup.")]
        public GameObject[] stylePrefabs = new GameObject[StyleCount];

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

        /// <summary>Signature row for Ribbon / Modular / Heavy / Soft.</summary>
        public Entry GetStyleEntry(int styleIndex)
        {
            return GetEntryByFamilyId(DefaultFamilyId) ?? GetEntry(0);
        }

        /// <summary>
        /// Instantiable flame for Ribbon / Modular / Heavy / Soft.
        /// Same four JetFlame prefabs the T-key debug cycle shows.
        /// </summary>
        public static GameObject LoadStylePrefab(int styleIndex)
        {
            return LoadStylePrefab(styleIndex, null);
        }

        /// <summary>
        /// Loads the authored color variant for a type (RedJetFlame, JetFlameSoftBlue, …).
        /// Falls back to the signature prefab when the color is empty or missing.
        /// </summary>
        public static GameObject LoadStylePrefab(int styleIndex, string colorName)
        {
            int i = WrapStyleIndex(styleIndex);
            if (!string.IsNullOrEmpty(colorName))
            {
                GameObject colored = LoadColoredStylePrefab(i, colorName);
                if (colored != null)
                    return colored;
            }

#if UNITY_EDITOR
            GameObject fromFolder = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(StyleEditorPaths[i]);
            if (fromFolder != null)
                return fromFolder;
#endif

            GameObject fromResources = Resources.Load<GameObject>(StyleResourceNames[i]);
            if (fromResources != null)
                return fromResources;

            ThrusterVfxBank bank = LoadDefault();
            if (bank != null
                && bank.stylePrefabs != null
                && i < bank.stylePrefabs.Length
                && bank.stylePrefabs[i] != null)
            {
                return bank.stylePrefabs[i];
            }

            return null;
        }

        static GameObject LoadColoredStylePrefab(int styleIndex, string colorName)
        {
            string safe = CanonicalFlameColorName(colorName);
            if (string.IsNullOrEmpty(safe))
                return null;

#if UNITY_EDITOR
            string editorPath = string.Format(StyleColorEditorFormats[styleIndex], safe);
            GameObject fromFolder = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(editorPath);
            if (fromFolder != null)
                return fromFolder;
#endif

            string resourceName = string.Format(StyleColorResourceFormats[styleIndex], safe);
            return Resources.Load<GameObject>(resourceName);
        }

        /// <summary>Maps a picker / team swatch onto the nearest authored flame name.</summary>
        public static string NearestFlameColorName(Color color)
        {
            Color.RGBToHSV(color, out float hue, out float sat, out float val);
            if (sat < 0.12f && val < 0.18f)
                return "Red";
            if (sat < 0.12f)
                return val > 0.65f ? "White" : "Blue";

            int best = 3;
            float bestDist = 2f;
            for (int i = 0; i < FlameHues.Length; i++)
            {
                float d = Mathf.Abs(hue - FlameHues[i]);
                if (d > 0.5f)
                    d = 1f - d;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }

            return FlameColorNames[best];
        }

        /// <summary>Bright well / HUD color for an authored flame name. Never black.</summary>
        public static Color32 GetFlameDisplayColor(string colorName)
        {
            string safe = CanonicalFlameColorName(colorName);
            for (int i = 0; i < FlameColorNames.Length; i++)
            {
                if (string.Equals(FlameColorNames[i], safe, StringComparison.OrdinalIgnoreCase))
                    return FlameDisplayColors[i];
            }

            return FlameDisplayColors[3];
        }

        public static string CanonicalFlameColorName(string colorName)
        {
            if (string.IsNullOrEmpty(colorName))
                return null;

            for (int i = 0; i < FlameColorNames.Length; i++)
            {
                if (string.Equals(FlameColorNames[i], colorName, StringComparison.OrdinalIgnoreCase))
                    return FlameColorNames[i];
            }

            if (colorName.IndexOf("Orange", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Yellow";

            return ExtractColorNameFromText(colorName);
        }

        public static string GetStyleDisplayName(int styleIndex)
        {
            return StyleDisplayNames[WrapStyleIndex(styleIndex)];
        }

        /// <summary>Steps the Customize Ship TYPE row (Default is first).</summary>
        public static int CycleStyleIndex(int current, int delta)
        {
            int wrapped = WrapStyleIndex(current);
            int pos = 0;
            for (int i = 0; i < StyleUiOrder.Length; i++)
            {
                if (StyleUiOrder[i] == wrapped)
                {
                    pos = i;
                    break;
                }
            }

            return StyleUiOrder[WrapStyleIndex(pos + delta)];
        }

        public string GetDisplayName(int index) => GetStyleDisplayName(index);

        /// <summary>Authored JetFlame prefab name for the T-key label (no Clone suffix).</summary>
        public string GetThrusterPrefabDisplayName(int index)
        {
            GameObject prefab = LoadStylePrefab(index);
            if (prefab == null)
                return string.Empty;
            return prefab.name.Replace("(Clone)", string.Empty).Trim();
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
