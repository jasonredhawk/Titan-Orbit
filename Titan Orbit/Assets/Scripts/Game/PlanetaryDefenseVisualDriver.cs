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
    /// Empty pads show placeholder copy instead of “Lv 0”. Parents to the unit-scale planet
    /// proxy root so pad/text/turret use true world sizes (no ÷ planetScale).
    /// See <see cref="PlanetVisualBody"/>.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Walks <see cref="EcsWorldVisualizer"/> planet proxy keys only — never
    /// <c>ToEntityArray</c> / map-body archetype gathers (Windows late-join Crash!!! under
    /// session-long TransformQuarantine).
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66300)]
    public sealed class PlanetaryDefenseVisualDriver : MonoBehaviour
    {
        /// <summary>Cosmetic facing turn speed when tracking a hostile.</summary>
        const float AimTurnSpeed = 8f;

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
        /// Turret world size vs pad radius. FighterDrone is tiny at authored 0.25 — we size
        /// relative to the soft pad so it reads as a gun on the disc, not a second ship.
        /// </summary>
        const float TurretSizeVsPadRadius = 0.42f;

        /// <summary>Hard clamp so tiny/huge planets still get a readable but not enormous turret.</summary>
        const float MinTurretWorldScale = 0.35f;
        const float MaxTurretWorldScale = 0.7f;

        /// <summary>
        /// World-space uniform scale for TMP on the info plate (unit-scale planet root).
        /// Paired with larger font sizes below — sharper glyphs than a huge transform scale.
        /// </summary>
        const float InfoTextWorldScale = 0.42f;

        /// <summary>Bold level line (top of the stack, closer to the pad).</summary>
        const float LevelFontSize = 8.5f;

        /// <summary>Gem progress line under the level (slightly smaller / softer).</summary>
        const float CostFontSize = 6.25f;

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
        const byte InfoStyleVersion = 3;

        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        static PlanetaryDefenseVisualDriver s_Instance;

        PlanetShipFamilyConfig _familyConfig;
        PlanetaryDefenseConfig _defaultConfig;
        TMP_FontAsset _font;

        readonly Dictionary<int, PlanetDefenseGroup> _groupsByPlanetId =
            new Dictionary<int, PlanetDefenseGroup>(16);
        readonly List<int> _alivePlanetIds = new List<int>(16);
        readonly List<int> _removePlanetIds = new List<int>(16);
        readonly List<Entity> _planetEntitiesScratch = new List<Entity>(32);
        readonly List<Entity> _shipEntitiesScratch = new List<Entity>(32);

        /// <summary>One planet's pad + turret GameObjects.</summary>
        sealed class PlanetDefenseGroup
        {
            public int PlanetId;
            public Transform Hub;
            public readonly List<SlotVisual> Slots = new List<SlotVisual>(6);
            public int LayoutFingerprint = int.MinValue;
        }

        /// <summary>
        /// One slot: soft pad zone (Shapes) + turret in the center + info text below the rim.
        /// </summary>
        struct SlotVisual
        {
            public int SlotIndex;
            public byte TurretLevel;
            public Transform SlotRoot;
            public PlanetaryDefensePadZoneVisual ZoneVisual;
            public GameObject TurretInstance;
            public TextMeshPro LevelText;
            public TextMeshPro CostText;
            public SpriteRenderer GemIcon;
            public Transform InfoRoot;
            /// <summary>Matches <see cref="InfoStyleVersion"/> after outline / hierarchy are applied.</summary>
            public byte StyleVersion;
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

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
            ClearAllGroups();
        }

        void LateUpdate()
        {
            if (ClientJoinSettleCache.Settling)
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
                int maxTurretLevel = PlanetaryDefenseMath.GetMaxTurretLevelForPlanet(planet.PlanetLevel);

                // Soft disc ≈ deposit zone. Root is unit-scale — slot-local == world.
                float padWorldRadius = math.clamp(config.depositZoneRadius, 0.8f, 2.5f);

                bool hasTarget = false;
                float3 targetPos = default;
                if (canAimShips)
                {
                    hasTarget = TryFindNearestHostileDisplay(
                        em, visualizer, planet.Ownership, planetDisplay,
                        PlanetaryDefenseMath.GetEngageRangeFromPlanetCenter(
                            planetSize, planet.PlanetLevel, config.rangeBeyondOrbitOuter),
                        hasMap, mapW, mapH, out targetPos);
                }

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

                            // Modest size vs pad — level visualScale nudges slightly, stays clamped.
                            float scaleMul = math.clamp(
                                config.GetLevelStats(slot.TurretLevel).visualScale, 0.5f, 1.25f);
                            float worldScale = math.clamp(
                                padWorldRadius * TurretSizeVsPadRadius * scaleMul,
                                MinTurretWorldScale,
                                MaxTurretWorldScale);
                            vis.TurretInstance.transform.localScale = Vector3.one * worldScale;

                            // Rest pose = radially outward from planet center. When a hostile is
                            // in engage range, ease toward that aim instead.
                            Vector3 outwardFlat = new Vector3(
                                slotWorld.x - planetDisplay.x,
                                0f,
                                slotWorld.z - planetDisplay.z);
                            Vector3 aimFlat = outwardFlat;
                            if (hasTarget)
                            {
                                Vector3 from = vis.TurretInstance.transform.position;
                                Vector3 toHostile = new Vector3(targetPos.x, from.y, targetPos.z) - from;
                                toHostile.y = 0f;
                                if (toHostile.sqrMagnitude > 0.0001f)
                                    aimFlat = toHostile;
                            }

                            if (aimFlat.sqrMagnitude > 0.0001f)
                            {
                                var want = Quaternion.LookRotation(aimFlat.normalized, Vector3.up);
                                vis.TurretInstance.transform.rotation = Quaternion.Slerp(
                                    vis.TurretInstance.transform.rotation,
                                    want,
                                    1f - math.exp(-AimTurnSpeed * Time.deltaTime));
                            }
                        }
                    }

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
            }

            if (vis.InfoRoot == null)
                return;

            // --- Paint copy ---
            // Empty pad → placeholder title (not “Lv 0”). Built pads → “Lv N”.
            if (vis.LevelText != null)
            {
                vis.LevelText.fontSize = LevelFontSize;
                vis.LevelText.fontStyle = FontStyles.Bold;
                vis.LevelText.color = Color.white;
                vis.LevelText.text = slot.TurretLevel <= 0
                    ? EmptyPadPlaceholder
                    : "Lv " + slot.TurretLevel;
            }

            bool atCap = slot.TurretLevel >= maxTurretLevel && slot.TurretLevel > 0;
            if (vis.CostText != null)
            {
                vis.CostText.fontSize = CostFontSize;
                vis.CostText.fontStyle = FontStyles.Normal;
                // Match moon gem current color family for the digits next to the icon.
                vis.CostText.color = new Color(GemIconColor.r, GemIconColor.g, GemIconColor.b, CostLineAlpha);

                if (atCap)
                {
                    vis.CostText.text = "MAX";
                }
                else
                {
                    float cost = config.GetGemsToNextLevel(slot.TurretLevel);
                    int current = Mathf.FloorToInt(math.max(0f, slot.BuildProgress));
                    int max = Mathf.Max(1, Mathf.CeilToInt(cost));
                    vis.CostText.text = current + " / " + max;
                }
            }

            if (vis.GemIcon != null)
            {
                // Hide the gem icon on the MAX line — no deposit target left.
                vis.GemIcon.enabled = !atCap && WorldStatLabelIcons.Gem != null;
                if (vis.GemIcon.enabled)
                    vis.GemIcon.color = GemIconColor;
            }

            // --- Same flat orientation as planet / moon labels (fixes inverted LookRotation) ---
            float s = InfoTextWorldScale;
            vis.InfoRoot.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            vis.InfoRoot.localScale = new Vector3(s, -s, s);

            LayoutInfoLines(ref vis);

            // Screen-below the pad: offset on world −Z from the slot center (not radial-out).
            float halfStackWorld = GetInfoStackHalfHeightLocal(vis) * InfoTextWorldScale;
            float belowDist = padWorldRadius + InfoGapPastRimWorld + halfStackWorld;
            vis.InfoRoot.localPosition = new Vector3(0f, InfoAbovePadWorld, -belowDist);
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
            vis.CostText.transform.localPosition = new Vector3(textCenterX, -top + costH * 0.5f, 0f);

            if (vis.GemIcon != null && vis.GemIcon.enabled && vis.GemIcon.sprite != null)
            {
                vis.GemIcon.transform.localPosition = new Vector3(
                    costRowLeft + iconW * 0.5f,
                    -top + costH * 0.5f,
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
                }

                group.Slots.Add(vis);
            }
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

        bool TryFindNearestHostileDisplay(
            EntityManager em,
            EcsWorldVisualizer visualizer,
            TeamId ownerTeam,
            float3 planetDisplay,
            float engageRange,
            bool hasMap,
            float mapW,
            float mapH,
            out float3 targetPos)
        {
            targetPos = default;
            float bestDistSq = engageRange * engageRange;
            bool found = false;

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
                float distSq;
                if (hasMap)
                {
                    float3 d = ToroidalMapEcs.ShortestOffsetXZ(planetDisplay, pos, mapW, mapH);
                    distSq = math.lengthsq(new float3(d.x, 0f, d.z));
                }
                else
                {
                    float3 d = pos - planetDisplay;
                    distSq = math.lengthsq(new float3(d.x, 0f, d.z));
                }

                if (distSq >= bestDistSq)
                    continue;
                bestDistSq = distSq;
                targetPos = pos;
                found = true;
            }

            _ = visualizer;
            return found;
        }

        static bool TryResolveMapSize(EntityManager em, out float mapW, out float mapH)
        {
            mapW = 0f;
            mapH = 0f;
            using var mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
            if (mapQuery.TryGetSingleton<MapStateSingleton>(out var map) &&
                ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
                return true;
            }

            if (MapSessionMetaCache.HasMapSize)
            {
                mapW = MapSessionMetaCache.MapWidth;
                mapH = MapSessionMetaCache.MapHeight;
                return true;
            }

            return false;
        }

        void ClearAllGroups()
        {
            foreach (var kv in _groupsByPlanetId)
                DestroyGroup(kv.Value);
            _groupsByPlanetId.Clear();
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
