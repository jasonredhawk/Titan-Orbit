using UnityEngine;
using TitanOrbit.Networking;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Tiny world-space (non-Canvas) health / gems / people bars for AI enemy ships.
    /// Attached to the Starship root (not BankPivot), so it does not inherit ship banking/visual rotation.
    /// Lies flat on the play plane (XZ) with surfaces facing world up for top-down readability.
    /// </summary>
    [DefaultExecutionOrder(33000)]
    public sealed class EnemyShipWorldStatsPanel : MonoBehaviour
    {
        public const string ChildObjectName = "EnemyShipWorldStats";

        [Tooltip("Local offset from Starship root. Negative Z moves the panel behind the ship.")]
        [SerializeField] private Vector3 localOffset = new Vector3(0f, 1f, -1.5f);

        [SerializeField] private float barWidth = 0.8f;
        [SerializeField] private float barHeight = 0.05f;
        [SerializeField] private float rowGap = 0.02f;
        [SerializeField] private int headerFontSize = 22;
        [SerializeField] private float panelScale = 2f;

        [SerializeField] private Color healthFill = new Color(0.2f, 0.9f, 0.45f, 1f);
        [SerializeField] private Color gemsFill = new Color(0.95f, 0.28f, 0.22f, 1f);
        [SerializeField] private Color peopleFill = new Color(0.9f, 0.75f, 0.28f, 1f);
        [SerializeField] private Color trackColor = new Color(0.08f, 0.08f, 0.1f, 0.92f);

        /// <summary>World rotation so default quads (Unity XY) lie in XZ with normals along +Y (visible from above).</summary>
        private static readonly Quaternion FlatFacingUpWorld = Quaternion.Euler(90f, 0f, 0f);

        private Starship _ship;
        private Transform _fillHealth;
        private Transform _fillGems;
        private Transform _fillPeople;
        private MeshRenderer _fillHealthRenderer;
        private MeshRenderer _fillGemsRenderer;
        private MeshRenderer _fillPeopleRenderer;
        private float _barWidthCached;
        private MeshRenderer[] _renderers;
        private TextMesh _headerText;
        private bool _isAiShip;

        /// <summary>Called from <see cref="Starship"/> to attach as a child of the Starship root.</summary>
        public static void CreateAsStarshipChild(Starship ship)
        {
            if (ship == null) return;
            Transform existing = ship.transform.Find(ChildObjectName);
            if (existing != null) return;

            var go = new GameObject(ChildObjectName);
            go.transform.SetParent(ship.transform, false);
            go.AddComponent<EnemyShipWorldStatsPanel>();
        }

        private void Awake()
        {
            _ship = GetComponentInParent<Starship>();
            _isAiShip = _ship != null && _ship.GetComponent<TitanOrbit.AI.AIStarshipController>() != null;
            if (_ship == null || !_isAiShip)
            {
                Destroy(gameObject);
                return;
            }

            transform.localPosition = localOffset;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one * Mathf.Max(0.1f, panelScale);

            _barWidthCached = barWidth;
            BuildHeaderText();
            BuildRow("Health", 0, healthFill, out _fillHealth, out _fillHealthRenderer);
            BuildRow("Gems", 1, gemsFill, out _fillGems, out _fillGemsRenderer);
            BuildRow("People", 2, peopleFill, out _fillPeople, out _fillPeopleRenderer);
            _renderers = GetComponentsInChildren<MeshRenderer>(true);
        }

        private void BuildHeaderText()
        {
            var header = new GameObject("NameLevel");
            header.transform.SetParent(transform, false);
            header.transform.localPosition = new Vector3(0f, barHeight + rowGap + 0.05f, 0f);
            header.transform.localRotation = Quaternion.identity;
            header.transform.localScale = Vector3.one;

            _headerText = header.AddComponent<TextMesh>();
            _headerText.alignment = TextAlignment.Center;
            _headerText.anchor = TextAnchor.MiddleCenter;
            _headerText.fontSize = Mathf.Max(8, headerFontSize);
            _headerText.characterSize = 0.04f;
            _headerText.color = Color.white;
            _headerText.text = string.Empty;
            var mr = _headerText.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
        }

        private void BuildRow(string label, int rowIndex, Color fillColor, out Transform fillTransform, out MeshRenderer fillRenderer)
        {
            float y = -rowIndex * (barHeight + rowGap);
            var row = new GameObject(label + "Row");
            row.transform.SetParent(transform, false);
            row.transform.localPosition = new Vector3(0f, y, 0f);
            row.transform.localRotation = Quaternion.identity;
            row.transform.localScale = Vector3.one;

            CreateQuad($"{label}Track", row.transform, trackColor, barWidth, barHeight, 0.002f);

            var fillGo = CreateQuad($"{label}Fill", row.transform, fillColor, barWidth, barHeight * 0.72f, 0.001f);
            fillTransform = fillGo.transform;
            fillRenderer = fillGo.GetComponent<MeshRenderer>();
            fillTransform.localPosition = new Vector3(barWidth * -0.5f + barWidth * 0.5f, 0f, 0f);
        }

        private static GameObject CreateQuad(string name, Transform parent, Color color, float width, float height, float z)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(width, height, 1f);
            go.transform.localPosition = new Vector3(0f, 0f, z);

            var col = go.GetComponent<Collider>();
            if (col != null)
                Destroy(col);

            var mr = go.GetComponent<MeshRenderer>();
            mr.material = CreateUnlitMaterial(color);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        private static Material CreateUnlitMaterial(Color c)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            m.color = c;
            // Keep bars opaque for stable depth rendering.
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 0f);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            return m;
        }

        private void LateUpdate()
        {
            if (_ship == null)
            {
                Destroy(gameObject);
                return;
            }

            bool show = !_ship.IsDead;
            if (_renderers != null)
            {
                for (int i = 0; i < _renderers.Length; i++)
                    _renderers[i].enabled = show;
            }
            if (!show) return;

            // Flat on the ground plane, facing +Y (not camera-billboarded), so bars stay readable from above.
            transform.rotation = FlatFacingUpWorld;
            if (_headerText != null)
            {
                string playerName;
                if (_isAiShip)
                {
                    // AI ships are usually server-owned (same OwnerClientId), so use ship id for a stable distinct AI label.
                    ulong aiId = _ship.NetworkObject != null && _ship.NetworkObject.IsSpawned
                        ? _ship.NetworkObjectId
                        : (ulong)Mathf.Abs(_ship.GetInstanceID());
                    playerName = "AI-" + (aiId % 1000ul);
                }
                else
                {
                    playerName = PlayerDisplayNames.GetDisplayName(_ship.OwnerClientId, false);
                }
                _headerText.text = playerName + "  Lv." + _ship.ShipLevel;
            }

            float h = _ship.MaxHealth > 0f ? Mathf.Clamp01(_ship.CurrentHealth / _ship.MaxHealth) : 0f;
            float g = _ship.GemCapacity > 0f ? Mathf.Clamp01(_ship.CurrentGems / _ship.GemCapacity) : 0f;
            float p = _ship.PeopleCapacity > 0f ? Mathf.Clamp01(_ship.CurrentPeople / _ship.PeopleCapacity) : 0f;

            ApplyFill(_fillHealth, h);
            ApplyFill(_fillGems, g);
            ApplyFill(_fillPeople, p);
            ApplyFillColor(_fillHealthRenderer, healthFill);
            ApplyFillColor(_fillGemsRenderer, gemsFill);
            ApplyFillColor(_fillPeopleRenderer, peopleFill);
        }

        private void ApplyFill(Transform fill, float t)
        {
            if (fill == null) return;
            t = Mathf.Clamp01(t);
            float w = _barWidthCached * t;
            fill.localScale = new Vector3(Mathf.Max(w, 0.0001f), barHeight * 0.72f, 1f);
            fill.localPosition = new Vector3(_barWidthCached * -0.5f + w * 0.5f, fill.localPosition.y, fill.localPosition.z);
        }

        private static void ApplyFillColor(MeshRenderer renderer, Color color)
        {
            if (renderer == null) return;
            Material mat = renderer.material;
            if (mat == null) return;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.color = color;
        }

    }
}
