using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Builds the shared sci-fi "calculation card" chrome for ship-stat hover tips
    /// (ability chips + speedometer bands). Presentation-only — no ECS reads/writes.
    /// <para>
    /// [TITAN-ORBIT] Dark void glass + thin category accent. Avoids bright Shift "Filled"
    /// sprites as the panel background (those washed out / overlapped the title). Section
    /// banners in rich text (<see cref="AppendSectionBanner"/>) split PARTS / PIPELINE / LIVE
    /// blocks inside the single TMP body.
    /// </para>
    /// Paired with <see cref="ShipAttributeUpgradeHUD"/> and <see cref="ShipSpeedometerHUD"/>.
    /// </summary>
    public static class ShipStatTooltipChrome
    {
        /// <summary>Handles returned by <see cref="Build"/> so hosts can tint accents and size the body.</summary>
        public struct Handles
        {
            /// <summary>Root GameObject (activate/deactivate on hover).</summary>
            public GameObject Root;

            /// <summary>Root rect — hosts set pivot, sizeDelta, and anchoredPosition.</summary>
            public RectTransform RootRect;

            /// <summary>Rich-text body TMP (hosts set .text from breakdown builders).</summary>
            public TextMeshProUGUI BodyLabel;

            /// <summary>Top caption ("ABILITY MATRIX" / "TELEMETRY").</summary>
            public TextMeshProUGUI CaptionLabel;

            /// <summary>Thin category/section accent along the top edge.</summary>
            public Image AccentStripe;

            /// <summary>Optional outline-only frame (muted; not accent-flooded).</summary>
            public Image FrameImage;

            /// <summary>
            /// Extra vertical padding (canvas units) to add on top of <c>preferredHeight</c>
            /// when sizing the root — covers caption strip + insets around the body.
            /// </summary>
            public float ExtraHeightPadding;
        }

        // --- Palette: near-black glass; accents stay on the thin stripe only ---
        // [TITAN-ORBIT] User preference: dark backgrounds, keep coloured accents.
        // Fully opaque so leaderboard / ship icons cannot bleed through the tip body.
        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 1f);
        static readonly Color CaptionPlateColor = new Color(0.018f, 0.028f, 0.045f, 1f);
        static readonly Color SeparatorColor = new Color(0.12f, 0.18f, 0.28f, 0.85f);
        static readonly Color FrameTint = new Color(0.22f, 0.32f, 0.45f, 0.55f);
        static readonly Color CaptionTextColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyTextColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color BracketColor = new Color(0.35f, 0.55f, 0.75f, 0.55f);
        static readonly Color DefaultAccent = new Color(0.35f, 0.72f, 0.95f, 0.95f);

        /// <summary>Muted rail colour for section dividers.</summary>
        public const string SectionRailHex = "2A3A4E";

        static SpinCardShiftVisuals _cachedVisuals;
        static TMP_FontAsset _cachedRajdhani;
        static bool _triedVisuals;
        static bool _triedFont;

        /// <summary>
        /// Creates a floating telemetry card under <paramref name="parent"/>.
        /// Starts inactive. Scale multipliers keep mobile / HUD scale consistent with hosts.
        /// </summary>
        /// <param name="name">GameObject name (e.g. ShipAbilityStatTooltip).</param>
        /// <param name="parent">Canvas / HUD transform that owns positioning.</param>
        /// <param name="caption">Short uppercase label in the header strip.</param>
        /// <param name="width">Initial width in canvas units (already scaled by caller).</param>
        /// <param name="height">Initial height in canvas units (already scaled by caller).</param>
        /// <param name="uiScale">Uniform scale for fonts, padding, brackets (1 = desktop).</param>
        /// <returns>Handles the host stores for show/hide, tint, and text refresh.</returns>
        public static Handles Build(
            string name,
            Transform parent,
            string caption,
            float width,
            float height,
            float uiScale)
        {
            float s = Mathf.Max(0.5f, uiScale);

            // --- Root ---
            // [UNITY] No Image on the root — children paint fill/frame so Outline cannot soft-blur the cut corners.
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            var rootRt = root.AddComponent<RectTransform>();
            rootRt.sizeDelta = new Vector2(width, height);

            SpinCardShiftVisuals visuals = ResolveVisuals();

            // --- Opaque dark fill (solid — no bright Shift "Background Basic" wash) ---
            Image fill = CreateChildImage(root.transform, "Fill", stretch: true);
            fill.color = FillColor;
            fill.raycastTarget = false;
            // Intentional: do NOT assign innerPanelSliced — those assets read as bright coloured plates.

            // --- Outline-only frame (3px cut if available; else UI Outline) ---
            Image frame = null;
            Sprite outlineSprite = visuals != null ? visuals.iconDockSliced : null;
            if (outlineSprite != null)
            {
                // [TITAN-ORBIT] iconDock = Cut Frame 3px — hollow border, does not paint a bright fill over text.
                frame = CreateChildImage(root.transform, "Frame", stretch: true);
                frame.sprite = outlineSprite;
                frame.type = Image.Type.Sliced;
                frame.color = FrameTint;
                frame.raycastTarget = false;
            }
            else
            {
                var outline = root.AddComponent<Outline>();
                outline.effectColor = FrameTint;
                outline.effectDistance = new Vector2(1.25f * s, -1.25f * s);
            }

            // --- Title strip layout (top → bottom): accent | caption plate | separator | gap | body ---
            // [TITAN-ORBIT] Previous caption used a tall Shift accent-line 9-slice as a background,
            // which visually crashed into the first body lines. Keep each band as a solid rect.
            float edgePad = 10f * s;
            float accentH = 3f * s;
            float captionH = 20f * s;
            float separatorH = 1f * s;
            float gapAfterCaption = 8f * s;
            float bodyPadX = 12f * s;
            float bodyPadBottom = 10f * s;
            float bodyTop = edgePad * 0.35f + accentH + captionH + separatorH + gapAfterCaption;

            // Accent stripe — only bright colour on the card (hosts recolor per category).
            Image accent = CreateChildImage(root.transform, "Accent", stretch: false);
            RectTransform accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = new Vector2(0f, -edgePad * 0.35f);
            accentRt.sizeDelta = new Vector2(-(edgePad * 1.2f), accentH);
            accent.color = DefaultAccent;
            accent.raycastTarget = false;

            // Caption plate (dark, under the accent).
            GameObject captionGo = new GameObject("CaptionBar");
            captionGo.transform.SetParent(root.transform, false);
            RectTransform captionRt = captionGo.AddComponent<RectTransform>();
            captionRt.anchorMin = new Vector2(0f, 1f);
            captionRt.anchorMax = new Vector2(1f, 1f);
            captionRt.pivot = new Vector2(0.5f, 1f);
            captionRt.anchoredPosition = new Vector2(0f, -(edgePad * 0.35f + accentH));
            captionRt.sizeDelta = new Vector2(-(edgePad * 1.2f), captionH);

            Image captionBg = captionGo.AddComponent<Image>();
            captionBg.color = CaptionPlateColor;
            captionBg.raycastTarget = false;

            GameObject captionTextGo = new GameObject("Caption");
            captionTextGo.transform.SetParent(captionGo.transform, false);
            RectTransform captionTextRt = captionTextGo.AddComponent<RectTransform>();
            captionTextRt.anchorMin = Vector2.zero;
            captionTextRt.anchorMax = Vector2.one;
            captionTextRt.offsetMin = new Vector2(10f * s, 2f * s);
            captionTextRt.offsetMax = new Vector2(-10f * s, -2f * s);

            TextMeshProUGUI captionLabel = captionTextGo.AddComponent<TextMeshProUGUI>();
            captionLabel.text = string.IsNullOrEmpty(caption) ? "TELEMETRY" : caption;
            captionLabel.fontSize = 10.5f * s;
            captionLabel.fontStyle = FontStyles.Bold;
            captionLabel.characterSpacing = 2.5f;
            captionLabel.alignment = TextAlignmentOptions.MidlineLeft;
            captionLabel.color = CaptionTextColor;
            captionLabel.raycastTarget = false;
            captionLabel.enableWordWrapping = false;
            captionLabel.overflowMode = TextOverflowModes.Ellipsis;
            captionLabel.margin = Vector4.zero;
            ApplyHudFont(captionLabel);

            // Hairline separator under the caption — clear break before body text.
            Image sep = CreateChildImage(root.transform, "CaptionSeparator", stretch: false);
            RectTransform sepRt = sep.rectTransform;
            sepRt.anchorMin = new Vector2(0f, 1f);
            sepRt.anchorMax = new Vector2(1f, 1f);
            sepRt.pivot = new Vector2(0.5f, 1f);
            sepRt.anchoredPosition = new Vector2(0f, -(edgePad * 0.35f + accentH + captionH));
            sepRt.sizeDelta = new Vector2(-(edgePad * 1.6f), separatorH);
            sep.color = SeparatorColor;
            sep.raycastTarget = false;

            // --- Corner brackets (outside the text inset) ---
            float bracket = 9f * s;
            float bracketThick = 1.5f * s;
            float bracketInset = 5f * s;
            CreateCornerBracket(root.transform, "BracketTL", new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(bracketInset, -bracketInset), bracket, bracketThick, true, true);
            CreateCornerBracket(root.transform, "BracketTR", new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-bracketInset, -bracketInset), bracket, bracketThick, false, true);
            CreateCornerBracket(root.transform, "BracketBL", new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(bracketInset, bracketInset), bracket, bracketThick, true, false);
            CreateCornerBracket(root.transform, "BracketBR", new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-bracketInset, bracketInset), bracket, bracketThick, false, false);

            // --- Body text (clearly below caption + separator + gap) ---
            GameObject bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(root.transform, false);
            RectTransform bodyRt = bodyGo.AddComponent<RectTransform>();
            bodyRt.anchorMin = Vector2.zero;
            bodyRt.anchorMax = Vector2.one;
            bodyRt.offsetMin = new Vector2(bodyPadX, bodyPadBottom);
            bodyRt.offsetMax = new Vector2(-bodyPadX, -bodyTop);

            TextMeshProUGUI body = bodyGo.AddComponent<TextMeshProUGUI>();
            body.fontSize = 11f * s;
            body.richText = true;
            body.enableWordWrapping = true;
            body.overflowMode = TextOverflowModes.Overflow;
            body.alignment = TextAlignmentOptions.TopLeft;
            body.color = BodyTextColor;
            body.raycastTarget = false;
            body.lineSpacing = 4f;
            body.paragraphSpacing = 2f;
            ApplyHudFont(body);

            // Body last sibling so it always draws above chrome.
            bodyGo.transform.SetAsLastSibling();

            root.SetActive(false);

            return new Handles
            {
                Root = root,
                RootRect = rootRt,
                BodyLabel = body,
                CaptionLabel = captionLabel,
                AccentStripe = accent,
                FrameImage = frame,
                ExtraHeightPadding = bodyTop + bodyPadBottom + 6f * s
            };
        }

        /// <summary>
        /// Recolors the accent stripe only. Frame stays muted dark — does not flood the card
        /// with category orange/green/etc. (that was the "bright background" look).
        /// </summary>
        /// <param name="handles">Panel built by <see cref="Build"/>.</param>
        /// <param name="accent">Category / section colour (alpha rewritten for the stripe).</param>
        public static void ApplyAccent(in Handles handles, Color accent)
        {
            if (handles.AccentStripe == null)
                return;

            Color c = accent;
            c.a = 0.95f;
            handles.AccentStripe.color = c;
        }

        /// <summary>Speedometer section → HUD accent (matches ODEMC family tones where possible).</summary>
        public static Color AccentForSpeedometerSection(SpeedometerStatSection section)
        {
            return section switch
            {
                SpeedometerStatSection.Speed => ShipAbilityCategoryColors.ShipForHud,
                SpeedometerStatSection.Accel => new Color(0.35f, 0.9f, 0.85f, 0.95f),
                SpeedometerStatSection.Mass => ShipAbilityCategoryColors.CargoForHud,
                SpeedometerStatSection.Ram => new Color(0.95f, 0.55f, 0.25f, 0.95f),
                SpeedometerStatSection.Bullets => ShipAbilityCategoryColors.WeaponForHud,
                _ => DefaultAccent
            };
        }

        /// <summary>Ability chip index 0–9 → power-breakdown stat colour.</summary>
        public static Color AccentForAbilityIndex(int abilityIndex)
        {
            return ShipAbilityCategoryColors.GetPowerBreakdownStatColorForHud(abilityIndex, 0.95f);
        }

        // --------------------------------------------------------------------------
        // Inner section banners (rich text "panels" inside the single TMP body)
        // -------------------------------------------------------------------------

        /// <summary>
        /// Starts a labelled inner block with a coloured title + thin rail.
        /// [TITAN-ORBIT] No TMP &lt;mark&gt; plates — those rendered as opaque grey rectangles over the tip.
        /// </summary>
        /// <param name="sb">Tip string builder.</param>
        /// <param name="title">Short uppercase section name (e.g. PARTS).</param>
        /// <param name="accentHex">6-digit hex without # (e.g. 5B9BD5).</param>
        public static void AppendSectionBanner(StringBuilder sb, string title, string accentHex = "5B9BD5")
        {
            if (sb == null)
                return;

            // Blank line before a new block (skip if builder is empty / just started).
            if (sb.Length > 0)
                sb.AppendLine();

            // Text-only header — accent colour carries the section identity.
            sb.Append("<color=#").Append(accentHex).Append(">> ")
                .Append(title)
                .Append("</color>")
                .AppendLine();
            sb.Append("<color=#").Append(SectionRailHex).Append(">------------------------</color>")
                .AppendLine();
        }

        /// <summary>Thin rail between sub-blocks inside a section (PRIMARY vs EXTRAS, etc.).</summary>
        public static void AppendSubDivider(StringBuilder sb)
        {
            if (sb == null)
                return;
            sb.Append("<color=#").Append(SectionRailHex).Append(">· · · · · · · · · · · ·</color>")
                .AppendLine();
        }

        /// <summary>Closing rail after a major block (optional — next banner already spaces).</summary>
        public static void AppendSectionClose(StringBuilder sb)
        {
            if (sb == null)
                return;
            sb.Append("<color=#").Append(SectionRailHex).Append(">------------------------</color>")
                .AppendLine();
        }

        // --------------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------------

        /// <summary>Loads Resources/SpinCardShiftVisuals once (same asset orbit station uses).</summary>
        static SpinCardShiftVisuals ResolveVisuals()
        {
            if (_triedVisuals)
                return _cachedVisuals;
            _triedVisuals = true;
            // [UNITY] Resources.Load — asset lives at Assets/Resources/SpinCardShiftVisuals.asset.
            _cachedVisuals = Resources.Load<SpinCardShiftVisuals>("SpinCardShiftVisuals");
            return _cachedVisuals;
        }

        /// <summary>Prefers Shift Rajdhani; falls back to TMP default.</summary>
        static void ApplyHudFont(TextMeshProUGUI tmp)
        {
            if (tmp == null)
                return;

            TMP_FontAsset font = ResolveRajdhani();
            if (font != null)
                tmp.font = font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
        }

        /// <summary>
        /// Tries common Shift font asset paths. Returns null if none found (LiberationSans still fine).
        /// </summary>
        static TMP_FontAsset ResolveRajdhani()
        {
            if (_triedFont)
                return _cachedRajdhani;
            _triedFont = true;

            _cachedRajdhani = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (_cachedRajdhani != null)
                return _cachedRajdhani;

#if UNITY_EDITOR
            // [EDITOR] Direct path load so Editor Play Mode gets the sci-fi face without a Resources copy.
            _cachedRajdhani = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/Shift - Complete Sci-Fi UI/Fonts/Rajdhani-SemiBold SDF.asset");
            if (_cachedRajdhani == null)
            {
                _cachedRajdhani = UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                    "Assets/Shift - Complete Sci-Fi UI/Fonts/Rajdhani-Bold SDF.asset");
            }
