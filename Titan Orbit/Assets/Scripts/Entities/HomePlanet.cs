using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using System.Collections;
using System.Collections.Generic;
using SpaceGraphicsToolkit;
using SpaceGraphicsToolkit.Atmosphere;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Special planet type that serves as a team's home base
    /// Cannot be neutral, has level system, and elimination condition
    /// Tracks per-player contributed gems for the Home Planet store.
    /// </summary>
    public class HomePlanet : Planet
    {
        /// <summary>All active HomePlanet instances. Updated on network spawn/despawn.</summary>
        public static readonly List<HomePlanet> AllHomePlanets = new List<HomePlanet>();
        [Header("Home Planet Settings")]
        [Tooltip("Max starship level allowed at each home planet level. Ship cannot exceed planet level. Level 7 (MEGA) requires planet 6 + full gems.")]
        [SerializeField] private int[] maxShipLevelPerPlanetLevel = { 0, 1, 2, 3, 4, 5, 6 }; // Planet level N → max ship level N (ship 7 is special)

        [Header("Level Visuals")]
        [Tooltip("Scale pulse multiplier when leveling up (e.g. 1.15 = 15% bigger briefly).")]
        [SerializeField] private float levelUpPulseScale = 1.15f;
        [Tooltip("Duration of scale-up phase of level-up pulse (seconds).")]
        [SerializeField] private float levelUpPulseUpDuration = 0.2f;
        [Tooltip("Duration of scale-down phase of level-up pulse (seconds).")]
        [SerializeField] private float levelUpPulseDownDuration = 0.3f;

        private NetworkVariable<TeamManager.Team> assignedTeam = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);

        private Vector3 baseLocalScale;

        /// <summary>Server-only: gems each player has contributed to this home planet (for store purchases).</summary>
        private Dictionary<ulong, float> contributedGemsByClientId = new Dictionary<ulong, float>();

        public int HomePlanetLevel => PlanetLevel;
        public TeamManager.Team AssignedTeam => assignedTeam.Value;
        public int MaxShipLevel => GetMaxShipLevelForPlanetLevel(PlanetLevel);

        /// <summary>
        /// Called by MapGenerator at spawn to set team and color. Call before NetworkObject.Spawn().
        /// </summary>
        public void InitForTeam(TeamManager.Team team)
        {
            assignedTeam.Value = team;
            SetInitialTeamOwnership(team); // Updates visual via Planet's team ownership
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (!AllHomePlanets.Contains(this))
                AllHomePlanets.Add(this);
            EnsureSolidColliderAndOrbitZone();
            if (IsServer)
                SetGrowthRate(GetGrowthRatePerSecond());
            baseLocalScale = transform.localScale;
            RemoveOldCylinderRings();
            EnsureShapesRingsDrawer();
            EnsureWaterComponents();
            EnsureAtmosphere();
            SetHomePlanetWaterLevel();
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            AllHomePlanets.Remove(this);
        }

        /// <summary>Set SGT planet water level so tropical water is visible (tropical materials use _HasWater).</summary>
        private void SetHomePlanetWaterLevel()
        {
            var sgt = GetPlanetVisualTargetObject().GetComponent<SpaceGraphicsToolkit.SgtPlanet>();
            if (sgt != null)
                sgt.WaterLevel = 0.2f;
        }

        protected override Color GetPopulationTextColor() => new Color(0.12f, 0.12f, 0.15f);
        protected override Color GetPopulationTextOutlineColor() => new Color(1f, 1f, 1f, 0.95f);
        // Keep text below camera when zoomed in: camera Y = 20*0.7 = 14, so localY*scale < 14 → localY < 0.7 for scale 20
        protected override Vector3 GetPopulationTextLocalPosition() => new Vector3(0f, 0.65f, 0f);
        protected override int GetPopulationTextRenderQueue() => (int)UnityEngine.Rendering.RenderQueue.Geometry + 100;

        /// <summary>Home planet surface is always tropical (water + atmosphere); team is shown only on rings.</summary>
        protected override Material GetEffectiveMaterialForPlanetSurface(TeamManager.Team team)
        {
            return GetNeutralMaterial();
        }

        /// <summary>Gem moon on home worlds is 1.5× the inverse-scaled size used for regular planets at the same PlanetSize.</summary>
        protected override float GetGemMoonHomeVisualScaleMultiplier() => MapGemMoonScaleMultiplier;

        /// <summary>Scale multiplier passed to <see cref="Planet.ComputeMapPlacementInfluenceRadiusWorld"/> for home-world ring spacing.</summary>
        public const float MapGemMoonScaleMultiplier = 1.5f;

        /// <summary>Starting level at spawn; used by map generation for ring clearance.</summary>
        public const int InitialSpawnLevel = 3;

        /// <summary>Initial planet level. Home planets start at 3.</summary>
        protected override int GetInitialPlanetLevel() => InitialSpawnLevel;

        /// <summary>Max level for home planets is 6.</summary>
        protected override int GetMaxLevel() => 6;

        /// <summary>Updates the orbit zone SphereCollider radius when level or setup changes. Home planets may use HomePlanetOrbitZone.</summary>
        protected override void RefreshOrbitZoneRadius()
        {
            var homeOz = GetComponentInChildren<HomePlanetOrbitZone>(true);
            if (homeOz != null)
            {
                foreach (var col in homeOz.GetComponents<SphereCollider>())
                {
                    if (col.isTrigger)
                    {
                        col.radius = GetOrbitRingOuterRadiusLocal();
                        break;
                    }
                }
            }

            base.RefreshOrbitZoneRadius();
        }

        /// <summary>
        /// Ensures body collider = planet sphere (radius 0.5). Orbit trigger + <see cref="PlanetOrbitZone"/> are created on the planet root by <see cref="Planet.EnsureOrbitZoneExists"/> from <see cref="Planet.OnNetworkSpawn"/>.
        /// </summary>
        private void EnsureSolidColliderAndOrbitZone()
        {
            SphereCollider bodyCollider = GetComponent<SphereCollider>();
            if (bodyCollider != null)
            {
                bodyCollider.isTrigger = false;
                bodyCollider.radius = 0.5f; // Match Unity primitive sphere (diameter 1)
            }
        }

        /// <summary>Server-only: apply gem deposit. Call this directly from server code instead of RPC when already on server.</summary>
        /// <param name="popupWorldPosition">Optional: where to show the floating gem count (e.g. gem moon). Defaults to planet center.</param>
        public void DepositGemsFromServer(float amount, TeamManager.Team depositingTeam, ulong depositingClientId, Vector3? popupWorldPosition = null)
        {
            if (!IsServer) return;
            // Only allow team members to deposit gems
            if (assignedTeam.Value == TeamManager.Team.None)
            {
                assignedTeam.Value = depositingTeam;
                SetInitialTeamOwnership(depositingTeam);
            }
            else if (assignedTeam.Value != depositingTeam)
            {
                return;
            }

            // Track contributed gems for this player (for store purchases) BEFORE calling base
            if (!contributedGemsByClientId.ContainsKey(depositingClientId))
                contributedGemsByClientId[depositingClientId] = 0f;
            contributedGemsByClientId[depositingClientId] += amount;

            base.DepositGemsFromServer(amount, depositingTeam, depositingClientId, popupWorldPosition);
        }

        [ServerRpc(RequireOwnership = false)]
        public new void DepositGemsServerRpc(float amount, TeamManager.Team depositingTeam, ulong depositingClientId)
        {
            DepositGemsFromServer(amount, depositingTeam, depositingClientId);
        }

        /// <summary>Server: add to contributed gems without depositing to planet level. Used when depositing at a captured planet (planet gets level gems; home gets store credit).</summary>
        public void AddContributedGemsFromServer(ulong clientId, float amount)
        {
            if (!IsServer) return;
            if (amount <= 0f) return;
            if (!contributedGemsByClientId.ContainsKey(clientId))
                contributedGemsByClientId[clientId] = 0f;
            contributedGemsByClientId[clientId] += amount;
        }

        /// <summary>Server: get contributed gems for a client. Used by store UI.</summary>
        public float GetContributedGems(ulong clientId)
        {
            return contributedGemsByClientId != null && contributedGemsByClientId.TryGetValue(clientId, out float v) ? v : 0f;
        }

        /// <summary>Server: spend contributed gems (e.g. store purchase). Returns true if successful.</summary>
        public bool TrySpendContributedGems(ulong clientId, float cost)
        {
            if (contributedGemsByClientId == null || !contributedGemsByClientId.TryGetValue(clientId, out float current) || current < cost)
                return false;
            contributedGemsByClientId[clientId] = current - cost;
            return true;
        }

        /// <summary>Server: refund contributed gems when a purchase could not be completed.</summary>
        public void RefundContributedGems(ulong clientId, float amount)
        {
            if (!IsServer || amount <= 0f) return;
            if (contributedGemsByClientId == null)
                contributedGemsByClientId = new Dictionary<ulong, float>();
            if (!contributedGemsByClientId.ContainsKey(clientId))
                contributedGemsByClientId[clientId] = 0f;
            contributedGemsByClientId[clientId] += amount;
        }

        /// <summary>Override to add scale pulse effect and auto-level ships when home planet levels up.</summary>
        protected override void OnPlanetLevelChanged(int previousLevel, int newLevel)
        {
            base.OnPlanetLevelChanged(previousLevel, newLevel);
            if (newLevel > previousLevel)
            {
                StartCoroutine(LevelUpScalePulse());
                Debug.Log($"Home Planet leveled up to level {newLevel}! Max ship level is now {GetMaxShipLevelForPlanetLevel(newLevel)}");
            }
        }

        /// <summary>Add SGT water components so home planet materials with water render correctly.</summary>
        private void EnsureWaterComponents()
        {
            GameObject target = GetPlanetVisualTargetObject();
            if (target.GetComponent<SgtPlanetWaterGradient>() == null)
                target.AddComponent<SgtPlanetWaterGradient>();
            if (target.GetComponent<SgtPlanetWaterTexture>() == null)
                target.AddComponent<SgtPlanetWaterTexture>();
        }

        /// <summary>Previously added SGT atmosphere for home planets; now disabled and cleans up any legacy instances.</summary>
        private void EnsureAtmosphere()
        {
            // Atmosphere visuals have been removed from home planets.
            // Clean up any legacy Atmosphere child and related components that might still exist on old prefabs/scenes.
            Transform existing = transform.Find("Atmosphere");
            if (existing != null)
            {
                Destroy(existing.gameObject);
            }

            foreach (var sgtAtmosphere in GetComponentsInChildren<SgtAtmosphere>(true))
            {
                Destroy(sgtAtmosphere.gameObject);
            }

            foreach (var model in GetComponentsInChildren<SpaceGraphicsToolkit.Atmosphere.SgtAtmosphereModel>(true))
            {
                Destroy(model.gameObject);
            }
        }

        /// <summary>Remove legacy cylinder-based Ring children so Shapes-drawn rings are the only ones visible.</summary>
        private void RemoveOldCylinderRings()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name == "Ring" || child.name.StartsWith("Ring"))
                    Object.Destroy(child.gameObject);
            }
        }

        /// <summary>Ensure a child with HomePlanetRingsDrawer exists so Saturn-style rings are drawn each frame.</summary>
        private void EnsureShapesRingsDrawer()
        {
            var allDrawers = GetComponentsInChildren<HomePlanetRingsDrawer>(true);
            HomePlanetRingsDrawer keep = null;
            foreach (var d in allDrawers)
            {
                if (d != null && d.transform.name == "HomePlanetRings")
                {
                    keep = d;
                    break;
                }
            }
            if (keep == null && allDrawers.Length > 0)
                keep = allDrawers[0];
            foreach (var d in allDrawers)
            {
                if (d == null || d == keep)
                    continue;
                Object.Destroy(d.gameObject);
            }

            if (keep != null)
                return;
            GameObject ringsObj = new GameObject("HomePlanetRings");
            ringsObj.transform.SetParent(transform);
            ringsObj.transform.localPosition = Vector3.zero;
            ringsObj.transform.localRotation = Quaternion.identity;
            ringsObj.transform.localScale = Vector3.one;
            ringsObj.AddComponent<HomePlanetRingsDrawer>();
        }

        private IEnumerator LevelUpScalePulse()
        {
            float elapsed = 0f;
            while (elapsed < levelUpPulseUpDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / levelUpPulseUpDuration;
                transform.localScale = Vector3.Lerp(baseLocalScale, baseLocalScale * levelUpPulseScale, t);
                yield return null;
            }
            transform.localScale = baseLocalScale * levelUpPulseScale;
            elapsed = 0f;
            while (elapsed < levelUpPulseDownDuration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / levelUpPulseDownDuration;
                transform.localScale = Vector3.Lerp(baseLocalScale * levelUpPulseScale, baseLocalScale, t);
                yield return null;
            }
            transform.localScale = baseLocalScale;
        }

        public int GetMaxShipLevelForPlanetLevel(int planetLevel)
        {
            if (planetLevel >= 1 && planetLevel < maxShipLevelPerPlanetLevel.Length)
            {
                return maxShipLevelPerPlanetLevel[planetLevel];
            }
            return 6; // Default (e.g. planet level 1+)
        }

        /// <summary>Gems required to reach this level (same as max capacity for that level).</summary>
        public float GetGemsThresholdForLevel(int level)
        {
            return GetMaxGemsForLevel(level);
        }

        /// <summary>True when home planet is level 6 and has at least the gem capacity for level 6 (unlocks ship level 7 MEGA).</summary>
        public bool IsFullGemsForLevel7Unlock()
        {
            if (PlanetLevel < 6) return false;
            return CurrentGems >= GetMaxGemsForLevel(6);
        }

        /// <summary>Gems still needed to fill current level capacity (and trigger level-up if not max level).</summary>
        public float GetGemsNeededForNextLevel()
        {
            int currentLevel = PlanetLevel;
            if (currentLevel >= 6) return 0f;
            float maxForLevel = GetMaxGemsForLevel(currentLevel);
            return Mathf.Max(0f, maxForLevel - CurrentGems);
        }

        public override bool CanBeCapturedBy(TeamManager.Team team)
        {
            // Home planets can be captured, but if captured, the team loses
            return assignedTeam.Value != TeamManager.Team.None && assignedTeam.Value != team;
        }

        [ServerRpc(RequireOwnership = false)]
        public void OnHomePlanetCapturedServerRpc(TeamManager.Team capturingTeam)
        {
            // This is called when home planet is captured
            // The team that owned this planet is eliminated
            if (assignedTeam.Value != TeamManager.Team.None)
            {
                EliminateTeamServerRpc(assignedTeam.Value);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void EliminateTeamServerRpc(TeamManager.Team eliminatedTeam)
        {
            if (TeamManager.Instance != null)
                TeamManager.Instance.EliminateTeamOnServer(eliminatedTeam);
            CheckWinConditions();
        }

        private void CheckWinConditions()
        {
            // This would be handled by GameManager or a separate WinConditionManager
            // For now, just log
            Debug.Log("Checking win conditions...");
        }
    }
}
