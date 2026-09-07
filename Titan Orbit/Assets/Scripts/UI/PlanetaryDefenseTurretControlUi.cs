using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using TMPro;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Client host for planetary defense turret possession UI.
    /// Publishes pad eligibility / camera pose / engage-range zoom radius, and shows <b>one</b>
    /// screen-space Take Control button above the eligible turret (never per-pad WorldSpace
    /// canvases — those GraphicRaycasters destroyed FPS).
    /// <para>
    /// [TITAN-ORBIT] Gem auto-deposit still waits 2s of stillness on the server; enter does not.
    /// Eligibility walks <see cref="EcsWorldVisualizer"/> planet proxies (no fresh EntityQuery).
    /// While controlling, <see cref="PlanetaryDefenseTurretClientState.DesiredViewRadiusWorld"/>
    /// is set from the pad's engage range so <c>CameraFollowEcs</c> zooms out to fit bullets.
    /// GhostSpawnBacklog (asteroid → gem Instantiates) skips ship gathers but does not hide the button.
    /// </para>
    /// </summary>
    // Before CameraFollowEcs (67001) so pad pose is fresh when possession starts.
    [DefaultExecutionOrder(66990)]
    public class PlanetaryDefenseTurretControlUi : MonoBehaviour
    {
        PlanetShipFamilyConfig _familyConfig;
        bool _warmed;

        Canvas _canvas;
        RectTransform _buttonRoot;
        Button _button;

        /// <summary>Cached gameplay camera — Camera.main lookup every LateUpdate is avoidable cost.</summary>
        UnityEngine.Camera _cachedCamera;

        readonly List<Entity> _planetScratch = new List<Entity>(32);

        /// <summary>
        /// Pixels above the projected pad center for the button (screen +Y = toward the top of
        /// the view on the top-down camera — above the turret mesh, not under the Lv/gem labels).
        /// </summary>
        const float ScreenOffsetAbovePadPx = 48f;

        /// <summary>[UNITY] Spawn a DontDestroyOnLoad host after the first scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<PlanetaryDefenseTurretControlUi>() != null)
                return;

            var go = new GameObject(nameof(PlanetaryDefenseTurretControlUi));
            DontDestroyOnLoad(go);
            go.AddComponent<PlanetaryDefenseTurretControlUi>();
        }

        /// <summary>Build the single Take Control HUD button and clear stale possession mirror.</summary>
        void Awake()
        {
            // [TITAN-ORBIT] DontDestroyOnLoad host can survive Domain Reload off — never inherit
            // IsControlling / pad pose from a prior Play Mode.
            PlanetaryDefenseTurretClientState.Clear();
            BuildHudButton();
        }

        /// <summary>Clear client mirror when this host is destroyed.</summary>
        void OnDestroy()
        {
            PlanetaryDefenseTurretClientState.Clear();
        }

        /// <summary>
        /// Each frame: resolve eligibility, place the single button above the pad, update camera pose.
        /// </summary>
        void LateUpdate()
        {
            // --- Instantiates gate: keep last button, do not hide ---
            // [TITAN-ORBIT] GhostSpawnBacklog is also true mid-combat when asteroids explode
            // into gem ghosts (MEGA plow). HideButton() here blinked the Take Control tile.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            EnsureWarmed();

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated || !EcsGameBridge.IsNetworkInGame())
            {
                PlanetaryDefenseTurretClientState.Clear();
                HideButton();
                return;
            }

            var em = world.EntityManager;
            if (!TryGetMapSize(em, out float mapW, out float mapH))
            {
                HideButton();
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<LocalTransform>(shipEntity))
            {
                PlanetaryDefenseTurretClientState.Clear();
                HideButton();
                return;
            }

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
            {
                PlanetaryDefenseTurretClientState.Clear();
                HideButton();
                return;
            }

            // --- Controlling: camera pad pose + zoom radius, hide enter button ---
            if (em.HasComponent<ShipTurretControlState>(shipEntity))
            {
                var control = em.GetComponentData<ShipTurretControlState>(shipEntity);
                if (control.IsControlling)
                {
                    bool hasPad = TryResolvePadWorldPosition(
                        em, control.PlanetId, control.SlotIndex, mapW, mapH, out Vector3 padPos);
                    // [TITAN-ORBIT] Camera height scales to this pad's engage/bullet range + margin.
                    float viewRadius = ResolveOccupiedViewRadius(
                        em, control.PlanetId, control.SlotIndex);
                    PlanetaryDefenseTurretClientState.SetControlling(
                        true, padPos, hasPad, viewRadius);
                    PlanetaryDefenseTurretClientState.SetEligibility(
                        false, 0, 0, 0f, padPos, hasPad);
                    HideButton();
                    return;
                }
            }

            PlanetaryDefenseTurretClientState.SetControlling(false, Vector3.zero, false, 0f);

            if (HUDController.MinimapExpandedObscuresHud)
            {
                HideButton();
                return;
            }

            float3 shipPos = em.GetComponentData<LocalTransform>(shipEntity).Position;
            shipPos.y = PlanetaryDefenseMath.FixedY;

            // --- Eligibility via visualizer planet list (no CreateEntityQuery per frame) ---
            if (!TryFindClosestEnterablePad(
                    em, ship.Team, shipPos, mapW, mapH,
                    out int planetId, out byte slotIndex, out float3 padWorld))
            {
                PlanetaryDefenseTurretClientState.SetEligibility(
                    false, 0, 0, 0f, Vector3.zero, false);
                HideButton();
                return;
            }

            Vector3 padV = new Vector3(padWorld.x, padWorld.y, padWorld.z);
            PlanetaryDefenseTurretClientState.SetEligibility(
                true, planetId, slotIndex, 0f, padV, true);

            ShowButtonAbovePad(padV);
        }

        /// <summary>Builds one overlay canvas + dark Take Control button.</summary>
        void BuildHudButton()
        {
            var canvasGo = new GameObject("PlanetaryDefenseTakeControlCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above most gameplay HUD; below modal orbit UI (200).
            _canvas.sortingOrder = 120;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasGo.AddComponent<GraphicRaycaster>();

            var rootGo = new GameObject("TakeControlButton", typeof(RectTransform));
            rootGo.transform.SetParent(canvasGo.transform, false);
            _buttonRoot = rootGo.GetComponent<RectTransform>();
            _buttonRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _buttonRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _buttonRoot.pivot = new Vector2(0.5f, 0.5f);
            _buttonRoot.sizeDelta = new Vector2(148f, 36f);

            var panelImage = rootGo.AddComponent<Image>();
            panelImage.color = new Color(0.012f, 0.016f, 0.028f, 0.94f);

            _button = rootGo.AddComponent<Button>();
            _button.targetGraphic = panelImage;
            var colors = _button.colors;
            colors.normalColor = panelImage.color;
            colors.highlightedColor = new Color(0.04f, 0.10f, 0.18f, 0.96f);
            colors.pressedColor = new Color(0.08f, 0.18f, 0.30f, 1f);
            colors.selectedColor = colors.highlightedColor;
            _button.colors = colors;
            _button.onClick.AddListener(OnTakeControlClicked);

            var accentGo = new GameObject("Accent", typeof(RectTransform));
            accentGo.transform.SetParent(rootGo.transform, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.sizeDelta = new Vector2(0f, 3f);
            accentRt.anchoredPosition = Vector2.zero;
            var accentImage = accentGo.AddComponent<Image>();
            accentImage.color = new Color(0.35f, 0.72f, 0.95f, 0.95f);
            accentImage.raycastTarget = false;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(rootGo.transform, false);
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(6f, 2f);
            labelRt.offsetMax = new Vector2(-6f, -4f);
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = 15f;
            label.fontStyle = FontStyles.Bold;
            label.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            label.text = "TAKE CONTROL";
            label.raycastTarget = false;

            HideButton();
        }

        void OnTakeControlClicked()
        {
            if (!PlanetaryDefenseTurretClientState.CanTakeControl)
                return;
            PlanetaryDefenseTurretRpcClient.RequestEnterTurret(
                PlanetaryDefenseTurretClientState.EligiblePlanetId,
                PlanetaryDefenseTurretClientState.EligibleSlotIndex);
        }

        /// <summary>
        /// Places the button above the turret on screen (opposite side from Lv / gem labels).
        /// </summary>
        void ShowButtonAbovePad(Vector3 padWorld)
        {
            if (_buttonRoot == null)
                return;

            if (_cachedCamera == null)
                _cachedCamera = UnityEngine.Camera.main;
            if (_cachedCamera == null)
            {
                HideButton();
                return;
            }

            // [TITAN-ORBIT] Pad labels sit on world −Z (screen-below). Anchor on +Z so the
            // Take Control button projects above the turret mesh instead.
            Vector3 anchorWorld = padWorld + new Vector3(0f, 0.2f, 1.35f);
            Vector3 screen = _cachedCamera.WorldToScreenPoint(anchorWorld);
            if (screen.z <= 0.01f)
            {
                HideButton();
                return;
            }

            // ScreenSpaceOverlay: anchoredPosition is in canvas local space ≈ screen pixels
            // when CanvasScaler uses ScaleWithScreenSize — convert via screen → canvas.
            RectTransform canvasRt = _canvas.transform as RectTransform;
            if (canvasRt != null &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRt, screen, null, out Vector2 local))
            {
                local.y += ScreenOffsetAbovePadPx;
                _buttonRoot.anchoredPosition = local;
            }
            else
            {
                _buttonRoot.position = new Vector3(screen.x, screen.y + ScreenOffsetAbovePadPx, 0f);
            }

            if (!_buttonRoot.gameObject.activeSelf)
                _buttonRoot.gameObject.SetActive(true);
        }

        void HideButton()
        {
            if (_buttonRoot != null && _buttonRoot.gameObject.activeSelf)
                _buttonRoot.gameObject.SetActive(false);
        }

        /// <summary>
        /// Closest built/free/friendly pad in zone — walks visualizer planet proxies only
        /// (same pattern as <see cref="PlanetaryDefenseVisualDriver"/>; no EntityQuery alloc).
        /// </summary>
        bool TryFindClosestEnterablePad(
            EntityManager em,
            TeamId shipTeam,
            float3 shipPos,
            float mapW,
            float mapH,
            out int planetId,
            out byte slotIndex,
            out float3 padWorldPos)
        {
            planetId = 0;
            slotIndex = 0;
            padWorldPos = float3.zero;

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return false;

            visualizer.CopyPlanetProxyEntities(_planetScratch);
            if (_planetScratch.Count == 0)
                return false;

            float bestDistSq = float.MaxValue;
            bool found = false;

            for (int p = 0; p < _planetScratch.Count; p++)
            {
                Entity planetEntity = _planetScratch[p];
                if (!em.Exists(planetEntity) ||
                    !em.HasComponent<PlanetState>(planetEntity) ||
                    !em.HasComponent<LocalTransform>(planetEntity) ||
                    !em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                    continue;

                var planet = em.GetComponentData<PlanetState>(planetEntity);
                if (planet.Ownership == TeamId.None || planet.Ownership != shipTeam)
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                if (buffer.Length == 0)
                    continue;

                var config = PlanetaryDefenseConfig.ResolveForFamily(
                    _familyConfig, planet.ShipFamilyConfigIndex);
                float zoneR = math.max(0.25f, config.depositZoneRadius);
                float zoneRSq = zoneR * zoneR;

                var planetXf = em.GetComponentData<LocalTransform>(planetEntity);
                float3 planetPos = planetXf.Position;
                float planetSize = math.max(0.25f, planetXf.Scale);

                for (int i = 0; i < buffer.Length; i++)
                {
                    var slot = buffer[i];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f || slot.OccupiedByNetworkId != 0)
                        continue;

                    float3 slotPos = PlanetaryDefenseMath.GetSlotWorldPositionNear(
                        shipPos, planetPos, planetSize, planet.PlanetLevel,
                        i, buffer.Length, mapW, mapH);
                    float3 delta = ToroidalMapEcs.ShortestOffsetXZ(shipPos, slotPos, mapW, mapH);
                    float distSq = math.lengthsq(new float3(delta.x, 0f, delta.z));
                    if (distSq > zoneRSq || distSq >= bestDistSq)
                        continue;

                    bestDistSq = distSq;
                    planetId = planet.PlanetId;
                    slotIndex = (byte)i;
                    padWorldPos = slotPos;
                    padWorldPos.y = PlanetaryDefenseMath.FixedY;
                    found = true;
                }
            }

            return found;
        }

        void EnsureWarmed()
        {
            if (_warmed)
                return;
            _familyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            _warmed = true;
        }

        /// <summary>
        /// Resolves torus map size without allocating a new EntityQuery.
        /// Prefers <see cref="ToroidalMapEcs"/> then join meta cache.
        /// </summary>
        static bool TryGetMapSize(EntityManager em, out float mapW, out float mapH)
        {
            mapW = 0f;
            mapH = 0f;
            if (ToroidalMapEcs.TryGetMapSize(out mapW, out mapH))
                return true;

            // --- Join meta already published (no ECS alloc) ---
            if (MapSessionMetaCache.HasMapSize &&
                ToroidalMapEcs.IsValidMapSize(MapSessionMetaCache.MapWidth, MapSessionMetaCache.MapHeight))
            {
                mapW = MapSessionMetaCache.MapWidth;
                mapH = MapSessionMetaCache.MapHeight;
                return true;
            }

            _ = em;
            return false;
        }

        /// <summary>
        /// Camera view radius for the occupied pad from
        /// <see cref="PlanetaryDefenseConfig.GetCameraViewRadius"/> (Level 1→6 asset lerp).
        /// Walks visualizer planet proxies only (no EntityQuery). Returns 0 when unknown.
        /// </summary>
        float ResolveOccupiedViewRadius(EntityManager em, int planetId, byte slotIndex)
        {
            EnsureWarmed();
            if (planetId <= 0)
                return 0f;

            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return 0f;

            visualizer.CopyPlanetProxyEntities(_planetScratch);
            for (int i = 0; i < _planetScratch.Count; i++)
            {
                Entity e = _planetScratch[i];
                if (!em.Exists(e) ||
                    !em.HasComponent<PlanetState>(e) ||
                    !em.HasBuffer<PlanetaryDefenseSlotElement>(e))
                    continue;

                var planet = em.GetComponentData<PlanetState>(e);
                if (planet.PlanetId != planetId)
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(e);
                if (slotIndex >= buffer.Length)
                    return 0f;

                var slot = buffer[slotIndex];
                if (slot.TurretLevel <= 0)
                    return 0f;

                var config = PlanetaryDefenseConfig.ResolveForFamily(
                    _familyConfig, planet.ShipFamilyConfigIndex);
                // [TITAN-ORBIT] Designer knobs on PlanetaryDefenseConfig — no hardcoded margin.
                return config.GetCameraViewRadius(slot.TurretLevel);
            }

            return 0f;
        }

        /// <summary>
        /// Pad world pose while controlling — quarantine-safe visualizer lookup
        /// (no planet archetype gather / EntityQuery).
        /// </summary>
        static bool TryResolvePadWorldPosition(
            EntityManager em,
            int planetId,
            byte slotIndex,
            float mapW,
            float mapH,
            out Vector3 padPos)
        {
            padPos = Vector3.zero;
            var visualizer = EcsWorldVisualizer.Active;
            if (visualizer == null)
                return false;

            // [TITAN-ORBIT] Walks hybrid planet proxies only — safe under join settle.
            if (!visualizer.TryGetPlanetPoseByPlanetId(
                    em, planetId, out float3 planetPos, out float planetSize, out PlanetState planet))
                return false;

            int slotCount = math.max(1, planet.PlanetLevel);
            float3 nearPos = float3.zero;
            if (ShipDisplayPose.HasLocalPose)
            {
                var p = ShipDisplayPose.LocalPosition;
                nearPos = new float3(p.x, PlanetaryDefenseMath.FixedY, p.z);
            }

            float3 worldPos = PlanetaryDefenseMath.GetSlotWorldPositionNear(
                nearPos,
                planetPos,
                math.max(0.25f, planetSize),
                planet.PlanetLevel,
                slotIndex,
                slotCount,
                mapW,
                mapH);
            worldPos.y = PlanetaryDefenseMath.FixedY;
            padPos = new Vector3(worldPos.x, worldPos.y, worldPos.z);
            return true;
        }
    }
}
