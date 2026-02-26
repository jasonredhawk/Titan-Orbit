using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using System.Collections.Generic;
using TitanOrbit.Entities;
using TitanOrbit.Core;
using TitanOrbit.Systems;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Main HUD: ship stats (top-left), home planet stats (top-right). Gaming-style layout.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        [Header("Ship Stats (Top-Left)")]
        [SerializeField] private Slider healthBar;
        [SerializeField] private TextMeshProUGUI healthText;
        [SerializeField] private Slider gemBar;
        [SerializeField] private TextMeshProUGUI gemCounter;
        [SerializeField] private Slider peopleBar;
        [SerializeField] private TextMeshProUGUI populationCounter;
        [SerializeField] private TextMeshProUGUI shipLevelText;
        [SerializeField] private TextMeshProUGUI shipTypeText;
        [SerializeField] private Image teamIndicator;

        [Header("Home Planet Stats (Top-Right)")]
        [SerializeField] private GameObject homePlanetPanel;
        [SerializeField] private TextMeshProUGUI homePlanetLevelText;
        [SerializeField] private Slider homePlanetGemBar;
        [SerializeField] private TextMeshProUGUI homePlanetGemsText;

        [Header("Team Colors")]
        [SerializeField] private Color teamAColor = Color.red;
        [SerializeField] private Color teamBColor = Color.blue;
        [SerializeField] private Color teamCColor = Color.green;

        [Header("Proximity Radar (planets around ship)")]
        [SerializeField] private GameObject proximityRadar;
        [SerializeField] private Key proximityRadarToggleKey = Key.R;

        [Header("Leaderboard (Right Side)")]
        [SerializeField] private GameObject leaderboardPanel;
        [SerializeField] private TextMeshProUGUI leaderboardTitleText;
        [SerializeField] private Key leaderboardToggleTeamKey = Key.Tab;
        [SerializeField] private float leaderboardRefreshInterval = 0.25f;

        private Starship playerShip;
        private int viewedTeamIndex = -1;
        private float nextLeaderboardRefreshTime;
        private ScrollRect leaderboardScrollRect;
        private RectTransform leaderboardViewportRect;
        private RectTransform leaderboardContentRect;
        private TextMeshProUGUI leaderboardEmptyText;
        private readonly List<LeaderboardRowWidgets> leaderboardRows = new List<LeaderboardRowWidgets>();
        private readonly TeamManager.Team[] teamOrder = new[]
        {
            TeamManager.Team.TeamA,
            TeamManager.Team.TeamB,
            TeamManager.Team.TeamC
        };
        private const float LeaderboardRowHeight = 56f;
        private const float LeaderboardRowSpacing = 8f;
        private const float LeaderboardContentPadding = 4f;

        private class LeaderboardRowWidgets
        {
            public GameObject root;
            public RectTransform badgeContainer;
            public TextMeshProUGUI rankText;
            public TextMeshProUGUI nameText;
            public TextMeshProUGUI scoreText;
        }

        private void Update()
        {
            // Toggle proximity radar (when off, GameObject is inactive so no Update or rendering)
            if (proximityRadar == null)
                proximityRadar = transform.Find("ProximityRadar")?.gameObject;

            var keyboard = Keyboard.current;
            if (proximityRadar != null && keyboard != null && keyboard[proximityRadarToggleKey].wasPressedThisFrame)
                proximityRadar.SetActive(!proximityRadar.activeSelf);
            if (keyboard != null && keyboard[leaderboardToggleTeamKey].wasPressedThisFrame)
                CycleViewedTeam();

            if (playerShip == null)
            {
                foreach (var ship in FindObjectsOfType<Starship>())
                {
                    if (ship.IsOwner) { playerShip = ship; break; }
                }
                if (playerShip != null && viewedTeamIndex < 0)
                    viewedTeamIndex = Mathf.Max(0, TeamToIndex(playerShip.ShipTeam));
            }
            if (playerShip == null || playerShip.IsDead)
            {
                UpdateLeaderboardPanel();
                return;
            }
            UpdateHUD();
        }

        private void UpdateHUD()
        {
            if (healthBar != null)
                healthBar.value = playerShip.CurrentHealth / playerShip.MaxHealth;

            if (healthText != null)
                healthText.text = $"{playerShip.CurrentHealth:F0}/{playerShip.MaxHealth:F0}";

            if (gemBar != null)
                gemBar.value = playerShip.GemCapacity > 0 ? playerShip.CurrentGems / playerShip.GemCapacity : 0f;

            if (gemCounter != null)
                gemCounter.text = $"Gems: {playerShip.CurrentGems:F0}/{playerShip.GemCapacity:F0}";

            if (peopleBar != null)
                peopleBar.value = playerShip.PeopleCapacity > 0 ? playerShip.CurrentPeople / playerShip.PeopleCapacity : 0f;

            if (populationCounter != null)
                populationCounter.text = $"People: {playerShip.CurrentPeople:F0}/{playerShip.PeopleCapacity:F0}";

            if (shipLevelText != null)
                shipLevelText.text = $"Level {playerShip.ShipLevel}";

            if (shipTypeText != null)
                shipTypeText.text = playerShip.FocusType.ToString();

            if (teamIndicator != null)
                teamIndicator.color = GetTeamColor(playerShip.ShipTeam);

            // Home planet stats (top-right): show player's team base
            UpdateHomePlanetPanel();
            UpdateLeaderboardPanel();
        }

        private void UpdateLeaderboardPanel()
        {
            EnsureLeaderboardPanelExists();
            if (leaderboardPanel == null || leaderboardTitleText == null || leaderboardContentRect == null) return;
            if (Time.time < nextLeaderboardRefreshTime) return;
            nextLeaderboardRefreshTime = Time.time + Mathf.Max(0.1f, leaderboardRefreshInterval);

            if (viewedTeamIndex < 0)
                viewedTeamIndex = Mathf.Max(0, TeamToIndex(playerShip != null ? playerShip.ShipTeam : TeamManager.Team.TeamA));

            TeamManager.Team viewedTeam = teamOrder[Mathf.Clamp(viewedTeamIndex, 0, teamOrder.Length - 1)];
            Dictionary<ulong, string> shipNameLookup = new Dictionary<ulong, string>();
            List<Starship> allShips = new List<Starship>();
            foreach (var ship in FindObjectsOfType<Starship>())
            {
                if (ship == null || !ship.IsSpawned) continue;
                allShips.Add(ship);
                string baseName = ship.GetComponent<TitanOrbit.AI.AIShipMarker>() != null
                    ? $"AI-{ship.NetworkObjectId % 1000}"
                    : $"Player {ship.OwnerClientId}";
                shipNameLookup[ship.NetworkObjectId] = baseName;
            }
            if (allShips.Count == 0)
            {
                for (int i = 0; i < leaderboardRows.Count; i++)
                    leaderboardRows[i].root.SetActive(false);
                if (leaderboardEmptyText != null)
                    leaderboardEmptyText.gameObject.SetActive(true);
                leaderboardTitleText.text = "Leaderboard   [TAB]";
                LayoutLeaderboardRows(0);
                return;
            }

            Dictionary<ulong, ScoreEntry> scoreByShipId = new Dictionary<ulong, ScoreEntry>();
            if (ScoreSystem.Instance != null && ScoreSystem.Instance.Entries != null)
            {
                foreach (var entry in ScoreSystem.Instance.Entries)
                    scoreByShipId[entry.ShipNetworkId] = entry;
            }

            List<Starship> teamShips = new List<Starship>();
            for (int i = 0; i < allShips.Count; i++)
            {
                if (allShips[i].ShipTeam == viewedTeam)
                    teamShips.Add(allShips[i]);
            }

            // If selected team is empty, fall back to local player's current team so teammates are visible by default.
            TeamManager.Team myTeam = playerShip != null ? playerShip.ShipTeam : TeamManager.Team.None;
            if (teamShips.Count == 0 && myTeam != TeamManager.Team.None && myTeam != viewedTeam)
            {
                viewedTeamIndex = TeamToIndex(myTeam);
                viewedTeam = teamOrder[Mathf.Clamp(viewedTeamIndex, 0, teamOrder.Length - 1)];
                teamShips.Clear();
                for (int i = 0; i < allShips.Count; i++)
                {
                    if (allShips[i].ShipTeam == viewedTeam)
                        teamShips.Add(allShips[i]);
                }
            }

            bool showingAllTeams = teamShips.Count == 0;
            if (showingAllTeams)
            {
                teamShips.AddRange(allShips);
                leaderboardTitleText.text = "All Teams Leaderboard   [TAB]";
            }
            else
            {
                leaderboardTitleText.text = $"{viewedTeam} Leaderboard   [TAB]";
            }

            List<ScoreEntry> rows = new List<ScoreEntry>();
            for (int i = 0; i < teamShips.Count; i++)
            {
                Starship ship = teamShips[i];
                if (scoreByShipId.TryGetValue(ship.NetworkObjectId, out ScoreEntry scored))
                {
                    rows.Add(scored);
                }
                else
                {
                    rows.Add(new ScoreEntry
                    {
                        ShipNetworkId = ship.NetworkObjectId,
                        OwnerClientId = ship.OwnerClientId,
                        Team = ship.ShipTeam,
                        Score = 0,
                        Kills = 0,
                        MinedGems = 0f,
                        DepositedGems = 0f,
                        HealedPeople = 0f,
                        TransportedPeople = 0f,
                        IsAI = ship.GetComponent<TitanOrbit.AI.AIShipMarker>() != null
                    });
                }
            }
            rows.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                int byKills = b.Kills.CompareTo(a.Kills);
                if (byKills != 0) return byKills;
                return a.OwnerClientId.CompareTo(b.OwnerClientId);
            });

            if (rows.Count == 0)
            {
                for (int i = 0; i < leaderboardRows.Count; i++)
                    leaderboardRows[i].root.SetActive(false);
                if (leaderboardEmptyText != null)
                    leaderboardEmptyText.gameObject.SetActive(true);
                LayoutLeaderboardRows(0);
                return;
            }
            if (leaderboardEmptyText != null)
                leaderboardEmptyText.gameObject.SetActive(false);

            ulong bestKillerId = 0;
            int bestKills = 0;
            ulong bestContributorId = 0;
            float bestContributed = 0f;
            ulong bestHealerId = 0;
            float bestHealed = 0f;
            ulong bestTransporterId = 0;
            float bestTransported = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                ScoreEntry row = rows[i];
                if (row.Kills > bestKills)
                {
                    bestKills = row.Kills;
                    bestKillerId = row.ShipNetworkId;
                }
                if (row.DepositedGems > bestContributed)
                {
                    bestContributed = row.DepositedGems;
                    bestContributorId = row.ShipNetworkId;
                }
                if (row.HealedPeople > bestHealed)
                {
                    bestHealed = row.HealedPeople;
                    bestHealerId = row.ShipNetworkId;
                }
                if (row.TransportedPeople > bestTransported)
                {
                    bestTransported = row.TransportedPeople;
                    bestTransporterId = row.ShipNetworkId;
                }
            }

            EnsureLeaderboardRowCount(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                ScoreEntry row = rows[i];
                string name = shipNameLookup.TryGetValue(row.ShipNetworkId, out string foundName)
                    ? foundName
                    : (row.IsAI ? $"AI-{row.ShipNetworkId % 1000}" : $"Player {row.OwnerClientId}");

                string shortName = name.Length > 22 ? name.Substring(0, 22) : name;
                LeaderboardRowWidgets widgets = leaderboardRows[i];
                widgets.root.SetActive(true);
                widgets.rankText.text = $"#{i + 1}";
                widgets.nameText.text = shortName;
                widgets.scoreText.text = row.Score.ToString();
                PopulateLeaderBadges(
                    widgets.badgeContainer,
                    bestKills > 0 && row.ShipNetworkId == bestKillerId,
                    bestContributed > 0f && row.ShipNetworkId == bestContributorId,
                    bestHealed > 0f && row.ShipNetworkId == bestHealerId,
                    bestTransported > 0f && row.ShipNetworkId == bestTransporterId
                );
            }
            for (int i = rows.Count; i < leaderboardRows.Count; i++)
                leaderboardRows[i].root.SetActive(false);
            LayoutLeaderboardRows(rows.Count);
        }

        private void EnsureLeaderboardRowCount(int count)
        {
            while (leaderboardRows.Count < count)
            {
                leaderboardRows.Add(CreateLeaderboardRow(leaderboardRows.Count));
            }
        }

        private void CycleViewedTeam()
        {
            viewedTeamIndex = (viewedTeamIndex + 1 + teamOrder.Length) % teamOrder.Length;
            nextLeaderboardRefreshTime = 0f;
        }

        private int TeamToIndex(TeamManager.Team team)
        {
            for (int i = 0; i < teamOrder.Length; i++)
            {
                if (teamOrder[i] == team) return i;
            }
            return 0;
        }

        private void EnsureLeaderboardPanelExists()
        {
            if (leaderboardPanel != null && leaderboardTitleText != null && leaderboardContentRect != null)
                return;

            leaderboardPanel = new GameObject("LeaderboardPanel");
            leaderboardPanel.transform.SetParent(transform, false);
            var panelRect = leaderboardPanel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 1f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 1f);
            panelRect.anchoredPosition = new Vector2(-16f, -128f);
            panelRect.sizeDelta = new Vector2(330f, 500f);
            var panelBg = leaderboardPanel.AddComponent<Image>();
            panelBg.color = new Color(0.06f, 0.08f, 0.12f, 0.86f);

            GameObject titleObj = new GameObject("Title");
            titleObj.transform.SetParent(leaderboardPanel.transform, false);
            var titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -8f);
            titleRect.sizeDelta = new Vector2(-16f, 26f);
            leaderboardTitleText = titleObj.AddComponent<TextMeshProUGUI>();
            leaderboardTitleText.fontSize = 16f;
            leaderboardTitleText.alignment = TextAlignmentOptions.TopLeft;
            leaderboardTitleText.color = new Color(0.8f, 0.88f, 1f);
            leaderboardTitleText.text = "Leaderboard";

            GameObject scrollObj = new GameObject("LeaderboardScroll");
            scrollObj.transform.SetParent(leaderboardPanel.transform, false);
            RectTransform scrollRectTransform = scrollObj.AddComponent<RectTransform>();
            scrollRectTransform.anchorMin = new Vector2(0f, 0f);
            scrollRectTransform.anchorMax = new Vector2(1f, 1f);
            scrollRectTransform.offsetMin = new Vector2(8f, 8f);
            scrollRectTransform.offsetMax = new Vector2(-8f, -38f);
            leaderboardScrollRect = scrollObj.AddComponent<ScrollRect>();
            leaderboardScrollRect.horizontal = false;
            leaderboardScrollRect.vertical = true;
            leaderboardScrollRect.movementType = ScrollRect.MovementType.Clamped;
            leaderboardScrollRect.scrollSensitivity = 24f;

            GameObject viewportObj = new GameObject("Viewport");
            viewportObj.transform.SetParent(scrollObj.transform, false);
            leaderboardViewportRect = viewportObj.AddComponent<RectTransform>();
            leaderboardViewportRect.anchorMin = Vector2.zero;
            leaderboardViewportRect.anchorMax = Vector2.one;
            leaderboardViewportRect.offsetMin = Vector2.zero;
            leaderboardViewportRect.offsetMax = Vector2.zero;
            Image viewportImage = viewportObj.AddComponent<Image>();
            // Mask needs a solid graphic to produce a reliable stencil region.
            viewportImage.color = Color.white;
            Mask viewportMask = viewportObj.AddComponent<Mask>();
            viewportMask.showMaskGraphic = false;

            GameObject contentObj = new GameObject("Content");
            contentObj.transform.SetParent(viewportObj.transform, false);
            leaderboardContentRect = contentObj.AddComponent<RectTransform>();
            leaderboardContentRect.anchorMin = new Vector2(0f, 1f);
            leaderboardContentRect.anchorMax = new Vector2(0f, 1f);
            leaderboardContentRect.pivot = new Vector2(0f, 1f);
            leaderboardContentRect.anchoredPosition = Vector2.zero;
            leaderboardContentRect.sizeDelta = new Vector2(300f, 100f);

            GameObject emptyObj = new GameObject("EmptyText");
            emptyObj.transform.SetParent(viewportObj.transform, false);
            RectTransform emptyRect = emptyObj.AddComponent<RectTransform>();
            emptyRect.anchorMin = new Vector2(0f, 0f);
            emptyRect.anchorMax = new Vector2(1f, 1f);
            emptyRect.offsetMin = new Vector2(12f, 12f);
            emptyRect.offsetMax = new Vector2(-12f, -12f);
            leaderboardEmptyText = emptyObj.AddComponent<TextMeshProUGUI>();
            leaderboardEmptyText.fontSize = 15f;
            leaderboardEmptyText.alignment = TextAlignmentOptions.Center;
            leaderboardEmptyText.color = new Color(0.72f, 0.8f, 0.92f);
            leaderboardEmptyText.text = "No players on this team.";
            leaderboardEmptyText.raycastTarget = false;

            leaderboardScrollRect.viewport = leaderboardViewportRect;
            leaderboardScrollRect.content = leaderboardContentRect;
            leaderboardScrollRect.verticalNormalizedPosition = 1f;
        }

        private void LayoutLeaderboardRows(int visibleCount)
        {
            if (leaderboardContentRect == null || leaderboardViewportRect == null) return;

            float y = -LeaderboardContentPadding;
            float viewportWidth = leaderboardViewportRect.rect.width;
            float viewportHeight = leaderboardViewportRect.rect.height;
            float contentWidth = Mathf.Max(1f, viewportWidth);
            float rowWidth = Mathf.Max(1f, contentWidth - LeaderboardContentPadding * 2f);
            for (int i = 0; i < visibleCount && i < leaderboardRows.Count; i++)
            {
                RectTransform rowRect = leaderboardRows[i].root.GetComponent<RectTransform>();
                if (rowRect == null) continue;
                rowRect.anchorMin = new Vector2(0f, 1f);
                rowRect.anchorMax = new Vector2(0f, 1f);
                rowRect.pivot = new Vector2(0f, 1f);
                rowRect.anchoredPosition = new Vector2(LeaderboardContentPadding, y);
                rowRect.sizeDelta = new Vector2(rowWidth, LeaderboardRowHeight);
                y -= LeaderboardRowHeight + LeaderboardRowSpacing;
            }

            float neededHeight = LeaderboardContentPadding * 2f;
            if (visibleCount > 0)
                neededHeight += visibleCount * LeaderboardRowHeight + (visibleCount - 1) * LeaderboardRowSpacing;
            leaderboardContentRect.sizeDelta = new Vector2(contentWidth, Mathf.Max(viewportHeight, neededHeight));
        }

        private LeaderboardRowWidgets CreateLeaderboardRow(int index)
        {
            GameObject rowObj = new GameObject($"Row_{index + 1}");
            rowObj.transform.SetParent(leaderboardContentRect, false);
            RectTransform rowRect = rowObj.AddComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0f, 56f);
            LayoutElement rowLayout = rowObj.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 56f;
            Image bg = rowObj.AddComponent<Image>();
            bg.color = index % 2 == 0
                ? new Color(0.11f, 0.16f, 0.23f, 0.92f)
                : new Color(0.09f, 0.13f, 0.19f, 0.88f);

            HorizontalLayoutGroup hlg = rowObj.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(10, 10, 8, 8);
            hlg.spacing = 10f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlHeight = true;
            hlg.childControlWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childForceExpandWidth = false;

            RectTransform badges = CreateCell(rowObj.transform, "Badges", 88f);
            HorizontalLayoutGroup badgesLayout = badges.gameObject.AddComponent<HorizontalLayoutGroup>();
            badgesLayout.spacing = 4f;
            badgesLayout.childAlignment = TextAnchor.MiddleLeft;
            badgesLayout.childControlWidth = false;
            badgesLayout.childControlHeight = false;
            badgesLayout.childForceExpandWidth = false;
            badgesLayout.childForceExpandHeight = false;

            TextMeshProUGUI rank = CreateRowLabel(CreateCell(rowObj.transform, "Rank", 40f), 14, TextAlignmentOptions.Center);
            rank.color = new Color(0.85f, 0.92f, 1f);

            RectTransform nameCell = CreateCell(rowObj.transform, "Name", -1f);
            LayoutElement nameLayout = nameCell.GetComponent<LayoutElement>();
            nameLayout.flexibleWidth = 1f;
            TextMeshProUGUI name = CreateRowLabel(nameCell, 16, TextAlignmentOptions.Left);
            name.color = Color.white;

            TextMeshProUGUI score = CreateRowLabel(CreateCell(rowObj.transform, "Score", 68f), 16, TextAlignmentOptions.Right);
            score.color = new Color(0.95f, 0.86f, 0.55f);

            return new LeaderboardRowWidgets
            {
                root = rowObj,
                badgeContainer = badges,
                rankText = rank,
                nameText = name,
                scoreText = score
            };
        }

        private static RectTransform CreateCell(Transform parent, string name, float width)
        {
            GameObject cellObj = new GameObject(name);
            cellObj.transform.SetParent(parent, false);
            RectTransform rect = cellObj.AddComponent<RectTransform>();
            LayoutElement layout = cellObj.AddComponent<LayoutElement>();
            if (width > 0f)
            {
                layout.preferredWidth = width;
                layout.minWidth = width;
            }
            return rect;
        }

        private static TextMeshProUGUI CreateRowLabel(RectTransform parent, int fontSize, TextAlignmentOptions align)
        {
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(parent, false);
            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            TextMeshProUGUI text = textObj.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.alignment = align;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }

        private void PopulateLeaderBadges(RectTransform parent, bool isBestKiller, bool isBestContributor, bool isBestHealer, bool isBestTransporter)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
                Destroy(parent.GetChild(i).gameObject);

            LayoutElement layout = parent.GetComponent<LayoutElement>();
            int count = 0;
            if (isBestKiller) { CreateBadge(parent, "K", new Color(0.96f, 0.36f, 0.36f)); count++; }
            if (isBestContributor) { CreateBadge(parent, "C", new Color(0.95f, 0.76f, 0.34f)); count++; }
            if (isBestHealer) { CreateBadge(parent, "H", new Color(0.38f, 0.92f, 0.5f)); count++; }
            if (isBestTransporter) { CreateBadge(parent, "T", new Color(0.35f, 0.78f, 0.96f)); count++; }
            if (layout != null)
            {
                float width = count > 0 ? Mathf.Min(88f, 6f + count * 16f + Mathf.Max(0, count - 1) * 4f) : 10f;
                layout.preferredWidth = width;
                layout.minWidth = width;
            }
        }

        private static void CreateBadge(RectTransform parent, string symbol, Color color)
        {
            GameObject badgeObj = new GameObject($"Badge_{symbol}");
            badgeObj.transform.SetParent(parent, false);
            RectTransform badgeRect = badgeObj.AddComponent<RectTransform>();
            badgeRect.sizeDelta = new Vector2(14f, 14f);
            LayoutElement layout = badgeObj.AddComponent<LayoutElement>();
            layout.preferredWidth = 14f;
            layout.preferredHeight = 14f;
            Image bg = badgeObj.AddComponent<Image>();
            bg.color = color;

            GameObject txtObj = new GameObject("Label");
            txtObj.transform.SetParent(badgeObj.transform, false);
            RectTransform txtRect = txtObj.AddComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;
            TextMeshProUGUI txt = txtObj.AddComponent<TextMeshProUGUI>();
            txt.text = symbol;
            txt.fontSize = 9;
            txt.alignment = TextAlignmentOptions.Center;
            txt.color = Color.white;
            txt.raycastTarget = false;
        }

        private void UpdateHomePlanetPanel()
        {
            HomePlanet homePlanet = GetHomePlanetForTeam(playerShip.ShipTeam);
            if (homePlanetPanel != null)
                homePlanetPanel.SetActive(homePlanet != null);
            if (homePlanet == null)
            {
                if (homePlanetLevelText != null) homePlanetLevelText.text = "—";
                if (homePlanetGemsText != null) homePlanetGemsText.text = "—";
                return;
            }
            if (homePlanetLevelText != null)
                homePlanetLevelText.text = $"Level {homePlanet.HomePlanetLevel}";
            
            if (homePlanetGemBar != null)
                homePlanetGemBar.value = homePlanet.MaxGems > 0 ? homePlanet.CurrentGems / homePlanet.MaxGems : 0f;
            
            if (homePlanetGemsText != null)
                homePlanetGemsText.text = $"{homePlanet.CurrentGems:F0} / {homePlanet.MaxGems:F0}";
        }

        private HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None) return null;
            foreach (var hp in FindObjectsOfType<HomePlanet>())
            {
                if (hp.AssignedTeam == team) return hp;
            }
            return null;
        }

        private Color GetTeamColor(Core.TeamManager.Team team)
        {
            switch (team)
            {
                case Core.TeamManager.Team.TeamA:
                    return teamAColor;
                case Core.TeamManager.Team.TeamB:
                    return teamBColor;
                case Core.TeamManager.Team.TeamC:
                    return teamCColor;
                default:
                    return Color.white;
            }
        }
    }
}
