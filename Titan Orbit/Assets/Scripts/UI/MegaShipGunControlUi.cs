using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Generation;
using Unity.NetCode;
using TMPro;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Client HUD: one screen-space TAKE CONTROL button above the nearest friendly MEGA gun pad.
    /// Mirrors <see cref="PlanetaryDefenseTurretControlUi"/> (no per-pad WorldSpace canvases).
    /// </summary>
    [DefaultExecutionOrder(66991)]
    public class MegaShipGunControlUi : MonoBehaviour
    {
        Canvas _canvas;
        RectTransform _buttonRoot;
        Button _button;
        UnityEngine.Camera _cachedCamera;
        int _eligibleMegaOwner;
        byte _eligibleMount;
        World _queryWorld;
        EntityQuery _mapQuery;
        EntityQuery _megaQuery;
        float _nextSearchTime;

        const float ScreenOffsetAbovePadPx = 48f;
        const float SearchIntervalSeconds = 0.12f;

        /// <summary>[UNITY] Spawn a DontDestroyOnLoad host after the first scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<MegaShipGunControlUi>() != null)
                return;

            var go = new GameObject(nameof(MegaShipGunControlUi));
            DontDestroyOnLoad(go);
            go.AddComponent<MegaShipGunControlUi>();
        }

        /// <summary>Build the Take Control button.</summary>
        void Awake() => BuildHudButton();

        /// <summary>Place or hide the button from the local ship's MEGA-pad eligibility.</summary>
        void LateUpdate()
        {
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                HideButton();
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated || !EcsGameBridge.IsNetworkInGame())
            {
                HideButton();
                return;
            }

            var em = world.EntityManager;
            EnsureQueries(world, em);
            if (!_mapQuery.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                HideButton();
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<LocalTransform>(shipEntity))
            {
                HideButton();
                return;
            }

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
            {
                HideButton();
                return;
            }

            if (em.HasComponent<ShipMegaGunControlState>(shipEntity)
                && em.GetComponentData<ShipMegaGunControlState>(shipEntity).IsControlling)
            {
                HideButton();
                return;
            }

            if (em.HasComponent<MegaShipState>(shipEntity)
                && em.GetComponentData<MegaShipState>(shipEntity).IsMega)
            {
                HideButton();
                return;
            }

            if (Time.unscaledTime < _nextSearchTime && _eligibleMegaOwner <= 0)
            {
                HideButton();
                return;
            }

            _nextSearchTime = Time.unscaledTime + SearchIntervalSeconds;
            float3 shipPos = em.GetComponentData<LocalTransform>(shipEntity).Position;
            if (!MegaShipGunnerLogic.TryFindClosestEnterableMount(
                    em, _megaQuery, shipEntity, shipPos, map.MapWidth, map.MapHeight,
                    out Entity mega, out byte mountIndex))
            {
                HideButton();
                return;
            }

            int megaOwner = 0;
            if (em.HasComponent<GhostOwner>(mega))
                megaOwner = em.GetComponentData<GhostOwner>(mega).NetworkId;
            if (megaOwner <= 0)
            {
                HideButton();
                return;
            }

            var megaXf = em.GetComponentData<LocalTransform>(mega);
            var mounts = em.GetBuffer<ShipWeaponMountElement>(mega);
            float3 pad = MegaShipGunnerLogic.GetMountWorldPosition(megaXf, mounts[mountIndex]);
            _eligibleMegaOwner = megaOwner;
            _eligibleMount = mountIndex;
            ShowButtonAbovePad(new Vector3(pad.x, pad.y, pad.z));
        }

        void BuildHudButton()
        {
            var canvasGo = new GameObject("MegaShipTakeControlCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 121;
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
            _buttonRoot.sizeDelta = new Vector2(160f, 36f);

            var panelImage = rootGo.AddComponent<Image>();
            panelImage.color = new Color(0.012f, 0.016f, 0.028f, 0.94f);
            _button = rootGo.AddComponent<Button>();
            _button.targetGraphic = panelImage;
            _button.onClick.AddListener(OnTakeControlClicked);

            var accentGo = new GameObject("Accent", typeof(RectTransform));
            accentGo.transform.SetParent(rootGo.transform, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.sizeDelta = new Vector2(0f, 3f);
            var accentImage = accentGo.AddComponent<Image>();
            accentImage.color = new Color(0.95f, 0.72f, 0.28f, 0.95f);
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
            if (_eligibleMegaOwner > 0)
                MegaShipGunRpcClient.RequestEnter(_eligibleMegaOwner, _eligibleMount);
        }

        void ShowButtonAbovePad(Vector3 world)
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

            Vector3 screen = _cachedCamera.WorldToScreenPoint(world);
            if (screen.z < 0f)
            {
                HideButton();
                return;
            }

            _buttonRoot.gameObject.SetActive(true);
            _buttonRoot.position = new Vector3(screen.x, screen.y + ScreenOffsetAbovePadPx, 0f);
        }

        void HideButton()
        {
            if (_buttonRoot != null)
                _buttonRoot.gameObject.SetActive(false);
            _eligibleMegaOwner = 0;
        }

        void EnsureQueries(World world, EntityManager em)
        {
            if (_queryWorld == world && world.IsCreated)
                return;

            _queryWorld = world;
            _mapQuery = em.CreateEntityQuery(typeof(MapStateSingleton));
            _megaQuery = em.CreateEntityQuery(
                typeof(ShipTag), typeof(MegaShipState), typeof(LocalTransform), typeof(ShipWeaponMountElement));
        }
    }
}
