using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Shared;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Top-left FPS-style ship vitals HUD (health, energy, gems, people). Reads local ship state from
    /// <see cref="EcsGameBridge"/> each <c>LateUpdate</c> — presentation only, not authoritative sim.
    /// Auto-binds Row0..Row3 child sliders when inspector references are empty.
    /// <para>
    /// [TITAN-ORBIT] Holds a last-good <see cref="ShipState"/> during GhostSpawnBacklog so asteroid→gem
    /// Instantiates do not flash the bars to 0/0 (same combat flicker class as the upgrade strip).
    /// During gem deposit, the gems row prefers <see cref="MoonOrbitClientState"/> optimistic cargo
    /// so it decrements by deposit-chunk size in sync with the metronome SFX.
    /// </para>
    /// </summary>
    public class ShipStatsFpsStyleHUD : MonoBehaviour
    {
        struct StatBarRow
        {
            public Slider Bar;
            public TextMeshProUGUI Value;
        }

        [Header("Layout")]
        [SerializeField] float valueColumnWidth = 120f;
        [SerializeField] float valueColumnInset = 8f;
        [SerializeField] float barValueGap = 12f;

        [Header("References (auto-bound from Row0..Row3 if empty)")]
        [SerializeField] Slider barHealth;
        [SerializeField] Slider barEnergy;
        [SerializeField] Slider barGems;
        [SerializeField] Slider barPeople;
        [SerializeField] TextMeshProUGUI valueHealth;
        [SerializeField] TextMeshProUGUI valueEnergy;
        [SerializeField] TextMeshProUGUI valueGems;
        [SerializeField] TextMeshProUGUI valuePeople;

        static Sprite s_squareBarSprite;
        bool _barsStyled;
        bool _layoutApplied;
        StatBarRow[] _rows;

        /// <summary>Last successful vitals snapshot — kept across brief Instantiates lookup gaps.</summary>
        bool _hasHudCache;
        ShipState _cachedShip;

        /// <summary>Dirty-check strings so TMP does not rebuild every frame while farming gems.</summary>
        readonly string[] _lastValueText = new string[4];

        void Awake()
        {
            // --- Unity lifecycle ---
            AutoBindReferences();
            CacheRows();
            ApplyLayoutToAllRows();
            ApplySquareBarStyleToAll();
        }

        void OnEnable()
        {
            // --- Unity lifecycle ---
            AutoBindReferences();
            CacheRows();
            ApplyLayoutToAllRows();
            ApplySquareBarStyleToAll();
        }

        /// <summary>Finds Bar/Value children under Row0..Row3 when serialized fields are unset.</summary>
        void AutoBindReferences()
        {
            // --- AutoBindReferences ---
            if (barHealth != null)
                return;

            int bound = 0;
            for (int i = 0; i < transform.childCount && bound < 4; i++)
            {
                Transform row = transform.GetChild(i);
                if (!row.name.StartsWith("Row"))
                    continue;

                var bar = row.Find("Bar")?.GetComponent<Slider>();
                var value = row.Find("Value")?.GetComponent<TextMeshProUGUI>();
                switch (bound)
                {
                    case 0: barHealth = bar; valueHealth = value; break;
                    case 1: barEnergy = bar; valueEnergy = value; break;
                    case 2: barGems = bar; valueGems = value; break;
                    case 3: barPeople = bar; valuePeople = value; break;
                }

                bound++;
            }
        }

        void CacheRows()
        {
            // --- CacheRows ---
            _rows = new[]
            {
                new StatBarRow { Bar = barHealth, Value = valueHealth },
                new StatBarRow { Bar = barEnergy, Value = valueEnergy },
                new StatBarRow { Bar = barGems, Value = valueGems },
                new StatBarRow { Bar = barPeople, Value = valuePeople },
            };
        }

        /// <summary>
        /// Polls ECS each frame and updates four stat rows. Uses last-good cache when ship entity
        /// lookups are gated; only zeros bars when the local ship is truly gone.
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (!_layoutApplied)
                ApplyLayoutToAllRows();
            if (!_barsStyled)
                ApplySquareBarStyleToAll();

            bool hasShip = EcsGameBridge.TryGetLocalShipState(out var ship);
            if (hasShip)
            {
                _cachedShip = ship;
                _hasHudCache = true;
            }
            else if (_hasHudCache &&
                     (EcsGameBridge.HasLocalPlayerShip() || ShipDisplayPose.HasLocalPose) &&
                     !ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                // [TITAN-ORBIT] GhostSpawnBacklog briefly skips ship scans — keep last vitals painted.
                ship = _cachedShip;
                hasShip = true;
            }

            if (!hasShip)
            {
                UpdateRow(ref _rows[0], 0f, 0f, 0);
                UpdateRow(ref _rows[1], 0f, 0f, 1);
                UpdateRow(ref _rows[2], 0f, 0f, 2);
                UpdateRow(ref _rows[3], 0f, 0f, 3);
                return;
            }

            UpdateRow(ref _rows[0], ship.Health, ship.MaxHealth, 0);
            UpdateRow(ref _rows[1], ship.CurrentEnergy, ship.MaxEnergy, 1);

            // --- Gems row ---
            // [TITAN-ORBIT] While depositing, show metronome chunk cargo so the bar drops by
            // ShipLevel with each beat — not ghost drip. Ignore a false optimistic 0 while ghost
            // still has cargo (bad seed).
            float displayGems = ship.CurrentGems;
            if (MoonOrbitClientState.WantDepositGems &&
                MoonOrbitClientState.TryGetOptimisticDepositCargo(out float optimisticCargo) &&
                !(optimisticCargo <= 0.001f && ship.CurrentGems > 0.001f))
            {
                displayGems = optimisticCargo;
            }
            else if (MoonOrbitClientState.WantDepositGems)
            {
                MoonOrbitClientState.EnsureOptimisticDepositCargoSeed(ship.CurrentGems);
            }

            UpdateRow(ref _rows[2], displayGems, ship.GemCapacity, 2);
            UpdateRow(ref _rows[3], ship.CurrentPeople, ship.PeopleCapacity, 3);
        }

        void UpdateRow(ref StatBarRow row, float current, float max, int dirtyIndex)
        {
            // --- Per-frame refresh ---
            float displayCurrent = max > 0.0001f ? Mathf.Min(current, max) : current;
            float fill01 = max > 0.0001f ? Mathf.Clamp01(displayCurrent / max) : 0f;
            SetBarFill(row.Bar, fill01);

            if (row.Value == null)
                return;

            int curInt = Mathf.RoundToInt(displayCurrent);
            int maxInt = Mathf.RoundToInt(max);
            string text = maxInt > 0 ? $"{curInt}/{maxInt}" : curInt.ToString();
            if (_lastValueText[dirtyIndex] == text)
                return;

            row.Value.text = text;
            _lastValueText[dirtyIndex] = text;
        }

        static void SetBarFill(Slider bar, float fill01)
        {
            // --- SetBarFill ---
            if (bar == null)
                return;

            fill01 = Mathf.Clamp01(fill01);
            if (bar.fillRect != null)
            {
                Vector2 min = bar.fillRect.anchorMin;
                Vector2 max = bar.fillRect.anchorMax;
                max.x = fill01;
                bar.fillRect.anchorMin = min;
                bar.fillRect.anchorMax = max;
            }

            bar.SetValueWithoutNotify(fill01);
        }

        void ApplyLayoutToAllRows()
        {
            // --- Apply changes ---
            if (_rows == null)
                CacheRows();
            for (int i = 0; i < _rows.Length; i++)
                ApplyRowLayout(ref _rows[i]);
            _layoutApplied = true;
        }

        void ApplyRowLayout(ref StatBarRow row)
        {
            // --- Apply changes ---
            if (row.Value != null)
            {
                var tmp = row.Value;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.alignment = TextAlignmentOptions.MidlineLeft;
                if (tmp.fontSize < 13f)
                    tmp.fontSize = 14f;

                RectTransform valueRect = tmp.rectTransform;
                valueRect.anchorMin = new Vector2(1f, 0.5f);
                valueRect.anchorMax = new Vector2(1f, 0.5f);
                valueRect.pivot = new Vector2(1f, 0.5f);
                valueRect.anchoredPosition = new Vector2(-valueColumnInset, 0f);
                float columnWidth = Mathf.Max(valueColumnWidth, valueRect.sizeDelta.x);
                valueRect.sizeDelta = new Vector2(columnWidth, valueRect.sizeDelta.y);
            }

            if (row.Bar != null)
            {
                RectTransform barRect = row.Bar.GetComponent<RectTransform>();
                barRect.anchorMin = new Vector2(0f, 0f);
                barRect.anchorMax = new Vector2(1f, 1f);
                barRect.pivot = new Vector2(0.5f, 0.5f);
                barRect.anchoredPosition = Vector2.zero;
                barRect.sizeDelta = Vector2.zero;

                float barLeft = 48f;
                Transform icon = barRect.parent != null ? barRect.parent.Find("Icon") : null;
                if (icon is RectTransform iconRect)
                    barLeft = iconRect.anchoredPosition.x + iconRect.sizeDelta.x + 8f;

                float barRightInset = valueColumnWidth + valueColumnInset + barValueGap;
                barRect.offsetMin = new Vector2(barLeft, 2f);
                barRect.offsetMax = new Vector2(-barRightInset, -2f);
            }
        }

        void ApplySquareBarStyleToAll()
        {
            // --- Apply changes ---
            if (_rows == null)
                CacheRows();
            for (int i = 0; i < _rows.Length; i++)
                ApplySquareBarStyle(_rows[i].Bar);
            _barsStyled = true;
        }

        static void ApplySquareBarStyle(Slider slider)
        {
            // --- Apply changes ---
            if (slider == null)
                return;

            Sprite square = GetSquareBarSprite();
            var images = slider.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] == null)
                    continue;
                images[i].sprite = square;
                images[i].type = Image.Type.Simple;
            }

            RectTransform fillArea = slider.fillRect != null ? slider.fillRect.parent as RectTransform : null;
            if (fillArea != null)
            {
                fillArea.offsetMin = Vector2.zero;
                fillArea.offsetMax = Vector2.zero;
                fillArea.anchorMin = Vector2.zero;
                fillArea.anchorMax = Vector2.one;
            }
        }

        static Sprite GetSquareBarSprite()
        {
            // --- Compute value ---
            if (s_squareBarSprite != null)
                return s_squareBarSprite;

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            s_squareBarSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            return s_squareBarSprite;
        }
    }
}