#endif
            return _cachedRajdhani;
        }

        /// <summary>Creates a full-stretch (or empty) child Image under parent.</summary>
        static Image CreateChildImage(Transform parent, string name, bool stretch)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            if (stretch)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }

            return go.AddComponent<Image>();
        }

        /// <summary>
        /// Two thin bars forming an L at a corner — classic targeting-HUD motif.
        /// </summary>
        static void CreateCornerBracket(
            Transform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 anchoredPos,
            float armLength,
            float thickness,
            bool openRight,
            bool openDown)
        {
            GameObject holder = new GameObject(name);
            holder.transform.SetParent(parent, false);
            RectTransform holderRt = holder.AddComponent<RectTransform>();
            holderRt.anchorMin = anchorMin;
            holderRt.anchorMax = anchorMax;
            holderRt.pivot = anchorMin;
            holderRt.anchoredPosition = anchoredPos;
            holderRt.sizeDelta = new Vector2(armLength, armLength);

            Image h = CreateChildImage(holder.transform, "H", stretch: false);
            RectTransform hRt = h.rectTransform;
            hRt.pivot = new Vector2(openRight ? 0f : 1f, openDown ? 1f : 0f);
            hRt.anchorMin = hRt.pivot;
            hRt.anchorMax = hRt.pivot;
            hRt.anchoredPosition = Vector2.zero;
            hRt.sizeDelta = new Vector2(armLength, thickness);
            h.color = BracketColor;
            h.raycastTarget = false;

            Image v = CreateChildImage(holder.transform, "V", stretch: false);
            RectTransform vRt = v.rectTransform;
            vRt.pivot = new Vector2(openRight ? 0f : 1f, openDown ? 1f : 0f);
            vRt.anchorMin = vRt.pivot;
            vRt.anchorMax = vRt.pivot;
            vRt.anchoredPosition = Vector2.zero;
            vRt.sizeDelta = new Vector2(thickness, armLength);
            v.color = BracketColor;
            v.raycastTarget = false;
        }
    }
}
