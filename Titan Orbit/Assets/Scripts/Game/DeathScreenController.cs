using TitanOrbit.ECS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen overlay while the local ship is destroyed and waiting to respawn. Reads
    /// <see cref="EcsGameBridge"/> local ship death state each frame; shows countdown from
    /// server-authoritative respawn timer. Client presentation only — respawn is server-driven.
    /// Hidden when not in-game or when the ship is alive again.
    /// </summary>
    public class DeathScreenController : MonoBehaviour
    {
        [SerializeField] GameObject overlayRoot;
        [SerializeField] TextMeshProUGUI messageText;

        bool _wasDead;
        float _clientDeathStartTime = -1f;

        void Awake()
        {
            EnsureUi();
            Hide();
        }

        void Update()
        {
            // --- Guard: only show overlay when in-game with a local ship ghost ---
            if (!EcsGameBridge.IsNetworkInGame() || !EcsGameBridge.HasLocalPlayerShip())
            {
                if (_wasDead)
                    Hide();
                _wasDead = false;
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                if (_wasDead)
                    Hide();
                _wasDead = false;
                return;
            }

            // --- Alive again: dismiss overlay ---
            if (!ship.IsDead)
            {
                if (_wasDead)
                    Hide();
                _wasDead = false;
                _clientDeathStartTime = -1f;
                return;
            }

            if (!_wasDead)
                _clientDeathStartTime = Time.time;

            _wasDead = true;
            Show();

            // --- Countdown from server RespawnAtTime (authoritative) ---
            float remaining = ShipRespawnSystem.RespawnDelaySeconds;
            if (EcsGameBridge.TryGetLocalShipDeathState(out var death))
            {
                var world = EcsGameBridge.GetVisualizationWorld();
                if (world != null && world.IsCreated)
                {
                    double elapsed = world.Time.ElapsedTime;
                    remaining = Mathf.Max(0f, death.RespawnAtTime - (float)elapsed);
                }
            }
            else if (_clientDeathStartTime >= 0f)
            {
                remaining = Mathf.Max(0f, ShipRespawnSystem.RespawnDelaySeconds - (Time.time - _clientDeathStartTime));
            }

            if (messageText != null)
            {
                messageText.text = remaining > 0.05f
                    ? $"Ship destroyed\nRespawning in {Mathf.CeilToInt(remaining)}..."
                    : "Respawning...";
            }
        }

        void Show()
        {
            EnsureUi();
            if (overlayRoot != null)
                overlayRoot.SetActive(true);
        }

        void Hide()
        {
            if (overlayRoot != null)
                overlayRoot.SetActive(false);
        }

        void EnsureUi()
        {
            if (overlayRoot != null && messageText != null)
                return;

            var canvasGo = new GameObject("DeathOverlay");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 8500;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;

            overlayRoot = canvasGo;

            var labelGo = new GameObject("Message");
            labelGo.transform.SetParent(canvasGo.transform, false);
            var rt = labelGo.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, 40f);
            rt.sizeDelta = new Vector2(520f, 120f);

            messageText = labelGo.AddComponent<TextMeshProUGUI>();
            messageText.alignment = TextAlignmentOptions.Center;
            messageText.fontSize = 34f;
            messageText.fontStyle = FontStyles.Bold;
            messageText.color = new Color(1f, 0.35f, 0.3f, 1f);
        }
    }
}
