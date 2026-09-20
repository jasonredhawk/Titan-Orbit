using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using TitanOrbit.UI;
using TMPro;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// World-space label above the orbiting gem moon. Matches the planet cockpit stack:
    /// Rajdhani place name, <c>HULL</c> / <c>GUN</c> rails, then labeled GEMS and SHIELD
    /// counts (current over BANK / MAX). World / family / gun strings match
    /// <see cref="PlanetWorldStatsLabel"/> (same planet <see cref="PlanetState"/> + config).
    /// Client presentation only. During idle theatrical camera,
    /// <see cref="TitanOrbit.UI.TheatricalWorldSpaceLabelRotation"/> billboards this stack
    /// toward the lens so gem/shield counts stay readable.
    /// </summary>
    public class GemMoonWorldStatsLabel : MonoBehaviour
    {
        const string GemsColorHex = "#FF3333";
        const string GemsMaxColorHex = "#FF333399";
        const string ShieldColorHex = "#40F2FF";
        const string ShieldMaxColorHex = "#40F2FF99";
        const int TextSortingOrder = 5001;
        const int IconSortingOrder = 5000;
        /// <summary>Fallback moon label scale before planet size is known (unit-scale roots).</summary>
        const float LabelWorldScaleFallback = 0.24f;
        /// <summary>Live GEMS / SHIELD digits — the number to read from orbit.</summary>
        const float CurrentFontSize = 22f;
        /// <summary>BANK / MAX line under current — clearly smaller than the live count.</summary>
        const float MaxFontSize = 10.5f;
        /// <summary>Tiny tracked rail above each moon stat (GEMS / SHIELD).</summary>
        const float StatCaptionFontSize = 7.5f;
        /// <summary>Place name — Rajdhani Bold, same family as the planet label.</summary>
        const float TitleFontSize = 18f;
        /// <summary>HULL line under the name.</summary>
        const float FamilyNameFontSize = 10.5f;
        /// <summary>GUN line under HULL.</summary>
        const float BulletTypeFontSize = 9.5f;
        const float TitleCharacterSpacing = 1.2f;
        const float CaptionCharacterSpacing = 2.6f;
        const float StatBlockGapLocal = 0.65f;
        const float HeaderToStatsGapLocal = 0.7f;
        const float TitleToSubtitleGapLocal = 0.16f;
        const float SubtitleStackGapLocal = 0.1f;
        const float ValueLineGapLocal = 0.18f;
        const float CaptionToValueGapLocal = 0.06f;
        /// <summary>Gap between the tucked icon and the live count (text stays on X = 0).</summary>
        const float IconGapLocal = 0.55f;
        /// <summary>Smaller than the planet people stack so gem/shield icons do not shove the count off-center.</summary>
        const float IconHeightOverFontSize = 0.055f;
        const float OutlineWidth = 0.14f;
        const float FamilyNameAlpha = 0.88f;
        const float BulletTypeAlpha = 0.72f;

        [SerializeField] int planetId;
        float _moonLocalRadius = 0.25f;

        Transform _labelRoot;
        TextMeshPro _titleText;
        TextMeshPro _familyText;
        TextMeshPro _bulletTypeText;
        StatRow _gemRow;
        StatRow _shieldRow;

        int _cachedCurrentGems = int.MinValue;
        int _cachedMaxGems = int.MinValue;
        int _cachedCurrentShield = int.MinValue;
        int _cachedMaxShield = int.MinValue;
        int _cachedFamilyConfigIndex = int.MinValue;
        int _cachedBulletBankIndex = int.MinValue;
        bool _cachedIsHomePlanet;
        TeamId _cachedTeam;
        bool _hasCachedPaint;
        string _cachedTitle;
        string _cachedFamilyName;
        string _cachedBulletType;
        /// <summary>Snug local Y from last ApplyLayout — reused while theatrical so we skip mesh walks.</summary>
        float _cachedGameplayLabelLocalY;
        /// <summary>Moon radius in world units from last layout.</summary>
        float _cachedBodyRadiusWorld = 0.25f;
        /// <summary>True last frame while theatrical owned label pose — forces ApplyLayout on exit.</summary>
        bool _wasTheatricalEngaged;

        static PlanetShipFamilyConfig _shipFamilyConfig;

        static readonly Color GemsColor = ParseHexColor(GemsColorHex);
        static readonly Color GemsMaxColor = ParseHexColor(GemsMaxColorHex);
        static readonly Color ShieldColor = ParseHexColor(ShieldColorHex);
        static readonly Color ShieldMaxColor = ParseHexColor(ShieldMaxColorHex);

        struct StatRow
        {
            public Transform Root;
            public SpriteRenderer Icon;
            public TextMeshPro CaptionText;
            public TextMeshPro CurrentText;
            public TextMeshPro MaxText;
        }

        public void Configure(int id, float moonLocalRadius)
        {
            // --- Configure ---
            planetId = id;
            _moonLocalRadius = Mathf.Max(0.02f, moonLocalRadius);
            EnsureLabel();
            ApplyLayout();
            Refresh();
        }

        void EnsureLabel()
        {
            // --- Already wired — skip Find / rebuild on LateUpdate ---
            if (_labelRoot != null &&
                _titleText != null &&
                _familyText != null &&
                _bulletTypeText != null &&
                _gemRow.CurrentText != null &&
                _shieldRow.CurrentText != null)
                return;

            if (TryRecoverExistingLabel())
                return;

            CleanupLegacyLabels();

            _labelRoot = CreateLabelRoot("GemsLabel", transform);
            _titleText = CreateDisplayText(_labelRoot, "FamilyTitle", TitleFontSize);
            _familyText = CreateTelemetryText(_labelRoot, "ShipFamily", FamilyNameFontSize);
            _bulletTypeText = CreateTelemetryText(_labelRoot, "BulletType", BulletTypeFontSize);
            _gemRow = CreateStatRow(_labelRoot, "GemRow", WorldStatLabelIcons.Gem, ParseHexColor(GemsColorHex));
            _shieldRow = CreateStatRow(_labelRoot, "ShieldRow", WorldStatLabelIcons.Shield, ParseHexColor(ShieldColorHex));
        }

        /// <summary>
        /// Re-wires children if GemsLabel already exists (Play Mode recompile), and adds
        /// FamilyTitle / BulletType when an older moon label is missing them.
        /// </summary>
        bool TryRecoverExistingLabel()
        {
            if (_labelRoot == null)
            {
                Transform existing = transform.Find("GemsLabel");
                if (existing != null)
                    _labelRoot = existing;
            }

            if (_labelRoot == null)
                return false;

            if (_titleText == null)
                _titleText = _labelRoot.Find("FamilyTitle")?.GetComponent<TextMeshPro>();
            if (_familyText == null)
                _familyText = _labelRoot.Find("ShipFamily")?.GetComponent<TextMeshPro>();
            if (_bulletTypeText == null)
                _bulletTypeText = _labelRoot.Find("BulletType")?.GetComponent<TextMeshPro>();

            TryRecoverStatRow(ref _gemRow, "GemRow");
            TryRecoverStatRow(ref _shieldRow, "ShieldRow");

            if (_gemRow.CurrentText == null || _shieldRow.CurrentText == null)
                return false;

            if (_titleText == null)
                _titleText = CreateDisplayText(_labelRoot, "FamilyTitle", TitleFontSize);
            if (_familyText == null)
                _familyText = CreateTelemetryText(_labelRoot, "ShipFamily", FamilyNameFontSize);
            if (_bulletTypeText == null)
                _bulletTypeText = CreateTelemetryText(_labelRoot, "BulletType", BulletTypeFontSize);

            if (_gemRow.CaptionText == null && _gemRow.Root != null)
                _gemRow.CaptionText = CreateCaptionText(_gemRow.Root, "Caption");
            if (_shieldRow.CaptionText == null && _shieldRow.Root != null)
                _shieldRow.CaptionText = CreateCaptionText(_shieldRow.Root, "Caption");

            ApplyReadableTextMaterial(_titleText);
            ApplyReadableTextMaterial(_familyText);
            ApplyReadableTextMaterial(_bulletTypeText);
            ApplyReadableTextMaterial(_gemRow.CurrentText);
            ApplyReadableTextMaterial(_gemRow.MaxText);
            ApplyReadableTextMaterial(_shieldRow.CurrentText);
            ApplyReadableTextMaterial(_shieldRow.MaxText);
            return true;
        }

        void TryRecoverStatRow(ref StatRow row, string rowName)
        {
            if (row.Root == null)
            {
                Transform found = _labelRoot != null ? _labelRoot.Find(rowName) : null;
                if (found == null)
                    return;

                row.Root = found;
                row.Icon = found.Find("Icon")?.GetComponent<SpriteRenderer>();
                row.CaptionText = found.Find("Caption")?.GetComponent<TextMeshPro>();
                row.CurrentText = found.Find("Current")?.GetComponent<TextMeshPro>();
                row.MaxText = found.Find("Max")?.GetComponent<TextMeshPro>();
            }
        }

        void CleanupLegacyLabels()
        {
            var legacyCanvas = transform.Find("GemMoonStatsCanvas");
            if (legacyCanvas != null)
                Destroy(legacyCanvas.gameObject);

            var legacyMax = transform.Find("GemsMax");
            if (legacyMax != null)
                Destroy(legacyMax.gameObject);

            var legacyValue = transform.Find("GemsValue");
            if (legacyValue != null)
                Destroy(legacyValue.gameObject);

            var legacyLabel = transform.Find("GemsLabel");
            if (legacyLabel != null)
                Destroy(legacyLabel.gameObject);

            _labelRoot = null;
            _titleText = null;
            _familyText = null;
            _bulletTypeText = null;
            _gemRow = default;
            _shieldRow = default;
        }

        static Transform CreateLabelRoot(string name, Transform parent)
        {
            // --- Create instance ---
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = new Vector3(
                LabelWorldScaleFallback, -LabelWorldScaleFallback, LabelWorldScaleFallback);
            go.transform.localPosition = Vector3.zero;
            return go.transform;
        }

        StatRow CreateStatRow(Transform parent, string rowName, Sprite iconSprite, Color iconColor)
        {
            // --- Create instance ---
            var rowGo = new GameObject(rowName);
            rowGo.transform.SetParent(parent, false);

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(rowGo.transform, false);
            var iconRenderer = iconGo.AddComponent<SpriteRenderer>();
            iconRenderer.sprite = iconSprite;
            iconRenderer.color = iconColor;
            iconRenderer.sortingOrder = IconSortingOrder;
            iconRenderer.enabled = iconSprite != null;

            var caption = CreateCaptionText(rowGo.transform, "Caption");
            var currentText = CreateDisplayText(rowGo.transform, "Current", CurrentFontSize);
            var maxText = CreateTelemetryText(rowGo.transform, "Max", MaxFontSize);

            if (iconSprite != null)
                ApplyIconScale(iconRenderer, iconSprite, CurrentFontSize);

            return new StatRow
            {
                Root = rowGo.transform,
                Icon = iconRenderer,
                CaptionText = caption,
                CurrentText = currentText,
                MaxText = maxText,
            };
        }

        static TextMeshPro CreateDisplayText(Transform parent, string name, float fontSize)
        {
            return CreateLabelText(parent, name, fontSize, WorldBodyLabelTheme.DisplayFont, richText: false);
        }

        static TextMeshPro CreateTelemetryText(Transform parent, string name, float fontSize)
        {
            return CreateLabelText(parent, name, fontSize, WorldBodyLabelTheme.TelemetryFont, richText: true);
        }

        static TextMeshPro CreateCaptionText(Transform parent, string name)
        {
            TextMeshPro tmp = CreateLabelText(
                parent,
                name,
                StatCaptionFontSize,
                WorldBodyLabelTheme.CaptionFont,
                richText: false);
            tmp.characterSpacing = CaptionCharacterSpacing;
            WorldBodyLabelTheme.ApplyCaptionOverlay(tmp);
            return tmp;
        }

        static TextMeshPro CreateLabelText(
            Transform parent,
            string name,
            float fontSize,
            TMP_FontAsset font,
            bool richText)
        {
            var textGo = new GameObject(name);
            textGo.transform.SetParent(parent, false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.font = font != null ? font : WorldBodyLabelTheme.DisplayFont;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.richText = richText;
            tmp.color = Color.white;
            ApplyReadableTextMaterial(tmp);
            return tmp;
        }

        void ApplyLayout()
        {
            // --- Apply changes ---
            if (_labelRoot == null)
                return;

            // --- World text scale under unit-scale planet root ---
            // Legacy moon labels inherited planet LocalTransform.Scale; restore that size.
            float planetSize = 10f;
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetSize = ecsScale;
            float s = WorldBodyLabelLayout.GetReadableMoonLabelWorldScale(planetSize);
            WorldBodyLabelLayout.ApplySnugMoonLabel(_labelRoot, transform, _moonLocalRadius);
            _cachedGameplayLabelLocalY = _labelRoot.localPosition.y;
            float moonWorld = Mathf.Max(0.05f, _moonLocalRadius * transform.lossyScale.x);
            _cachedBodyRadiusWorld = moonWorld;
            TheatricalWorldSpaceLabelRotation.RestoreGameplayPose(
                _labelRoot,
                _cachedGameplayLabelLocalY,
                s);
        }

        static void ApplyIconScale(SpriteRenderer iconRenderer, Sprite iconSprite, float fontSize)
        {
            // --- Apply changes ---
            if (iconRenderer == null || iconSprite == null)
                return;

            float iconHeight = fontSize * IconHeightOverFontSize;
            float spriteHeight = Mathf.Max(0.001f, iconSprite.bounds.size.y);
            iconRenderer.transform.localScale = Vector3.one * (iconHeight / spriteHeight);
        }

        static void LayoutStatRow(ref StatRow row)
        {
            // --- LayoutStatRow ---
            // Caption / count / BANK stay on X = 0 so they line up with the moon name.
            // The gem / shield icon tucks left of the live digits and does not shift the stack.
            if (row.CurrentText == null || row.MaxText == null)
                return;

            row.CurrentText.fontSize = CurrentFontSize;
            row.MaxText.fontSize = MaxFontSize;
            row.CurrentText.alignment = TextAlignmentOptions.Center;
            row.MaxText.alignment = TextAlignmentOptions.Center;

            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
                ApplyIconScale(row.Icon, row.Icon.sprite, row.CurrentText.fontSize);

            bool showCaption = row.CaptionText != null && row.CaptionText.gameObject.activeSelf;
            if (showCaption)
            {
                row.CaptionText.fontSize = StatCaptionFontSize;
                row.CaptionText.characterSpacing = CaptionCharacterSpacing;
                row.CaptionText.alignment = TextAlignmentOptions.Center;
                row.CaptionText.ForceMeshUpdate();
            }

            row.CurrentText.ForceMeshUpdate();
            row.MaxText.ForceMeshUpdate();

            float captionHeight = showCaption ? row.CaptionText.preferredHeight : 0f;
            float captionGap = showCaption ? CaptionToValueGapLocal : 0f;
            float currentHeight = row.CurrentText.preferredHeight;
            float maxHeight = row.MaxText.preferredHeight;
            float textHeight = captionHeight + captionGap + currentHeight + ValueLineGapLocal + maxHeight;

            float stackTop = textHeight * 0.5f;
            float cursor = stackTop;
            if (showCaption)
            {
                row.CaptionText.transform.localPosition = new Vector3(0f, cursor - captionHeight * 0.5f, 0f);
                cursor -= captionHeight + captionGap;
            }

            float currentY = cursor - currentHeight * 0.5f;
            row.CurrentText.transform.localPosition = new Vector3(0f, currentY, 0f);
            cursor -= currentHeight + ValueLineGapLocal;
            row.MaxText.transform.localPosition = new Vector3(0f, cursor - maxHeight * 0.5f, 0f);

            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
            {
                float iconWidth = row.Icon.transform.localScale.x * row.Icon.sprite.bounds.size.x;
                float currentHalf = row.CurrentText.preferredWidth * 0.5f;
                row.Icon.transform.localPosition = new Vector3(
                    -(currentHalf + IconGapLocal + iconWidth * 0.5f),
                    currentY,
                    0f);
            }
        }

        static float GetStatRowHeight(StatRow row)
        {
            // --- Compute value ---
            if (row.CurrentText == null || row.MaxText == null)
                return 0f;

            bool showCaption = row.CaptionText != null && row.CaptionText.gameObject.activeSelf;
            float caption = showCaption ? row.CaptionText.preferredHeight + CaptionToValueGapLocal : 0f;
            return caption + row.CurrentText.preferredHeight + ValueLineGapLocal + row.MaxText.preferredHeight;
        }

        /// <summary>
        /// Centers world name, ship family, gun type, then gem and shield rows.
        /// Same identity order as <see cref="PlanetWorldStatsLabel"/> so moon and planet match.
        /// </summary>
        void LayoutLabelBlock(bool showTitle, bool showFamily, bool showBulletType)
        {
            // --- LayoutLabelBlock ---
            LayoutStatRow(ref _gemRow);
            LayoutStatRow(ref _shieldRow);

            float titleHeight = MeasureLine(_titleText, TitleFontSize, showTitle);
            float familyHeight = MeasureLine(_familyText, FamilyNameFontSize, showFamily);
            float bulletTypeHeight = MeasureLine(_bulletTypeText, BulletTypeFontSize, showBulletType);

            bool hasSubtitle = showFamily || showBulletType;
            bool hasHeader = showTitle || hasSubtitle;
            float afterTitleGap = showTitle && hasSubtitle ? TitleToSubtitleGapLocal : 0f;
            float afterFamilyGap = showFamily && showBulletType ? SubtitleStackGapLocal : 0f;
            float headerGap = hasHeader ? HeaderToStatsGapLocal : 0f;
            float gemHeight = GetStatRowHeight(_gemRow);
            float shieldHeight = GetStatRowHeight(_shieldRow);
            float headerHeight = (showTitle ? titleHeight : 0f)
                + (showFamily ? familyHeight : 0f)
                + (showBulletType ? bulletTypeHeight : 0f)
                + afterTitleGap
                + afterFamilyGap
                + headerGap;
            float statsHeight = gemHeight + StatBlockGapLocal + shieldHeight;
            float totalHeight = headerHeight + statsHeight;

            float cursor = totalHeight * 0.5f;
            if (showTitle && _titleText != null)
            {
                _titleText.fontStyle = FontStyles.Bold;
                _titleText.characterSpacing = TitleCharacterSpacing;
                _titleText.transform.localPosition = new Vector3(0f, cursor - titleHeight * 0.5f, 0f);
                cursor -= titleHeight + afterTitleGap;
            }

            if (showFamily && _familyText != null)
            {
                _familyText.fontStyle = FontStyles.Bold;
                _familyText.transform.localPosition = new Vector3(0f, cursor - familyHeight * 0.5f, 0f);
                cursor -= familyHeight + afterFamilyGap;
            }

            if (showBulletType && _bulletTypeText != null)
            {
                _bulletTypeText.fontStyle = FontStyles.Bold;
                _bulletTypeText.transform.localPosition = new Vector3(0f, cursor - bulletTypeHeight * 0.5f, 0f);
                cursor -= bulletTypeHeight + headerGap;
            }
            else if (hasHeader)
            {
                cursor -= headerGap;
            }

            _gemRow.Root.localPosition = new Vector3(0f, cursor - gemHeight * 0.5f, 0f);
            cursor -= gemHeight + StatBlockGapLocal;
            _shieldRow.Root.localPosition = new Vector3(0f, cursor - shieldHeight * 0.5f, 0f);
        }

        /// <summary>Measures one TMP line after forcing <paramref name="fontSize"/>.</summary>
        static float MeasureLine(TextMeshPro text, float fontSize, bool show)
        {
            if (!show || text == null)
                return 0f;

            text.fontSize = fontSize;
            text.ForceMeshUpdate();
            return text.preferredHeight;
        }

        static void ApplyReadableTextMaterial(TMP_Text text)
        {
            WorldBodyLabelTheme.ApplyOverlay(text, OutlineWidth, 0.08f);
        }

        static Color ParseHexColor(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out Color color) ? color : Color.white;
        }

        static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        /// <summary>
        /// Writes one moon telemetry column: tracked caption, live count, labeled capacity.
        /// </summary>
        static void PaintStatRow(
            ref StatRow row,
            string caption,
            int current,
            string maxLine,
            Color currentColor,
            Color maxColor)
        {
            if (row.CaptionText != null)
            {
                row.CaptionText.gameObject.SetActive(true);
                row.CaptionText.text = caption;
                row.CaptionText.color = WorldBodyLabelTheme.CaptionIce;
            }

            row.CurrentText.text = current.ToString();
            row.CurrentText.color = currentColor;
            row.MaxText.richText = true;
            row.MaxText.text = maxLine;
            row.MaxText.color = maxColor;
        }

        static PlanetShipFamilyConfig ShipFamilyConfig
        {
            get
            {
                if (_shipFamilyConfig != null)
                    return _shipFamilyConfig;

                _shipFamilyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
                return _shipFamilyConfig;
            }
        }

        /// <summary>
        /// Resolves the parent planet's proper world name (not the ship family).
        /// Same string the planet world label and minimap hover tip show.
        /// </summary>
        static string ResolvePlanetTitle(in PlanetState state)
        {
            // --- Same catalog as the planet label ---
            var config = ShipFamilyConfig;
            if (config == null)
                return string.Empty;

            return config.GetPlanetDisplayName(
                state.PlanetId,
                state.IsHomePlanet,
                state.ShipFamilyConfigIndex);
        }

        /// <summary>
        /// Ship-tree family for the parent planet (Astro Eagle, Cosmic Shark, …).
        /// Same catalog string the planet world label and minimap hover tip use.
        /// </summary>
        static string ResolveShipFamilyName(in PlanetState state)
        {
            var config = ShipFamilyConfig;
            if (config == null)
                return string.Empty;

            return config.GetFamilyDisplayName(
                state.PlanetId,
                state.IsHomePlanet,
                state.ShipFamilyConfigIndex);
        }

        static string ResolveShipFamilyBulletType(in PlanetState state)
        {
            var config = ShipFamilyConfig;
            if (config == null)
                return string.Empty;

            return config.GetPlanetBulletTypeName(
                state.PlanetId,
                state.IsHomePlanet,
                state.ShipFamilyConfigIndex,
                state.BulletBankIndex);
        }

        void LateUpdate()
        {
            // Dirty Refresh skips ApplyLayout when gem/shield/world-name text is unchanged.
            if (Refresh())
                ApplyLayout();

            ApplyTheatricalLabelPose();
        }

        /// <summary>
        /// While idle theatrical orbit is on, billboard the moon label at the camera.
        /// When theatrical ends, snap back to the snug top-down layout.
        /// </summary>
        void ApplyTheatricalLabelPose()
        {
            if (_labelRoot == null)
                return;

            bool theatrical = TheatricalWorldSpaceLabelRotation.IsTheatricalEngaged();
            if (!theatrical)
            {
                if (_wasTheatricalEngaged)
                {
                    ApplyLayout();
                    _wasTheatricalEngaged = false;
                }

                return;
            }

            _wasTheatricalEngaged = true;
            if (_cachedGameplayLabelLocalY <= 0.0001f)
                ApplyLayout();

            float planetSize = 10f;
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetSize = ecsScale;
            float s = WorldBodyLabelLayout.GetReadableMoonLabelWorldScale(planetSize);
            TheatricalWorldSpaceLabelRotation.ApplyTheatricalBillboard(
                _labelRoot,
                transform,
                _cachedBodyRadiusWorld,
                s,
                WorldBodyLabelLayout.MoonPaddingAboveSurfaceLocal);
        }

        /// <summary>
        /// Updates family / gun / gem / shield TMP only when values change.
        /// [TITAN-ORBIT] Per-frame ParseHexColor + .text on every moon was ~1.4ms (Profiler 41220).
        /// World name is resolved only when id / config / home / team is dirty (same as planet).
        /// </summary>
        /// <returns>True when layout should run.</returns>
        bool Refresh()
        {
            // --- Refresh ---
            if (planetId == 0)
                return false;

            EnsureLabel();
            if (_titleText == null ||
                _familyText == null ||
                _bulletTypeText == null ||
                _gemRow.CurrentText == null ||
                _shieldRow.CurrentText == null)
                return false;

            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return false;

            int currentGems = Mathf.RoundToInt(state.CurrentGems);
            int maxGems = Mathf.RoundToInt(PlanetEconomyMath.GetMaxGemsForLevel(state.PlanetLevel));

            int currentShield;
            int maxShield;
            if (EcsGameBridge.TryGetPlanetGemMoonStateByPlanetId(planetId, out PlanetGemMoonState moonState))
            {
                currentShield = Mathf.RoundToInt(moonState.CurrentShield);
                maxShield = Mathf.RoundToInt(moonState.MaxShield);
            }
            else
            {
                maxShield = Mathf.RoundToInt(PlanetGemMoonMath.GetMaxShieldForLevel(state.PlanetLevel));
                currentShield = maxShield;
            }

            // --- Dirty check BEFORE ResolvePlanetTitle ---
            // [TITAN-ORBIT] World name is stable for the match (PlanetId + optional family override).
            if (_hasCachedPaint &&
                _cachedCurrentGems == currentGems &&
                _cachedMaxGems == maxGems &&
                _cachedCurrentShield == currentShield &&
                _cachedMaxShield == maxShield &&
                _cachedFamilyConfigIndex == state.ShipFamilyConfigIndex &&
                _cachedBulletBankIndex == state.BulletBankIndex &&
                _cachedIsHomePlanet == state.IsHomePlanet &&
                _cachedTeam == state.Ownership)
            {
                return false;
            }

            bool titleDirty = !_hasCachedPaint ||
                               _cachedFamilyConfigIndex != state.ShipFamilyConfigIndex ||
                               _cachedBulletBankIndex != state.BulletBankIndex ||
                               _cachedIsHomePlanet != state.IsHomePlanet;
            string planetTitle;
            string familyName;
            string bulletType;
            if (titleDirty)
            {
                planetTitle = ResolvePlanetTitle(state);
                familyName = ResolveShipFamilyName(state);
                bulletType = ResolveShipFamilyBulletType(state);
            }
            else
            {
                planetTitle = _cachedTitle;
                familyName = _cachedFamilyName;
                bulletType = _cachedBulletType;
            }

            bool hasTitle = !string.IsNullOrEmpty(planetTitle);
            bool hasFamily = hasTitle && !string.IsNullOrEmpty(familyName);
            bool hasBulletType = hasTitle && !string.IsNullOrEmpty(bulletType);

            _hasCachedPaint = true;
            _cachedCurrentGems = currentGems;
            _cachedMaxGems = maxGems;
            _cachedCurrentShield = currentShield;
            _cachedMaxShield = maxShield;
            _cachedFamilyConfigIndex = state.ShipFamilyConfigIndex;
            _cachedBulletBankIndex = state.BulletBankIndex;
            _cachedIsHomePlanet = state.IsHomePlanet;
            _cachedTeam = state.Ownership;
            _cachedTitle = planetTitle;
            _cachedFamilyName = familyName;
            _cachedBulletType = bulletType;

            Color teamColor = state.Ownership.ToColor();

            _titleText.gameObject.SetActive(hasTitle);
            _titleText.text = hasTitle ? planetTitle : string.Empty;
            _titleText.color = teamColor;

            _familyText.gameObject.SetActive(hasFamily);
            _familyText.richText = true;
            _familyText.text = hasFamily ? WorldBodyLabelTheme.FormatHullLine(familyName) : string.Empty;
            _familyText.color = WithAlpha(teamColor, FamilyNameAlpha);

            _bulletTypeText.gameObject.SetActive(hasBulletType);
            _bulletTypeText.richText = true;
            _bulletTypeText.text = hasBulletType ? WorldBodyLabelTheme.FormatGunLine(bulletType) : string.Empty;
            _bulletTypeText.color = WithAlpha(teamColor, BulletTypeAlpha);

            PaintStatRow(
                ref _gemRow,
                WorldBodyLabelTheme.GemsCaption,
                currentGems,
                WorldBodyLabelTheme.FormatBankLine(WorldBodyLabelTheme.GemBankCaption, maxGems),
                GemsColor,
                GemsMaxColor);
            PaintStatRow(
                ref _shieldRow,
                WorldBodyLabelTheme.ShieldCaption,
                currentShield,
                WorldBodyLabelTheme.FormatBankLine(WorldBodyLabelTheme.ShieldMaxCaption, maxShield),
                ShieldColor,
                ShieldMaxColor);

            LayoutLabelBlock(hasTitle, hasFamily, hasBulletType);
            return true;
        }
    }
}
