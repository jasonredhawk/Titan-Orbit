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
    /// World-space label floating above a planet body. Reads as cockpit telemetry, not a
    /// spreadsheet: Rajdhani place name, optional HOME PLANET stamp, <c>FLEET</c> / <c>GUN</c>
    /// rails, then a labeled people stack — <c>CREW</c> (live count), <c>CAP</c> (hold limit),
    /// and <c>+N LINK</c> when connection triangles add extra beds.
    /// Optional capture-contributor name sits between identity and the people stack.
    /// Client / hybrid presentation only — reads replicated <see cref="PlanetState"/> and the
    /// published connection graph; never drives sim. Paired with <see cref="WorldBodyVisualApplier"/>
    /// (adds this component) and <see cref="PlanetPopulationMath"/> for cap formulas.
    /// During idle theatrical camera, <see cref="TitanOrbit.UI.TheatricalWorldSpaceLabelRotation"/>
    /// billboards this stack toward the lens so it stays readable off the top-down plane.
    /// </summary>
    public class PlanetWorldStatsLabel : MonoBehaviour
    {
        /// <summary>Last painted population — skip TMP writes when unchanged.</summary>
        int _cachedPopulation = int.MinValue;
        int _cachedBaseMax = int.MinValue;
        int _cachedBonusAmount = int.MinValue;
        TeamId _cachedTeam;
        int _cachedFamilyConfigIndex = int.MinValue;
        int _cachedBulletBankIndex = int.MinValue;
        bool _cachedIsHomePlanet;
        int _cachedContributorNetworkId = int.MinValue;
        bool _hasCachedPaint;
        string _cachedTitle;
        string _cachedFamilyName;
        string _cachedBulletType;
        string _cachedContributorName;
        bool _legacyIconRemoved;
        /// <summary>
        /// True after label TMP children are wired and materials applied once.
        /// [TITAN-ORBIT] EnsureLabel used to re-enter TryRecoverExistingLabel every LateUpdate and
        /// call fontMaterial (TMP instance alloc) ×3 per planet → ~16KB GC (Profiler frame 2224).
        /// </summary>
        bool _labelReady;
        float _cachedLayoutPlanetSize = float.NaN;
        /// <summary>Snug local Y from last ApplyLayout — reused while theatrical so we skip mesh walks.</summary>
        float _cachedGameplayLabelLocalY;
        /// <summary>Planet radius in world units from last layout (half of ECS / presentation size).</summary>
        float _cachedBodyRadiusWorld;
        /// <summary>True last frame while theatrical owned label pose — forces ApplyLayout on exit.</summary>
        bool _wasTheatricalEngaged;
        /// <summary>Live CREW digits — the one number players should read from orbit.</summary>
        const float CurrentFontSize = 40f;

        /// <summary>CAP line under CREW — clearly smaller so it cannot be mistaken for crew.</summary>
        const float MaxFontSize = 16f;

        /// <summary>LINK bonus under CAP — same size as CAP, cyan instead of team color.</summary>
        const float BonusFontSize = 14f;

        /// <summary>Tiny tracked rail above the crew digits.</summary>
        const float StatCaptionFontSize = 9.5f;

        /// <summary>Place name — largest identity line, Rajdhani Bold.</summary>
        const float TitleFontSize = 28f;

        /// <summary>HOME PLANET stamp — smaller than the name, heavier tracking.</summary>
        const float HomeRoleFontSize = 13.5f;

        /// <summary>FLEET line under the name / stamp.</summary>
        const float FamilyNameFontSize = 13f;

        /// <summary>GUN line under FLEET — one step quieter.</summary>
        const float BulletTypeFontSize = 12f;

        /// <summary>Player name on the capture credit — smaller than the world-name title.</summary>
        const float ContributorNameFontSize = TitleFontSize * 0.55f;

        /// <summary>"Captured by" caption — smaller than the player name underneath.</summary>
        const float CapturedByFontSize = ContributorNameFontSize * 0.7f;

        /// <summary>Tight gap between the world name and the first line under it.</summary>
        const float TitleToSubtitleGapLocal = 0.28f;

        /// <summary>Gap between stacked identity lines (home stamp, family, gun type).</summary>
        const float SubtitleStackGapLocal = 0.2f;

        /// <summary>Local-space gap around the capture-contributor line.</summary>
        const float ContributorGapLocal = 0.35f;

        /// <summary>Player-name alpha vs full team color.</summary>
        const float ContributorAlpha = 0.85f;

        /// <summary>"Captured by" caption is a bit dimmer than the name underneath.</summary>
        const float CapturedByAlpha = 0.65f;

        /// <summary>Tight gap from the CREW rail to the big digits.</summary>
        const float CaptionToValueGapLocal = 0.12f;

        /// <summary>Local-space gap between current and capacity lines.</summary>
        const float ValueLineGapLocal = 0.42f;

        /// <summary>Gap from CAP to the LINK bonus line.</summary>
        const float BonusGapLocal = 0.18f;

        /// <summary>Height of the thin team-color rule under the identity block.</summary>
        const float RuleHeightLocal = 0.12f;

        /// <summary>Gap around the identity rule.</summary>
        const float RuleGapLocal = 0.45f;

        /// <summary>Title letter-spacing so the place name reads as a hull stencil.</summary>
        const float TitleCharacterSpacing = 1.8f;

        /// <summary>Caption tracking (CREW / CAP rails).</summary>
        const float CaptionCharacterSpacing = 4.2f;

        /// <summary>Slight tracking on HOME PLANET so the stamp reads as a banner, not a caption.</summary>
        const float HomeRoleCharacterSpacing = 3.4f;

        /// <summary>FLEET value alpha vs full team color.</summary>
        const float FamilyNameAlpha = 0.92f;

        /// <summary>GUN value alpha — quieter than FLEET.</summary>
        const float BulletTypeAlpha = 0.78f;

        /// <summary>Capacity-line alpha vs full team color (current stays opaque).</summary>
        const float MaxLineAlpha = 0.6f;

        /// <summary>[TITAN-ORBIT] Stable planet id from <see cref="PlanetState.PlanetId"/> — set by Configure.</summary>
        [SerializeField] int planetId;

        Transform _labelRoot;
        TextMeshPro _titleText;
        TextMeshPro _homeRoleText;
        TextMeshPro _familyText;
        TextMeshPro _bulletTypeText;
        SpriteRenderer _identityRule;
        CaptureCreditRow _captureCredit;
        StatRow _populationRow;

        static PlanetShipFamilyConfig _shipFamilyConfig;

        /// <summary>
        /// People telemetry: CREW rail, live count, CAP hold-limit, optional LINK bonus.
        /// </summary>
        struct StatRow
        {
            public Transform Root;
            public TextMeshPro CaptionText;
            public TextMeshPro CurrentText;
            public TextMeshPro MaxText;
            public TextMeshPro BonusText;
        }

        /// <summary>Capture credit: small "Captured by" caption over the player name.</summary>
        struct CaptureCreditRow
        {
            public Transform Root;
            public TextMeshPro CaptionText;
            public TextMeshPro NameText;
        }

        /// <summary>
        /// Binds this label to a planet id, builds missing TMP children, and refreshes once.
        /// Called from <see cref="WorldBodyVisualApplier"/> when the hybrid planet proxy spawns.
        /// </summary>
        /// <param name="id">Stable <see cref="PlanetState.PlanetId"/> for ECS lookups.</param>
        public void Configure(int id)
        {
            // --- Bind id and build / refresh label ---
            planetId = id;
            EnsureLabel();
            Refresh();
            ApplyLayout();
        }

        /// <summary>Creates the label hierarchy once, or recovers children after domain reload / reparent.</summary>
        void EnsureLabel()
        {
            // --- Already ready — skip Find / fontMaterial (hot LateUpdate path) ---
            if (_labelReady &&
                _labelRoot != null &&
                _titleText != null &&
                _homeRoleText != null &&
                _familyText != null &&
                _bulletTypeText != null &&
                _captureCredit.CaptionText != null &&
                _captureCredit.NameText != null &&
                _populationRow.CaptionText != null &&
                _populationRow.CurrentText != null &&
                _populationRow.MaxText != null &&
                _populationRow.BonusText != null)
                return;

            // --- Ensure setup ---
            if (TryRecoverExistingLabel())
            {
                _labelReady = true;
                return;
            }

            CleanupLegacyLabels();

            _labelRoot = CreateLabelRoot("PlanetStatsLabel", transform);
            _titleText = CreateDisplayText(_labelRoot, "FamilyTitle", TitleFontSize);
            _homeRoleText = CreateHomeRoleText(_labelRoot);
            _familyText = CreateTelemetryText(_labelRoot, "ShipFamily", FamilyNameFontSize);
            _bulletTypeText = CreateTelemetryText(_labelRoot, "BulletType", BulletTypeFontSize);
            _identityRule = CreateIdentityRule(_labelRoot);
            _captureCredit = CreateCaptureCreditRow(_labelRoot, "CaptureCredit");
            _populationRow = CreatePopulationRow(_labelRoot, "PopulationRow");

            KeepLabelOnPlanetRoot();
            _labelReady = true;
        }

        /// <summary>
        /// Re-wires references if PlanetStatsLabel already exists under this planet (Play Mode recompile).
        /// </summary>
        /// <returns>True when title + population TMP children were found and materials applied.</returns>
        bool TryRecoverExistingLabel()
        {
            // --- Attempt resolution ---
            if (_labelRoot == null)
            {
                Transform existing = transform.Find("PlanetStatsLabel");
                if (existing != null)
                    _labelRoot = existing;
            }

            if (_labelRoot == null)
                return false;

            if (_titleText == null)
                _titleText = _labelRoot.Find("FamilyTitle")?.GetComponent<TextMeshPro>();

            if (_homeRoleText == null)
                _homeRoleText = _labelRoot.Find("HomeRole")?.GetComponent<TextMeshPro>();

            if (_familyText == null)
                _familyText = _labelRoot.Find("ShipFamily")?.GetComponent<TextMeshPro>();

            if (_bulletTypeText == null)
                _bulletTypeText = _labelRoot.Find("BulletType")?.GetComponent<TextMeshPro>();

            if (_identityRule == null)
                _identityRule = _labelRoot.Find("IdentityRule")?.GetComponent<SpriteRenderer>();

            if (_captureCredit.Root == null)
            {
                Transform credit = _labelRoot.Find("CaptureCredit");
                if (credit != null)
                {
                    _captureCredit.Root = credit;
                    _captureCredit.CaptionText = credit.Find("CapturedBy")?.GetComponent<TextMeshPro>();
                    _captureCredit.NameText = credit.Find("ContributorName")?.GetComponent<TextMeshPro>();
                }
            }

            if (_populationRow.Root == null)
            {
                Transform row = _labelRoot.Find("PopulationRow");
                if (row != null)
                {
                    _populationRow.Root = row;
                    _populationRow.CaptionText = row.Find("Caption")?.GetComponent<TextMeshPro>();
                    _populationRow.CurrentText = row.Find("Current")?.GetComponent<TextMeshPro>();
                    _populationRow.MaxText = row.Find("Max")?.GetComponent<TextMeshPro>();
                    _populationRow.BonusText = row.Find("Bonus")?.GetComponent<TextMeshPro>();
                    RemoveLegacyPopulationIcon(row);
                }
            }

            if (_titleText == null ||
                _populationRow.CurrentText == null ||
                _populationRow.MaxText == null)
                return false;

            RemoveLegacySingleLineContributor(_labelRoot);

            // Play Mode recompile: older labels have no HomeRole / family / gun child — add once.
            if (_homeRoleText == null)
                _homeRoleText = CreateHomeRoleText(_labelRoot);
            else
                StyleHomeRoleText(_homeRoleText);

            if (_familyText == null)
                _familyText = CreateTelemetryText(_labelRoot, "ShipFamily", FamilyNameFontSize);
            else
                _familyText.richText = true;

            if (_bulletTypeText == null)
                _bulletTypeText = CreateTelemetryText(_labelRoot, "BulletType", BulletTypeFontSize);
            else
                _bulletTypeText.richText = true;

            if (_identityRule == null)
                _identityRule = CreateIdentityRule(_labelRoot);

            if (_populationRow.Root != null && _populationRow.CaptionText == null)
                _populationRow.CaptionText = CreateCaptionText(_populationRow.Root, "Caption");
            if (_populationRow.Root != null && _populationRow.BonusText == null)
                _populationRow.BonusText = CreateTelemetryText(_populationRow.Root, "Bonus", BonusFontSize);

            if (_captureCredit.CaptionText == null || _captureCredit.NameText == null)
                _captureCredit = CreateCaptureCreditRow(_labelRoot, "CaptureCredit");

            // Capacity line uses rich text for "base + bonus" coloring.
            _populationRow.MaxText.richText = true;

            ApplyThemeFonts();
            ApplyReadableTextMaterial(_titleText);
            WorldBodyLabelTheme.ApplyHomeStampOverlay(_homeRoleText);
            ApplyReadableTextMaterial(_familyText);
            ApplyReadableTextMaterial(_bulletTypeText);
            ApplyReadableTextMaterial(_captureCredit.CaptionText);
            ApplyReadableTextMaterial(_captureCredit.NameText);
            WorldBodyLabelTheme.ApplyCaptionOverlay(_populationRow.CaptionText);
            ApplyReadableTextMaterial(_populationRow.CurrentText);
            ApplyReadableTextMaterial(_populationRow.MaxText);
            ApplyReadableTextMaterial(_populationRow.BonusText);
            KeepLabelOnPlanetRoot();
            return true;
        }

        /// <summary>Destroys old single-line PopulationText / previous PlanetStatsLabel before rebuilding.</summary>
        void CleanupLegacyLabels()
        {
            // --- CleanupLegacyLabels ---
            var legacy = transform.Find("PopulationText");
            if (legacy != null)
                Destroy(legacy.gameObject);

            if (_labelRoot != null)
            {
                Destroy(_labelRoot.gameObject);
                _labelRoot = null;
                _titleText = null;
                _homeRoleText = null;
                _familyText = null;
                _bulletTypeText = null;
                _identityRule = null;
                _captureCredit = default;
                _populationRow = default;
            }
        }

        /// <summary>
        /// Registers the label with <see cref="PlanetSpinVisualProxy"/> so spin does not rotate the text.
        /// </summary>
        void KeepLabelOnPlanetRoot()
        {
            // --- KeepLabelOnPlanetRoot ---
            if (_labelRoot == null)
                return;

            // Spin lives on PlanetVisualBody — search children, not only this root.
            var spin = GetComponentInChildren<PlanetSpinVisualProxy>(true);
            if (spin != null)
                spin.KeepOnPlanetRoot(_labelRoot);
            else if (_labelRoot.parent != transform)
                _labelRoot.SetParent(transform, true);
        }

        /// <summary>Creates the flat billboard root (rotated for top-down camera, flipped Y for TMP).</summary>
        static Transform CreateLabelRoot(string name, Transform parent)
        {
            // --- Create instance ---
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = new Vector3(
                WorldBodyLabelLayout.TextWorldScale,
                -WorldBodyLabelLayout.TextWorldScale,
                WorldBodyLabelLayout.TextWorldScale);
            go.transform.localPosition = Vector3.zero;
            return go.transform;
        }

        /// <summary>Drops the old single-line CaptureContributor TMP from an earlier credit layout.</summary>
        static void RemoveLegacySingleLineContributor(Transform labelRoot)
        {
            if (labelRoot == null)
                return;

            Transform legacy = labelRoot.Find("CaptureContributor");
            if (legacy != null)
                Object.Destroy(legacy.gameObject);
        }

        /// <summary>Removes the old people icon if a prior build left one under PopulationRow.</summary>
        static void RemoveLegacyPopulationIcon(Transform populationRow)
        {
            // --- RemoveLegacyPopulationIcon ---
            if (populationRow == null)
                return;

            Transform icon = populationRow.Find("Icon");
            if (icon != null)
                Object.Destroy(icon.gameObject);
        }

        /// <summary>Builds the two-line capture credit: "Captured by" over the player name.</summary>
        static CaptureCreditRow CreateCaptureCreditRow(Transform parent, string rowName)
        {
            Transform existing = parent.Find(rowName);
            if (existing != null)
                Object.Destroy(existing.gameObject);

            var rowGo = new GameObject(rowName);
            rowGo.transform.SetParent(parent, false);

            var caption = CreateCaptionText(rowGo.transform, "CapturedBy");
            caption.fontSize = CapturedByFontSize;
            caption.characterSpacing = CaptionCharacterSpacing * 0.45f;
            var name = CreateDisplayText(rowGo.transform, "ContributorName", ContributorNameFontSize);
            caption.text = "Captured by";

            return new CaptureCreditRow
            {
                Root = rowGo.transform,
                CaptionText = caption,
                NameText = name,
            };
        }

        /// <summary>Stacks "Captured by" above the player name inside the credit row.</summary>
        static void LayoutCaptureCredit(ref CaptureCreditRow row)
        {
            if (row.CaptionText == null || row.NameText == null)
                return;

            row.CaptionText.fontSize = CapturedByFontSize;
            row.NameText.fontSize = ContributorNameFontSize;
            row.CaptionText.fontStyle = FontStyles.Bold;
            row.NameText.fontStyle = FontStyles.Bold;
            row.CaptionText.ForceMeshUpdate();
            row.NameText.ForceMeshUpdate();

            float captionHeight = row.CaptionText.preferredHeight;
            float nameHeight = row.NameText.preferredHeight;
            float textHeight = captionHeight + ContributorGapLocal + nameHeight;
            float stackTop = textHeight * 0.5f;

            row.CaptionText.transform.localPosition = new Vector3(
                0f,
                stackTop - captionHeight * 0.5f,
                0f);
            row.NameText.transform.localPosition = new Vector3(
                0f,
                -stackTop + nameHeight * 0.5f,
                0f);
        }

        /// <summary>Preferred height of the capture-credit stack.</summary>
        static float GetCaptureCreditHeight(CaptureCreditRow row)
        {
            if (row.CaptionText == null || row.NameText == null)
                return 0f;

            return row.CaptionText.preferredHeight + ContributorGapLocal + row.NameText.preferredHeight;
        }

        /// <summary>Builds CREW rail + live digits + CAP + optional LINK under one row root.</summary>
        StatRow CreatePopulationRow(Transform parent, string rowName)
        {
            // --- Create instance ---
            var rowGo = new GameObject(rowName);
            rowGo.transform.SetParent(parent, false);

            var caption = CreateCaptionText(rowGo.transform, "Caption");
            caption.text = WorldBodyLabelTheme.CrewCaption;
            var currentText = CreateDisplayText(rowGo.transform, "Current", CurrentFontSize);
            var maxText = CreateTelemetryText(rowGo.transform, "Max", MaxFontSize);
            var bonusText = CreateTelemetryText(rowGo.transform, "Bonus", BonusFontSize);

            return new StatRow
            {
                Root = rowGo.transform,
                CaptionText = caption,
                CurrentText = currentText,
                MaxText = maxText,
                BonusText = bonusText,
            };
        }

        /// <summary>Place name / live CREW digits — Rajdhani Bold.</summary>
        static TextMeshPro CreateDisplayText(Transform parent, string name, float fontSize)
        {
            return CreateLabelText(parent, name, fontSize, WorldBodyLabelTheme.DisplayFont, richText: false);
        }

        /// <summary>FLEET / GUN / CAP lines — Rajdhani SemiBold with rich-text caption prefixes.</summary>
        static TextMeshPro CreateTelemetryText(Transform parent, string name, float fontSize)
        {
            return CreateLabelText(parent, name, fontSize, WorldBodyLabelTheme.TelemetryFont, richText: true);
        }

        /// <summary>Tiny tracked rail (CREW).</summary>
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

        /// <summary>Thin team-color bar that splits identity from the people stack.</summary>
        static SpriteRenderer CreateIdentityRule(Transform parent)
        {
            Transform existing = parent.Find("IdentityRule");
            if (existing != null)
            {
                var found = existing.GetComponent<SpriteRenderer>();
                if (found != null)
                    return found;
            }

            var go = new GameObject("IdentityRule");
            go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = WorldBodyLabelTheme.PixelSprite;
            renderer.sortingOrder = 5000;
            renderer.drawMode = SpriteDrawMode.Simple;
            go.transform.localScale = new Vector3(8f, RuleHeightLocal, 1f);
            return renderer;
        }

        /// <summary>Creates a centered TextMeshPro child with a theme font and overlay material.</summary>
        static TextMeshPro CreateLabelText(
            Transform parent,
            string name,
            float fontSize,
            TMP_FontAsset font,
            bool richText)
        {
            // --- Create instance ---
            var textGo = new GameObject(name);
            textGo.transform.SetParent(parent, false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.font = font != null ? font : WorldBodyLabelTheme.DisplayFont;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
            tmp.enableWordWrapping = false;
            tmp.richText = richText;
            tmp.color = Color.white;
            ApplyReadableTextMaterial(tmp);
            return tmp;
        }

        /// <summary>
        /// Builds the home-capital stamp. Same TMP family as the other lines, but heavier
        /// dilate / outline and a little tracking so the smaller size still feels important.
        /// </summary>
        /// <param name="parent">PlanetStatsLabel root.</param>
        static TextMeshPro CreateHomeRoleText(Transform parent)
        {
            // --- Heavy subtitle, not a second title ---
            // [TITAN-ORBIT] Players scan the place name first (Helios). This line is the
            // rank badge: smaller, thicker, full team color. Neutrals hide the GameObject.
            TextMeshPro tmp = CreateDisplayText(parent, "HomeRole", HomeRoleFontSize);
            StyleHomeRoleText(tmp);
            tmp.gameObject.SetActive(false);
            return tmp;
        }

        /// <summary>
        /// Applies the heavy stamp look. Safe to call again after a Play Mode recompile
        /// so an older HomeRole child picks up the thicker SDF settings.
        /// </summary>
        /// <param name="tmp">HomeRole TMP. Null is ignored.</param>
        static void StyleHomeRoleText(TextMeshPro tmp)
        {
            if (tmp == null)
                return;

            tmp.font = WorldBodyLabelTheme.DisplayFont;
            tmp.fontSize = HomeRoleFontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.fontWeight = FontWeight.Black;
            tmp.characterSpacing = HomeRoleCharacterSpacing;
            WorldBodyLabelTheme.ApplyHomeStampOverlay(tmp);
        }

        /// <summary>
        /// Snaps the label to the planet surface and sets a readable world TMP scale.
        /// Unit-scale planet roots no longer enlarge children — scale from ECS diameter.
        /// </summary>
        void ApplyLayout()
        {
            // --- Apply changes ---
            if (_labelRoot == null)
                return;

            KeepLabelOnPlanetRoot();

            // --- World text scale (matches pre–PlanetVisualBody inherited look) ---
            float planetSize = PlanetVisualBody.ResolvePresentationSize(transform);
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetSize = ecsScale;
            float s = WorldBodyLabelLayout.GetReadablePlanetLabelWorldScale(planetSize);
            WorldBodyLabelLayout.ApplySnugPlanetLabel(_labelRoot, transform);
            _cachedGameplayLabelLocalY = _labelRoot.localPosition.y;
            _cachedBodyRadiusWorld = Mathf.Max(0.5f, planetSize * 0.5f);
            TheatricalWorldSpaceLabelRotation.RestoreGameplayPose(
                _labelRoot,
                _cachedGameplayLabelLocalY,
                s);
        }

        /// <summary>Stacks CREW rail, live digits, CAP, and optional LINK inside the people row.</summary>
        static void LayoutStatRow(ref StatRow row)
        {
            // --- LayoutStatRow ---
            if (row.CurrentText == null || row.MaxText == null)
                return;

            bool showCaption = row.CaptionText != null && row.CaptionText.gameObject.activeSelf;
            bool showBonus = row.BonusText != null && row.BonusText.gameObject.activeSelf;

            if (showCaption)
            {
                row.CaptionText.fontSize = StatCaptionFontSize;
                row.CaptionText.characterSpacing = CaptionCharacterSpacing;
                row.CaptionText.ForceMeshUpdate();
            }

            row.CurrentText.fontSize = CurrentFontSize;
            row.MaxText.fontSize = MaxFontSize;
            row.CurrentText.fontStyle = FontStyles.Bold;
            row.MaxText.fontStyle = FontStyles.Bold;
            row.CurrentText.ForceMeshUpdate();
            row.MaxText.ForceMeshUpdate();

            float captionHeight = showCaption ? row.CaptionText.preferredHeight : 0f;
            float currentHeight = row.CurrentText.preferredHeight;
            float maxHeight = row.MaxText.preferredHeight;
            float bonusHeight = 0f;
            if (showBonus)
            {
                row.BonusText.fontSize = BonusFontSize;
                row.BonusText.fontStyle = FontStyles.Bold;
                row.BonusText.ForceMeshUpdate();
                bonusHeight = row.BonusText.preferredHeight;
            }

            float captionGap = showCaption ? CaptionToValueGapLocal : 0f;
            float bonusGap = showBonus ? BonusGapLocal : 0f;
            float textHeight = captionHeight + captionGap + currentHeight + ValueLineGapLocal + maxHeight
                + bonusGap + bonusHeight;
            float cursor = textHeight * 0.5f;

            if (showCaption)
            {
                row.CaptionText.transform.localPosition = new Vector3(0f, cursor - captionHeight * 0.5f, 0f);
                cursor -= captionHeight + captionGap;
            }

            row.CurrentText.transform.localPosition = new Vector3(0f, cursor - currentHeight * 0.5f, 0f);
            cursor -= currentHeight + ValueLineGapLocal;
            row.MaxText.transform.localPosition = new Vector3(0f, cursor - maxHeight * 0.5f, 0f);
            cursor -= maxHeight + bonusGap;

            if (showBonus)
                row.BonusText.transform.localPosition = new Vector3(0f, cursor - bonusHeight * 0.5f, 0f);
        }

        /// <summary>Preferred height of the CREW / digits / CAP / LINK stack.</summary>
        static float GetStatRowHeight(StatRow row)
        {
            // --- Compute value ---
            if (row.CurrentText == null || row.MaxText == null)
                return 0f;

            bool showCaption = row.CaptionText != null && row.CaptionText.gameObject.activeSelf;
            bool showBonus = row.BonusText != null && row.BonusText.gameObject.activeSelf;
            float caption = showCaption ? row.CaptionText.preferredHeight + CaptionToValueGapLocal : 0f;
            float bonus = showBonus ? BonusGapLocal + row.BonusText.preferredHeight : 0f;
            return caption + row.CurrentText.preferredHeight + ValueLineGapLocal + row.MaxText.preferredHeight + bonus;
        }

        /// <summary>
        /// Centers world name, optional home stamp, ship family, gun type, capture credit,
        /// and population as one vertical block on the planet label.
        /// </summary>
        /// <param name="showTitle">False when this planet has no world name.</param>
        /// <param name="showHomeRole">True only for team home worlds — the HOME PLANET stamp.</param>
        /// <param name="showFamily">False when the catalog has no family label.</param>
        /// <param name="showBulletType">False when the family has no named gun bank.</param>
        /// <param name="showContributor">False when this planet has no capture contributor.</param>
        void LayoutLabelBlock(
            bool showTitle,
            bool showHomeRole,
            bool showFamily,
            bool showBulletType,
            bool showContributor)
        {
            // --- Measure each visible row ---
            if (_titleText == null)
                return;

            LayoutStatRow(ref _populationRow);

            float titleHeight = MeasureTitle(showTitle);
            float homeRoleHeight = MeasureHomeRole(showHomeRole);
            float familyHeight = MeasureSubtitle(_familyText, FamilyNameFontSize, showFamily);
            float bulletTypeHeight = MeasureSubtitle(_bulletTypeText, BulletTypeFontSize, showBulletType);

            float creditHeight = 0f;
            if (showContributor && _captureCredit.Root != null)
            {
                LayoutCaptureCredit(ref _captureCredit);
                creditHeight = GetCaptureCreditHeight(_captureCredit);
            }

            // --- Stack heights ---
            // Identity block = place name + home stamp + family + gun. Credit sits under that.
            bool hasSubtitle = showHomeRole || showFamily || showBulletType;
            bool hasNameBlock = showTitle || hasSubtitle;
            float afterTitleGap = showTitle && hasSubtitle ? TitleToSubtitleGapLocal : 0f;
            float afterHomeGap = showHomeRole && (showFamily || showBulletType) ? SubtitleStackGapLocal : 0f;
            float afterFamilyGap = showFamily && showBulletType ? SubtitleStackGapLocal : 0f;
            float nameToCreditGap = hasNameBlock && showContributor ? ContributorGapLocal : 0f;
            bool showRule = hasNameBlock || showContributor;
            float ruleBlock = showRule ? RuleGapLocal + RuleHeightLocal + RuleGapLocal : 0f;
            float populationHeight = GetStatRowHeight(_populationRow);
            float headerHeight = (showTitle ? titleHeight : 0f)
                + (showHomeRole ? homeRoleHeight : 0f)
                + (showFamily ? familyHeight : 0f)
                + (showBulletType ? bulletTypeHeight : 0f)
                + (showContributor ? creditHeight : 0f)
                + afterTitleGap
                + afterHomeGap
                + afterFamilyGap
                + nameToCreditGap
                + ruleBlock;
            float totalHeight = populationHeight + headerHeight;

            // --- Place from the top of the centered stack ---
            float cursor = totalHeight * 0.5f;
            if (showTitle)
            {
                _titleText.fontStyle = FontStyles.Bold;
                _titleText.characterSpacing = TitleCharacterSpacing;
                _titleText.transform.localPosition = new Vector3(0f, cursor - titleHeight * 0.5f, 0f);
                cursor -= titleHeight + afterTitleGap;
            }

            if (showHomeRole && _homeRoleText != null)
            {
                _homeRoleText.transform.localPosition = new Vector3(0f, cursor - homeRoleHeight * 0.5f, 0f);
                cursor -= homeRoleHeight + afterHomeGap;
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
                cursor -= bulletTypeHeight + nameToCreditGap;
            }
            else if (hasNameBlock && showContributor)
            {
                cursor -= nameToCreditGap;
            }

            if (showContributor && _captureCredit.Root != null)
            {
                _captureCredit.Root.localPosition = new Vector3(0f, cursor - creditHeight * 0.5f, 0f);
                cursor -= creditHeight;
            }

            if (showRule && _identityRule != null)
            {
                _identityRule.gameObject.SetActive(true);
                cursor -= RuleGapLocal;
                float ruleWidth = MeasureRuleWidth(titleHeight, showTitle, showFamily, showBulletType);
                _identityRule.transform.localScale = new Vector3(ruleWidth, RuleHeightLocal, 1f);
                _identityRule.transform.localPosition = new Vector3(0f, cursor - RuleHeightLocal * 0.5f, 0f);
                cursor -= RuleHeightLocal + RuleGapLocal;
            }
            else if (_identityRule != null)
            {
                _identityRule.gameObject.SetActive(false);
            }

            _populationRow.Root.localPosition = new Vector3(0f, cursor - populationHeight * 0.5f, 0f);
        }

        /// <summary>Measures the world-name title after forcing the live font size.</summary>
        float MeasureTitle(bool show)
        {
            if (!show || _titleText == null)
                return 0f;

            _titleText.fontSize = TitleFontSize;
            _titleText.characterSpacing = TitleCharacterSpacing;
            _titleText.ForceMeshUpdate();
            return _titleText.preferredHeight;
        }

        /// <summary>Measures the heavy HOME PLANET stamp and reapplies Bold + Black weight.</summary>
        float MeasureHomeRole(bool show)
        {
            if (!show || _homeRoleText == null)
                return 0f;

            _homeRoleText.fontSize = HomeRoleFontSize;
            _homeRoleText.fontStyle = FontStyles.Bold;
            _homeRoleText.fontWeight = FontWeight.Black;
            _homeRoleText.characterSpacing = HomeRoleCharacterSpacing;
            _homeRoleText.ForceMeshUpdate();
            return _homeRoleText.preferredHeight;
        }

        /// <summary>Measures a smaller identity line (family or gun type) at <paramref name="fontSize"/>.</summary>
        static float MeasureSubtitle(TextMeshPro text, float fontSize, bool show)
        {
            if (!show || text == null)
                return 0f;

            text.fontSize = fontSize;
            text.ForceMeshUpdate();
            return text.preferredHeight;
        }

        /// <summary>Rule width follows the widest identity line so the bar matches the stencil.</summary>
        float MeasureRuleWidth(float titleHeight, bool showTitle, bool showFamily, bool showBulletType)
        {
            float width = 6f;
            if (showTitle && titleHeight > 0f)
                width = Mathf.Max(width, _titleText.preferredWidth * 0.92f);
            if (showFamily && _familyText != null)
                width = Mathf.Max(width, _familyText.preferredWidth * 0.85f);
            if (showBulletType && _bulletTypeText != null)
                width = Mathf.Max(width, _bulletTypeText.preferredWidth * 0.85f);
            return Mathf.Clamp(width, 5f, 22f);
        }

        /// <summary>Rebinds Rajdhani faces onto recovered TMP children after a Play Mode reload.</summary>
        void ApplyThemeFonts()
        {
            if (_titleText != null)
                _titleText.font = WorldBodyLabelTheme.DisplayFont;
            if (_homeRoleText != null)
                _homeRoleText.font = WorldBodyLabelTheme.DisplayFont;
            if (_familyText != null)
                _familyText.font = WorldBodyLabelTheme.TelemetryFont;
            if (_bulletTypeText != null)
                _bulletTypeText.font = WorldBodyLabelTheme.TelemetryFont;
            if (_populationRow.CaptionText != null)
                _populationRow.CaptionText.font = WorldBodyLabelTheme.CaptionFont;
            if (_populationRow.CurrentText != null)
                _populationRow.CurrentText.font = WorldBodyLabelTheme.DisplayFont;
            if (_populationRow.MaxText != null)
                _populationRow.MaxText.font = WorldBodyLabelTheme.TelemetryFont;
            if (_populationRow.BonusText != null)
                _populationRow.BonusText.font = WorldBodyLabelTheme.TelemetryFont;
        }

        /// <summary>Overlay SDF material via the shared space-HUD theme.</summary>
        static void ApplyReadableTextMaterial(TMP_Text text)
        {
            WorldBodyLabelTheme.ApplyOverlay(text);
        }

        /// <summary>Returns <paramref name="color"/> with a replaced alpha channel.</summary>
        static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        /// <summary>Lazy-loads the config that maps planet id → proper world name + gun type.</summary>
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
        /// Resolves the proper world name for this planet (not the ship family).
        /// Same helper the minimap hover tip uses so both surfaces stay in sync.
        /// </summary>
        static string ResolvePlanetTitle(in PlanetState state)
        {
            // --- Resolve value ---
            var config = ShipFamilyConfig;
            if (config == null)
                return string.Empty;

            return config.GetPlanetDisplayName(
                state.PlanetId,
                state.IsHomePlanet,
                state.ShipFamilyConfigIndex);
        }

        /// <summary>
        /// Resolves this planet's ship-tree family (Astro Eagle, Cosmic Shark, …).
        /// Same catalog string hulls, the upgrade tree, and the minimap hover tip use.
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

        /// <summary>
        /// Resolves this planet's rolled gun type (Fireballs, Rift, Laserbolt, …).
        /// Independent of the ship family so the same tree can fire a different bank next match.
        /// </summary>
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

        /// <summary>
        /// Formats the CAP line under CREW. LINK bonus is a separate cyan line so the extra
        /// beds cannot be read as a second crew count.
        /// </summary>
        static string FormatCapacityLine(int baseMax)
        {
            return WorldBodyLabelTheme.FormatCapLine(baseMax);
        }

        /// <summary>
        /// [UNITY] Per-frame refresh so population and triangle bonuses stay live.
        /// Dirty-checks TMP writes — assigning .text every frame rebuilt meshes + GC
        /// (~4ms / 93KB across labels, Profiler frame 41220).
        /// Layout/scale runs when text is dirty or the planet diameter changes — not every frame.
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (planetId == 0)
                return;

            bool textDirty = Refresh();
            float planetSize = PlanetVisualBody.ResolvePresentationSize(transform);
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetSize = ecsScale;
            bool sizeDirty = float.IsNaN(_cachedLayoutPlanetSize) ||
                             Mathf.Abs(_cachedLayoutPlanetSize - planetSize) > 0.01f;
            if (textDirty || sizeDirty)
            {
                ApplyLayout();
                _cachedLayoutPlanetSize = planetSize;
            }

            ApplyTheatricalLabelPose();
        }

        /// <summary>
        /// While idle theatrical orbit is on, billboard the label at the camera and lift it
        /// off the mesh. When theatrical ends, snap back to the snug top-down layout.
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
            if (_cachedBodyRadiusWorld < 0.01f)
                ApplyLayout();

            float s = WorldBodyLabelLayout.GetReadablePlanetLabelWorldScale(
                float.IsNaN(_cachedLayoutPlanetSize) ? 10f : _cachedLayoutPlanetSize);
            TheatricalWorldSpaceLabelRotation.ApplyTheatricalBillboard(
                _labelRoot,
                transform,
                _cachedBodyRadiusWorld,
                s,
                WorldBodyLabelLayout.PlanetPaddingAboveSurfaceLocal);
        }

        /// <summary>
        /// Pulls planet state + triangle bonus, then writes world name, home stamp,
        /// ship family, gun type, capture credit, and population when dirty.
        /// </summary>
        /// <returns>True when TMP / layout need ApplyLayout.</returns>
        bool Refresh()
        {
            // --- Refresh ---
            if (planetId == 0)
                return false;

            EnsureLabel();
            if (_titleText == null ||
                _homeRoleText == null ||
                _familyText == null ||
                _bulletTypeText == null ||
                _captureCredit.CaptionText == null ||
                _captureCredit.NameText == null ||
                _populationRow.CaptionText == null ||
                _populationRow.CurrentText == null ||
                _populationRow.MaxText == null ||
                _populationRow.BonusText == null)
                return false;

            if (!_legacyIconRemoved)
            {
                RemoveLegacyPopulationIcon(_populationRow.Root);
                _legacyIconRemoved = true;
            }

            // [HYBRID] Replicated PlanetState — Population is the live count clients already trust.
            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return false;

            // Prefer ECS scale — unit-scale roots no longer carry diameter on lossyScale.
            float planetScale = PlanetVisualBody.ResolvePresentationSize(transform);
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetScale = ecsScale;

            // --- Capacity: base from size/level, bonus from client connection triangles ---
            float bonusFraction = PlanetConnectionGraphCache.GetStackedConnectionBonusFraction(planetId);
            PlanetPopulationMath.GetMaxPopulationBreakdown(
                planetScale,
                state.PlanetLevel,
                bonusFraction,
                out int baseMax,
                out int bonusAmount);

            // Roster cache first — avoids "Player N" string alloc every LateUpdate before announce.
            int contributorId = state.TopContributorNetworkId;
            string contributorName = string.Empty;
            if (contributorId > 0 &&
                !PlayerNameRosterCache.TryGet(contributorId, out contributorName))
            {
                if (_hasCachedPaint &&
                    _cachedContributorNetworkId == contributorId &&
                    !string.IsNullOrEmpty(_cachedContributorName))
                    contributorName = _cachedContributorName;
                else
                    contributorName = EcsGameBridge.GetCachedPlayerDisplayName(contributorId);
            }

            bool hasContributor = !string.IsNullOrEmpty(contributorName);

            // --- Dirty check BEFORE ResolvePlanetTitle ---
            // [TITAN-ORBIT] Title only depends on planet id / home / optional family override —
            // not live population. Skip the catalog lookup when nothing name-related changed.
            if (_hasCachedPaint &&
                _cachedPopulation == state.Population &&
                _cachedBaseMax == baseMax &&
                _cachedBonusAmount == bonusAmount &&
                _cachedTeam == state.Ownership &&
                _cachedFamilyConfigIndex == state.ShipFamilyConfigIndex &&
                _cachedBulletBankIndex == state.BulletBankIndex &&
                _cachedIsHomePlanet == state.IsHomePlanet &&
                _cachedContributorNetworkId == contributorId &&
                _cachedContributorName == contributorName)
            {
                return false;
            }

            string planetTitle = ResolvePlanetTitle(state);
            string familyName = ResolveShipFamilyName(state);
            string bulletType = ResolveShipFamilyBulletType(state);
            bool hasTitle = !string.IsNullOrEmpty(planetTitle);
            bool showHomeRole = hasTitle && state.IsHomePlanet;
            bool hasFamily = hasTitle && !string.IsNullOrEmpty(familyName);
            bool hasBulletType = hasTitle && !string.IsNullOrEmpty(bulletType);

            _hasCachedPaint = true;
            _cachedPopulation = state.Population;
            _cachedBaseMax = baseMax;
            _cachedBonusAmount = bonusAmount;
            _cachedTeam = state.Ownership;
            _cachedFamilyConfigIndex = state.ShipFamilyConfigIndex;
            _cachedBulletBankIndex = state.BulletBankIndex;
            _cachedIsHomePlanet = state.IsHomePlanet;
            _cachedContributorNetworkId = contributorId;
            _cachedTitle = planetTitle;
            _cachedFamilyName = familyName;
            _cachedBulletType = bulletType;
            _cachedContributorName = contributorName;

            Color teamColor = state.Ownership.ToColor();

            _titleText.gameObject.SetActive(hasTitle);
            _titleText.text = hasTitle ? planetTitle : string.Empty;
            _titleText.color = teamColor;

            // --- Home capital stamp ---
            // Full team color (not the dim gun-type alpha) so the smaller line still owns the eye.
            _homeRoleText.gameObject.SetActive(showHomeRole);
            _homeRoleText.text = showHomeRole ? WorldBodyLabelTheme.FormatHomeStamp() : string.Empty;
            _homeRoleText.color = teamColor;

            // Every world: FLEET rail + family, then GUN rail + default bank.
            _familyText.gameObject.SetActive(hasFamily);
            _familyText.richText = true;
            _familyText.text = hasFamily ? WorldBodyLabelTheme.FormatFleetLine(familyName) : string.Empty;
            _familyText.color = WithAlpha(teamColor, FamilyNameAlpha);

            _bulletTypeText.gameObject.SetActive(hasBulletType);
            _bulletTypeText.richText = true;
            _bulletTypeText.text = hasBulletType ? WorldBodyLabelTheme.FormatGunLine(bulletType) : string.Empty;
            _bulletTypeText.color = WithAlpha(teamColor, BulletTypeAlpha);

            if (_captureCredit.Root != null)
                _captureCredit.Root.gameObject.SetActive(hasContributor);
            _captureCredit.CaptionText.text = hasContributor ? "CAPTURED BY" : string.Empty;
            _captureCredit.CaptionText.color = WithAlpha(teamColor, CapturedByAlpha);
            _captureCredit.NameText.text = hasContributor ? contributorName : string.Empty;
            _captureCredit.NameText.color = WithAlpha(teamColor, ContributorAlpha);

            _populationRow.CaptionText.gameObject.SetActive(true);
            _populationRow.CaptionText.text = WorldBodyLabelTheme.CrewCaption;
            _populationRow.CaptionText.color = WorldBodyLabelTheme.CaptionIce;

            _populationRow.CurrentText.text = state.Population.ToString();
            _populationRow.CurrentText.color = teamColor;

            _populationRow.MaxText.richText = true;
            _populationRow.MaxText.text = FormatCapacityLine(baseMax);
            _populationRow.MaxText.color = WithAlpha(teamColor, MaxLineAlpha);

            bool showLink = bonusAmount > 0;
            _populationRow.BonusText.gameObject.SetActive(showLink);
            _populationRow.BonusText.text = showLink ? WorldBodyLabelTheme.FormatLinkLine(bonusAmount) : string.Empty;
            _populationRow.BonusText.color = WorldBodyLabelTheme.LinkCyan;

            if (_identityRule != null)
                _identityRule.color = WithAlpha(teamColor, 0.55f);

            LayoutLabelBlock(hasTitle, showHomeRole, hasFamily, hasBulletType, hasContributor);
            return true;
        }
    }
}
