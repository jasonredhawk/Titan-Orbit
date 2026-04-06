using UnityEngine;
using TitanOrbit.Networking;
using System;
using System.IO;

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
        private float _nextDebugLogTime;

        private static readonly string DebugLogPath = Path.Combine(Application.dataPath, "..", "debug-82adea.log");
        private static int s_alivePanels;
        private static float s_perfAccumMs;
        private static int s_perfAccumCalls;
        private static float s_nextPerfLogTime;
        private static int s_lastPerfLogFrame = -1;

        /// <summary>Called from <see cref="Starship"/> to attach as a child of the Starship root.</summary>
        public static void CreateAsStarshipChild(Starship ship)
        {
            if (ship == null) return;
            Transform existing = ship.transform.Find(ChildObjectName);
            // #region agent log
            AppendDebugLog("run-lag-debug", "H2", "EnemyShipWorldStatsPanel.cs:58", "create_panel_requested",
                "{\"shipInstanceId\":" + ship.GetInstanceID() +
                ",\"ownerClientId\":" + ship.OwnerClientId +
                ",\"existingPanel\":" + (existing != null ? "true" : "false") + "}");
            // #endregion
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
            s_alivePanels++;
            // #region agent log
            AppendDebugLog("run-name-debug", "H1", "EnemyShipWorldStatsPanel.cs:63", "panel_awake",
                "{\"shipInstanceId\":" + _ship.GetInstanceID() + ",\"ownerClientId\":" + _ship.OwnerClientId + ",\"isAiShip\":" + (_isAiShip ? "true" : "false") + "}");
            // #endregion

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

        private void OnDestroy()
        {
            s_alivePanels = Mathf.Max(0, s_alivePanels - 1);
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
            float perfStart = Time.realtimeSinceStartup;
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
                if (Time.unscaledTime >= _nextDebugLogTime)
                {
                    ulong netId = (_ship.NetworkObject != null && _ship.NetworkObject.IsSpawned) ? _ship.NetworkObjectId : 0ul;
                    // #region agent log
                    AppendDebugLog("run-name-debug", "H2", "EnemyShipWorldStatsPanel.cs:203", "header_name_resolution",
                        "{\"ownerClientId\":" + _ship.OwnerClientId +
                        ",\"isAiShip\":" + (_isAiShip ? "true" : "false") +
                        ",\"networkObjectSpawned\":" + ((_ship.NetworkObject != null && _ship.NetworkObject.IsSpawned) ? "true" : "false") +
                        ",\"networkObjectId\":" + netId +
                        ",\"resolvedName\":\"" + EscapeJson(playerName) + "\"" +
                        ",\"headerText\":\"" + EscapeJson(_headerText.text) + "\"}");
                    // #endregion
                    _nextDebugLogTime = Time.unscaledTime + 1.0f;
                }
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

            // #region agent log
            s_perfAccumCalls++;
            s_perfAccumMs += (Time.realtimeSinceStartup - perfStart) * 1000f;
            if (Time.unscaledTime >= s_nextPerfLogTime && s_lastPerfLogFrame != Time.frameCount)
            {
                s_lastPerfLogFrame = Time.frameCount;
                float avg = s_perfAccumCalls > 0 ? (s_perfAccumMs / s_perfAccumCalls) : 0f;
                AppendDebugLog("run-lag-debug", "H1", "EnemyShipWorldStatsPanel.cs:236", "panel_perf_window",
                    "{\"alivePanels\":" + s_alivePanels +
                    ",\"windowCalls\":" + s_perfAccumCalls +
                    ",\"windowTotalMs\":" + s_perfAccumMs +
                    ",\"avgMsPerCall\":" + avg +
                    ",\"renderersPerPanel\":" + (_renderers != null ? _renderers.Length : 0) +
                    ",\"sampleShipId\":" + (_ship.NetworkObject != null && _ship.NetworkObject.IsSpawned ? _ship.NetworkObjectId : 0ul) + "}");
                s_perfAccumCalls = 0;
                s_perfAccumMs = 0f;
                s_nextPerfLogTime = Time.unscaledTime + 1.0f;
            }
            // #endregion
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

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void AppendDebugLog(string runId, string hypothesisId, string location, string message, string dataJson)
        {
            try
            {
                string line = "{\"sessionId\":\"82adea\",\"runId\":\"" + runId + "\",\"hypothesisId\":\"" + hypothesisId +
                              "\",\"location\":\"" + location + "\",\"message\":\"" + message + "\",\"data\":" + dataJson +
                              ",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
                File.AppendAllText(DebugLogPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
