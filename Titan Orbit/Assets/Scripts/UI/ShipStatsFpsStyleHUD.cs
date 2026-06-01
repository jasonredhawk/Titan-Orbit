using UnityEngine;
using Unity.Netcode;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Top-left ship stats HUD: horizontal progress bars (Health, Energy, Gems, People)
    /// with icons, square-edged fills, capacity notches, and current/max labels.
    /// </summary>
    public class ShipStatsFpsStyleHUD : MonoBehaviour
    {
        private enum StatBarKind
        {
            Health,
            Energy,
            Gems,
            People
        }

        private struct StatBarRow
        {
            public Slider Bar;
            public TextMeshProUGUI Value;
            public StatBarKind Kind;
            public ShipStatBarNotchesUI Notches;
        }

        [Header("Layout")]
        [SerializeField] private float valueColumnWidth = 92f;
        [SerializeField] private float valueColumnInset = 8f;

        [Header("References (assigned by GameSetup or inspector)")]
        [SerializeField] private Image iconHealth;
        [SerializeField] private Image iconEnergy;
        [SerializeField] private Image iconGems;
        [SerializeField] private Image iconPeople;
        [SerializeField] private Slider barHealth;
        [SerializeField] private Slider barEnergy;
        [SerializeField] private Slider barGems;
        [SerializeField] private Slider barPeople;
        [SerializeField] private TextMeshProUGUI valueHealth;
        [SerializeField] private TextMeshProUGUI valueEnergy;
        [SerializeField] private TextMeshProUGUI valueGems;
        [SerializeField] private TextMeshProUGUI valuePeople;

        [Header("Orbit-only (shown at end of Gems/People bars when in orbit)")]
        [SerializeField] private Button btnDepositGems;
        [SerializeField] private Button btnLoadPeopleUp;
        [SerializeField] private Button btnUnloadPeopleDown;

        private Starship _playerShip;
        private ulong _boundShipNetworkId;
        private static Sprite s_squareBarSprite;
        private bool _barsStyled;
        private bool _layoutApplied;
        private StatBarRow[] _rows;

        private void Awake()
        {
            CacheRows();
            ApplyLayoutToAllRows();
            ApplySquareBarStyleToAll();
        }

        private void OnEnable()
        {
            CacheRows();
            ApplyLayoutToAllRows();
            ApplySquareBarStyleToAll();
            InvalidateShipBinding();

            if (btnDepositGems != null) btnDepositGems.onClick.RemoveAllListeners();
            if (btnLoadPeopleUp != null) btnLoadPeopleUp.onClick.RemoveAllListeners();
            if (btnUnloadPeopleDown != null) btnUnloadPeopleDown.onClick.RemoveAllListeners();

            if (btnDepositGems != null) btnDepositGems.onClick.AddListener(OnDepositGemsClick);
            if (btnLoadPeopleUp != null) btnLoadPeopleUp.onClick.AddListener(OnLoadPeopleUp);
            if (btnUnloadPeopleDown != null) btnUnloadPeopleDown.onClick.AddListener(OnUnloadPeopleDown);
        }

        private void CacheRows()
        {
            _rows = new[]
            {
                new StatBarRow { Bar = barHealth, Value = valueHealth, Kind = StatBarKind.Health },
                new StatBarRow { Bar = barEnergy, Value = valueEnergy, Kind = StatBarKind.Energy },
                new StatBarRow { Bar = barGems, Value = valueGems, Kind = StatBarKind.Gems },
                new StatBarRow { Bar = barPeople, Value = valuePeople, Kind = StatBarKind.People },
            };
        }

        private void InvalidateShipBinding()
        {
            _playerShip = null;
            _boundShipNetworkId = 0;
        }

        private Starship GetPlayerShip()
        {
            if (_playerShip != null)
            {
                if (_playerShip.IsSpawned && !_playerShip.IsDead && _playerShip.IsOwner)
                {
                    var net = _playerShip.NetworkObject;
                    if (net != null && net.NetworkObjectId == _boundShipNetworkId)
                        return _playerShip;
                }
                InvalidateShipBinding();
            }

            Starship resolved = TryResolveLocalStarship();
            if (resolved != null)
            {
                _playerShip = resolved;
                _boundShipNetworkId = resolved.NetworkObject != null ? resolved.NetworkObject.NetworkObjectId : 0;
            }
            return _playerShip;
        }

        private static Starship TryResolveLocalStarship()
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.SpawnManager != null)
            {
                NetworkObject localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
                if (localPlayer != null)
                {
                    Starship ship = localPlayer.GetComponent<Starship>();
                    if (ship == null)
                        ship = localPlayer.GetComponentInChildren<Starship>(true);
                    if (ship != null && ship.IsSpawned && !ship.IsDead && ship.IsOwner)
                        return ship;
                }
            }

            Starship[] ships = Object.FindObjectsByType<Starship>(FindObjectsSortMode.None);
            for (int i = 0; i < ships.Length; i++)
            {
                Starship ship = ships[i];
                if (ship != null && ship.IsOwner && ship.IsSpawned && !ship.IsDead)
                    return ship;
            }
            return null;
        }

        private void LateUpdate()
        {
            if (!_layoutApplied)
                ApplyLayoutToAllRows();
            if (!_barsStyled)
                ApplySquareBarStyleToAll();

            Starship ship = GetPlayerShip();
            if (ship == null)
            {
                UpdateRow(ref _rows[0], 0f, 0f);
                UpdateRow(ref _rows[1], 0f, 0f);
                UpdateRow(ref _rows[2], 0f, 0f);
                UpdateRow(ref _rows[3], 0f, 0f);
                SetOrbitButtonsVisible(false);
                return;
            }

            UpdateRow(ref _rows[0], ship.CurrentHealth, ship.MaxHealth);
            UpdateRow(ref _rows[1], ship.CurrentEnergy, ship.EnergyCapacity);
            UpdateRow(ref _rows[2], ship.CurrentGems, ship.GemCapacity);
            UpdateRow(ref _rows[3], ship.CurrentPeople, ship.PeopleCapacity);

            SetOrbitButtonsVisible(false);
        }

        private void UpdateRow(ref StatBarRow row, float current, float max)
        {
            float fill01 = max > 0.0001f ? Mathf.Clamp01(current / max) : 0f;
            SetBarFill(row.Bar, fill01);

            if (row.Value != null)
            {
                int curInt = Mathf.FloorToInt(current);
                int maxInt = Mathf.FloorToInt(max);
                row.Value.text = maxInt > 0 ? $"{curInt}/{maxInt}" : curInt.ToString();
            }

            if (row.Notches == null && row.Bar != null)
                row.Notches = EnsureNotches(row.Bar);
            if (row.Notches != null)
                row.Notches.SetSegmentCount(ComputeSegmentCount(row.Kind, max));
        }

        /// <summary>How many segments (and thus notches) to draw for this stat's max value.</summary>
        private static int ComputeSegmentCount(StatBarKind kind, float max)
        {
            if (max <= 0.0001f) return 1;

            switch (kind)
            {
                case StatBarKind.Gems:
                case StatBarKind.People:
                {
                    int cap = Mathf.Max(1, Mathf.CeilToInt(max));
                    if (cap <= 1) return 1;
                    // One segment per discrete unit when capacity is modest; coarser steps when large.
                    if (cap <= 20) return cap;
                    int step = Mathf.Max(1, Mathf.CeilToInt(cap / 10f));
                    return Mathf.Clamp(Mathf.CeilToInt(cap / (float)step), 4, 12);
                }
                case StatBarKind.Health:
                case StatBarKind.Energy:
                default:
                    return 4;
            }
        }

        private static void SetBarFill(Slider bar, float fill01)
        {
            if (bar == null) return;
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

        private void ApplyLayoutToAllRows()
        {
            if (_rows == null) CacheRows();
            for (int i = 0; i < _rows.Length; i++)
                ApplyRowLayout(ref _rows[i]);
            _layoutApplied = true;
        }

        private void ApplyRowLayout(ref StatBarRow row)
        {
            if (row.Bar != null)
            {
                RectTransform barRect = row.Bar.GetComponent<RectTransform>();
                float barRightInset = valueColumnWidth + valueColumnInset;
                barRect.offsetMax = new Vector2(-barRightInset, barRect.offsetMax.y);

                row.Notches = EnsureNotches(row.Bar);
            }

            if (row.Value != null)
            {
                TextMeshProUGUI tmp = row.Value;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.alignment = TextAlignmentOptions.MidlineRight;
                if (tmp.fontSize < 13f) tmp.fontSize = 14f;

                RectTransform valueRect = tmp.rectTransform;
                valueRect.anchorMin = new Vector2(1f, 0.5f);
                valueRect.anchorMax = new Vector2(1f, 0.5f);
                valueRect.pivot = new Vector2(1f, 0.5f);
                valueRect.anchoredPosition = new Vector2(-valueColumnInset * 0.5f, 0f);
                valueRect.sizeDelta = new Vector2(valueColumnWidth, valueRect.sizeDelta.y);
            }
        }

        private static ShipStatBarNotchesUI EnsureNotches(Slider bar)
        {
            Transform existing = bar.transform.Find("Notches");
            GameObject notchRoot;
            if (existing != null)
                notchRoot = existing.gameObject;
            else
            {
                notchRoot = new GameObject("Notches", typeof(RectTransform), typeof(ShipStatBarNotchesUI));
                notchRoot.transform.SetParent(bar.transform, false);
                notchRoot.transform.SetAsLastSibling();

                RectTransform rt = notchRoot.GetComponent<RectTransform>();
                Transform bg = bar.transform.Find("Background");
                if (bg is RectTransform bgRect)
                {
                    rt.anchorMin = bgRect.anchorMin;
                    rt.anchorMax = bgRect.anchorMax;
                    rt.offsetMin = bgRect.offsetMin;
                    rt.offsetMax = bgRect.offsetMax;
                    rt.pivot = bgRect.pivot;
                }
                else
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }
            }

            var notches = notchRoot.GetComponent<ShipStatBarNotchesUI>();
            notches.BindTrack(notchRoot.GetComponent<RectTransform>());
            return notches;
        }

        private void ApplySquareBarStyleToAll()
        {
            if (_rows == null) CacheRows();
            for (int i = 0; i < _rows.Length; i++)
                ApplySquareBarStyle(_rows[i].Bar);
            _barsStyled = true;
        }

        private static void ApplySquareBarStyle(Slider slider)
        {
            if (slider == null) return;

            Sprite square = GetSquareBarSprite();
            Image[] images = slider.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                Image img = images[i];
                if (img == null) continue;
                if (img.GetComponentInParent<ShipStatBarNotchesUI>() != null)
                    continue;
                img.sprite = square;
                img.type = Image.Type.Simple;
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

        private static Sprite GetSquareBarSprite()
        {
            if (s_squareBarSprite != null) return s_squareBarSprite;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            s_squareBarSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            return s_squareBarSprite;
        }

        private void SetOrbitButtonsVisible(bool visible)
        {
            if (btnDepositGems != null) btnDepositGems.gameObject.SetActive(visible);
            if (btnLoadPeopleUp != null) btnLoadPeopleUp.gameObject.SetActive(visible);
            if (btnUnloadPeopleDown != null) btnUnloadPeopleDown.gameObject.SetActive(visible);
        }

        private void OnDepositGemsClick()
        {
            var ship = GetPlayerShip();
            if (ship == null) return;
            ship.SetWantToDepositGemsServerRpc(!ship.WantToDepositGems);
        }

        private void OnLoadPeopleUp()
        {
            var ship = GetPlayerShip();
            if (ship != null && ship.IsInOrbit)
            {
                ship.SetWantToLoadPeopleServerRpc(true);
                ship.SetWantToUnloadPeopleServerRpc(false);
            }
        }

        private void OnUnloadPeopleDown()
        {
            var ship = GetPlayerShip();
            if (ship != null && ship.IsInOrbit)
            {
                ship.SetWantToUnloadPeopleServerRpc(true);
                ship.SetWantToLoadPeopleServerRpc(false);
            }
        }
    }
}
