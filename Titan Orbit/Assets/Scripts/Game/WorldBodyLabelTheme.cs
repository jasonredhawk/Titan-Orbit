using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Shared space-HUD look for world-space planet and moon labels.
    /// Client presentation only — fonts, captions, and overlay TMP materials. No ECS reads.
    /// <para>
    /// [TITAN-ORBIT] LiberationSans made every line feel like the same spreadsheet cell.
    /// Shift Rajdhani is the cockpit face the rest of the HUD already uses. Titles go Bold,
    /// numbers SemiBold, tiny tracked captions Light so CREW / CAP / HULL read as telemetry
    /// rails, not body copy. Paired with <see cref="PlanetWorldStatsLabel"/> and
    /// <see cref="GemMoonWorldStatsLabel"/>.
    /// </para>
    /// </summary>
    public static class WorldBodyLabelTheme
    {
        /// <summary>Uppercase stamp under a home-world name.</summary>
        public const string HomePlanetRole = "HOME PLANET";

        /// <summary>Tiny rail above the live people count.</summary>
        public const string CrewCaption = "CREW";

        /// <summary>Prefix on the capacity line — how many people this world can hold.</summary>
        public const string CapCaption = "CAP";

        /// <summary>
        /// Suffix on the triangle-bonus amount. LINK = extra cap from connected territory,
        /// not a second current-people number.
        /// </summary>
        public const string LinkCaption = "LINK";

        /// <summary>Prefix on the ship-family line.</summary>
        public const string HullCaption = "HULL";

        /// <summary>Prefix on the default weapon line.</summary>
        public const string GunCaption = "GUN";

        /// <summary>Tiny rail above moon gem current.</summary>
        public const string GemsCaption = "GEMS";

        /// <summary>Tiny rail above moon matrix-shield current.</summary>
        public const string ShieldCaption = "SHIELD";

        /// <summary>Prefix on moon gem capacity.</summary>
        public const string GemBankCaption = "BANK";

        /// <summary>Prefix on moon shield capacity.</summary>
        public const string ShieldMaxCaption = "MAX";

        /// <summary>Ice caption used for HULL / GUN / CREW / CAP rails.</summary>
        public static readonly Color CaptionIce = new Color(0.62f, 0.78f, 0.95f, 0.88f);

        /// <summary>Cyan for the LINK bonus so extra cap does not look like a second crew count.</summary>
        public static readonly Color LinkCyan = new Color(0.42f, 0.94f, 1f, 0.96f);

        /// <summary>Dimmer ice for GUN under HULL.</summary>
        public static readonly Color GunIce = new Color(0.62f, 0.78f, 0.95f, 0.72f);

        const int TextSortingOrder = 5001;
        const float OutlineWidth = 0.18f;
        const float FaceDilate = 0.1f;
        const float CaptionOutlineWidth = 0.12f;
        const float CaptionFaceDilate = 0.04f;
        const float HomeOutlineWidth = 0.3f;
        const float HomeFaceDilate = 0.26f;

        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        static TMP_FontAsset _display;
        static TMP_FontAsset _telemetry;
        static TMP_FontAsset _caption;
        static bool _triedFonts;
        static Sprite _pixelSprite;

        /// <summary>
        /// Rajdhani Bold for place names, home stamp, and the live CREW / gem / shield digits.
        /// Falls back to SemiBold, then TMP default.
        /// </summary>
        public static TMP_FontAsset DisplayFont
        {
            get
            {
                EnsureFonts();
                return _display != null ? _display : TMP_Settings.defaultFontAsset;
            }
        }

        /// <summary>Rajdhani SemiBold for family / gun values and CAP digits.</summary>
        public static TMP_FontAsset TelemetryFont
        {
            get
            {
                EnsureFonts();
                return _telemetry != null ? _telemetry : DisplayFont;
            }
        }

        /// <summary>Rajdhani Light for tracked uppercase rails (CREW, HULL, GUN).</summary>
        public static TMP_FontAsset CaptionFont
        {
            get
            {
                EnsureFonts();
                return _caption != null ? _caption : TelemetryFont;
            }
        }

        /// <summary>
        /// <c>HULL  Astro Eagle</c> — ice caption, then the family in the TMP's face color.
        /// </summary>
        public static string FormatHullLine(string familyName)
        {
            if (string.IsNullOrEmpty(familyName))
                return string.Empty;

            return PrefixLine(HullCaption, familyName);
        }

        /// <summary>
        /// <c>GUN  Fireballs</c> — ice caption, then the weapon in the TMP's face color.
        /// </summary>
        public static string FormatGunLine(string weaponName)
        {
            if (string.IsNullOrEmpty(weaponName))
                return string.Empty;

            return PrefixLine(GunCaption, weaponName);
        }

        /// <summary>
        /// Capacity line under CREW. No bonus → <c>CAP  400</c>. With triangles →
        /// <c>CAP  400</c> on this TMP and a separate LINK line from <see cref="FormatLinkLine"/>.
        /// </summary>
        public static string FormatCapLine(int baseMax)
        {
            return PrefixLine(CapCaption, baseMax.ToString());
        }

        /// <summary>
        /// Extra people from connection triangles. Empty when <paramref name="bonusAmount"/> is 0
        /// so the CAP line stays a single meaning.
        /// </summary>
        public static string FormatLinkLine(int bonusAmount)
        {
            if (bonusAmount <= 0)
                return string.Empty;

            return $"+{bonusAmount}  {LinkCaption}";
        }

        /// <summary>Moon gem / shield capacity with a BANK or MAX rail.</summary>
        public static string FormatBankLine(string caption, int max)
        {
            return PrefixLine(caption, max.ToString());
        }

        /// <summary>
        /// Home stamp under the place name. No decorative brackets — Rajdhani has no
        /// ⟨ ⟩ glyphs, so those rendered as tofu squares on each side.
        /// </summary>
        public static string FormatHomeStamp()
        {
            return HomePlanetRole;
        }

        /// <summary>
        /// Applies the overlay SDF material once at create / recover.
        /// [TITAN-ORBIT] Do not call from LateUpdate — <c>fontMaterial</c> instances a copy.
        /// </summary>
        public static void ApplyOverlay(
            TMP_Text text,
            float outlineWidth = OutlineWidth,
            float faceDilate = FaceDilate)
        {
            if (text == null)
                return;

            Material mat = text.fontMaterial;
            if (mat == null)
                return;

            mat.EnableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(0.04f, 0.06f, 0.1f, 0.92f));
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", outlineWidth);
            if (mat.HasProperty("_OutlineSoftness"))
                mat.SetFloat("_OutlineSoftness", 0.05f);
            if (mat.HasProperty("_FaceDilate"))
                mat.SetFloat("_FaceDilate", faceDilate);
            mat.renderQueue = RenderQueueOverlay;

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder;
        }

        /// <summary>Heavier overlay for the HOME PLANET stamp.</summary>
        public static void ApplyHomeStampOverlay(TMP_Text text)
        {
            ApplyOverlay(text, HomeOutlineWidth, HomeFaceDilate);
        }

        /// <summary>Lighter overlay for tiny caption rails so they stay thin.</summary>
        public static void ApplyCaptionOverlay(TMP_Text text)
        {
            ApplyOverlay(text, CaptionOutlineWidth, CaptionFaceDilate);
        }

        /// <summary>
        /// Shared 1×1 white sprite for the thin team-color rule under the identity block.
        /// Created once — not per planet, not per frame.
        /// </summary>
        public static Sprite PixelSprite
        {
            get
            {
                if (_pixelSprite != null)
                    return _pixelSprite;

                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.name = "WorldBodyLabelPixel";
                tex.SetPixel(0, 0, Color.white);
                tex.Apply(false, true);
                tex.filterMode = FilterMode.Point;
                _pixelSprite = Sprite.Create(
                    tex,
                    new Rect(0f, 0f, 1f, 1f),
                    new Vector2(0.5f, 0.5f),
                    1f);
                _pixelSprite.name = "WorldBodyLabelPixelSprite";
                return _pixelSprite;
            }
        }

        /// <summary>Ice hex for rich-text caption prefixes (HULL / GUN / CAP).</summary>
        public static string CaptionIceHex => ColorUtility.ToHtmlStringRGBA(CaptionIce);

        static string PrefixLine(string caption, string value)
        {
            return $"<color=#{CaptionIceHex}>{caption}</color>  {value}";
        }

        static void EnsureFonts()
        {
            if (_triedFonts)
                return;

            _triedFonts = true;
            _display = LoadFont(
                "Rajdhani-Bold SDF",
                "Assets/Shift - Complete Sci-Fi UI/Fonts/Rajdhani-Bold SDF.asset");
            _telemetry = LoadFont(
                "Rajdhani-SemiBold SDF",
                "Assets/Shift - Complete Sci-Fi UI/Fonts/Rajdhani-SemiBold SDF.asset");
            _caption = LoadFont(
                "Rajdhani-Light SDF",
                "Assets/Shift - Complete Sci-Fi UI/Fonts/Rajdhani-Light SDF.asset");

            if (_display == null)
                _display = _telemetry;
            if (_telemetry == null)
                _telemetry = _display;
            if (_caption == null)
                _caption = _telemetry;
        }

        /// <summary>
        /// Resources first (player builds if someone copied the SDF into Resources),
        /// then the authored Shift path in the Editor.
        /// </summary>
        static TMP_FontAsset LoadFont(string resourcesName, string editorAssetPath)
        {
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>(resourcesName);
            if (font != null)
                return font;

#if UNITY_EDITOR
            font = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(editorAssetPath);
#endif
            return font;
        }
    }
}
