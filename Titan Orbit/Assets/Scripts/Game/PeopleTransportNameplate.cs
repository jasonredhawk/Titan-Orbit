using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// World-upright plate under a troop transport: health bar twice the capsule
    /// width, owner badge centered on the bar. Unparented so hull yaw does not
    /// spin the chrome. Screen-below like regular ship plates — not on top.
    /// </summary>
    [DefaultExecutionOrder(67010)]
    public sealed class PeopleTransportNameplate : MonoBehaviour
    {
        const int BarSortingOrder = 5000;
        const int BadgeSortingOrder = 5004;
        /// <summary>Local bar width; LateUpdate scales the root so this equals the capsule diameter.</summary>
        const float BarWidth = 10f;
        const float BarHeight = 1.7f;
        /// <summary>Medallion over the health bar (local units, ~40% of bar width).</summary>
        const float BadgeSize = 4f;
        const float PadPastHull = 0.08f;
        const float MinTransportWidth = 0.12f;
        static readonly Vector3 ScreenBelowWorld = new Vector3(0f, 0f, -1f);
        const float BarSpritePpu = 8f;
        const int BarFillQuantizeSteps = 64;
        const float BarSliceMinWidth = 2f / BarSpritePpu;
        const float HealthHighRatio = 2f / 3f;
        const float HealthLowRatio = 1f / 3f;
        const float PlayerBadgeMipBias = -0.85f;

        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;
        static readonly Color BarBgColor = new Color(0.12f, 0.14f, 0.18f, 0.92f);
        static readonly Color HealthFillFull = new Color(0.15f, 1f, 0.25f, 0.98f);
        static readonly Color HealthFillMid = new Color(1f, 0.55f, 0.05f, 0.98f);
        static readonly Color HealthFillEmpty = new Color(1f, 0.15f, 0.12f, 0.98f);

        static Sprite s_BarSprite;
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

        /// <summary>Adds the plate if missing (one-shot at visual spawn).</summary>
        public static PeopleTransportNameplate Ensure(GameObject host)
        {
            if (host == null)
                return null;
            var plate = host.GetComponent<PeopleTransportNameplate>();
            if (plate == null)
                plate = host.AddComponent<PeopleTransportNameplate>();
            plate.EnsureHierarchy();
            return plate;
        }

        /// <summary>
        /// Owner badge + live HP. Max is <c>ComputeMaxHealth(amount)</c>; pass
        /// server health when known (ghost vitals / pose RPC).
        /// </summary>
        public static void Sync(GameObject host, int networkId, float peopleAmount, float health = -1f)
        {
            if (host == null)
                return;
            var plate = host.GetComponent<PeopleTransportNameplate>();
            if (plate == null)
                plate = Ensure(host);
            if (plate == null)
                return;

            float max = PeopleTransportMath.ComputeMaxHealth(Mathf.Max(0.001f, peopleAmount));
            float hp = health < 0f ? max : Mathf.Clamp(health, 0f, max);
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

        void LateUpdate()
        {
            if (!_ready || _labelRoot == null)
                return;

            ApplyBadge();

            float diameter = Mathf.Max(
                MinTransportWidth,
                Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z)));
            float scale = diameter * 2f / BarWidth;
            float clearance = (diameter * 0.5f + PadPastHull) * 2f;
            Vector3 pos = transform.position + ScreenBelowWorld * clearance;
            pos.y = transform.position.y;
            _labelRoot.SetPositionAndRotation(pos, Quaternion.Euler(-90f, 0f, 0f));
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

            var rootGo = new GameObject("PeopleTransportNameplate");
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

            var bgGo = new GameObject("Bg");
            bgGo.transform.SetParent(_barRoot, false);
            var bgSr = bgGo.AddComponent<SpriteRenderer>();
            bgSr.color = BarBgColor;
            ApplyBarRendererStyle(bgSr, BarSortingOrder);
            bgSr.size = new Vector2(BarWidth, BarHeight);

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(_barRoot, false);
            fillGo.transform.localPosition = new Vector3(-BarWidth * 0.5f, 0f, 0f);
            _barFill = fillGo.transform;
            _barFillRenderer = fillGo.AddComponent<SpriteRenderer>();
            _barFillRenderer.color = HealthFillFull;
            ApplyBarRendererStyle(_barFillRenderer, BarSortingOrder + 1);
            _barFillRenderer.size = new Vector2(BarSliceMinWidth, BarHeight);
            _barFillRenderer.enabled = false;
        }

        static void ApplyBarRendererStyle(SpriteRenderer renderer, int sortingOrder)
        {
            renderer.sprite = GetBarSprite();
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
            tex.name = "PeopleTransportNameplateBarTex";
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 0;

            var pixels = new Color32[dim * dim];
            var clear = new Color32(255, 255, 255, 0);
            var solid = new Color32(255, 255, 255, 255);
            for (int y = 0; y < dim; y++)
            {
                for (int x = 0; x < dim; x++)
                {
                    bool rim = x == 0 || y == 0 || x == dim - 1 || y == dim - 1;
                    pixels[y * dim + x] = rim ? clear : solid;
                }
            }

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
            s_BarSprite.name = "PeopleTransportNameplateBarSprite";
            return s_BarSprite;
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
            s_PlayerBadgeMaterial.name = "PeopleTransportNameplateBadge";
            s_PlayerBadgeMaterial.renderQueue = RenderQueueOverlay;
            return s_PlayerBadgeMaterial;
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
