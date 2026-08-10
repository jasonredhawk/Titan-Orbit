using TitanOrbit.Core;
using TitanOrbit.ECS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>Full-screen overlay when a team captures all planets.</summary>
    public class MatchEndScreenController : MonoBehaviour
    {
        [SerializeField] GameObject overlayRoot;
        [SerializeField] TextMeshProUGUI titleText;
        [SerializeField] TextMeshProUGUI subtitleText;
        [SerializeField] Button continueButton;

        TeamId _shownWinner = TeamId.None;

        void Awake()
        {
            EnsureUi();
            Hide();
        }

        void Update()
        {
            // --- Per-frame refresh ---
            if (!EcsGameBridge.TryGetMatchState(out var match))
            {
                if (_shownWinner != TeamId.None)
                    Hide();
                return;
            }

            if (match.WinningTeam == TeamId.None)
            {
                if (_shownWinner != TeamId.None)
                    Hide();
                return;
            }

            if (_shownWinner == match.WinningTeam)
                return;

            _shownWinner = match.WinningTeam;
            ShowWinner(match.WinningTeam, match.MatchTimer);
        }

        void ShowWinner(TeamId team, float matchSeconds)
        {
            // --- ShowWinner ---
            EnsureUi();
            if (overlayRoot != null)
                overlayRoot.SetActive(true);

            if (titleText != null)
            {
                titleText.text = $"{FormatTeamName(team)} Wins!";
                titleText.color = team.ToColor();
            }

            if (subtitleText != null)
                subtitleText.text = $"Match time: {Mathf.RoundToInt(matchSeconds)}s";

            if (continueButton != null)
            {
                continueButton.onClick.RemoveAllListeners();
                continueButton.onClick.AddListener(Hide);
            }
        }

        void Hide()
        {
            // --- Hide ---
            _shownWinner = TeamId.None;
            if (overlayRoot != null)
                overlayRoot.SetActive(false);
        }

        static string FormatTeamName(TeamId team) =>
            team switch
            {
                TeamId.TeamA => "Team A",
                TeamId.TeamB => "Team B",
                TeamId.TeamC => "Team C",
                TeamId.TeamD => "Team D",
                TeamId.TeamE => "Team E",
                _ => "Unknown Team",
            };

        void EnsureUi()
        {
            // --- Ensure setup ---
            if (overlayRoot != null && titleText != null)
                return;

            var canvasGo = new GameObject("MatchEndOverlay");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9000;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            overlayRoot = canvasGo;

            var panel = new GameObject("Panel");
            panel.transform.SetParent(canvasGo.transform, false);
            var panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0f, 0f, 0f, 0.72f);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = Vector2.zero;
            panelRt.anchorMax = Vector2.one;
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            titleText = CreateLabel(panel.transform, "Title", new Vector2(0f, 80f), 52f, FontStyles.Bold);
            subtitleText = CreateLabel(panel.transform, "Subtitle", new Vector2(0f, 10f), 28f, FontStyles.Normal);

            var buttonGo = new GameObject("ContinueButton");
            buttonGo.transform.SetParent(panel.transform, false);
            var buttonRt = buttonGo.AddComponent<RectTransform>();
            buttonRt.anchorMin = buttonRt.anchorMax = new Vector2(0.5f, 0.5f);
            buttonRt.pivot = new Vector2(0.5f, 0.5f);
            buttonRt.anchoredPosition = new Vector2(0f, -90f);
            buttonRt.sizeDelta = new Vector2(220f, 48f);

            var buttonImage = buttonGo.AddComponent<Image>();
            buttonImage.color = new Color(0.2f, 0.22f, 0.28f, 0.95f);
            continueButton = buttonGo.AddComponent<Button>();

            var buttonLabel = CreateLabel(buttonGo.transform, "Label", Vector2.zero, 24f, FontStyles.Bold);
            buttonLabel.text = "Continue";
            var labelRt = buttonLabel.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
        }

        static TextMeshProUGUI CreateLabel(Transform parent, string name, Vector2 anchoredPos, float fontSize, FontStyles style)
        {
            // --- Create instance ---
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = new Vector2(700f, 80f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = Color.white;
            return tmp;
        }
    }
}
