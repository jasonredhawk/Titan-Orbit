using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Client thruster jets. <c>Assets/Resources/ThrusterVfxBank.asset</c> lists
    /// four types (V1, V2, V3, Soft). Each type has one prefab per team color.
    /// Team D is orange and uses the yellow flame, because that folder has no
    /// orange prefab. Placement and size come from the live mount.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ThrusterVfxBank",
        menuName = "Titan Orbit/Thruster VFX Bank",
        order = 48)]
    public class ThrusterVfxBank : ScriptableObject
    {
        public const string ResourcesLoadName = "ThrusterVfxBank";
        public const string ResourcesAssetPath = "Assets/Resources/ThrusterVfxBank.asset";

        /// <summary>V1, V2, V3, Soft. Index 1 is V2, the unmatched default.</summary>
        public const int StyleCount = 4;

        /// <summary>V2.</summary>
        public const int DefaultStyleIndex = 1;

        /// <summary>Soft folder.</summary>
        public const int SoftStyleIndex = 3;

        /// <summary>Soft white fallback when a team color prefab is missing.</summary>
        public const string NeutralFlameColorName = "White";

        /// <summary>Stored style index → folder name. Customize Ship shows these names.</summary>
        static readonly string[] StyleIds = { "V1", "V2", "V3", "Soft" };

        /// <summary>UI cycle follows folder order. Stored indices stay 0–3.</summary>
        static readonly int[] StyleUiOrder = { 0, 1, 2, 3 };

        /// <summary>V1, V2, V3, then the Soft folder. Index order matches saved style picks.</summary>
        public const string JetFlameFolder =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Interactive/JetFlame";

        static readonly string[] StyleEditorPaths =
        {
            JetFlameFolder + "/V1/ModularJetFlame.prefab",
            JetFlameFolder + "/V2/ModularJetFlame2.prefab",
            JetFlameFolder + "/V3/ModularJetFlame3.prefab",
            JetFlameFolder + "/Soft/JetFlameSoftWhite.prefab",
        };

        static readonly string[] StyleResourceNames =
        {
            "ModularJetFlame",
            "ModularJetFlame2",
            "ModularJetFlame3",
            "JetFlameSoftWhite",
        };

        /// <summary>V1 / V2 / V3 / Soft color-variant paths. Orange files are named Yellow.</summary>
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

        /// <summary>Team color words. Orange is Team D and points at the yellow prefab.</summary>
        public static readonly string[] FlameColorNames = { "Red", "Blue", "Green", "Orange", "Purple" };

        static readonly Color[] FlameDisplayColors =
        {
            new Color(0.90f, 0.25f, 0.25f, 1f),
            new Color(0.25f, 0.40f, 0.90f, 1f),
            new Color(0.20f, 0.70f, 0.28f, 1f),
            new Color(0.95f, 0.55f, 0.12f, 1f),
            new Color(0.65f, 0.25f, 0.85f, 1f),
        };

        static readonly float[] FlameHues = { 0.00f, 0.60f, 0.33f, 0.08f, 0.80f };

        /// <summary>
        /// Child name for Instantiated jet instances so stash/restore can strip them
        /// instead of baking a live flame into the original part.
        /// </summary>
        public const string JetInstanceName = "_ThrusterJetVfx";

        /// <summary>One team-color prefab on a thruster type.</summary>
        [Serializable]
        public class TeamColorFlame
        {
            public TeamId team;
            [Tooltip("Team color word. Team D is Orange and uses the yellow flame prefab.")]
            public string colorName;
            public GameObject prefab;
        }

        /// <summary>One jet type: V1, V2, V3, or Soft, with a prefab for each team.</summary>
        [Serializable]
        public class ThrusterType
        {
            [Tooltip("V1, V2, V3, or Soft.")]
            public string name;

            public List<TeamColorFlame> teamColors = new List<TeamColorFlame>();

            [HideInInspector]
            public GameObject fallbackPrefab;
        }

        [Tooltip("Four jet types. Each row lists Team A–E and the prefab for that color.")]
        public List<ThrusterType> thrusters = new List<ThrusterType>();

        /// <summary>Client debug cycle index (T-key). Not serialized; not a ghost field.</summary>
        public static int DebugCycleIndex;

        static ThrusterVfxBank s_Cached;

        public int EntryCount => thrusters != null && thrusters.Count > 0 ? thrusters.Count : StyleCount;

        public static ThrusterVfxBank LoadDefault()
        {
            if (s_Cached != null)
                return s_Cached;
            s_Cached = Resources.Load<ThrusterVfxBank>(ResourcesLoadName);
            return s_Cached;
        }

        /// <summary>Wraps 0..3 for V1 / V2 / V3 / Soft.</summary>
        public static int WrapStyleIndex(int index)
        {
            int i = index % StyleCount;
            return i < 0 ? i + StyleCount : i;
        }

        public static string GetStyleId(int styleIndex)
        {
            return StyleIds[WrapStyleIndex(styleIndex)];
        }

        /// <summary>Row whose <see cref="ThrusterType.name"/> is V1, V2, V3, or Soft.</summary>
        public ThrusterType GetThruster(int styleIndex)
        {
            if (thrusters == null || thrusters.Count == 0)
                return null;

            string id = GetStyleId(styleIndex);
            for (int i = 0; i < thrusters.Count; i++)
            {
                ThrusterType row = thrusters[i];
                if (row != null && string.Equals(row.name, id, StringComparison.OrdinalIgnoreCase))
                    return row;
            }

            int wrapped = WrapStyleIndex(styleIndex);
            if (wrapped < thrusters.Count)
                return thrusters[wrapped];
            return null;
        }

        /// <summary>
        /// Instantiable flame for V1 / V2 / V3 / Soft.
        /// Same four JetFlame types the T-key debug cycle shows.
        /// </summary>
        public static GameObject LoadStylePrefab(int styleIndex)
        {
            return LoadStylePrefab(styleIndex, null);
        }

        /// <summary>
        /// Team-colored flame for a type (RedJetFlame, JetFlameSoftBlue, …).
        /// Orange resolves to the yellow prefab. Falls back to the type's
        /// neutral flame when the color is empty or missing.
        /// </summary>
        public static GameObject LoadStylePrefab(int styleIndex, string colorName)
        {
            int i = WrapStyleIndex(styleIndex);
            ThrusterVfxBank bank = LoadDefault();
            if (bank != null)
            {
                GameObject fromBank = bank.ResolvePrefab(i, colorName);
                if (fromBank != null)
                    return fromBank;
            }

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

            return Resources.Load<GameObject>(StyleResourceNames[i]);
        }

        /// <summary>Team color word for a faction. Team D is Orange.</summary>
        public static string FlameColorNameForTeam(TeamId team)
        {
            switch (team == TeamId.None ? TeamId.TeamA : team)
            {
                case TeamId.TeamB: return "Blue";
                case TeamId.TeamC: return "Green";
                case TeamId.TeamD: return "Orange";
                case TeamId.TeamE: return "Purple";
                default: return "Red";
            }
        }

        GameObject ResolvePrefab(int styleIndex, string colorName)
        {
            ThrusterType type = GetThruster(styleIndex);
            if (type == null)
                return null;

            if (!string.IsNullOrEmpty(colorName) && type.teamColors != null)
            {
                string asked = CanonicalFlameColorName(colorName);
                for (int i = 0; i < type.teamColors.Count; i++)
                {
                    TeamColorFlame slot = type.teamColors[i];
                    if (slot == null || slot.prefab == null)
                        continue;
                    if (string.Equals(CanonicalFlameColorName(slot.colorName), asked, StringComparison.OrdinalIgnoreCase))
                        return slot.prefab;
                }
            }

            return type.fallbackPrefab;
        }

        static GameObject LoadColoredStylePrefab(int styleIndex, string colorName)
        {
            string token = PrefabFileColorToken(colorName);
            if (string.IsNullOrEmpty(token))
                return null;

#if UNITY_EDITOR
            string editorPath = string.Format(StyleColorEditorFormats[styleIndex], token);
            GameObject fromFolder = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(editorPath);
            if (fromFolder != null)
                return fromFolder;
#endif

            string resourceName = string.Format(StyleColorResourceFormats[styleIndex], token);
            return Resources.Load<GameObject>(resourceName);
        }

        /// <summary>Folder token. Orange has no file; those prefabs are named Yellow.</summary>
        public static string PrefabFileColorToken(string colorName)
        {
            string safe = CanonicalFlameColorName(colorName);
            if (string.IsNullOrEmpty(safe))
                return null;
            if (string.Equals(safe, "Orange", StringComparison.OrdinalIgnoreCase))
                return "Yellow";
            return safe;
        }

        /// <summary>Maps a picker / team swatch onto the nearest team flame name.</summary>
        public static string NearestFlameColorName(Color color)
        {
            Color.RGBToHSV(color, out float hue, out float sat, out float val);
            if (sat < 0.12f && val < 0.18f)
                return "Red";
            if (sat < 0.12f)
                return val > 0.65f ? "White" : "Blue";

            int best = 0;
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

        /// <summary>Bright well / HUD color for a team flame name.</summary>
        public static Color32 GetFlameDisplayColor(string colorName)
        {
            string safe = CanonicalFlameColorName(colorName);
            for (int i = 0; i < FlameColorNames.Length; i++)
            {
                if (string.Equals(FlameColorNames[i], safe, StringComparison.OrdinalIgnoreCase))
                    return FlameDisplayColors[i];
            }

            return FlameDisplayColors[0];
        }

        /// <summary>
        /// Red, Blue, Green, Orange, Purple, or White.
        /// Yellow is the orange team's file name, so it canonicalizes to Orange.
        /// </summary>
        public static string CanonicalFlameColorName(string colorName)
        {
            if (string.IsNullOrEmpty(colorName))
                return null;

            if (string.Equals(colorName, "White", StringComparison.OrdinalIgnoreCase))
                return "White";

            if (colorName.IndexOf("Orange", StringComparison.OrdinalIgnoreCase) >= 0
                || colorName.IndexOf("Yellow", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Orange";

            for (int i = 0; i < FlameColorNames.Length; i++)
            {
                if (string.Equals(FlameColorNames[i], colorName, StringComparison.OrdinalIgnoreCase)
                    || colorName.IndexOf(FlameColorNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return FlameColorNames[i];
            }

            return null;
        }

        public static string GetStyleDisplayName(int styleIndex)
        {
            ThrusterVfxBank bank = LoadDefault();
            ThrusterType type = bank != null ? bank.GetThruster(styleIndex) : null;
            if (type != null && !string.IsNullOrWhiteSpace(type.name))
                return type.name;
            return GetStyleId(styleIndex);
        }

        /// <summary>Steps the Customize Ship type row (V1, V2, V3, Soft).</summary>
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

            int next = pos + delta;
            int count = StyleUiOrder.Length;
            int wrappedPos = next % count;
            if (wrappedPos < 0)
                wrappedPos += count;
            return StyleUiOrder[wrappedPos];
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
    }
}
