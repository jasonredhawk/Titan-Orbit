using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using TMPro;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only hybrid: Instantiates soft pad zones and turret meshes for ghosted
    /// <see cref="PlanetaryDefenseSlotElement"/> buffers on owned planets.
    /// <para>
    /// [HYBRID] Pad = Shapes soft blue disc matching the planet orbit-ring fill and
    /// <see cref="GemMoonOrbitZoneVisual"/> tint. Turret sits in the disc center; level +
    /// gem cost text (with the same gem icon as the moon label) sits screen-below the pad.
    /// Active turrets also show a thin horizontal HP bar under the mesh. Empty pads show
    /// placeholder copy instead of “Lv 0”. Parents to the unit-scale planet proxy root so
    /// pad/text/turret use true world sizes (no ÷ planetScale). See <see cref="PlanetVisualBody"/>.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Active turrets bank (roll) while turning to aim — same cosmetic curve as
    /// ships (<see cref="ShipBankVisualApplier"/> / <see cref="ShipBankVisualSettingsCache"/>).
    /// Yaw and bank are kept separate so roll never fights the aim slerp. Hostile tracking uses
    /// the same <see cref="PlanetaryDefenseAimMath"/> lead as server combat (identical
    /// per-level <c>bulletSpeed</c>, engage range for max-lead cap, and
    /// <see cref="PlanetaryDefenseAimMath.ShipVelocityLeadScale"/>) so barrels point where
    /// bullets go (ships via <see cref="ShipKinematics"/>, people transports via
    /// <see cref="PeopleTransportVfxDriver.CopyAimFlights"/>).
    /// </para>
    /// <para>
    /// [HYBRID] Turret meshes use the same GenericSpaceships team materials as people transports
    /// / attack·mining·shield drones (<see cref="PeopleTransportTeamMaterials"/>) so captured
    /// planets paint pads in the owning team's skin.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Walks <see cref="EcsWorldVisualizer"/> planet proxy keys only — never
    /// <c>ToEntityArray</c> / map-body archetype gathers (Windows late-join Crash!!! under
    /// session-long TransformQuarantine).
    /// </para>
    /// <para>
    /// [NETCODE] Slot <see cref="PlanetaryDefenseSlotElement.Health"/> is ghosted, but planet
    /// ghosts use a low MaxSendRate — the HP bar would look “stuck” for a long time after a
    /// real hit. <see cref="NotifyAuthoritativeHit"/> applies a short optimistic bar punch from
    /// <see cref="BulletHitRpc"/> (server Health-after), then reconciles when the ghost catches up.
    /// Friendly fire is off: same-team shots never damage pads (no HitRpc PD payload).
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66300)]
    public sealed class PlanetaryDefenseVisualDriver : MonoBehaviour
    {
        /// <summary>
        /// Cosmetic facing turn speed when tracking a hostile.
        /// Half of the original 8 — turrets track slower so aim reads less snappy.
        /// </summary>
        const float AimTurnSpeed = 4f;

        /// <summary>
        /// Yaw-rate deadband (°/s): below this, bank eases flat so idle turrets do not jitter.
        /// Matches <see cref="ShipBankVisualApplier"/> idle deadband.
        /// </summary>
        const float IdleBankAngularVelDeadbandDegPerSec = 18f;

        /// <summary>
        /// World Y for the whole slot vs planet center. Slightly below the flight plane so
        /// ships clear the pad instead of flying under a raised disc.
        /// </summary>
        const float PresentationLiftY = -0.08f;

        /// <summary>How far above the pad plane the turret mesh sits (world units).</summary>
        const float TurretAbovePadWorld = 0.22f;

        /// <summary>
        /// World gap from the faded pad rim to the near edge of the info plate.
        /// Plate sits on world −Z (“below” on a typical top-down camera), not radially out.
        /// </summary>
        const float InfoGapPastRimWorld = 0.22f;

        /// <summary>Info plate height above the pad plane so TMP clears the transparent disc.</summary>
        const float InfoAbovePadWorld = 0.06f;

        /// <summary>Placeholder title when <see cref="PlanetaryDefenseSlotElement.TurretLevel"/> is 0.</summary>
        const string EmptyPadPlaceholder = "Empty";

        /// <summary>Same red gem tint as <see cref="GemMoonWorldStatsLabel"/> gem counts.</summary>
        static readonly Color GemIconColor = new Color(1f, 0.2f, 0.2f, 1f);

        /// <summary>TMP sorting for the gem icon (just under the cost glyphs).</summary>
        const int IconSortingOrder = 5001;

        /// <summary>Local gap between gem icon and cost digits (moon-label family).</summary>
        const float IconGapLocal = 0.35f;

        /// <summary>Icon height as a fraction of the cost font size (moon uses ~0.11 of 33).</summary>
        const float IconHeightOverFontSize = 0.11f;

        /// <summary>
        /// Turret world size vs pad radius. GenericSpaceship4 reads large at authored scale 1 —
        /// we size relative to the soft pad so it sits as a gun on the disc, not a second ship.
        /// </summary>
        const float TurretSizeVsPadRadius = 0.42f;

        /// <summary>
        /// Hard clamp so tiny/huge planets still get a readable but not enormous turret.
        /// ~20% smaller than the prior 0.45–0.95 clamp so GenericSpaceship4 sits lighter on the pad.
        /// </summary>
        const float MinTurretWorldScale = 0.36f;
        const float MaxTurretWorldScale = 0.76f;

        // --- Health bar (thin strip under the turret mesh) ---

        /// <summary>Full bar width floor/ceiling in slot-local / world units.</summary>
        const float HealthBarWidthMin = 1.2f;
        const float HealthBarWidthMax = 2.4f;

        /// <summary>Strip height — thin progress bar, readable from top-down.</summary>
        const float HealthBarHeight = 0.18f;

        /// <summary>
        /// How much larger the outline is than the fill track (world units on each axis).
        /// Makes the max-HP frame readable even when the fill is near-full green.
        /// </summary>
        const float HealthBarOutlinePad = 0.06f;

        /// <summary>Lift above the pad plane so the bar clears the soft disc.</summary>
        const float HealthBarAbovePadWorld = 0.08f;

        /// <summary>
        /// Extra gap past the turret mesh footprint toward screen-below (−Z).
        /// Pushed further down so the strip clears the bulkier GenericSpaceship4 mesh.
        /// </summary>
        const float HealthBarClearancePastTurret = 1.25f;

        /// <summary>
        /// Approx. how far the turret mesh extends from its pivot as a fraction of
        /// <c>localScale</c> (GenericSpaceship4 hull is roughly 1 unit at scale 1).
        /// </summary>
        const float TurretMeshExtentOverScale = 0.85f;

        /// <summary>Sprite sorting — under pad labels, above world meshes.</summary>
        const int HealthBarSortingOrder = 5000;

        /// <summary>
        /// Light rim around the whole bar so the max-HP frame stays visible against the map.
        /// </summary>
        static readonly Color HealthBarOutlineColor = new Color(0.92f, 0.94f, 0.98f, 0.95f);

        /// <summary>
        /// Empty (missing HP) track — darker than the fill but lighter than space so
        /// current vs max is obvious at a glance.
        /// </summary>
        static readonly Color HealthBarBgColor = new Color(0.12f, 0.14f, 0.18f, 0.92f);

        // --- HP fill traffic-light (bright, readable on the dark map) ---
        // [TITAN-ORBIT] High / mid / low bands at 2/3 and 1/3 — see HealthBarFillColor.

        /// <summary>HP ratio at or above this → high band (green, lerped from orange).</summary>
        const float HealthBarHighRatio = 2f / 3f;

        /// <summary>HP ratio below this → low band (red → orange toward mid).</summary>
        const float HealthBarLowRatio = 1f / 3f;

        /// <summary>Healthy fill tint — bright green (full HP).</summary>
        static readonly Color HealthBarFillFull = new Color(0.15f, 1f, 0.25f, 0.98f);

        /// <summary>Medium fill tint — bright orange (middle third of HP).</summary>
        static readonly Color HealthBarFillMid = new Color(1f, 0.55f, 0.05f, 0.98f);

        /// <summary>Critical fill tint — bright red (near-empty HP).</summary>
        static readonly Color HealthBarFillEmpty = new Color(1f, 0.15f, 0.12f, 0.98f);

        /// <summary>
        /// Brief white/red flash tint while a HitRpc optimistic punch is live.
        /// Client presentation only — does not affect server combat.
        /// </summary>
        static readonly Color HealthBarHitFlashColor = new Color(1f, 0.95f, 0.85f, 1f);

        /// <summary>
        /// How long (seconds) we prefer HitRpc Health-after over lagging ghost Health.
        /// Safety timeout — ghost usually catches up sooner under MaxSendRate.
        /// </summary>
        const float OptimisticHpHoldSeconds = 2.5f;

        /// <summary>How long (seconds) the bar/turret hit flash lasts after a HitRpc punch.</summary>
        const float HitFlashSeconds = 0.22f;

        /// <summary>Turret scale mul at the peak of the hit punch (1 = no punch).</summary>
        const float HitPunchScalePeak = 1.12f;

        /// <summary>
        /// Ghost Health within this of optimistic Health → clear override (reconciled).
        /// Quantization=100 on the ghost field is 0.01; this is a comfortable match window.
        /// </summary>
        const float OptimisticHpReconcileEpsilon = 0.75f;

        /// <summary>Shared 1×1 white sprite for outline + bg + fill (created once).</summary>
        static Sprite s_HealthBarSprite;

        /// <summary>
        /// World-space uniform scale for TMP on the info plate (unit-scale planet root).
        /// Paired with larger font sizes below — sharper glyphs than a huge transform scale.
        /// </summary>
        const float InfoTextWorldScale = 0.462f;

        /// <summary>Bold level line (top of the stack, closer to the pad).</summary>
        const float LevelFontSize = 9.35f;

        /// <summary>Gem progress line under the level (slightly smaller / softer).</summary>
        const float CostFontSize = 6.875f;

        /// <summary>Local-space gap between Level and Cost after preferredHeight layout.</summary>
        const float InfoLineGapLocal = 0.12f;

        /// <summary>TMP sorting so pad text draws above world meshes / Shapes discs.</summary>
        const int TextSortingOrder = 5002;

        /// <summary>Soft outline like planet/moon labels (white halo, not a hard black stroke).</summary>
        const float OutlineWidth = 0.18f;
        const float FaceDilate = 0.1f;

        /// <summary>Cost-line alpha vs full white — secondary hierarchy under the level.</summary>
        const float CostLineAlpha = 0.78f;

        /// <summary>
        /// Bump when pad-label materials / hierarchy change so live SlotVisuals rebuild the
        /// info plate once (gem icon, orientation) without a turret-level fingerprint change.
        /// </summary>
        // Bump cleans orphan per-pad WorldSpace Canvas Take Control buttons (FPS regression).
        const byte InfoStyleVersion = 6;

        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        static PlanetaryDefenseVisualDriver s_Instance;

        PlanetShipFamilyConfig _familyConfig;
        PlanetaryDefenseConfig _defaultConfig;
        TMP_FontAsset _font;

        /// <summary>
        /// Shared GenericSpaceships1-8 team skins (same catalog as drones / people transports).
        /// </summary>
        PeopleTransportTeamMaterials _teamMaterials;

        readonly Dictionary<int, PlanetDefenseGroup> _groupsByPlanetId =
            new Dictionary<int, PlanetDefenseGroup>(16);
        readonly List<int> _alivePlanetIds = new List<int>(16);
        readonly List<int> _removePlanetIds = new List<int>(16);
        readonly List<Entity> _planetEntitiesScratch = new List<Entity>(32);
        readonly List<Entity> _shipEntitiesScratch = new List<Entity>(32);

        /// <summary>
        /// Cached <see cref="MapStateSingleton"/> query — created once, disposed in OnDestroy.
        /// Avoids <c>CreateEntityQuery</c> every LateUpdate (GC tax on the PD path).
        /// </summary>
        EntityQuery _mapQuery;

        /// <summary>True after <see cref="_mapQuery"/> has been created for the current world lifetime.</summary>
        bool _mapQueryCreated;

        /// <summary>
        /// Cached ship-aim query for occupied pads (GhostOwner + ShipInput).
        /// Created once — per-slot CreateEntityQuery was an FPS bomb while anyone piloted.
        /// </summary>
        EntityQuery _occupantAimQuery;

        /// <summary>True after <see cref="_occupantAimQuery"/> has been created.</summary>
        bool _occupantAimQueryCreated;

        /// <summary>
        /// Scratch for people-transport VFX aim samples (no ECS gather — VFX driver list walk).
        /// </summary>
        readonly List<PeopleTransportVfxDriver.AimFlightSample> _transportAimScratch =
            new List<PeopleTransportVfxDriver.AimFlightSample>(32);

        /// <summary>
        /// HitRpc Health-after overrides keyed by planetId×slot (see <see cref="MakeOptimisticKey"/>).
        /// Cleared when ghost Health catches up or the hold timer expires.
        /// </summary>
        readonly Dictionary<long, OptimisticSlotHp> _optimisticHpBySlot =
            new Dictionary<long, OptimisticSlotHp>(32);

        /// <summary>
        /// Short-lived client display of server Health-after from <see cref="BulletHitRpc"/>.
        /// Not a second sim — presentation only until the planet ghost buffer updates.
        /// </summary>
        struct OptimisticSlotHp
        {
            /// <summary>Authoritative remaining HP from the HitRpc (0 = destroyed this hit).</summary>
            public float HealthAfter;

            /// <summary>Unity <c>Time.time</c> when we stop preferring this over ghost Health.</summary>
            public float ExpireAt;

            /// <summary>Unity <c>Time.time</c> until the bar/turret flash ends.</summary>
            public float FlashUntil;
        }

        /// <summary>One planet's pad + turret GameObjects.</summary>
        sealed class PlanetDefenseGroup
        {
            public int PlanetId;
            public Transform Hub;
            public readonly List<SlotVisual> Slots = new List<SlotVisual>(6);
            public int LayoutFingerprint = int.MinValue;
        }

        /// <summary>
        /// One slot: soft pad zone (Shapes) + turret in the center + HP bar under the mesh +
        /// info text below the rim. Also holds cosmetic bank (roll-while-turning) state for
        /// the active turret mesh.
        /// </summary>
        struct SlotVisual
        {
            public int SlotIndex;
            public byte TurretLevel;
            public Transform SlotRoot;
            public PlanetaryDefensePadZoneVisual ZoneVisual;
            public GameObject TurretInstance;
            /// <summary>
            /// Last team skin applied to <see cref="TurretInstance"/> — refreshed on capture.
            /// </summary>
            public TeamId AppliedTeam;
            public Transform HealthBarRoot;
            public Transform HealthBarFill;
            public SpriteRenderer HealthBarFillRenderer;
            public TextMeshPro LevelText;
            public TextMeshPro CostText;
            public SpriteRenderer GemIcon;
            public Transform InfoRoot;
            /// <summary>Matches <see cref="InfoStyleVersion"/> after outline / hierarchy are applied.</summary>
            public byte StyleVersion;

            /// <summary>
            /// Last painted level line (skip TMP assign + ForceMeshUpdate when unchanged).
            /// [TITAN-ORBIT] Profiler: UpdateInfoPlate allocated strings + ForceMeshUpdate ×2 per pad
            /// every LateUpdate (~4k mesh updates/sec with 25 planets × 81 pads).
            /// </summary>
            public string CachedLevelText;

            /// <summary>Last painted cost line (e.g. <c>12 / 174</c> or <c>MAX</c>).</summary>
            public string CachedCostText;

            /// <summary>Last Floor(BuildProgress) used for cost digits (avoids string rebuild).</summary>
            public int CachedCostCurrent;

            /// <summary>Last Ceil(gemsToNext) used for cost digits.</summary>
            public int CachedCostMax;

            /// <summary>Last at-cap flag (controls MAX vs progress + gem icon).</summary>
            public bool CachedAtCap;

            /// <summary>True after first successful LayoutInfoLines for this plate.</summary>
            public bool PlateLayoutSeeded;

            /// <summary>Cached half-height of Level+cost stack (InfoRoot local) for pad-below offset.</summary>
            public float CachedHalfStackLocal;

            /// <summary>Pad radius used when <see cref="CachedHalfStackLocal"/> was computed.</summary>
            public float CachedPadRadiusForPos;

            // --- Cosmetic bank (yaw vs roll kept separate so aim slerp stays clean) ---

            /// <summary>
            /// Yaw-only facing for this turret (no roll). Written each frame before bank is applied
            /// onto <see cref="TurretInstance"/> as <c>yaw * roll</c>.
            /// </summary>
            public Quaternion TurretYawRotation;

            /// <summary>True after <see cref="TurretYawRotation"/> has been seeded once.</summary>
            public bool TurretYawInitialized;

            /// <summary>Previous planar yaw (°) used to estimate turn rate for bank.</summary>
            public float BankPrevYawDeg;

            /// <summary>True after the first yaw sample (avoids a huge spike on the first frame).</summary>
            public bool BankYawInitialized;

            /// <summary>Smoothed yaw rate (°/s) feeding the bank curve.</summary>
            public float BankYawRateDegPerSec;

            /// <summary>Current cosmetic roll angle (°); positive = bank into the turn.</summary>
            public float BankCurrentAngle;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (s_Instance != null)
                return;

            var go = new GameObject("PlanetaryDefenseVisualDriver");
            DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<PlanetaryDefenseVisualDriver>();
#endif
        }

        void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_Instance = this;
            _familyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            _defaultConfig = PlanetaryDefenseConfig.LoadDefault();
            _font = ResolveFont();
        }

        /// <summary>
        /// Applies a server-authored turret HP punch from <see cref="BulletHitRpc"/>.
        /// Called by <see cref="BulletVfxDriver"/> when PlanetId &gt; 0 on the hit payload.
        /// <para>
        /// [TITAN-ORBIT] Planet ghosts lag MaxSendRate — without this the HP bar looks frozen
        /// even though <see cref="PlanetaryDefenseHitScan.ApplyDamage"/> already ran on the server.
        /// We never invent permanent HP: ghost Health wins as soon as it is ≤ this value
        /// (or within epsilon), or when the hold timer expires.
        /// </para>
        /// </summary>
        /// <param name="planetId">Stable <see cref="PlanetState.PlanetId"/>.</param>
        /// <param name="slotIndex">Slot index in the planet’s defense buffer.</param>
        /// <param name="healthAfter">
        /// Remaining Health after the hit (0 = destroyed / empty placeholder).
        /// </param>
        public static void NotifyAuthoritativeHit(int planetId, int slotIndex, float healthAfter)
        {
            if (s_Instance == null || planetId <= 0 || slotIndex < 0)
                return;

            s_Instance.ApplyOptimisticHit(planetId, slotIndex, healthAfter);
        }

        /// <summary>
        /// Stores or tightens the optimistic HP for one pad and arms the hit flash.
        /// Instance path for <see cref="NotifyAuthoritativeHit"/>.
        /// </summary>
        /// <param name="planetId">Stable planet id.</param>
        /// <param name="slotIndex">Defense slot index.</param>
        /// <param name="healthAfter">Server Health after this hit.</param>
        void ApplyOptimisticHit(int planetId, int slotIndex, float healthAfter)
        {
            // --- Key + clamp ---
            // [STANDARD] Dictionary key packs planet + slot so multi-pad planets stay independent.
            long key = MakeOptimisticKey(planetId, slotIndex);
            float clampedHp = math.max(0f, healthAfter);
            float now = Time.time;

            // --- Prefer the lowest remaining HP when multiple HitRpcs race ---
            // [TITAN-ORBIT] Rapid fire can enqueue several hits before LateUpdate reads once;
            // keep the most damaged value so the bar never “heals” from an older RPC.
            if (_optimisticHpBySlot.TryGetValue(key, out var existing) &&
                existing.ExpireAt > now)
            {
                clampedHp = math.min(clampedHp, existing.HealthAfter);
            }

            _optimisticHpBySlot[key] = new OptimisticSlotHp
            {
                HealthAfter = clampedHp,
                ExpireAt = now + OptimisticHpHoldSeconds,
                FlashUntil = now + HitFlashSeconds,
            };
        }

        /// <summary>
        /// Packs planet id + slot into one dictionary key (planet in high bits, slot in low byte).
        /// </summary>
        static long MakeOptimisticKey(int planetId, int slotIndex) =>
            ((long)planetId << 8) | (byte)math.clamp(slotIndex, 0, 255);

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
            ClearAllGroups();

            // --- Dispose cached ECS queries ---
            // [ECS/DOTS] EntityQuery owns native allocations; leave them when the driver dies.
            // This Entities version has no EntityQuery.IsCreated — track lifetime with bool flags.
            if (_mapQueryCreated)
            {
                _mapQuery.Dispose();
                _mapQueryCreated = false;
            }

            if (_occupantAimQueryCreated)
            {
                _occupantAimQuery.Dispose();
                _occupantAimQueryCreated = false;
            }
        }

        void LateUpdate()
        {
            if (ClientJoinSettleCache.Settling)
            {
                ClearAllGroups();
                return;
            }

            // --- Wait for map GO load before Instantiates pads/turrets ---
            // [TITAN-ORBIT] Settling can latch OFF while MapLoadingProxyCount is still below the
            // Join Team gate. RebuildSlots then Instantiates ~80 turret prefabs + TMP plates in a
            // burst and the hybrid map drain stalls. Pads are presentation-only — defer until
            // the map proxy build is ready.
            if (!EcsGameBridge.IsMapProxyCountReady(out _, out _, out _))
            {
                ClearAllGroups();
                return;
            }

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return;

            World world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            visualizer.CopyPlanetProxyEntities(_planetEntitiesScratch);
            if (_planetEntitiesScratch.Count == 0)
            {
                ClearAllGroups();
                return;
            }

            float mapW = 0f, mapH = 0f;
            bool hasMap = TryResolveMapSize(em, out mapW, out mapH);

            _alivePlanetIds.Clear();
            bool canAimShips = !ClientJoinSettleCache.ShouldSkipShipEntityQueries;

            for (int p = 0; p < _planetEntitiesScratch.Count; p++)
            {
                Entity planetEntity = _planetEntitiesScratch[p];
                if (!em.Exists(planetEntity) ||
                    !em.HasComponent<PlanetState>(planetEntity) ||
                    !em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                    continue;

                if (!visualizer.TryGetProxy(planetEntity, out GameObject planetProxy) ||
                    planetProxy == null)
                    continue;

                var planet = em.GetComponentData<PlanetState>(planetEntity);
                if (planet.Ownership == TeamId.None || planet.PlanetId == 0)
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                if (buffer.Length == 0)
                    continue;

                _alivePlanetIds.Add(planet.PlanetId);
                var config = PlanetaryDefenseConfig.ResolveForFamily(
                    _familyConfig, planet.ShipFamilyConfigIndex);

                if (!_groupsByPlanetId.TryGetValue(planet.PlanetId, out var group))
                {
                    group = CreateGroup(planet.PlanetId);
                    _groupsByPlanetId[planet.PlanetId] = group;
                }

                int fingerprint = ComputeFingerprint(buffer);
                if (group.LayoutFingerprint != fingerprint)
                {
                    RebuildSlots(group, buffer, config);
                    group.LayoutFingerprint = fingerprint;
                }

                if (group.Hub != null && group.Hub.parent != planetProxy.transform)
                    group.Hub.SetParent(planetProxy.transform, worldPositionStays: false);
                if (group.Hub != null)
                {
                    group.Hub.localPosition = Vector3.zero;
                    group.Hub.localRotation = Quaternion.identity;
                    group.Hub.localScale = Vector3.one;
                }

                // ECS scale is authoritative — unit-scale planet roots no longer carry diameter.
                float planetSize = math.max(0.25f, PlanetVisualBody.ResolvePresentationSize(planetProxy.transform));
                if (em.HasComponent<LocalTransform>(planetEntity))
                    planetSize = math.max(0.25f, em.GetComponentData<LocalTransform>(planetEntity).Scale);

                float3 planetDisplay = (float3)planetProxy.transform.position;
                int slotCount = buffer.Length;

                // Crown Lv7 when planet is maxed and the gem-moon reservoir is full (ghosted).
                float moonCurrent = 0f;
                float moonMax = 0f;
                if (em.HasComponent<PlanetGemMoonState>(planetEntity))
                {
                    var moon = em.GetComponentData<PlanetGemMoonState>(planetEntity);
                    moonCurrent = moon.CurrentMoonGems;
                    moonMax = moon.MaxMoonGems;
                }

                int maxTurretLevel = PlanetaryDefenseMath.GetMaxTurretLevelForPlanet(
                    planet.PlanetLevel, moonCurrent, moonMax);

                // Soft disc ≈ deposit zone. Root is unit-scale — slot-local == world.
                float padWorldRadius = math.clamp(config.depositZoneRadius, 0.8f, 2.5f);

                for (int i = 0; i < group.Slots.Count && i < buffer.Length; i++)
                {
                    var slot = buffer[i];
                    var vis = group.Slots[i];
                    if (vis.SlotRoot == null)
                        continue;

                    float3 slotWorld = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetDisplay, planetSize, planet.PlanetLevel, i, slotCount);
                    slotWorld.y = planetDisplay.y + PresentationLiftY;

                    Vector3 local = planetProxy.transform.InverseTransformPoint(
                        new Vector3(slotWorld.x, slotWorld.y, slotWorld.z));

                    vis.SlotRoot.localPosition = local;
                    vis.SlotRoot.localRotation = Quaternion.identity;
                    vis.SlotRoot.localScale = Vector3.one;

                    // --- Soft blue pad zone (Shapes — same tint as orbit ring / moon zone) ---
                    if (vis.ZoneVisual != null)
                        vis.ZoneVisual.SetRadiusLocal(padWorldRadius);

                    // --- Level + gems just below / outside the pad rim ---
                    // Take Control is a single screen-space HUD button (not per-pad Canvas).
                    UpdateInfoPlate(
                        ref vis, slot, config, maxTurretLevel,
                        padWorldRadius, planetDisplay, slotWorld);

                    // --- Turret in the pad center (faces outward; tracks hostiles when in range) ---
                    if (vis.TurretInstance != null)
                    {
                        bool active = slot.TurretLevel > 0;
                        vis.TurretInstance.SetActive(active);
                        if (active)
                        {
                            vis.TurretInstance.transform.localPosition =
                                new Vector3(0f, TurretAbovePadWorld, 0f);

                            // [HYBRID] Same GenericSpaceships team skins as drones / transports.
                            // Re-apply when ownership flips (capture) or the mesh was just spawned.
                            if (vis.AppliedTeam != planet.Ownership)
                            {
                                ApplyTurretTeamMaterials(vis.TurretInstance, planet.Ownership);
                                vis.AppliedTeam = planet.Ownership;
                            }

                            var levelStats = config.GetLevelStats(slot.TurretLevel);

                            // Modest size vs pad — level visualScale nudges slightly, stays clamped.
                            float scaleMul = math.clamp(levelStats.visualScale, 0.4f, 1.1f);
                            float worldScale = math.clamp(
                                padWorldRadius * TurretSizeVsPadRadius * scaleMul,
                                MinTurretWorldScale,
                                MaxTurretWorldScale);
                            vis.TurretInstance.transform.localScale = Vector3.one * worldScale;

                            // Absolute fire distance from the pad (same world units as server combat).
                            float engageFromTurret = math.max(0.5f, levelStats.engageRange);

                            // Rest pose = radially outward from planet center. When a hostile is
                            // in this pad’s engage range, ease toward the lead aim point instead
                            // (same PlanetaryDefenseAimMath as server fire direction).
                            // Player-occupied pads aim from the occupant ship's ShipInput instead.
                            Vector3 outwardFlat = new Vector3(
                                slotWorld.x - planetDisplay.x,
                                0f,
                                slotWorld.z - planetDisplay.z);
                            Vector3 aimFlat = outwardFlat;
                            float bulletSpeed = math.max(1f, levelStats.bulletSpeed);
                            if (slot.OccupiedByNetworkId != 0)
                            {
                                // [HYBRID] Manual control — ghosted AimPlanarDir from the piloting ship.
                                if (TryGetOccupantAimFlat(em, slot.OccupiedByNetworkId, out Vector3 occupiedAim))
                                    aimFlat = occupiedAim;
                            }
                            else if (canAimShips &&
                                hasMap &&
                                TryFindNearestHostileDisplay(
                                    em, planet.Ownership, slotWorld, engageFromTurret,
                                    mapW, mapH, out float3 targetPos, out float3 targetVel))
                            {
                                float3 muzzle = (float3)vis.TurretInstance.transform.position;
                                muzzle.y = PlanetaryDefenseMath.FixedY;
                                // [HYBRID] Presentation lead — does not drive sim; matches server
                                // combat (same per-level bulletSpeed + engageRange + lead scale).
                                if (PlanetaryDefenseAimMath.TryComputeFireDirection(
                                        muzzle, targetPos, targetVel, bulletSpeed, mapW, mapH,
                                        engageFromTurret,
                                        PlanetaryDefenseAimMath.ShipVelocityLeadScale,
                                        out float3 fireDir))
                                {
                                    aimFlat = new Vector3(fireDir.x, 0f, fireDir.z);
                                }
                            }

                            // [TITAN-ORBIT] Yaw + ship-style bank roll (cosmetic only).
                            ApplyTurretAimAndBank(ref vis, aimFlat, Time.deltaTime);
                        }
                        else
                        {
                            // Empty pad — drop bank state so a rebuilt turret starts flat.
                            ResetTurretBankState(ref vis);
                        }
                    }

                    // Thin HP strip under the turret footprint (hidden on empty pads).
                    // [NETCODE] May punch from HitRpc optimistic Health before the ghost arrives.
                    float turretScaleForBar = 0f;
                    if (vis.TurretInstance != null && vis.TurretInstance.activeSelf)
                        turretScaleForBar = vis.TurretInstance.transform.localScale.x;
                    UpdateHealthBar(ref vis, slot, turretScaleForBar, planet.PlanetId, i);

                    group.Slots[i] = vis;
                }
            }

            _removePlanetIds.Clear();
            foreach (var kv in _groupsByPlanetId)
            {
                if (!_alivePlanetIds.Contains(kv.Key))
                    _removePlanetIds.Add(kv.Key);
            }

            for (int i = 0; i < _removePlanetIds.Count; i++)
            {
                DestroyGroup(_groupsByPlanetId[_removePlanetIds[i]]);
                _groupsByPlanetId.Remove(_removePlanetIds[i]);
            }
        }

        /// <summary>
        /// Places level + gem cost <b>screen-below</b> the soft pad (world −Z), with the same
        /// flat TMP orientation as planet/moon labels — not aimed radially off the planet.
        /// World units under the unit-scale planet root — no ÷ planetScale.
        /// Rebuilds TMP / layout only when level, build progress, or cap state changes.
        /// </summary>
        void UpdateInfoPlate(
            ref SlotVisual vis,
            PlanetaryDefenseSlotElement slot,
            PlanetaryDefenseConfig config,
            int maxTurretLevel,
            float padWorldRadius,
            float3 planetDisplay,
            float3 slotWorld)
        {
            // planetDisplay / slotWorld kept in the signature for call-site symmetry; placement
            // is pad-local (screen-below), not planet-radial.
            _ = planetDisplay;
            _ = slotWorld;

            // --- Strip legacy per-pad WorldSpace Take Control canvases (FPS bomb) ---
            // [TITAN-ORBIT] Style v5 parented a Canvas+GraphicRaycaster under every SlotRoot.
            // Dozens of GraphicRaycasters made EventSystem crawl (~6 FPS). Always strip —
            // do not wait for InfoStyleVersion rebuild (already-v6 pads can still hold orphans).
            if (vis.SlotRoot != null)
            {
                Transform legacy = vis.SlotRoot.Find("TakeControlButton");
                if (legacy != null)
                    Destroy(legacy.gameObject);
            }

            // --- Rebuild plate when style/hierarchy changes (adds gem icon, fixes orientation) ---
            // Do not key off GemIcon.sprite — Editor-only load can be null without looping Destroy.
            if (vis.InfoRoot == null || vis.StyleVersion != InfoStyleVersion)
            {
                if (vis.InfoRoot != null)
                {
                    Destroy(vis.InfoRoot.gameObject);
                    vis.InfoRoot = null;
                    vis.LevelText = null;
                    vis.CostText = null;
                    vis.GemIcon = null;
                }

                CreateInfoPlate(ref vis);
                // New hierarchy — force a full paint + layout on this frame.
                vis.CachedLevelText = null;
                vis.CachedCostText = null;
                vis.PlateLayoutSeeded = false;
            }

            if (vis.InfoRoot == null)
                return;

            // --- Resolve desired copy (no TMP writes yet) ---
            // Empty pad → placeholder title (not “Lv 0”). Built pads → “Lv N”.
            // Crown rung shows as Lv 7 (Solfeggio 963) once unlocked + built.
            string levelText = slot.TurretLevel <= 0
                ? EmptyPadPlaceholder
                : slot.TurretLevel >= PlanetaryDefenseMath.CrownTurretLevel
                    ? "Lv 7"
                    : ResolveLevelLabel(slot.TurretLevel);

            bool atCap = slot.TurretLevel >= maxTurretLevel && slot.TurretLevel > 0;
            int costCurrent = 0;
            int costMax = 1;
            string costText;
            if (atCap)
            {
                // At Lv6 with crown locked → MAX until the moon is full again.
                costText = "MAX";
            }
            else
            {
                float cost = config.GetGemsToNextLevel(slot.TurretLevel);
                costCurrent = Mathf.FloorToInt(math.max(0f, slot.BuildProgress));
                costMax = Mathf.Max(1, Mathf.CeilToInt(cost));
                // Reuse cached string when digits unchanged (deposit ticks only change Floor progress).
                if (vis.CachedCostText != null
                    && !vis.CachedAtCap
                    && vis.CachedCostCurrent == costCurrent
                    && vis.CachedCostMax == costMax)
                {
                    costText = vis.CachedCostText;
                }
                else
                {
                    costText = costCurrent + " / " + costMax;
                }
            }

            bool levelChanged = vis.CachedLevelText != levelText;
            bool costChanged = vis.CachedCostText != costText || vis.CachedAtCap != atCap;
            bool needsLayout = !vis.PlateLayoutSeeded || levelChanged || costChanged;

            // --- Paint TMP only when copy changed ---
            if (vis.LevelText != null && levelChanged)
            {
                vis.LevelText.fontSize = LevelFontSize;
                vis.LevelText.fontStyle = FontStyles.Bold;
                vis.LevelText.color = Color.white;
                vis.LevelText.text = levelText;
                vis.CachedLevelText = levelText;
            }

            if (vis.CostText != null && costChanged)
            {
                vis.CostText.fontSize = CostFontSize;
                vis.CostText.fontStyle = FontStyles.Normal;
                // Match moon gem current color family for the digits next to the icon.
                vis.CostText.color = new Color(GemIconColor.r, GemIconColor.g, GemIconColor.b, CostLineAlpha);
                vis.CostText.text = costText;
                vis.CachedCostText = costText;
                vis.CachedCostCurrent = costCurrent;
                vis.CachedCostMax = costMax;
                vis.CachedAtCap = atCap;
            }

            if (vis.GemIcon != null && (needsLayout || costChanged))
            {
                // Hide the gem icon on the MAX line — no deposit target left.
                bool gemOn = !atCap && WorldStatLabelIcons.Gem != null;
                if (vis.GemIcon.enabled != gemOn)
                    vis.GemIcon.enabled = gemOn;
                if (gemOn)
                    vis.GemIcon.color = GemIconColor;
            }

            // --- Orientation once (cheap); layout + ForceMeshUpdate only when copy dirty ---
            float s = InfoTextWorldScale;
            vis.InfoRoot.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            vis.InfoRoot.localScale = new Vector3(s, -s, s);

            if (needsLayout)
            {
                LayoutInfoLines(ref vis);
                vis.CachedHalfStackLocal = GetInfoStackHalfHeightLocal(vis);
                vis.PlateLayoutSeeded = true;
            }

            // Screen-below the pad: offset on world −Z from the slot center (not radial-out).
            // Skip SetLocalPosition when pad radius + stack height are unchanged.
            if (!vis.PlateLayoutSeeded
                || needsLayout
                || !Mathf.Approximately(vis.CachedPadRadiusForPos, padWorldRadius))
            {
                float halfStackWorld = vis.CachedHalfStackLocal * InfoTextWorldScale;
                float belowDist = padWorldRadius + InfoGapPastRimWorld + halfStackWorld;
                vis.InfoRoot.localPosition = new Vector3(0f, InfoAbovePadWorld, -belowDist);
                vis.CachedPadRadiusForPos = padWorldRadius;
            }
        }

        /// <summary>
        /// Interned-style level labels for common turret levels (avoids <c>"Lv " + n</c> every paint).
        /// </summary>
        /// <param name="turretLevel">1–6 (caller handles 0 / crown).</param>
        static string ResolveLevelLabel(int turretLevel)
        {
            switch (turretLevel)
            {
                case 1: return "Lv 1";
                case 2: return "Lv 2";
                case 3: return "Lv 3";
                case 4: return "Lv 4";
                case 5: return "Lv 5";
                case 6: return "Lv 6";
                default: return "Lv " + turretLevel;
            }
        }

        /// <summary>
        /// Eases turret yaw toward <paramref name="aimFlat"/>, then applies ship-style bank roll
        /// from yaw rate. Writes the composite pose onto <see cref="SlotVisual.TurretInstance"/>.
        /// Cosmetic only — does not drive sim; <paramref name="aimFlat"/> should already be the
        /// lead direction from <see cref="PlanetaryDefenseAimMath"/> (same helper as server fire).
        /// </summary>
        /// <param name="vis">Slot visual (yaw/bank state mutated in place).</param>
        /// <param name="aimFlat">Desired flat facing (XZ); ignored when near-zero length.</param>
        /// <param name="dt">Frame delta for slerp / bank smoothing.</param>
        static void ApplyTurretAimAndBank(ref SlotVisual vis, Vector3 aimFlat, float dt)
        {
            if (vis.TurretInstance == null)
                return;

            dt = math.max(1e-5f, dt);

            // --- Desired yaw (flat LookRotation) ---
            Quaternion wantYaw = vis.TurretYawInitialized
                ? vis.TurretYawRotation
                : Quaternion.identity;
            if (aimFlat.sqrMagnitude > 0.0001f)
                wantYaw = Quaternion.LookRotation(aimFlat.normalized, Vector3.up);

            // Seed on first active frame so we do not inherit an identity→want snap spike.
            if (!vis.TurretYawInitialized)
            {
                vis.TurretYawRotation = wantYaw;
                vis.TurretYawInitialized = true;
                vis.BankPrevYawDeg = GetPlanarYawDegrees(wantYaw);
                vis.BankYawInitialized = true;
                vis.BankYawRateDegPerSec = 0f;
                vis.BankCurrentAngle = 0f;
                vis.TurretInstance.transform.rotation = wantYaw;
                return;
            }

            // --- Ease yaw only (no roll in this quaternion) ---
            // [TITAN-ORBIT] Same AimTurnSpeed as before — bank is layered after this slerp.
            float yawT = 1f - math.exp(-AimTurnSpeed * dt);
            vis.TurretYawRotation = Quaternion.Slerp(vis.TurretYawRotation, wantYaw, yawT);

            // --- Sample / smooth yaw rate (°/s) ---
            float yawDeg = GetPlanarYawDegrees(vis.TurretYawRotation);
            if (!vis.BankYawInitialized)
            {
                vis.BankPrevYawDeg = yawDeg;
                vis.BankYawInitialized = true;
                vis.BankYawRateDegPerSec = 0f;
            }
            else
            {
                float instantRate = Mathf.DeltaAngle(vis.BankPrevYawDeg, yawDeg) / dt;
                vis.BankPrevYawDeg = yawDeg;

                // Same exponential catch-up ships use for yaw-rate sampling.
                float smoothing = ShipBankVisualSettingsCache.BankSmoothing;
                float velT = 1f - math.exp(-smoothing * dt);
                vis.BankYawRateDegPerSec = math.lerp(vis.BankYawRateDegPerSec, instantRate, velT);
            }

            // --- Target bank from turn rate (shared ship curve + Inspector knobs) ---
            float signedRate = vis.BankYawRateDegPerSec;
            if (math.abs(signedRate) < IdleBankAngularVelDeadbandDegPerSec)
                signedRate = 0f;

            float maxBank = ShipBankVisualSettingsCache.MaxBankAngleDegrees;
            float sensitivity = ShipBankVisualSettingsCache.BankSensitivity;
            float maxTurnDegPerSec =
                ShipPropulsionAggregation.GetGlobalMaxTurnSpeedDegreesPerSecond();
            float targetBank = ShipPropulsionAggregation.ComputeVisualBankTargetAngle(
                signedRate, maxBank, maxTurnDegPerSec, sensitivity);

            float bankT = 1f - math.exp(-ShipBankVisualSettingsCache.BankSmoothing * dt);
            vis.BankCurrentAngle = math.lerp(vis.BankCurrentAngle, targetBank, bankT);

            // --- Composite pose: yaw * local Z roll (same sign as ShipBankVisualApplier) ---
            vis.TurretInstance.transform.rotation =
                vis.TurretYawRotation * Quaternion.Euler(0f, 0f, -vis.BankCurrentAngle);
        }

        /// <summary>
        /// Clears yaw/bank filters so the next active turret starts flat instead of inheriting
        /// the previous gun's lean.
        /// </summary>
        static void ResetTurretBankState(ref SlotVisual vis)
        {
            vis.TurretYawInitialized = false;
            vis.TurretYawRotation = Quaternion.identity;
            vis.BankYawInitialized = false;
            vis.BankPrevYawDeg = 0f;
            vis.BankYawRateDegPerSec = 0f;
            vis.BankCurrentAngle = 0f;
        }

        /// <summary>
        /// Planar yaw (degrees) from a world rotation — ignores pitch/roll so bank tracks turn only.
        /// Same helper idea as <see cref="ShipBankVisualApplier"/>.
        /// </summary>
        static float GetPlanarYawDegrees(Quaternion rotation)
        {
            Vector3 fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f)
                return 0f;
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Stacks Level above the gem-cost row; cost row is <c>[icon] digits</c> centered as one unit.
        /// </summary>
        static void LayoutInfoLines(ref SlotVisual vis)
        {
            if (vis.LevelText == null || vis.CostText == null)
                return;

            // --- Icon size vs cost font (same ratio family as moon gem row) ---
            if (vis.GemIcon != null && vis.GemIcon.enabled && vis.GemIcon.sprite != null)
            {
                float iconHeight = CostFontSize * IconHeightOverFontSize;
                float spriteHeight = Mathf.Max(0.001f, vis.GemIcon.sprite.bounds.size.y);
                vis.GemIcon.transform.localScale = Vector3.one * (iconHeight / spriteHeight);
            }

            vis.LevelText.ForceMeshUpdate();
            vis.CostText.ForceMeshUpdate();

            float levelH = Mathf.Max(0.01f, vis.LevelText.preferredHeight);
            float costH = Mathf.Max(0.01f, vis.CostText.preferredHeight);
            float costW = Mathf.Max(0.01f, vis.CostText.preferredWidth);

            float iconW = 0f;
            if (vis.GemIcon != null && vis.GemIcon.enabled && vis.GemIcon.sprite != null)
                iconW = vis.GemIcon.transform.localScale.x * vis.GemIcon.sprite.bounds.size.x;

            float gap = iconW > 0.001f ? IconGapLocal : 0f;
            float costRowW = iconW + gap + costW;
            float costRowLeft = -costRowW * 0.5f;
            float textCenterX = costRowLeft + iconW + gap + costW * 0.5f;

            float total = levelH + InfoLineGapLocal + costH;
            float top = total * 0.5f;

            // Level on top (toward the pad when plate sits on −Z); cost row underneath.
            vis.LevelText.transform.localPosition = new Vector3(0f, top - levelH * 0.5f, 0f);
            float costY = top - levelH - InfoLineGapLocal - costH * 0.5f;
            vis.CostText.transform.localPosition = new Vector3(textCenterX, costY, 0f);

            if (vis.GemIcon != null && vis.GemIcon.enabled && vis.GemIcon.sprite != null)
            {
                vis.GemIcon.transform.localPosition = new Vector3(
                    costRowLeft + iconW * 0.5f,
                    costY,
                    0f);
            }
        }

        /// <summary>Half-height of the Level + cost stack in InfoRoot local units (before world scale).</summary>
        static float GetInfoStackHalfHeightLocal(in SlotVisual vis)
        {
            if (vis.LevelText == null || vis.CostText == null)
                return 1.2f;

            float levelH = Mathf.Max(0.01f, vis.LevelText.preferredHeight);
            float costH = Mathf.Max(0.01f, vis.CostText.preferredHeight);
            return (levelH + InfoLineGapLocal + costH) * 0.5f;
        }

        PlanetDefenseGroup CreateGroup(int planetId)
        {
            var hubGo = new GameObject($"PlanetaryDefense_{planetId}");
            return new PlanetDefenseGroup
            {
                PlanetId = planetId,
                Hub = hubGo.transform,
            };
        }

        void RebuildSlots(
            PlanetDefenseGroup group,
            DynamicBuffer<PlanetaryDefenseSlotElement> buffer,
            PlanetaryDefenseConfig config)
        {
            for (int i = 0; i < group.Slots.Count; i++)
            {
                var s = group.Slots[i];
                if (s.SlotRoot != null)
                    Destroy(s.SlotRoot.gameObject);
            }

            group.Slots.Clear();

            GameObject turretPrefab = config.visualPrefab != null
                ? config.visualPrefab
                : _defaultConfig != null ? _defaultConfig.visualPrefab : null;
            if (turretPrefab == null)
                turretPrefab = Resources.Load<GameObject>("FighterDrone");

            for (int i = 0; i < buffer.Length; i++)
            {
                var slot = buffer[i];
                var vis = new SlotVisual
                {
                    SlotIndex = i,
                    TurretLevel = slot.TurretLevel,
                };

                var rootGo = new GameObject($"DefenseSlot_{i}");
                rootGo.transform.SetParent(group.Hub, false);
                vis.SlotRoot = rootGo.transform;

                // Soft blue faded disc — same tint as orbit ring / moon orbit zone.
                vis.ZoneVisual = PlanetaryDefensePadZoneVisual.EnsureOnSlotRoot(vis.SlotRoot);

                CreateInfoPlate(ref vis);
                CreateHealthBar(ref vis);

                if (turretPrefab != null)
                {
                    vis.TurretInstance = Instantiate(turretPrefab, vis.SlotRoot);
                    vis.TurretInstance.name = "Turret";
                    var cols = vis.TurretInstance.GetComponentsInChildren<Collider>(true);
                    for (int c = 0; c < cols.Length; c++)
                        Destroy(cols[c]);
                    // LateUpdate owns world size from pad radius — reset authored scale.
                    vis.TurretInstance.transform.localScale = Vector3.one;
                    vis.TurretInstance.SetActive(slot.TurretLevel > 0);
                    // AppliedTeam stays None until the first LateUpdate paints ownership skin.
                    vis.AppliedTeam = TeamId.None;
                }

                group.Slots.Add(vis);
            }
        }

        /// <summary>
        /// Paints every mesh renderer on the turret with the GenericSpaceships team material
        /// used by people transports and attack/mining/shield drones.
        /// </summary>
        /// <param name="turretRoot">Instantiated turret prefab root.</param>
        /// <param name="team">Planet ownership team (skin source).</param>
        void ApplyTurretTeamMaterials(GameObject turretRoot, TeamId team)
        {
            if (turretRoot == null || team == TeamId.None)
                return;

            Material material = ResolveTeamMaterial(team);
            if (material == null)
                return;

            // --- Swap sharedMaterials on all mesh / skinned renderers ---
            // [HYBRID] Cosmetic only — same pack mats as PeopleTransportVisualApplier.
            var renderers = turretRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    continue;

                Material[] current = renderer.sharedMaterials;
                if (current == null || current.Length == 0)
                {
                    renderer.sharedMaterial = material;
                    continue;
                }

                var replaced = new Material[current.Length];
                for (int s = 0; s < current.Length; s++)
                    replaced[s] = material;
                renderer.sharedMaterials = replaced;
            }
        }

        /// <summary>
        /// Resolves the GenericSpaceships1-8 material for <paramref name="team"/> from
        /// <see cref="PeopleTransportTeamMaterials"/> (Resources), with a tinted Unlit fallback.
        /// </summary>
        Material ResolveTeamMaterial(TeamId team)
        {
            EnsureTeamMaterialsCatalog();
            if (_teamMaterials != null)
            {
                Material fromCatalog = _teamMaterials.GetMaterialForTeam(team);
                if (fromCatalog != null)
                    return fromCatalog;
            }

            // --- Fallback: solid team colour if the catalog asset is missing from the build ---
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            var mat = new Material(shader);
            Color color = team.ToColor();
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            mat.color = color;
            return mat;
        }

        /// <summary>
        /// Loads <c>Resources/PeopleTransportTeamMaterials</c> once — same asset drones/transports use.
        /// </summary>
        void EnsureTeamMaterialsCatalog()
        {
            if (_teamMaterials != null)
                return;

            _teamMaterials = Resources.Load<PeopleTransportTeamMaterials>(
                PeopleTransportTeamMaterials.ResourcesPath);

#if UNITY_EDITOR
            if (_teamMaterials == null)
            {
                _teamMaterials = UnityEditor.AssetDatabase.LoadAssetAtPath<PeopleTransportTeamMaterials>(
                    "Assets/Resources/PeopleTransportTeamMaterials.asset");
            }
#endif
        }

        /// <summary>
        /// Builds a thin horizontal HP track under the turret: light outline (max HP frame) +
        /// dark background track + fill that shrinks from the left as health drops.
        /// Hidden until a turret is active.
        /// </summary>
        void CreateHealthBar(ref SlotVisual vis)
        {
            if (vis.SlotRoot == null)
                return;

            // Flat on the flight plane (same −90° as planet/moon labels) so top-down cameras read it.
            var rootGo = new GameObject("HealthBar");
            rootGo.transform.SetParent(vis.SlotRoot, false);
            rootGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            // Placeholder pose — UpdateHealthBar places it past the live turret footprint each frame.
            rootGo.transform.localPosition = new Vector3(0f, HealthBarAbovePadWorld, -1f);
            rootGo.transform.localScale = Vector3.one;
            vis.HealthBarRoot = rootGo.transform;

            Sprite sprite = GetOrCreateHealthBarSprite();

            // --- Outline (max-HP frame) ---
            // Drawn first / lowest sorting so the dark track + fill sit inside a visible rim.
            // Without this, a near-full green bar blends into the map and max HP is hard to read.
            var outlineGo = new GameObject("Outline");
            outlineGo.transform.SetParent(rootGo.transform, false);
            outlineGo.transform.localPosition = Vector3.zero;
            outlineGo.transform.localScale = new Vector3(
                HealthBarWidthMax + HealthBarOutlinePad * 2f,
                HealthBarHeight + HealthBarOutlinePad * 2f,
                1f);
            var outlineRenderer = outlineGo.AddComponent<SpriteRenderer>();
            outlineRenderer.sprite = sprite;
            outlineRenderer.color = HealthBarOutlineColor;
            outlineRenderer.sortingOrder = HealthBarSortingOrder;

            // --- Background track (empty / missing HP) ---
            var bgGo = new GameObject("Bg");
            bgGo.transform.SetParent(rootGo.transform, false);
            bgGo.transform.localPosition = Vector3.zero;
            bgGo.transform.localScale = new Vector3(HealthBarWidthMax, HealthBarHeight, 1f);
            var bgRenderer = bgGo.AddComponent<SpriteRenderer>();
            bgRenderer.sprite = sprite;
            bgRenderer.color = HealthBarBgColor;
            bgRenderer.sortingOrder = HealthBarSortingOrder + 1;

            // --- Fill (left-anchored: scale.x + centered offset so it drains toward the left) ---
            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(rootGo.transform, false);
            fillGo.transform.localPosition = Vector3.zero;
            fillGo.transform.localScale = new Vector3(HealthBarWidthMax, HealthBarHeight, 1f);
            var fillRenderer = fillGo.AddComponent<SpriteRenderer>();
            fillRenderer.sprite = sprite;
            fillRenderer.color = HealthBarFillFull;
            fillRenderer.sortingOrder = HealthBarSortingOrder + 2;

            vis.HealthBarFill = fillGo.transform;
            vis.HealthBarFillRenderer = fillRenderer;

            // Empty pads start with no turret — hide until UpdateHealthBar sees TurretLevel > 0.
            rootGo.SetActive(false);
        }

        /// <summary>
        /// Shows/hides the HP bar and sets fill width + color from ghosted Health / MaxHealth,
        /// optionally punched by HitRpc optimistic Health-after (planet ghost MaxSendRate lag).
        /// Places the strip just past the turret mesh toward screen-below so it does not cut
        /// through the gun. Outline + bg always span full max HP; fill shrinks with current HP.
        /// </summary>
        /// <param name="turretWorldScale">
        /// Live turret <c>localScale.x</c> (0 when inactive) — drives bar offset and width.
        /// </param>
        /// <param name="planetId">Stable planet id for optimistic HitRpc lookup.</param>
        /// <param name="slotIndex">Slot index for optimistic HitRpc lookup.</param>
        void UpdateHealthBar(
            ref SlotVisual vis,
            PlanetaryDefenseSlotElement slot,
            float turretWorldScale,
            int planetId,
            int slotIndex)
        {
            if (vis.HealthBarRoot == null)
                return;

            // --- Resolve display HP (ghost vs HitRpc optimistic punch) ---
            // [NETCODE] Ghost Health is truth long-term; HitRpc Health-after is truth for the
            // short window after a confirmed server hit (same idea as asteroid AsteroidHealthAfter).
            float displayHealth = slot.Health;
            float maxHealth = slot.MaxHealth;
            bool showFromGhost = slot.TurretLevel > 0 && maxHealth > 0.01f;
            bool hitFlash = false;
            float flashT = 0f; // 1 = flash peak, 0 = flash done
            bool optimisticDestroyed = false;

            long optKey = MakeOptimisticKey(planetId, slotIndex);
            if (_optimisticHpBySlot.TryGetValue(optKey, out var opt))
            {
                float now = Time.time;
                bool expired = now >= opt.ExpireAt;
                // Ghost caught up (equal/lower) → drop override so regen / later snapshots win.
                bool ghostCaughtUp = showFromGhost &&
                    slot.Health <= opt.HealthAfter + OptimisticHpReconcileEpsilon;
                // Ghost already shows empty/destroyed while we still hold a destroy punch.
                bool ghostEmpty = !showFromGhost && opt.HealthAfter <= 0.01f;

                if (expired || ghostCaughtUp || ghostEmpty)
                {
                    _optimisticHpBySlot.Remove(optKey);
                }
                else
                {
                    displayHealth = opt.HealthAfter;
                    optimisticDestroyed = opt.HealthAfter <= 0.01f;
                    if (now < opt.FlashUntil)
                    {
                        hitFlash = true;
                        flashT = math.saturate(
                            (opt.FlashUntil - now) / math.max(0.01f, HitFlashSeconds));
                    }
                }
            }

            // Show bar while the turret is alive on the ghost, or while we still punch a
            // destroy-to-empty transition (bar drains to 0 before the mesh hides).
            bool show = showFromGhost || (optimisticDestroyed && maxHealth > 0.01f);
            if (vis.HealthBarRoot.gameObject.activeSelf != show)
                vis.HealthBarRoot.gameObject.SetActive(show);

            // --- Hit scale punch on the turret mesh (cosmetic only) ---
            // [HYBRID] Applied after aim sizing wrote localScale — multiplies the base world scale.
            if (vis.TurretInstance != null && vis.TurretInstance.activeSelf && hitFlash)
            {
                float punch = math.lerp(1f, HitPunchScalePeak, flashT);
                float punched = math.max(0.01f, turretWorldScale) * punch;
                vis.TurretInstance.transform.localScale = Vector3.one * punched;
                turretWorldScale = punched;
            }

            if (!show)
                return;

            // Hot-swap sprite if this bar still holds the old microscopic PPU-100 asset.
            Sprite sprite = GetOrCreateHealthBarSprite();
            if (vis.HealthBarFillRenderer != null && vis.HealthBarFillRenderer.sprite != sprite)
                vis.HealthBarFillRenderer.sprite = sprite;

            // Size the bar to the gun: a bit wider than the mesh, not pad-wide.
            float extent = math.max(MinTurretWorldScale, turretWorldScale) * TurretMeshExtentOverScale;
            float barWidth = math.clamp(extent * 2.4f, HealthBarWidthMin, HealthBarWidthMax);
            // Push well past the footprint so the strip sits clearly below the turret
            // (between the gun and the info plate), not tucked under the mesh.
            float barZ = -(extent + HealthBarClearancePastTurret);

            // --- Outline = full max-HP frame (slightly larger than the track) ---
            // Lazy-add if this bar was built before the outline layer existed (Play Mode hot reload).
            var outline = vis.HealthBarRoot.Find("Outline");
            if (outline == null)
            {
                var outlineGo = new GameObject("Outline");
                outlineGo.transform.SetParent(vis.HealthBarRoot, false);
                outlineGo.transform.localPosition = Vector3.zero;
                var outlineSr = outlineGo.AddComponent<SpriteRenderer>();
                outlineSr.sprite = sprite;
                outlineSr.color = HealthBarOutlineColor;
                outlineSr.sortingOrder = HealthBarSortingOrder;
                outline = outlineGo.transform;

                // Keep bg / fill above the new rim.
                var existingBg = vis.HealthBarRoot.Find("Bg");
                if (existingBg != null)
                {
                    var bgSr = existingBg.GetComponent<SpriteRenderer>();
                    if (bgSr != null)
                        bgSr.sortingOrder = HealthBarSortingOrder + 1;
                }

                if (vis.HealthBarFillRenderer != null)
                    vis.HealthBarFillRenderer.sortingOrder = HealthBarSortingOrder + 2;
            }

            if (outline != null)
            {
                var outlineRenderer = outline.GetComponent<SpriteRenderer>();
                if (outlineRenderer != null)
                {
                    if (outlineRenderer.sprite != sprite)
                        outlineRenderer.sprite = sprite;
                    outlineRenderer.color = HealthBarOutlineColor;
                }

                outline.localScale = new Vector3(
                    barWidth + HealthBarOutlinePad * 2f,
                    HealthBarHeight + HealthBarOutlinePad * 2f,
                    1f);
            }

            // --- Bg = empty track (always full width = max HP) ---
            var bg = vis.HealthBarRoot.Find("Bg");
            if (bg != null)
            {
                var bgRenderer = bg.GetComponent<SpriteRenderer>();
                if (bgRenderer != null)
                {
                    if (bgRenderer.sprite != sprite)
                        bgRenderer.sprite = sprite;
                    bgRenderer.color = HealthBarBgColor;
                }

                bg.localScale = new Vector3(barWidth, HealthBarHeight, 1f);
            }

            // Flat on XZ; −Z = screen-below on a typical top-down camera.
            vis.HealthBarRoot.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            vis.HealthBarRoot.localPosition = new Vector3(0f, HealthBarAbovePadWorld, barZ);

            float safeMax = math.max(0.01f, maxHealth);
            // When HitRpc says destroyed but ghost still shows an alive turret, drain to 0
            // against the ghost MaxHealth so the bar visibly empties before the mesh hides.
            float ratio = math.saturate(displayHealth / safeMax);
            float fillW = barWidth * ratio;

            // Left-anchored fill: left edge stays put, right edge moves with HP.
            if (vis.HealthBarFill != null)
            {
                vis.HealthBarFill.localScale = new Vector3(fillW, HealthBarHeight, 1f);
                vis.HealthBarFill.localPosition = new Vector3(
                    -barWidth * 0.5f + fillW * 0.5f,
                    0f,
                    0f);
            }

            if (vis.HealthBarFillRenderer != null)
            {
                // Bright green (high) / orange (mid) / red (low) — see HealthBarFillColor.
                Color fill = HealthBarFillColor(ratio);
                if (hitFlash)
                    fill = Color.Lerp(fill, HealthBarHitFlashColor, flashT);
                vis.HealthBarFillRenderer.color = fill;
            }
        }

        /// <summary>
        /// Maps current HP ratio (0 = empty, 1 = full) to a bright traffic-light fill color.
        /// High (≥2/3): orange → bright green. Mid (1/3–2/3): solid bright orange.
        /// Low (&lt;1/3): bright red → orange as HP climbs toward the mid band.
        /// Client presentation only — does not affect server combat.
        /// </summary>
        /// <param name="ratio">Saturated Health / MaxHealth in [0, 1].</param>
        /// <returns>Opaque-ish fill tint for the HP strip SpriteRenderer.</returns>
        static Color HealthBarFillColor(float ratio)
        {
            // --- High band: tip into green as the turret approaches full HP ---
            if (ratio >= HealthBarHighRatio)
            {
                float t = (ratio - HealthBarHighRatio) / (1f - HealthBarHighRatio);
                return Color.Lerp(HealthBarFillMid, HealthBarFillFull, t);
            }

            // --- Mid band: hold orange so the middle third stays readable (no muddy brown) ---
            if (ratio >= HealthBarLowRatio)
                return HealthBarFillMid;

            // --- Low band: red at empty, easing toward orange at the mid threshold ---
            return Color.Lerp(HealthBarFillEmpty, HealthBarFillMid, ratio / HealthBarLowRatio);
        }

        /// <summary>
        /// Lazy 1×1 white sprite shared by all defense HP bars.
        /// [UNITY] pixelsPerUnit must match the texture size so localScale = world size
        /// (PPU 100 on a 4×4 texture made every bar ~0.04 units — microscopic).
        /// </summary>
        static Sprite GetOrCreateHealthBarSprite()
        {
            // Replace the old microscopic PPU-100 cache if a prior Play Mode session left it.
            if (s_HealthBarSprite != null &&
                s_HealthBarSprite.name == "PlanetaryDefenseHealthBarSprite_v2")
                return s_HealthBarSprite;

            // 4×4 white tex / PPU 4 → sprite bounds are 1×1 world unit.
            s_HealthBarSprite = Sprite.Create(
                Texture2D.whiteTexture,
                new Rect(0f, 0f, 4f, 4f),
                new Vector2(0.5f, 0.5f),
                4f);
            s_HealthBarSprite.name = "PlanetaryDefenseHealthBarSprite_v2";
            return s_HealthBarSprite;
        }

        /// <summary>
        /// Two-line plate under each pad: title (Empty / Lv N) on top, gem icon + progress under.
        /// Pose / stack layout are refreshed every frame in <see cref="UpdateInfoPlate"/>.
        /// </summary>
        void CreateInfoPlate(ref SlotVisual vis)
        {
            if (vis.SlotRoot == null)
                return;

            var infoGo = new GameObject("InfoPlate");
            infoGo.transform.SetParent(vis.SlotRoot, false);
            // Same flat orientation as planet/moon labels — UpdateInfoPlate refreshes pose each frame.
            float s = InfoTextWorldScale;
            infoGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            infoGo.transform.localScale = new Vector3(s, -s, s);
            vis.InfoRoot = infoGo.transform;

            vis.LevelText = CreateLabelLine(infoGo.transform, "Level", EmptyPadPlaceholder, LevelFontSize, FontStyles.Bold);
            vis.CostText = CreateLabelLine(infoGo.transform, "Cost", "0 / 40", CostFontSize, FontStyles.Normal);

            // Same gem sprite + red tint as the moon gem row.
            var iconGo = new GameObject("GemIcon");
            iconGo.transform.SetParent(infoGo.transform, false);
            var iconRenderer = iconGo.AddComponent<SpriteRenderer>();
            iconRenderer.sprite = WorldStatLabelIcons.Gem;
            iconRenderer.color = GemIconColor;
            iconRenderer.sortingOrder = IconSortingOrder;
            iconRenderer.enabled = iconRenderer.sprite != null;
            vis.GemIcon = iconRenderer;

            vis.StyleVersion = InfoStyleVersion;
            LayoutInfoLines(ref vis);
        }

        /// <summary>Creates one centered TMP line with outline material for world readability.</summary>
        TextMeshPro CreateLabelLine(
            Transform parent,
            string name,
            string text,
            float fontSize,
            FontStyles style)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = _font != null ? _font : ResolveFont();
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
            tmp.enableWordWrapping = false;
            tmp.richText = false;
            tmp.color = Color.white;
            tmp.text = text;
            // Extra character spacing keeps “12 / 40” from looking cramped at world scale.
            tmp.characterSpacing = 2f;
            tmp.rectTransform.sizeDelta = new Vector2(18f, 3.2f);
            ApplyReadableTextMaterial(tmp);
            return tmp;
        }

        /// <summary>
        /// Soft white halo outline (same family as planet/moon labels) so glyphs stay
        /// readable over the blue pad fill and busy planet textures.
        /// </summary>
        static void ApplyReadableTextMaterial(TMP_Text text)
        {
            if (text == null)
                return;

            Material mat = text.fontMaterial;
            if (mat == null)
                return;

            mat.EnableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(1f, 1f, 1f, 0.9f));
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", OutlineWidth);
            if (mat.HasProperty("_OutlineSoftness"))
                mat.SetFloat("_OutlineSoftness", 0.05f);
            if (mat.HasProperty("_FaceDilate"))
                mat.SetFloat("_FaceDilate", FaceDilate);
            mat.renderQueue = RenderQueueOverlay;

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder;
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

        static int ComputeFingerprint(DynamicBuffer<PlanetaryDefenseSlotElement> buffer)
        {
            unchecked
            {
                int hash = buffer.Length * 397;
                for (int i = 0; i < buffer.Length; i++)
                    hash = (hash * 31) + buffer[i].TurretLevel;
                return hash;
            }
        }

        /// <summary>
        /// Reads ghosted <see cref="ShipInput.AimPlanarDir"/> from the ship owned by
        /// <paramref name="networkId"/> (player currently occupying a defense pad).
        /// Skipped during <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>.
        /// Uses a cached EntityQuery — never allocates a new query per occupied pad per frame.
        /// </summary>
        bool TryGetOccupantAimFlat(EntityManager em, int networkId, out Vector3 aimFlat)
        {
            aimFlat = Vector3.zero;
            if (networkId <= 0 || ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            // --- Ensure cached ship-aim query ---
            // [ECS/DOTS] CreateEntityQuery every LateUpdate (× occupied pads) destroyed FPS (~6).
            if (!_occupantAimQueryCreated)
            {
                _occupantAimQuery = em.CreateEntityQuery(
                    ComponentType.ReadOnly<ShipTag>(),
                    ComponentType.ReadOnly<GhostOwner>(),
                    ComponentType.ReadOnly<ShipInput>());
                _occupantAimQueryCreated = true;
            }

            using var owners = _occupantAimQuery.ToComponentDataArray<GhostOwner>(
                Unity.Collections.Allocator.Temp);
            using var inputs = _occupantAimQuery.ToComponentDataArray<ShipInput>(
                Unity.Collections.Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                float2 dir = inputs[i].AimPlanarDir;
                if (math.lengthsq(dir) < 0.0001f)
                    return false;
                aimFlat = new Vector3(dir.x, 0f, dir.y);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Nearest enemy ship or people-transport VFX flight within
        /// <paramref name="engageRange"/> of the turret pad (<paramref name="muzzleDisplay"/>),
        /// for cosmetic lead aim only. Returns planar velocity so
        /// <see cref="PlanetaryDefenseAimMath"/> can match server fire direction.
        /// <para>
        /// Ships: presentation cache + ghosted <see cref="ShipKinematics"/> (dictionary walk —
        /// already gated by <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> at
        /// the call site). Transports: <see cref="PeopleTransportVfxDriver.CopyAimFlights"/>
        /// (no ECS transport archetype gather).
        /// </para>
        /// </summary>
        /// <param name="em">Client world entity manager.</param>
        /// <param name="ownerTeam">Planet ownership — skip friendlies.</param>
        /// <param name="muzzleDisplay">Pad / turret display position.</param>
        /// <param name="engageRange">Absolute world engage range (same as server).</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="targetPos">Nearest hostile display position.</param>
        /// <param name="targetVel">Planar velocity for lead math.</param>
        /// <returns>True when a hostile is inside engage range.</returns>
        bool TryFindNearestHostileDisplay(
            EntityManager em,
            TeamId ownerTeam,
            float3 muzzleDisplay,
            float engageRange,
            float mapW,
            float mapH,
            out float3 targetPos,
            out float3 targetVel)
        {
            targetPos = default;
            targetVel = float3.zero;
            float bestDistSq = engageRange * engageRange;
            bool found = false;

            // --- Enemy ships (presentation pose + kinematics velocity) ---
            GhostPresentationTransformCache.CopyShipEntities(_shipEntitiesScratch);
            for (int i = 0; i < _shipEntitiesScratch.Count; i++)
            {
                Entity shipEntity = _shipEntitiesScratch[i];
                if (!em.Exists(shipEntity) || !em.HasComponent<ShipState>(shipEntity))
                    continue;

                var ship = em.GetComponentData<ShipState>(shipEntity);
                if (ship.IsDead || ship.Team == TeamId.None || ship.Team == ownerTeam)
                    continue;

                if (!GhostPresentationTransformCache.TryGetShip(shipEntity, out var snap))
                    continue;

                float3 pos = snap.Position;
                pos.y = PlanetaryDefenseMath.FixedY;
                float3 d = ToroidalMapEcs.ShortestOffsetXZ(muzzleDisplay, pos, mapW, mapH);
                float distSq = math.lengthsq(new float3(d.x, 0f, d.z));
                if (distSq > bestDistSq)
                    continue;

                bestDistSq = distSq;
                targetPos = pos;
                // [NETCODE] Ghosted kinematics — same field the server combat system reads.
                targetVel = float3.zero;
                if (em.HasComponent<ShipKinematics>(shipEntity))
                {
                    float3 vel = em.GetComponentData<ShipKinematics>(shipEntity).Velocity;
                    vel.y = 0f;
                    targetVel = vel;
                }

                found = true;
            }

            // --- Enemy people transports (VFX flights — descending / landing pods) ---
            // [HYBRID] No PeopleTransportTag ToEntityArray — transports are not ghosts on client.
            var vfx = PeopleTransportVfxDriver.Active;
            if (vfx != null)
            {
                vfx.CopyAimFlights(_transportAimScratch);
                for (int i = 0; i < _transportAimScratch.Count; i++)
                {
                    var sample = _transportAimScratch[i];
                    var team = (TeamId)sample.Team;
                    if (team == TeamId.None || team == ownerTeam)
                        continue;

                    float3 pos = sample.DisplayPos;
                    pos.y = PlanetaryDefenseMath.FixedY;
                    float3 d = ToroidalMapEcs.ShortestOffsetXZ(muzzleDisplay, pos, mapW, mapH);
                    float distSq = math.lengthsq(new float3(d.x, 0f, d.z));
                    if (distSq > bestDistSq)
                        continue;

                    bestDistSq = distSq;
                    targetPos = pos;
                    targetVel = sample.Velocity;
                    targetVel.y = 0f;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Resolves torus map size for aim math. Prefers session meta (no ECS query alloc),
        /// then a cached singleton query — never <c>CreateEntityQuery</c> every LateUpdate.
        /// </summary>
        bool TryResolveMapSize(EntityManager em, out float mapW, out float mapH)
        {
            mapW = 0f;
            mapH = 0f;

            // --- Fast path: join meta already published ---
            if (MapSessionMetaCache.HasMapSize
                && ToroidalMapEcs.IsValidMapSize(MapSessionMetaCache.MapWidth, MapSessionMetaCache.MapHeight))
            {
                mapW = MapSessionMetaCache.MapWidth;
                mapH = MapSessionMetaCache.MapHeight;
                return true;
            }

            // --- Cached singleton query (created once) ---
            // [ECS/DOTS] CreateEntityQuery every frame was a GC tax on the PD LateUpdate path.
            if (!_mapQueryCreated)
            {
                _mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
                _mapQueryCreated = true;
            }

            if (_mapQuery.TryGetSingleton<MapStateSingleton>(out var map) &&
                ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Destroys every pad hub and clears HitRpc optimistic HP (session leave / no proxies).
        /// </summary>
        void ClearAllGroups()
        {
            foreach (var kv in _groupsByPlanetId)
                DestroyGroup(kv.Value);
            _groupsByPlanetId.Clear();
            // [TITAN-ORBIT] Drop stale punches so a new join cannot flash old planet ids.
            _optimisticHpBySlot.Clear();
        }

        static void DestroyGroup(PlanetDefenseGroup group)
        {
            if (group == null)
                return;
            for (int i = 0; i < group.Slots.Count; i++)
            {
                var s = group.Slots[i];
                if (s.SlotRoot != null)
                    Destroy(s.SlotRoot.gameObject);
            }

            group.Slots.Clear();
            if (group.Hub != null)
                Destroy(group.Hub.gameObject);
        }
    }
}
