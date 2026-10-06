using TitanOrbit.Data;
using TitanOrbit.ECS;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// World-upright plate under a deployed mine: compact thin health bar + owner badge,
    /// same chrome as <see cref="PeopleTransportNameplate"/> (no name). Unparented
    /// so mesh yaw does not spin the bar. Screen-below (world −Z).
    /// HP is fuse remaining from <c>MineHealthMath</c> (lifetime countdown).
    /// </summary>
    [DefaultExecutionOrder(67030)]
    public sealed class MineNameplate : MonoBehaviour
    {
        const int BarSortingOrder = 5000;
        const int BadgeSortingOrder = 5004;
        /// <summary>Local bar width; LateUpdate scales the root so this equals ~1× mine width.</summary>
        const float BarWidth = 10f;
        const float BarHeight = 0.72f;
        const float BarOutlinePad = 0.22f;
        const float BadgeSize = 2.2f;
        const float PadPastHull = 0.06f;
        const float MinMineWidth = 0.18f;
        const float BarWorldWidthMul = 1.05f;
        const float LiftAboveMine = 0.08f;
        static readonly Vector3 ScreenBelowWorld = new Vector3(0f, 0f, -1f);
        const float BarSpritePpu = 8f;
        const int BarFillQuantizeSteps = 64;
        const float BarSliceMinWidth = 2f / BarSpritePpu;
        const float HealthHighRatio = 2f / 3f;
        const float HealthLowRatio = 1f / 3f;
        const float PlayerBadgeMipBias = -0.85f;

        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;
        /// <summary>Solid black rim so the empty track does not pick up the team-green mine mesh.</summary>
        static readonly Color BarOutlineColor = new Color(0.02f, 0.02f, 0.03f, 1f);
        static readonly Color BarBgColor = new Color(0.06f, 0.07f, 0.09f, 1f);
        static readonly Color HealthFillFull = new Color(0.45f, 1f, 0.2f, 1f);
        static readonly Color HealthFillMid = new Color(1f, 0.72f, 0.12f, 1f);
        static readonly Color HealthFillEmpty = new Color(1f, 0.28f, 0.16f, 1f);

        static Sprite s_BarSprite;
        static Material s_BarMaterial;
        static Material s_PlayerBadgeMaterial;

        int _networkId;
        float _health = 1f;
        float _maxHealth = 1f;
        float _cachedHealthRatio = -1f;
        int _cachedBadgeId = int.MinValue;

        Transform _labelRoot;
        SpriteRenderer _playerBadge;
        Transform _barRoot;
        Transform _barFill;
        SpriteRenderer _barFillRenderer;
        bool _ready;

        /// <summary>Adds the plate if missing (one-shot at mesh spawn).</summary>
        public static MineNameplate Ensure(GameObject host)
        {
            if (host == null)
                return null;
            var plate = host.GetComponent<MineNameplate>();
            if (plate == null)
                plate = host.AddComponent<MineNameplate>();
            plate.EnsureHierarchy();
            return plate;
        }

        /// <summary>Owner badge + live fuse HP. Max is catalog / ghosted <c>MaxHealth</c>.</summary>
        public static void Sync(GameObject host, int networkId, float health, float maxHealth)
        {
            if (host == null)
                return;
            var plate = host.GetComponent<MineNameplate>();
            if (plate == null)
                plate = Ensure(host);
            if (plate == null)
                return;

            float max = Mathf.Max(1f, maxHealth);
            float hp = Mathf.Clamp(health, 0f, max);
            plate.Bind(networkId, hp, max);
        }

        public void Bind(int networkId, float health, float maxHealth)
        {
            if (networkId > 0)
                _networkId = networkId;
            _health = Mathf.Max(0f, health);
            _maxHealth = Mathf.Max(0.001f, maxHealth);
            EnsureHierarchy();
            ApplyBadge();
            ApplyHealthBar();
        }

        void OnDisable()
        {
            if (_labelRoot != null)
                _labelRoot.gameObject.SetActive(false);
        }

        void OnEnable()
        {
            if (_labelRoot != null)
                _labelRoot.gameObject.SetActive(true);
        }

        void LateUpdate()
        {
            ApplyBadge();
            FollowHostNow();
        }

        /// <summary>Snap the unparented plate after the Bomb_4 pose this frame.</summary>
        public static void FollowHost(GameObject host)
        {
            if (host == null)
                return;
            var plate = host.GetComponent<MineNameplate>();
            plate?.FollowHostNow();
        }

        public void FollowHostNow()
        {
            if (!_ready || _labelRoot == null || !isActiveAndEnabled)
                return;

            float diameter = Mathf.Max(
                MinMineWidth,
                Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z)));
            float scale = diameter * BarWorldWidthMul / BarWidth;
            float clearance = diameter * 0.5f + PadPastHull;
            Vector3 pos = transform.position + ScreenBelowWorld * clearance;
            pos.y = transform.position.y + LiftAboveMine;
            bool theatrical = TitanOrbit.UI.TheatricalWorldSpaceLabelRotation.IsTheatricalEngaged();
            Quaternion rot = theatrical
                ? TitanOrbit.UI.TheatricalWorldSpaceLabelRotation.BillboardRotationFacingCamera()
                : Quaternion.Euler(-90f, 0f, 0f);
            _labelRoot.SetPositionAndRotation(pos, rot);
            _labelRoot.localScale = new Vector3(scale, -scale, scale);
        }

        void OnDestroy()
        {
            if (_labelRoot != null)
            {
                Destroy(_labelRoot.gameObject);
                _labelRoot = null;
            }
        }

        void EnsureHierarchy()
        {
            if (_ready && _labelRoot != null)
                return;

            var rootGo = new GameObject("MineNameplate");
            _labelRoot = rootGo.transform;
            _labelRoot.SetParent(null, false);

            _playerBadge = CreateBadge(_labelRoot);
            CreateHealthBar(_labelRoot);
            Layout();
            _ready = true;
        }

        void Layout()
        {
            if (_barRoot != null)
                _barRoot.localPosition = Vector3.zero;
            if (_playerBadge != null)
                _playerBadge.transform.localPosition = new Vector3(0f, 0f, -0.04f);
        }

        void ApplyBadge()
        {
            if (_playerBadge == null)
                return;

            int badgeId = _networkId > 0
                ? EcsGameBridge.GetCachedPlayerBadgeId(_networkId)
                : PlayerBadgeIdUtil.None;
            int cleaned = PlayerBadgeIdUtil.Sanitize(badgeId);
            if (cleaned == _cachedBadgeId)
                return;

            Sprite sprite = PlayerBadgeCatalog.FindSprite(cleaned);
            bool show = sprite != null;
            _playerBadge.sprite = sprite;
            _playerBadge.enabled = show;
            if (show)
            {
                ApplyCrispBadgeSampling(sprite);
                ScaleSpriteToSize(_playerBadge, BadgeSize);
            }

            _cachedBadgeId = cleaned;
        }

        void ApplyHealthBar()
        {
            if (_barFill == null || _barFillRenderer == null)
                return;

            float ratio = Mathf.Clamp01(_health / _maxHealth);
            float qRatio = Mathf.Round(ratio * BarFillQuantizeSteps) / BarFillQuantizeSteps;
            float fillW = BarWidth * qRatio;
            bool showFill = fillW >= BarSliceMinWidth;
            Color fillColor = HealthFillColor(qRatio);

            if (Mathf.Abs(qRatio - _cachedHealthRatio) < 0.0001f
                && _barFillRenderer.enabled == showFill
                && (!showFill || _barFillRenderer.color == fillColor))
                return;

            _cachedHealthRatio = qRatio;
            if (!showFill)
            {
                _barFillRenderer.enabled = false;
                _barFill.localPosition = new Vector3(-BarWidth * 0.5f, 0f, 0f);
                _barFillRenderer.size = new Vector2(BarSliceMinWidth, BarHeight);
                return;
            }

            _barFillRenderer.enabled = true;
            _barFill.localPosition = new Vector3((-BarWidth + fillW) * 0.5f, 0f, 0f);
            _barFillRenderer.size = new Vector2(fillW, BarHeight);
            _barFillRenderer.color = fillColor;
        }

        static Color HealthFillColor(float ratio)
        {
            if (ratio >= HealthHighRatio)
                return HealthFillFull;
            if (ratio >= HealthLowRatio)
                return HealthFillMid;
            return HealthFillEmpty;
        }

        SpriteRenderer CreateBadge(Transform parent)
        {
            var go = new GameObject("PlayerBadge");
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = null;
            sr.color = Color.white;
            sr.sortingOrder = BadgeSortingOrder;
            sr.enabled = false;
            sr.sharedMaterial = GetPlayerBadgeMaterial();
            sr.drawMode = SpriteDrawMode.Simple;
            sr.shadowCastingMode = ShadowCastingMode.Off;
            sr.receiveShadows = false;
            sr.allowOcclusionWhenDynamic = false;
            return sr;
        }

        void CreateHealthBar(Transform parent)
        {
            var rootGo = new GameObject("HealthBar");
            rootGo.transform.SetParent(parent, false);
            _barRoot = rootGo.transform;

            var outlineGo = new GameObject("Outline");
            outlineGo.transform.SetParent(_barRoot, false);
            var outlineSr = outlineGo.AddComponent<SpriteRenderer>();
            outlineSr.color = BarOutlineColor;
            ApplyBarRendererStyle(outlineSr, BarSortingOrder);
            outlineSr.size = new Vector2(BarWidth + BarOutlinePad * 2f, BarHeight + BarOutlinePad * 2f);

            var bgGo = new GameObject("Bg");
            bgGo.transform.SetParent(_barRoot, false);
            var bgSr = bgGo.AddComponent<SpriteRenderer>();
            bgSr.color = BarBgColor;
            ApplyBarRendererStyle(bgSr, BarSortingOrder + 1);
            bgSr.size = new Vector2(BarWidth, BarHeight);

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(_barRoot, false);
            fillGo.transform.localPosition = new Vector3(-BarWidth * 0.5f, 0f, 0f);
            _barFill = fillGo.transform;
            _barFillRenderer = fillGo.AddComponent<SpriteRenderer>();
            _barFillRenderer.color = HealthFillFull;
            ApplyBarRendererStyle(_barFillRenderer, BarSortingOrder + 2);
            _barFillRenderer.size = new Vector2(BarSliceMinWidth, BarHeight);
            _barFillRenderer.enabled = false;
        }

        static void ApplyBarRendererStyle(SpriteRenderer renderer, int sortingOrder)
        {
            renderer.sprite = GetBarSprite();
            renderer.sharedMaterial = GetBarMaterial();
            renderer.drawMode = SpriteDrawMode.Sliced;
            renderer.sortingOrder = sortingOrder;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.allowOcclusionWhenDynamic = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        static Sprite GetBarSprite()
        {
            if (s_BarSprite != null)
                return s_BarSprite;

            const int dim = 8;
            var tex = new Texture2D(dim, dim, TextureFormat.RGBA32, false);
            tex.name = "MineNameplateBarTex";
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 0;

            var pixels = new Color32[dim * dim];
            var solid = new Color32(255, 255, 255, 255);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = solid;

            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            s_BarSprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, dim, dim),
                new Vector2(0.5f, 0.5f),
                BarSpritePpu,
                0,
                SpriteMeshType.FullRect,
                new Vector4(1f, 1f, 1f, 1f));
            s_BarSprite.name = "MineNameplateBarSprite";
            return s_BarSprite;
        }

        static Material GetBarMaterial()
        {
            if (s_BarMaterial != null)
                return s_BarMaterial;

            s_BarMaterial = CreateOverlaySpriteMaterial("MineNameplateBar");
            return s_BarMaterial;
        }

        static Material GetPlayerBadgeMaterial()
        {
            if (s_PlayerBadgeMaterial != null)
                return s_PlayerBadgeMaterial;

            s_PlayerBadgeMaterial = CreateOverlaySpriteMaterial("MineNameplateBadge");
            return s_PlayerBadgeMaterial;
        }

        static Material CreateOverlaySpriteMaterial(string name)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");

            var mat = shader != null
                ? new Material(shader)
                : new Material(Shader.Find("Hidden/InternalErrorShader"));
            mat.name = name;
            mat.renderQueue = RenderQueueOverlay;
            return mat;
        }

        static void ApplyCrispBadgeSampling(Sprite sprite)
        {
            if (sprite == null || sprite.texture == null)
                return;
            Texture tex = sprite.texture;
            if (tex.filterMode != FilterMode.Bilinear)
                tex.filterMode = FilterMode.Bilinear;
            if (tex.anisoLevel < 4)
                tex.anisoLevel = 4;
            if (Mathf.Abs(tex.mipMapBias - PlayerBadgeMipBias) > 0.01f)
                tex.mipMapBias = PlayerBadgeMipBias;
        }

        static void ScaleSpriteToSize(SpriteRenderer renderer, float targetSize)
        {
            if (renderer == null || renderer.sprite == null)
                return;
            float worldW = renderer.sprite.rect.width / Mathf.Max(1f, renderer.sprite.pixelsPerUnit);
            float scale = targetSize / Mathf.Max(0.01f, worldW);
            renderer.transform.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
