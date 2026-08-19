using System.Collections.Generic;
using System.Text;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// World-space nameplate locked to world orientation so it does <b>not</b> spin when the hull yaws.
    /// Regular ships sit <b>screen-below</b> the hull (world −Z); MEGA hulls sit <b>above mid-center</b>
    /// (world +Y):
    /// <code>
    /// [Name] .............. [Lv N]
    /// [Score] ............. [#Rank]
    /// [---- Full Version ----]
    /// [------ Health bar -----]
    /// [      (Badge)          ]  mid-center, overlaps the three bars
    /// [------- Mine bar ------]
    /// [---- Transports bar ---]
    /// [ K ] [ G ] [ T ]
    /// </code>
    /// Name / score are left-justified; ship level / rank are right-justified inside one shared
    /// content width. Long names are truncated by cutting characters.
    /// <para>
    /// [HYBRID] Client presentation only — fed by <see cref="EcsWorldVisualizer"/>. Regular-ship
    /// clearance is half the widest local footprint (+ padding); MEGA clearance is half-height
    /// above mid-center. Both are frozen until ability-upgrade growth; yaw must not move the plate.
    /// The label root is unparented so text/bars stay world-upright. Fully moon-docked ships hide
    /// the plate until takeoff.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(120)]
    public sealed class ShipWorldNameplate : MonoBehaviour
    {
        // --- Visual constants ---

        const int TextSortingOrder = 5002;
        const int BarSortingOrder = 5000;
        const int RoleSortingOrder = 5003;
        const int BadgeSortingOrder = 5001;
        const int PlayerBadgeSortingOrder = 5004;

        /// <summary>
        /// World scale for the unparented flat label root.
        /// Visual size ≈ fontSize × this.
        /// </summary>
        const float LabelWorldScale = 0.22f;

        /// <summary>Shared content width in label-local units (world width ≈ this × LabelWorldScale ≈ 3.1).</summary>
        const float ContentWidth = 14f;

        /// <summary>Primary row font — visual ≈ 2.2 world units.</summary>
        const float NameFontSize = 12f;

        /// <summary>Secondary row font — visual ≈ 1.76 world units.</summary>
        const float MetaFontSize = 9.5f;
        const float RoleLetterFontSize = 8.4f;

        const float OutlineWidth = 0.16f;
        const float FaceDilate = 0.1f;

        const float HeightAbovePlane = 0.08f;

        /// <summary>Gap past the hull edge — keep readable space under regular ships.</summary>
        const float PaddingPastHull = 0.35f;

        /// <summary>Gap above the hull top so MEGA plates sit over mid-center, not inside the mesh.</summary>
        const float PaddingAboveHull = 0.45f;
        const float FallbackHullExtentWorld = 0.4f;

        /// <summary>Hard cap so a bad bounds read can never throw a regular plate off-screen.</summary>
        const float MaxClearanceWorld = 1.6f;

        /// <summary>Fraction of half-widest used as the under-ship offset (full half-extent + padding).</summary>
        const float ClearanceScale = 1.0f;

        /// <summary>Hard cap on measured half-height so a bad MEGA bounds read cannot throw the plate away.</summary>
        const float MaxHeightWorld = 8f;

        /// <summary>Bar track height in label-local units (world ≈ 0.10 after 25% thinner).</summary>
        const float BarHeight = 0.45f;
        const float RowGap = 0.15f;

        /// <summary>Extra gap between name and score after preferredHeight (label-local).</summary>
        const float TextRowGap = 0.3f;

        const float BadgeHeight = 0.5f;

        /// <summary>K/G/T badge square size (label-local). ~40% larger than the first pass.</summary>
        const float RoleSlotSize = 0.98f;
        const float RoleSlotGap = 0.12f;

        /// <summary>
        /// Profile emblem over the three stat bars (label-local). Taller than the bar stack
        /// so it overlaps health / mine / transports as a centered medallion.
        /// </summary>
        const float PlayerBadgeSize = 2.97f;

        const float HealthHighRatio = 2f / 3f;
        const float HealthLowRatio = 1f / 3f;

        /// <summary>Discrete fill steps across the bar — reduces subpixel edge shimmer while moving.</summary>
        const int BarFillQuantizeSteps = 128;

        /// <summary>
        /// Bump when row spacing / fonts / clearance policy change so live proxies refresh layout.
        /// </summary>
        const int LayoutVersion = 18;

        /// <summary>Max name characters before width-fit (wider plate allows longer names).</summary>
        const int MaxNameCharacters = 28;

        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        static readonly Color RoleKiller = new Color(0.35f, 0.55f, 1f, 1f);
        static readonly Color RoleMiner = new Color(0.95f, 0.35f, 0.35f, 1f);
        static readonly Color RoleTransporter = new Color(0.95f, 0.85f, 0.25f, 1f);

        static readonly Color BarBgColor = new Color(0.12f, 0.14f, 0.18f, 0.92f);
        static readonly Color HealthFillFull = new Color(0.15f, 1f, 0.25f, 0.98f);
        static readonly Color HealthFillMid = new Color(1f, 0.55f, 0.05f, 0.98f);
        static readonly Color HealthFillEmpty = new Color(1f, 0.15f, 0.12f, 0.98f);
        static readonly Color GemsFill = new Color(0.95f, 0.35f, 0.35f, 0.98f);
        static readonly Color PeopleFill = new Color(0.95f, 0.85f, 0.25f, 0.98f);
        static readonly Color FullVersionBadgeColor = new Color(1f, 0.82f, 0.25f, 0.95f);
        static readonly Color MetaRightColor = new Color(0.85f, 0.90f, 1f, 0.95f);
        static readonly Color ScoreColor = new Color(0.95f, 0.86f, 0.55f, 1f);

        static readonly Vector3 ScreenBelowWorld = new Vector3(0f, 0f, -1f);

        static Sprite s_WhiteSprite;
        static Material s_PlayerBadgeMaterial;
        static readonly StringBuilder s_NameScratch = new StringBuilder(32);

        // --- Bound identity ---

        int _networkId;

        // --- Hierarchy (world-space root — not a child of the yawing hull) ---

        Transform _labelRoot;
        TextMeshPro _nameText;
        TextMeshPro _shipLevelText;
        TextMeshPro _scoreText;
        TextMeshPro _rankText;
        SpriteRenderer _fullVersionBadge;
        SpriteRenderer _playerBadge;
        ThinBar _healthBar;
        ThinBar _gemsBar;
        ThinBar _peopleBar;
        RoleSlot _roleKiller;
        RoleSlot _roleMiner;
        RoleSlot _roleTransporter;
        bool _ready;

        // --- Dirty caches ---

        string _cachedName;
        string _cachedRawName;
        string _cachedShipLevel;
        string _cachedScore;
        string _cachedRank;
        bool _cachedShowBadge;
        int _cachedBadgeId = int.MinValue;
        float _cachedHealthRatio = -1f;
        float _cachedGemsRatio = -1f;
        float _cachedPeopleRatio = -1f;
        bool _cachedKiller;
        bool _cachedMiner;
        bool _cachedTransporter;
        bool _cachedVisible = true;
        bool _isMega;

        /// <summary>
        /// Half of the ship's widest horizontal dimension in <b>world</b> units, cached until growth.
        /// </summary>
        float _cachedHalfWidestWorld = -1f;
        float _cachedHalfHeightWorld;

        /// <summary>
        /// Ship-local XZ center of the hull footprint (y ignored). Plate anchors from this point
        /// so an off-center pivot does not make the plate slide while yawing.
        /// </summary>
        Vector3 _cachedLocalCenter;

        /// <summary>Cached hull mesh renderers — avoids GetComponentsInChildren alloc every frame.</summary>
        Renderer[] _hullRenderers;

        /// <summary>
        /// Growth signature (root + part lossy scales). When unchanged we never remeasure —
        /// remasuring every frame was still shifting clearance while the ship turned.
        /// </summary>
        float _cachedGrowthSignature = float.NaN;

        /// <summary>Which <see cref="LayoutVersion"/> was last applied to this instance.</summary>
        int _appliedLayoutVersion = -1;

        struct ThinBar
        {
            public Transform Root;
            public Transform Fill;
            public SpriteRenderer FillRenderer;
        }

        struct RoleSlot
        {
            public Transform Root;
            public SpriteRenderer Bg;
            public TextMeshPro Letter;
        }

        /// <summary>
        /// Binds this nameplate to an owner NetworkId and builds the hierarchy once.
        /// </summary>
        public void Bind(int networkId)
        {
            _networkId = networkId;
            EnsureHierarchy();
        }

        /// <summary>
        /// Pushes live vitals, name, ship level, match score / team rank, badge, and top-role flags.
        /// Hides the plate while the ship is dead, awaiting team pick, unteamed, fully landed on a
        /// gem moon, or stowed in a planetary defense turret (label root is unparented from the hull).
        /// </summary>
        /// <param name="isLandedOnMoon">
        /// True when <c>ShipMoonDockState</c> reports a completed moon landing
        /// (<c>MoonPlanetId != 0</c> and landing progress at the complete threshold).
        /// </param>
        /// <param name="isStowedInTurret">
        /// True when <c>ShipTurretControlState.IsControlling</c> — hull is hidden on a pad.
        /// </param>
        /// <param name="isMega">
        /// True when this hull is a purchased MEGA — plate sits above mid-center instead of under the ship.
        /// </param>
        /// <param name="badgeId">Filename-stable profile badge id, or 0 for none.</param>
        public void ApplyPresentation(
            int networkId,
            string displayName,
            int badgeId,
            TeamId team,
            bool isDead,
            bool awaitingTeamSelection,
            bool isLandedOnMoon,
            bool isStowedInTurret,
            int shipLevel,
            int matchScore,
            int teamRank,
            float health,
            float maxHealth,
            float currentGems,
            float gemCapacity,
            int currentPeople,
            int peopleCapacity,
            bool isTopKiller,
            bool isTopMiner,
            bool isTopTransporter,
            bool isMega)
        {
            if (networkId > 0)
                _networkId = networkId;
            _isMega = isMega;
            EnsureHierarchy();
            if (!_ready || _labelRoot == null)
                return;

            EnsureLayoutCurrent();

            // --- Visibility ---
            // [TITAN-ORBIT] Moon dock: hide once fully landed (orbit-station UI owns that state).
            // Turret stow: label root is SetParent(null) so hiding the ship proxy alone is not enough.
            bool visible = !isDead
                           && !awaitingTeamSelection
                           && !isLandedOnMoon
                           && !isStowedInTurret
                           && team != TeamId.None;
            if (visible != _cachedVisible)
            {
                _labelRoot.gameObject.SetActive(visible);
                _cachedVisible = visible;
            }

            if (!visible)
                return;

            ApplyPlayerBadge(badgeId);

            // --- Row 1: Name (left) + Ship Level (right) ---
            string rawName = string.IsNullOrEmpty(displayName) ? $"Player {_networkId}" : displayName;
            bool textDirty = false;
            if (_nameText != null && rawName != _cachedRawName)
            {
                string name = FitNameToWidth(_nameText, TruncateName(rawName));
                _nameText.text = name;
                _cachedName = name;
                _cachedRawName = rawName;
                LayoutDualTextRow(_nameText, _shipLevelText);
                textDirty = true;
            }

            string levelStr = "Lv " + Mathf.Max(1, shipLevel);
            if (_shipLevelText != null && levelStr != _cachedShipLevel)
            {
                _shipLevelText.text = levelStr;
                _cachedShipLevel = levelStr;
                LayoutDualTextRow(_nameText, _shipLevelText);
                textDirty = true;
            }

            // --- Row 2: Score (left) + Rank (right) ---
            string scoreStr = Mathf.Max(0, matchScore).ToString();
            if (_scoreText != null && scoreStr != _cachedScore)
            {
                _scoreText.text = scoreStr;
                _cachedScore = scoreStr;
                LayoutDualTextRow(_scoreText, _rankText);
                textDirty = true;
            }

            string rankStr = "#" + Mathf.Max(1, teamRank);
            if (_rankText != null && rankStr != _cachedRank)
            {
                _rankText.text = rankStr;
                _cachedRank = rankStr;
                LayoutDualTextRow(_scoreText, _rankText);
                textDirty = true;
            }

            if (textDirty)
                ApplyStackLayout();

            // --- Full-version badge (full content width; hidden for free users) ---
            bool showBadge = ShipFullVersionBadge.IsFullVersionUser(_networkId);
            if (_fullVersionBadge != null && showBadge != _cachedShowBadge)
            {
                _fullVersionBadge.enabled = showBadge;
                _cachedShowBadge = showBadge;
            }

            // --- Thin bars (health / mine / transports) ---
            float hpRatio = maxHealth > 0.01f ? Mathf.Clamp01(health / maxHealth) : 0f;
            float gemRatio = gemCapacity > 0.01f ? Mathf.Clamp01(currentGems / gemCapacity) : 0f;
            float peopleRatio = peopleCapacity > 0
                ? Mathf.Clamp01(currentPeople / (float)peopleCapacity)
                : 0f;

            SetBarRatio(ref _healthBar, hpRatio, ref _cachedHealthRatio, HealthFillColor(hpRatio));
            SetBarRatio(ref _gemsBar, gemRatio, ref _cachedGemsRatio, GemsFill);
            SetBarRatio(ref _peopleBar, peopleRatio, ref _cachedPeopleRatio, PeopleFill);

            SetRoleSlot(ref _roleKiller, isTopKiller, ref _cachedKiller);
            SetRoleSlot(ref _roleMiner, isTopMiner, ref _cachedMiner);
            SetRoleSlot(ref _roleTransporter, isTopTransporter, ref _cachedTransporter);

            RefreshAnchorPose();
        }

        /// <summary>
        /// After attribute-scale LateUpdate — follow the ship with a <b>fixed</b> clearance
        /// from the widest hull dimension. Clearance only recalculates when the ship grows
        /// (ability upgrades / root scale), not when it yaws.
        /// </summary>
        void LateUpdate()
        {
            if (!_ready || _labelRoot == null || !_cachedVisible)
                return;

            EnsureLayoutCurrent();
            RefreshAnchorPose();
        }

        /// <summary>
        /// Re-applies fonts + stack spacing when <see cref="LayoutVersion"/> changes
        /// (hot reload / already-spawned proxies still holding the blown-out row heights).
        /// </summary>
        void EnsureLayoutCurrent()
        {
            if (_appliedLayoutVersion == LayoutVersion || _labelRoot == null)
                return;

            RestyleBarsAndBadgeToCurrentConstants();
            ApplyStackLayout();
            LayoutDualTextRow(_nameText, _shipLevelText);
            LayoutDualTextRow(_scoreText, _rankText);
            LayoutRoleRow(_labelRoot.Find("RoleRow"));

            // Force a fresh snug clearance with the world-unit conversion fix.
            _cachedHalfWidestWorld = -1f;
            _cachedHalfHeightWorld = 0f;
            _cachedGrowthSignature = float.NaN;
            _hullRenderers = null;

            // Invalidate bar caches so SetBarRatio re-hides empty fills (no leftover speck).
            _cachedHealthRatio = -1f;
            _cachedGemsRatio = -1f;
            _cachedPeopleRatio = -1f;

            _appliedLayoutVersion = LayoutVersion;
        }

        /// <summary>Destroys the unparented world label when the ship proxy goes away.</summary>
        void OnDestroy()
        {
            if (_labelRoot != null)
            {
                Destroy(_labelRoot.gameObject);
                _labelRoot = null;
            }
        }

        /// <summary>
        /// Regular ships: screen-below the hull footprint by half the widest world dimension.
        /// MEGA hulls: above the geometric mid-center by measured half-height.
        /// Clearance is frozen until ability-upgrade growth changes the signature.
        /// </summary>
        void RefreshAnchorPose()
        {
            if (_labelRoot == null)
                return;

            RefreshCachedHullFootprintIfGrown();

            Vector3 worldPos;
            if (_isMega)
            {
                float lift = Mathf.Clamp(
                    Mathf.Max(0.12f, _cachedHalfHeightWorld) + PaddingAboveHull,
                    0.2f,
                    MaxHeightWorld);

                Vector3 centerWorld = transform.TransformPoint(_cachedLocalCenter);
                worldPos = centerWorld;
                worldPos.y = centerWorld.y + lift + HeightAbovePlane;
            }
            else
            {
                float clearance = Mathf.Clamp(
                    Mathf.Max(0.1f, _cachedHalfWidestWorld) * ClearanceScale + PaddingPastHull,
                    0.1f,
                    MaxClearanceWorld);

                // Anchor from XZ hull center (not raw pivot) so yaw keeps the plate under the ship.
                Vector3 localCenter = _cachedLocalCenter;
                localCenter.y = 0f;
                Vector3 centerWorld = transform.TransformPoint(localCenter);
                worldPos = centerWorld + ScreenBelowWorld * clearance;
                worldPos.y = centerWorld.y + HeightAbovePlane;
            }

            // [TITAN-ORBIT] World rotation — plate stays upright while the hull turns.
            _labelRoot.SetPositionAndRotation(worldPos, Quaternion.Euler(-90f, 0f, 0f));
            _labelRoot.localScale = new Vector3(LabelWorldScale, -LabelWorldScale, LabelWorldScale);
        }

        /// <summary>
        /// Remeasures hull footprint <b>only</b> when root/part scales change (ability growth).
        /// Does not remeasure while turning — that was the crawl bug.
        /// </summary>
        void RefreshCachedHullFootprintIfGrown()
        {
            float growthSig = ComputeGrowthSignature();
            bool growthChanged = float.IsNaN(_cachedGrowthSignature) ||
                                 Mathf.Abs(growthSig - _cachedGrowthSignature) > 0.01f;

            if (!growthChanged && _cachedHalfWidestWorld > 0f)
                return;

            if (growthChanged)
                _hullRenderers = null;

            MeasureHullFootprint(out _cachedLocalCenter, out _cachedHalfWidestWorld, out _cachedHalfHeightWorld);
            _cachedGrowthSignature = growthSig;
        }

        /// <summary>
        /// Sum of root + hull-part lossy scales. Attribute upgrades change child scales without
        /// always changing the proxy root — those must still dirty the footprint cache.
        /// </summary>
        float ComputeGrowthSignature()
        {
            float sig = transform.lossyScale.x + transform.lossyScale.y + transform.lossyScale.z;
            EnsureHullRendererCache();
            if (_hullRenderers == null)
                return sig;

            int counted = 0;
            for (int i = 0; i < _hullRenderers.Length && counted < 12; i++)
            {
                Renderer renderer = _hullRenderers[i];
                if (renderer == null || WorldBodyLabelLayout.ShouldSkipRenderer(renderer))
                    continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    continue;

                Vector3 ls = renderer.transform.lossyScale;
                sig += ls.x + ls.y + ls.z;
                counted++;
            }

            return sig;
        }

        /// <summary>
        /// Ship-local XZ footprint → world clearance.
        /// Important: local half-width must be converted with <see cref="Transform.TransformVector"/>;
        /// ship proxies use <c>ShipPresentationScale</c> (~0.155), so local units are ~6× world.
        /// Using local as world was throwing the plate to the bottom of the screen.
        /// </summary>
        void MeasureHullFootprint(out Vector3 localCenter, out float halfWidestWorld, out float halfHeightWorld)
        {
            EnsureHullRendererCache();

            localCenter = Vector3.zero;
            halfWidestWorld = FallbackHullExtentWorld;
            halfHeightWorld = FallbackHullExtentWorld;

            if (_hullRenderers == null)
                return;

            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            float minZ = float.PositiveInfinity;
            float maxZ = float.NegativeInfinity;
            bool any = false;

            for (int i = 0; i < _hullRenderers.Length; i++)
            {
                Renderer renderer = _hullRenderers[i];
                if (renderer == null || WorldBodyLabelLayout.ShouldSkipRenderer(renderer))
                    continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    continue;

                Bounds lb = renderer.localBounds;
                Vector3 c = lb.center;
                Vector3 e = lb.extents;
                for (int xi = -1; xi <= 1; xi += 2)
                for (int yi = -1; yi <= 1; yi += 2)
                for (int zi = -1; zi <= 1; zi += 2)
                {
                    Vector3 localCorner = c + new Vector3(e.x * xi, e.y * yi, e.z * zi);
                    Vector3 world = renderer.transform.TransformPoint(localCorner);
                    Vector3 shipLocal = transform.InverseTransformPoint(world);
                    if (shipLocal.x < minX) minX = shipLocal.x;
                    if (shipLocal.x > maxX) maxX = shipLocal.x;
                    if (shipLocal.y < minY) minY = shipLocal.y;
                    if (shipLocal.y > maxY) maxY = shipLocal.y;
                    if (shipLocal.z < minZ) minZ = shipLocal.z;
                    if (shipLocal.z > maxZ) maxZ = shipLocal.z;
                    any = true;
                }
            }

            if (!any)
                return;

            localCenter = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            float localW = maxX - minX;
            float localH = maxY - minY;
            float localD = maxZ - minZ;

            // Local → world (accounts for ShipPresentationScale on the proxy root).
            float worldW = transform.TransformVector(new Vector3(localW, 0f, 0f)).magnitude;
            float worldH = transform.TransformVector(new Vector3(0f, localH, 0f)).magnitude;
            float worldD = transform.TransformVector(new Vector3(0f, 0f, localD)).magnitude;
            halfWidestWorld = 0.5f * Mathf.Max(worldW, worldD);
            halfHeightWorld = 0.5f * Mathf.Max(worldH, 0.2f);
            halfWidestWorld = Mathf.Max(0.12f, halfWidestWorld);
            halfHeightWorld = Mathf.Clamp(halfHeightWorld, 0.12f, MaxHeightWorld);
        }

        /// <summary>Caches child renderers once (invalidated on chassis / root-scale rebuild).</summary>
        void EnsureHullRendererCache()
        {
            if (_hullRenderers != null)
                return;

            _hullRenderers = GetComponentsInChildren<Renderer>(true);
        }

        void EnsureHierarchy()
        {
            if (_ready &&
                _labelRoot != null &&
                _nameText != null &&
                _shipLevelText != null &&
                _scoreText != null &&
                _rankText != null &&
                _healthBar.Root != null &&
                _roleKiller.Root != null)
                return;

            // Stale layout from older builds — rebuild cleanly.
            if (_labelRoot != null && (_shipLevelText == null || _scoreText == null || _rankText == null))
            {
                Destroy(_labelRoot.gameObject);
                _labelRoot = null;
                _ready = false;
                ClearHierarchyRefs();
            }

            if (TryRecoverExisting())
            {
                RestyleBarsAndBadgeToCurrentConstants();
                ApplyStackLayout();
                LayoutDualTextRow(_nameText, _shipLevelText);
                LayoutDualTextRow(_scoreText, _rankText);
                Transform recoveredRoleRow = _labelRoot != null ? _labelRoot.Find("RoleRow") : null;
                LayoutRoleRow(recoveredRoleRow);
                _ready = true;
                _appliedLayoutVersion = LayoutVersion;
                RefreshAnchorPose();
                return;
            }

            // --- Build world-space root (not parented to the yawing ship) ---
            _labelRoot = CreateLabelRoot("ShipNameplate");

            var nameRow = new GameObject("NameRow");
            nameRow.transform.SetParent(_labelRoot, false);
            _nameText = CreateValueText(nameRow.transform, "Name", NameFontSize, Color.white, TextAlignmentOptions.Left);
            _shipLevelText = CreateValueText(nameRow.transform, "ShipLevel", MetaFontSize, MetaRightColor, TextAlignmentOptions.Right);

            var scoreRow = new GameObject("ScoreRow");
            scoreRow.transform.SetParent(_labelRoot, false);
            _scoreText = CreateValueText(scoreRow.transform, "Score", MetaFontSize, ScoreColor, TextAlignmentOptions.Left);
            _rankText = CreateValueText(scoreRow.transform, "Rank", MetaFontSize, MetaRightColor, TextAlignmentOptions.Right);

            _fullVersionBadge = CreateFullWidthBadge(_labelRoot, "FullVersionBadge", FullVersionBadgeColor);
            _fullVersionBadge.enabled = false;

            _healthBar = CreateThinBar(_labelRoot, "HealthBar");
            _gemsBar = CreateThinBar(_labelRoot, "GemsBar");
            _peopleBar = CreateThinBar(_labelRoot, "PeopleBar");

            DestroyChildIfPresent(_labelRoot, "PlayerBadgeBack");
            _playerBadge = CreatePlayerBadgeRenderer(_labelRoot, "PlayerBadge", PlayerBadgeSortingOrder);

            var roleRow = new GameObject("RoleRow");
            roleRow.transform.SetParent(_labelRoot, false);
            _roleKiller = CreateRoleSlot(roleRow.transform, "Killer", "K", RoleKiller);
            _roleMiner = CreateRoleSlot(roleRow.transform, "Miner", "G", RoleMiner);
            _roleTransporter = CreateRoleSlot(roleRow.transform, "Transporter", "T", RoleTransporter);

            ApplyStackLayout();
            LayoutDualTextRow(_nameText, _shipLevelText);
            LayoutDualTextRow(_scoreText, _rankText);
            LayoutRoleRow(roleRow.transform);
            _ready = true;
            _appliedLayoutVersion = LayoutVersion;
            RefreshAnchorPose();
        }

        void ClearHierarchyRefs()
        {
            _nameText = null;
            _shipLevelText = null;
            _scoreText = null;
            _rankText = null;
            _fullVersionBadge = null;
            _playerBadge = null;
            _healthBar = default;
            _gemsBar = default;
            _peopleBar = default;
            _roleKiller = default;
            _roleMiner = default;
            _roleTransporter = default;
            _cachedName = null;
            _cachedRawName = null;
            _cachedShipLevel = null;
            _cachedScore = null;
            _cachedRank = null;
            _cachedHealthRatio = -1f;
            _cachedGemsRatio = -1f;
            _cachedPeopleRatio = -1f;
            _cachedShowBadge = false;
            _cachedBadgeId = int.MinValue;
            _cachedHalfWidestWorld = -1f;
            _cachedLocalCenter = Vector3.zero;
            _isMega = false;
            _hullRenderers = null;
            _cachedGrowthSignature = float.NaN;
            _appliedLayoutVersion = -1;
        }

        bool TryRecoverExisting()
        {
            if (_labelRoot == null)
            {
                // May still be parented from an older build, or floating in the scene.
                Transform existing = transform.Find("ShipNameplate");
                if (existing != null)
                    _labelRoot = existing;
            }

            if (_labelRoot == null)
                return false;

            // Ensure world-space (older builds parented under the ship).
            if (_labelRoot.parent != null)
                _labelRoot.SetParent(null, true);

            Transform nameRow = _labelRoot.Find("NameRow");
            Transform scoreRow = _labelRoot.Find("ScoreRow");
            if (nameRow == null || scoreRow == null)
                return false;

            _nameText = nameRow.Find("Name")?.GetComponent<TextMeshPro>();
            _shipLevelText = nameRow.Find("ShipLevel")?.GetComponent<TextMeshPro>();
            RecoverOrMigratePlayerBadge(nameRow);
            _scoreText = scoreRow.Find("Score")?.GetComponent<TextMeshPro>();
            _rankText = scoreRow.Find("Rank")?.GetComponent<TextMeshPro>();
            _fullVersionBadge = _labelRoot.Find("FullVersionBadge")?.GetComponent<SpriteRenderer>();

            _healthBar = RecoverBar(_labelRoot, "HealthBar");
            _gemsBar = RecoverBar(_labelRoot, "GemsBar");
            _peopleBar = RecoverBar(_labelRoot, "PeopleBar");

            Transform roleRow = _labelRoot.Find("RoleRow");
            if (roleRow != null)
            {
                _roleKiller = RecoverRoleSlot(roleRow, "Killer");
                _roleMiner = RecoverRoleSlot(roleRow, "Miner");
                _roleTransporter = RecoverRoleSlot(roleRow, "Transporter");
            }

            bool ok = _nameText != null &&
                      _shipLevelText != null &&
                      _scoreText != null &&
                      _rankText != null &&
                      _healthBar.Root != null &&
                      _roleKiller.Root != null;
            if (!ok)
                return false;

            ApplyReadableTextMaterial(_nameText);
            ApplyReadableTextMaterial(_shipLevelText);
            ApplyReadableTextMaterial(_scoreText);
            ApplyReadableTextMaterial(_rankText);
            return true;
        }

        /// <summary>
        /// Stack grows in label-local <b>−Y</b>. Uses TMP <c>preferredHeight</c> so score/rank
        /// sit just under the name without overlapping (fontSize ≠ fixed row slot).
        /// </summary>
        void ApplyStackLayout()
        {
            if (_labelRoot == null)
                return;

            Transform nameRow = _labelRoot.Find("NameRow");
            Transform scoreRow = _labelRoot.Find("ScoreRow");
            Transform badge = _fullVersionBadge != null ? _fullVersionBadge.transform : null;
            Transform roleRow = _labelRoot.Find("RoleRow");

            if (_nameText != null) _nameText.ForceMeshUpdate();
            if (_shipLevelText != null) _shipLevelText.ForceMeshUpdate();
            if (_scoreText != null) _scoreText.ForceMeshUpdate();
            if (_rankText != null) _rankText.ForceMeshUpdate();

            float nameH = 0.5f;
            if (_nameText != null || _shipLevelText != null)
            {
                float a = _nameText != null ? _nameText.preferredHeight : 0f;
                float b = _shipLevelText != null ? _shipLevelText.preferredHeight : 0f;
                nameH = Mathf.Max(0.35f, Mathf.Max(a, b));
            }

            float scoreH = 0.45f;
            if (_scoreText != null || _rankText != null)
            {
                float a = _scoreText != null ? _scoreText.preferredHeight : 0f;
                float b = _rankText != null ? _rankText.preferredHeight : 0f;
                scoreH = Mathf.Max(0.3f, Mathf.Max(a, b));
            }

            // Root origin = near edge (closest to hull). Grow toward −Y → screen-below.
            float y = 0f;

            if (nameRow != null)
            {
                y -= nameH * 0.5f;
                nameRow.localPosition = new Vector3(0f, y, 0f);
                y -= nameH * 0.5f + TextRowGap;
            }

            if (scoreRow != null)
            {
                y -= scoreH * 0.5f;
                scoreRow.localPosition = new Vector3(0f, y, 0f);
                y -= scoreH * 0.5f + RowGap;
            }

            if (badge != null)
            {
                y -= BadgeHeight * 0.5f;
                badge.localPosition = new Vector3(0f, y, 0f);
                badge.localScale = new Vector3(ContentWidth, BadgeHeight, 1f);
                y -= BadgeHeight * 0.5f + RowGap;
            }

            float barsTop = y;
            PlaceBar(ref _healthBar, ref y);
            PlaceBar(ref _gemsBar, ref y);
            PlaceBar(ref _peopleBar, ref y);
            float barsBottom = y + RowGap;
            PlacePlayerBadgeOverBars((barsTop + barsBottom) * 0.5f);

            y -= RowGap;
            if (roleRow != null)
            {
                y -= RoleSlotSize * 0.5f;
                roleRow.localPosition = new Vector3(0f, y, 0f);
            }
        }

        void PlaceBar(ref ThinBar bar, ref float y)
        {
            if (bar.Root == null)
                return;

            float h = BarHeight;
            y -= h * 0.5f;
            bar.Root.localPosition = new Vector3(0f, y, 0f);
            y -= h * 0.5f + RowGap;
        }

        /// <summary>Left + right TMP inside <see cref="ContentWidth"/>; row height from preferredHeight.</summary>
        void LayoutDualTextRow(TextMeshPro left, TextMeshPro right)
        {
            if (left == null || right == null)
                return;

            left.ForceMeshUpdate();
            right.ForceMeshUpdate();
            float rowH = Mathf.Max(0.3f, Mathf.Max(left.preferredHeight, right.preferredHeight));
            left.rectTransform.sizeDelta = new Vector2(ContentWidth, rowH);
            right.rectTransform.sizeDelta = new Vector2(ContentWidth, rowH);
            left.transform.localPosition = Vector3.zero;
            right.transform.localPosition = Vector3.zero;
        }

        static void LayoutRoleRow(Transform roleRow)
        {
            if (roleRow == null)
                return;

            float totalW = RoleSlotSize * 3f + RoleSlotGap * 2f;
            float x = -totalW * 0.5f + RoleSlotSize * 0.5f;
            for (int i = 0; i < roleRow.childCount; i++)
            {
                roleRow.GetChild(i).localPosition = new Vector3(x, 0f, 0f);
                x += RoleSlotSize + RoleSlotGap;
            }
        }

        /// <summary>Cuts characters until the name fits the left half of the content width.</summary>
        static string TruncateName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            string trimmed = raw.Trim();
            if (trimmed.Length > MaxNameCharacters)
                trimmed = trimmed.Substring(0, MaxNameCharacters);

            // Prefer hard char cap first; then shrink further if TMP would still overflow.
            // (PreferredWidth needs a live TMP — approximate with char budget when building.)
            return trimmed;
        }

        /// <summary>
        /// Further shrinks <paramref name="name"/> against a live TMP until it fits
        /// roughly half of <see cref="ContentWidth"/> (leaves room for ship level).
        /// </summary>
        string FitNameToWidth(TextMeshPro tmp, string name)
        {
            if (tmp == null || string.IsNullOrEmpty(name))
                return name ?? string.Empty;

            float maxW = ContentWidth * 0.58f;
            s_NameScratch.Clear();
            s_NameScratch.Append(name);
            tmp.text = name;
            tmp.ForceMeshUpdate();
            while (s_NameScratch.Length > 1 && tmp.preferredWidth > maxW)
            {
                s_NameScratch.Length -= 1;
                tmp.text = s_NameScratch.ToString();
                tmp.ForceMeshUpdate();
            }

            return tmp.text;
        }

        /// <summary>
        /// Sets fill width from a 0–1 ratio. Empty bars hide the fill renderer entirely —
        /// a leftover min-width scale left a colored speck that flickered while flying.
        /// </summary>
        static void SetBarRatio(ref ThinBar bar, float ratio, ref float cached, Color fillColor)
        {
            if (bar.Fill == null || bar.FillRenderer == null)
                return;

            // Quantize fill so the leading edge sits on stable steps (less crawl while moving).
            float qRatio = Mathf.Round(Mathf.Clamp01(ratio) * BarFillQuantizeSteps) / BarFillQuantizeSteps;
            bool showFill = qRatio > 0f;

            if (Mathf.Abs(qRatio - cached) < 0.0001f
                && bar.FillRenderer.enabled == showFill
                && (!showFill || bar.FillRenderer.color == fillColor))
                return;

            cached = qRatio;

            // --- Empty: hide fill (do not leave a 0.001-wide colored sliver) ---
            // [TITAN-ORBIT] While the camera moves, subpixel sampling of a tiny SpriteRenderer
            // fill reads as blinking colored artifacts on gem/people bars at 0.
            if (!showFill)
            {
                bar.FillRenderer.enabled = false;
                bar.Fill.localScale = new Vector3(0.001f, BarHeight, 1f);
                bar.Fill.localPosition = new Vector3(-ContentWidth * 0.5f, 0f, 0f);
                return;
            }

            // --- Non-empty: left-aligned fill inside the track ---
            float fillW = ContentWidth * qRatio;
            bar.FillRenderer.enabled = true;
            bar.Fill.localScale = new Vector3(fillW, BarHeight, 1f);
            bar.Fill.localPosition = new Vector3((-ContentWidth + fillW) * 0.5f, 0f, 0f);
            bar.FillRenderer.color = fillColor;
        }

        static void SetRoleSlot(ref RoleSlot slot, bool active, ref bool cached)
        {
            if (slot.Root == null || active == cached)
                return;

            cached = active;
            if (slot.Bg != null)
                slot.Bg.enabled = active;
            if (slot.Letter != null)
                slot.Letter.enabled = active;
        }

        static Color HealthFillColor(float ratio)
        {
            if (ratio >= HealthHighRatio)
            {
                float t = Mathf.InverseLerp(HealthHighRatio, 1f, ratio);
                return Color.Lerp(HealthFillMid, HealthFillFull, t);
            }

            if (ratio <= HealthLowRatio)
            {
                float t = Mathf.InverseLerp(0f, HealthLowRatio, ratio);
                return Color.Lerp(HealthFillEmpty, HealthFillMid, t);
            }

            return HealthFillMid;
        }

        /// <summary>Creates an unparented world-space label root.</summary>
        static Transform CreateLabelRoot(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(null, false);
            go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = new Vector3(LabelWorldScale, -LabelWorldScale, LabelWorldScale);
            return go.transform;
        }

        /// <summary>
        /// Applies current <see cref="ContentWidth"/> / <see cref="BarHeight"/> to recovered
        /// hierarchy (Play Mode reload or older nameplate builds).
        /// </summary>
        void RestyleBarsAndBadgeToCurrentConstants()
        {
            RestyleBar(ref _healthBar);
            RestyleBar(ref _gemsBar);
            RestyleBar(ref _peopleBar);

            if (_fullVersionBadge != null)
                _fullVersionBadge.transform.localScale = new Vector3(ContentWidth, BadgeHeight, 1f);

            if (_playerBadge != null)
            {
                _playerBadge.sortingOrder = PlayerBadgeSortingOrder;
                ApplyPlayerBadgeRendererStyle(_playerBadge);
                if (_playerBadge.enabled && _playerBadge.sprite != null)
                    ScaleSpriteToSize(_playerBadge, PlayerBadgeSize);
            }

            // Fonts may be from an older build — re-apply current sizes.
            if (_nameText != null) _nameText.fontSize = NameFontSize;
            if (_shipLevelText != null) _shipLevelText.fontSize = MetaFontSize;
            if (_scoreText != null) _scoreText.fontSize = MetaFontSize;
            if (_rankText != null) _rankText.fontSize = MetaFontSize;

            RestyleRoleSlot(ref _roleKiller);
            RestyleRoleSlot(ref _roleMiner);
            RestyleRoleSlot(ref _roleTransporter);
        }

        /// <summary>Applies current <see cref="RoleSlotSize"/> / letter font to a recovered role badge.</summary>
        static void RestyleRoleSlot(ref RoleSlot slot)
        {
            if (slot.Root == null)
                return;

            Transform bg = slot.Root.Find("Bg");
            if (bg != null)
                bg.localScale = new Vector3(RoleSlotSize, RoleSlotSize, 1f);

            if (slot.Letter != null)
                slot.Letter.fontSize = RoleLetterFontSize;
        }

        static void RestyleBar(ref ThinBar bar)
        {
            if (bar.Root == null)
                return;

            Transform outline = bar.Root.Find("Outline");
            if (outline != null)
                Object.Destroy(outline.gameObject);

            Transform bg = bar.Root.Find("Bg");
            if (bg != null)
                bg.localScale = new Vector3(ContentWidth, BarHeight, 1f);

            // Keep height in sync; width/visibility come from SetBarRatio (do not force a speck).
            if (bar.Fill != null)
            {
                Vector3 s = bar.Fill.localScale;
                bar.Fill.localScale = new Vector3(Mathf.Max(0.001f, s.x), BarHeight, 1f);
            }
        }

        static ThinBar CreateThinBar(Transform parent, string name)
        {
            // --- Bg + fill only (no white outline rim) ---
            Sprite sprite = GetWhiteSprite();
            var rootGo = new GameObject(name);
            rootGo.transform.SetParent(parent, false);

            var bgGo = new GameObject("Bg");
            bgGo.transform.SetParent(rootGo.transform, false);
            bgGo.transform.localScale = new Vector3(ContentWidth, BarHeight, 1f);
            var bgSr = bgGo.AddComponent<SpriteRenderer>();
            bgSr.sprite = sprite;
            bgSr.color = BarBgColor;
            bgSr.sortingOrder = BarSortingOrder;

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(rootGo.transform, false);
            // Start hidden at left edge — SetBarRatio enables + sizes on first vitals sync.
            fillGo.transform.localScale = new Vector3(0.001f, BarHeight, 1f);
            fillGo.transform.localPosition = new Vector3(-ContentWidth * 0.5f, 0f, 0f);
            var fillSr = fillGo.AddComponent<SpriteRenderer>();
            fillSr.sprite = sprite;
            fillSr.color = HealthFillFull;
            fillSr.sortingOrder = BarSortingOrder + 1;
            fillSr.enabled = false;

            return new ThinBar
            {
                Root = rootGo.transform,
                Fill = fillGo.transform,
                FillRenderer = fillSr,
            };
        }

        static ThinBar RecoverBar(Transform labelRoot, string name)
        {
            Transform root = labelRoot.Find(name);
            if (root == null)
                return default;

            // Strip legacy white Outline from older nameplate builds.
            Transform outline = root.Find("Outline");
            if (outline != null)
                Object.Destroy(outline.gameObject);

            Transform bg = root.Find("Bg");
            if (bg != null)
                bg.localScale = new Vector3(ContentWidth, BarHeight, 1f);

            Transform fill = root.Find("Fill");
            SpriteRenderer fillSr = fill != null ? fill.GetComponent<SpriteRenderer>() : null;
            if (fill != null)
            {
                // Height only — SetBarRatio owns width/visibility (avoids a full-width flash).
                Vector3 s = fill.localScale;
                fill.localScale = new Vector3(Mathf.Max(0.001f, s.x), BarHeight, 1f);
            }

            return new ThinBar
            {
                Root = root,
                Fill = fill,
                FillRenderer = fillSr,
            };
        }

        void RecoverOrMigratePlayerBadge(Transform nameRow)
        {
            DestroyChildIfPresent(_labelRoot, "PlayerBadgeBack");
            if (nameRow != null)
                DestroyChildIfPresent(nameRow, "PlayerBadgeBack");

            _playerBadge = _labelRoot.Find("PlayerBadge")?.GetComponent<SpriteRenderer>();

            // Older builds parented the emblem on NameRow — move it onto the plate root.
            if (_playerBadge == null && nameRow != null)
            {
                Transform leftover = nameRow.Find("PlayerBadge");
                if (leftover != null)
                    leftover.SetParent(_labelRoot, false);
                _playerBadge = leftover != null
                    ? leftover.GetComponent<SpriteRenderer>()
                    : null;
            }

            if (_playerBadge == null)
                _playerBadge = CreatePlayerBadgeRenderer(_labelRoot, "PlayerBadge", PlayerBadgeSortingOrder);
            else
            {
                _playerBadge.sortingOrder = PlayerBadgeSortingOrder;
                ApplyPlayerBadgeRendererStyle(_playerBadge);
            }
        }

        void ApplyPlayerBadge(int badgeId)
        {
            int cleaned = PlayerBadgeIdUtil.Sanitize(badgeId);
            if (cleaned == _cachedBadgeId && _playerBadge != null)
                return;

            Sprite sprite = PlayerBadgeCatalog.FindSprite(cleaned);
            bool show = sprite != null;
            if (_playerBadge != null)
            {
                _playerBadge.sprite = sprite;
                _playerBadge.enabled = show;
                ApplyPlayerBadgeRendererStyle(_playerBadge);
                if (show)
                    ScaleSpriteToSize(_playerBadge, PlayerBadgeSize);
            }

            _cachedBadgeId = cleaned;
        }

        void PlacePlayerBadgeOverBars(float midY)
        {
            if (_playerBadge == null)
                return;

            _playerBadge.transform.localPosition = new Vector3(0f, midY, -0.04f);
        }

        static SpriteRenderer CreatePlayerBadgeRenderer(Transform parent, string name, int sortingOrder)
        {
            Transform existing = parent.Find(name);
            GameObject go = existing != null ? existing.gameObject : new GameObject(name);
            if (existing == null)
                go.transform.SetParent(parent, false);

            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null)
                sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = null;
            sr.color = Color.white;
            sr.sortingOrder = sortingOrder;
            sr.enabled = false;
            ApplyPlayerBadgeRendererStyle(sr);
            go.transform.localScale = new Vector3(PlayerBadgeSize, PlayerBadgeSize, 1f);
            return sr;
        }

        static void ApplyPlayerBadgeRendererStyle(SpriteRenderer renderer)
        {
            if (renderer == null)
                return;

            renderer.sharedMaterial = GetPlayerBadgeMaterial();
            renderer.color = Color.white;
            renderer.drawMode = SpriteDrawMode.Simple;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.allowOcclusionWhenDynamic = false;
        }

        static Material GetPlayerBadgeMaterial()
        {
            if (s_PlayerBadgeMaterial != null)
                return s_PlayerBadgeMaterial;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");

            s_PlayerBadgeMaterial = shader != null
                ? new Material(shader)
                : new Material(Shader.Find("Hidden/InternalErrorShader"));
            s_PlayerBadgeMaterial.name = "ShipNameplatePlayerBadge";
            s_PlayerBadgeMaterial.renderQueue = RenderQueueOverlay;
            return s_PlayerBadgeMaterial;
        }

        static void DestroyChildIfPresent(Transform parent, string name)
        {
            if (parent == null)
                return;
            Transform child = parent.Find(name);
            if (child == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(child.gameObject);
            else
                Object.DestroyImmediate(child.gameObject);
        }

        static void ScaleSpriteToSize(SpriteRenderer renderer, float targetSize)
        {
            if (renderer == null || renderer.sprite == null)
                return;

            Sprite sprite = renderer.sprite;
            float worldW = sprite.rect.width / Mathf.Max(1f, sprite.pixelsPerUnit);
            float scale = targetSize / Mathf.Max(0.01f, worldW);
            renderer.transform.localScale = new Vector3(scale, scale, 1f);
        }

        /// <summary>Full-width bling strip under the score row (hidden until entitlement exists).</summary>
        static SpriteRenderer CreateFullWidthBadge(Transform parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GetWhiteSprite();
            sr.color = color;
            sr.sortingOrder = BadgeSortingOrder;
            sr.enabled = false;
            go.transform.localScale = new Vector3(ContentWidth, BadgeHeight, 1f);
            return sr;
        }

        static RoleSlot CreateRoleSlot(Transform parent, string name, string letter, Color color)
        {
            var rootGo = new GameObject(name);
            rootGo.transform.SetParent(parent, false);

            var bgGo = new GameObject("Bg");
            bgGo.transform.SetParent(rootGo.transform, false);
            bgGo.transform.localScale = new Vector3(RoleSlotSize, RoleSlotSize, 1f);
            var bg = bgGo.AddComponent<SpriteRenderer>();
            bg.sprite = GetWhiteSprite();
            bg.color = color;
            bg.sortingOrder = RoleSortingOrder;
            bg.enabled = false;

            var letterTmp = CreateValueText(rootGo.transform, "Letter", RoleLetterFontSize, Color.white, TextAlignmentOptions.Center);
            letterTmp.text = letter;
            letterTmp.enabled = false;
            letterTmp.transform.localPosition = new Vector3(0f, 0f, -0.02f);

            return new RoleSlot { Root = rootGo.transform, Bg = bg, Letter = letterTmp };
        }

        static RoleSlot RecoverRoleSlot(Transform roleRow, string name)
        {
            Transform root = roleRow.Find(name);
            if (root == null)
                return default;

            return new RoleSlot
            {
                Root = root,
                Bg = root.Find("Bg")?.GetComponent<SpriteRenderer>(),
                Letter = root.Find("Letter")?.GetComponent<TextMeshPro>(),
            };
        }

        static TextMeshPro CreateValueText(
            Transform parent,
            string name,
            float fontSize,
            Color color,
            TextAlignmentOptions align)
        {
            var textGo = new GameObject(name);
            textGo.transform.SetParent(parent, false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.font = ResolveFont();
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = align;
            tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.richText = false;
            tmp.color = color;
            tmp.rectTransform.sizeDelta = new Vector2(ContentWidth, Mathf.Max(0.35f, fontSize * 1.2f));
            ApplyReadableTextMaterial(tmp);
            return tmp;
        }

        static void ApplyReadableTextMaterial(TMP_Text text)
        {
            if (text == null)
                return;

            var mat = text.fontMaterial;
            if (mat == null)
                return;

            mat.EnableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 0.85f));
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", OutlineWidth);
            if (mat.HasProperty("_OutlineSoftness"))
                mat.SetFloat("_OutlineSoftness", 0.04f);
            if (mat.HasProperty("_FaceDilate"))
                mat.SetFloat("_FaceDilate", FaceDilate);
            mat.renderQueue = RenderQueueOverlay;

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder + 1;
        }

        static TMP_FontAsset ResolveFont()
        {
            if (TMP_Settings.defaultFontAsset != null)
                return TMP_Settings.defaultFontAsset;

            var fallback = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF - Fallback");
            if (fallback != null)
                return fallback;

#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset");
#else
            return null;
#endif
        }

        /// <summary>
        /// 1×1 white sprite with point filtering — bilinear on a stretched bar softens edges
        /// and makes them crawl under camera/ship motion.
        /// </summary>
        static Sprite GetWhiteSprite()
        {
            if (s_WhiteSprite != null)
                return s_WhiteSprite;

            // Own tex (do not mutate Texture2D.whiteTexture) — Point filter = sharp bar edges.
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.name = "ShipNameplateBarTex_v1";
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false, true);

            s_WhiteSprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                1f);
            s_WhiteSprite.name = "ShipNameplateWhiteSprite_v3";
            return s_WhiteSprite;
        }
    }

    /// <summary>
    /// Combined match score + per-team rank for ship nameplates and scoreboards.
    /// Weights match the old NGO ScoreSystem / <c>TeamLeaderboardHUD</c>:
    /// kill=100, deposited gem=2, delivered person=5.
    /// </summary>
    public static class ShipMatchScoreLogic
    {
        public const int PointsPerKill = 100;
        public const int PointsPerGem = 2;
        public const int PointsPerPerson = 5;

        /// <summary>Combined score from ghosted match-long stats.</summary>
        public static int ComputeCombinedScore(int kills, int gemsDeposited, int peopleDelivered)
        {
            return kills * PointsPerKill
                   + gemsDeposited * PointsPerGem
                   + peopleDelivered * PointsPerPerson;
        }

        /// <summary>
        /// Fills <paramref name="rankByNetworkId"/> with 1-based rank within each team
        /// (1 = highest combined score). Ties → lower NetworkId ranks better.
        /// </summary>
        public static void ComputeTeamRanks(
            IReadOnlyList<ShipTopOfTeamRoles.Candidate> candidates,
            Dictionary<int, int> rankByNetworkId)
        {
            rankByNetworkId.Clear();
            if (candidates == null || candidates.Count == 0)
                return;

            // Group living ships by team.
            var byTeam = new Dictionary<TeamId, List<ShipTopOfTeamRoles.Candidate>>(8);
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.IsDead || c.Team == TeamId.None || c.OwnerNetworkId <= 0)
                    continue;

                if (!byTeam.TryGetValue(c.Team, out var list))
                {
                    list = new List<ShipTopOfTeamRoles.Candidate>(8);
                    byTeam[c.Team] = list;
                }

                list.Add(c);
            }

            foreach (var kv in byTeam)
            {
                var list = kv.Value;
                list.Sort(CompareScoreThenId);
                for (int i = 0; i < list.Count; i++)
                    rankByNetworkId[list[i].OwnerNetworkId] = i + 1;
            }
        }

        static int CompareScoreThenId(ShipTopOfTeamRoles.Candidate a, ShipTopOfTeamRoles.Candidate b)
        {
            int scoreA = ComputeCombinedScore(a.Kills, a.GemsDeposited, a.PeopleDelivered);
            int scoreB = ComputeCombinedScore(b.Kills, b.GemsDeposited, b.PeopleDelivered);
            int c = scoreB.CompareTo(scoreA);
            if (c != 0)
                return c;
            return a.OwnerNetworkId.CompareTo(b.OwnerNetworkId);
        }
    }
}
