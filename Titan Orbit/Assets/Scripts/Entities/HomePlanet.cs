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
        [Header("Home Planet Settings")]
        [Tooltip("Max gems capacity per level. Level 3=800, 4=1600, 5=3200, 6=6400. Filling capacity levels up the planet.")]
        private static readonly float[] MaxGemsPerLevel = { 0f, 0f, 0f, 800f, 1600f, 3200f, 6400f }; // Index = level (1-6)
        [Tooltip("Max starship level allowed at each home planet level. Ship cannot exceed planet level. Level 7 (MEGA) requires planet 6 + full gems.")]
        [SerializeField] private int[] maxShipLevelPerPlanetLevel = { 0, 1, 2, 3, 4, 5, 6 }; // Planet level N → max ship level N (ship 7 is special)

        [Header("Home Planet SGT: Water & Atmosphere")]
        [Tooltip("SGT Atmosphere material (e.g. CW Atmosphere + Lighting + Scattering). Required for atmosphere on home planets.")]
        [SerializeField] private Material atmosphereSourceMaterial;
        [Tooltip("SGT Atmosphere outer mesh (e.g. Geosphere40 from CW).")]
        [SerializeField] private Mesh atmosphereOuterMesh;

        [Header("Level Visuals")]
        [Tooltip("Scale pulse multiplier when leveling up (e.g. 1.15 = 15% bigger briefly).")]
        [SerializeField] private float levelUpPulseScale = 1.15f;
        [Tooltip("Duration of scale-up phase of level-up pulse (seconds).")]
        [SerializeField] private float levelUpPulseUpDuration = 0.2f;
        [Tooltip("Duration of scale-down phase of level-up pulse (seconds).")]
        [SerializeField] private float levelUpPulseDownDuration = 0.3f;

        private NetworkVariable<float> currentGems = new NetworkVariable<float>(0f);
        private NetworkVariable<int> homePlanetLevel = new NetworkVariable<int>(1);
        private NetworkVariable<TeamManager.Team> assignedTeam = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);

        private Vector3 baseLocalScale;

        /// <summary>Server-only: gems each player has contributed to this home planet (for store purchases).</summary>
        private Dictionary<ulong, float> contributedGemsByClientId = new Dictionary<ulong, float>();

        public float CurrentGems => currentGems.Value;
        /// <summary>Max gems this home planet can hold at its current level. Level 3=800, 4=1600, 5=3200, 6=6400.</summary>
        public float MaxGems => GetMaxGemsForLevel(homePlanetLevel.Value);
        public int HomePlanetLevel => homePlanetLevel.Value;
        public TeamManager.Team AssignedTeam => assignedTeam.Value;
        public int MaxShipLevel => GetMaxShipLevelForPlanetLevel(homePlanetLevel.Value);

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
            EnsureSolidColliderAndOrbitZone();
            if (IsServer)
            {
                homePlanetLevel.Value = 3; // Start at 3 so starships can level 1→2→3 without leveling planet
                currentGems.Value = 0f;
            }
            baseLocalScale = transform.localScale;
            RemoveOldCylinderRings();
            EnsureShapesRingsDrawer();
            EnsureWaterComponents();
            EnsureAtmosphere();
            SetHomePlanetWaterLevel();
            homePlanetLevel.OnValueChanged += OnLevelChanged;
        }

        /// <summary>Set SGT planet water level so tropical water is visible (tropical materials use _HasWater).</summary>
        private void SetHomePlanetWaterLevel()
        {
            var sgt = GetComponent<SpaceGraphicsToolkit.SgtPlanet>();
            if (sgt != null)
                sgt.WaterLevel = 0.2f;
        }

        /// <summary>Dark color so population text is readable on the white home planet ring.</summary>
        protected override Color GetPopulationTextColor() => new Color(0.12f, 0.12f, 0.15f);

        /// <summary>Position text above the ring so it's visible (not underneath).</summary>
        protected override Vector3 GetPopulationTextLocalPosition() => new Vector3(0f, 0.8f, 0f);

        /// <summary>Home planet surface is always tropical (water + atmosphere); team is shown only on rings.</summary>
        protected override Material GetEffectiveMaterialForPlanetSurface(TeamManager.Team team)
        {
            return GetNeutralMaterial();
        }

        /// <summary>Home planets have max 100 people (until level upgrade increases it later).</summary>
        protected override float GetMaxPopulationForPlanet() => 100f;

        /// <summary>Home planets: 1 person per 5 seconds.</summary>
        protected override float GetGrowthRatePerSecond() => 1f / 5f;

        /// <summary>
        /// Ensures body collider = planet sphere (radius 0.5), orbit zone = 0.5 to 0.85. Base Planet may already create zone; we fix sizes.
        /// </summary>
        private void EnsureSolidColliderAndOrbitZone()
        {
            SphereCollider bodyCollider = GetComponent<SphereCollider>();
            if (bodyCollider != null)
            {
                bodyCollider.isTrigger = false;
                bodyCollider.radius = 0.5f; // Match Unity primitive sphere (diameter 1)
            }
            PlanetOrbitZone existing = GetComponentInChildren<PlanetOrbitZone>();
            if (existing != null)
            {
                var col = existing.GetComponent<SphereCollider>();
                if (col != null) col.radius = 0.85f;
                return;
            }
            GameObject orbitZoneObj = new GameObject("OrbitZone");
            orbitZoneObj.transform.SetParent(transform);
            orbitZoneObj.transform.localPosition = Vector3.zero;
            orbitZoneObj.transform.localScale = Vector3.one;
            SphereCollider orbitCol = orbitZoneObj.AddComponent<SphereCollider>();
            orbitCol.isTrigger = true;
            orbitCol.radius = 0.85f;
            PlanetOrbitZone zone = orbitZoneObj.AddComponent<PlanetOrbitZone>();
            zone.SetPlanet(this);
        }

        public override void OnNetworkDespawn()
        {
            homePlanetLevel.OnValueChanged -= OnLevelChanged;
            base.OnNetworkDespawn();
        }

        [ServerRpc(RequireOwnership = false)]
        public void DepositGemsServerRpc(float amount, TeamManager.Team depositingTeam, ulong depositingClientId)
        {
            // Only allow team members to deposit gems
            if (assignedTeam.Value == TeamManager.Team.None)
            {
                assignedTeam.Value = depositingTeam;
            }
            else if (assignedTeam.Value != depositingTeam)
            {
                return;
            }

            float maxGems = GetMaxGemsForLevel(homePlanetLevel.Value);
            currentGems.Value = Mathf.Min(currentGems.Value + amount, maxGems);

            // Track contributed gems for this player (for store purchases)
            if (!contributedGemsByClientId.ContainsKey(depositingClientId))
                contributedGemsByClientId[depositingClientId] = 0f;
            contributedGemsByClientId[depositingClientId] += amount;

            CheckLevelUp();
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

        /// <summary>Max gems capacity for a given level. Level 3=800, 4=1600, 5=3200, 6=6400.</summary>
        public static float GetMaxGemsForLevel(int level)
        {
            if (level >= 1 && level < MaxGemsPerLevel.Length)
                return MaxGemsPerLevel[level];
            return level >= 6 ? 6400f : 0f;
        }

        private void CheckLevelUp()
        {
            if (!IsServer) return;

            int currentLevel = homePlanetLevel.Value;
            if (currentLevel >= 6) return;

            float maxForLevel = GetMaxGemsForLevel(currentLevel);
            if (maxForLevel > 0f && currentGems.Value >= maxForLevel)
                LevelUpServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void LevelUpServerRpc()
        {
            if (homePlanetLevel.Value >= 6) return; // Max level

            homePlanetLevel.Value++;
            currentGems.Value = 0f; // Reset gem count to 0 when leveling up
            LevelUpClientRpc(homePlanetLevel.Value);
        }

        [ClientRpc]
        private void LevelUpClientRpc(int newLevel)
        {
            Debug.Log($"Home Planet leveled up to level {newLevel}! Max ship level is now {GetMaxShipLevelForPlanetLevel(newLevel)}");
        }

        private void OnLevelChanged(int previousLevel, int newLevel)
        {
            if (newLevel > previousLevel)
            {
                if (VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.PlayLevelUpEffect(transform.position);
                StartCoroutine(LevelUpScalePulse());
            }
            Debug.Log($"Home Planet level changed from {previousLevel} to {newLevel}");
        }

        /// <summary>Add SGT water components so home planet materials with water render correctly.</summary>
        private void EnsureWaterComponents()
        {
            if (GetComponent<SgtPlanetWaterGradient>() == null)
                gameObject.AddComponent<SgtPlanetWaterGradient>();
            if (GetComponent<SgtPlanetWaterTexture>() == null)
                gameObject.AddComponent<SgtPlanetWaterTexture>();
        }

        /// <summary>Add SGT atmosphere as child for home planets (volumetric atmosphere).</summary>
        private void EnsureAtmosphere()
        {
            if (atmosphereSourceMaterial == null || atmosphereOuterMesh == null) return;
            Transform existing = transform.Find("Atmosphere");
            SgtAtmosphere sgtAtmosphere = existing != null ? existing.GetComponent<SgtAtmosphere>() : null;

            if (sgtAtmosphere == null)
            {
                GameObject atmosphereObj = new GameObject("Atmosphere");
                atmosphereObj.transform.SetParent(transform);
                atmosphereObj.transform.localPosition = Vector3.zero;
                atmosphereObj.transform.localRotation = Quaternion.identity;
                atmosphereObj.transform.localScale = Vector3.one;

                sgtAtmosphere = atmosphereObj.AddComponent<SgtAtmosphere>();
                sgtAtmosphere.SourceMaterial = atmosphereSourceMaterial;
                sgtAtmosphere.OuterMesh = atmosphereOuterMesh;
            sgtAtmosphere.InnerMeshRadius = 0.5f;
            sgtAtmosphere.OuterMeshRadius = 1f;   // CW mesh is unit sphere (radius 1)
            sgtAtmosphere.Height = 0.012f;       // Thin shell close to planet surface
            }

            // Required so SourceMaterial has Inner/Outer DepthTex, LightingTex, ScatteringTex (avoids black circle / inspector errors)
            GameObject atmObj = sgtAtmosphere.gameObject;
            if (atmObj.GetComponent<SgtAtmosphereDepthTex>() == null)
                atmObj.AddComponent<SgtAtmosphereDepthTex>();
            if (atmObj.GetComponent<SgtAtmosphereLightingTex>() == null)
                atmObj.AddComponent<SgtAtmosphereLightingTex>();
            if (atmObj.GetComponent<SgtAtmosphereScatteringTex>() == null)
                atmObj.AddComponent<SgtAtmosphereScatteringTex>();
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
            var drawer = GetComponentInChildren<HomePlanetRingsDrawer>(true);
            if (drawer != null) return;
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
            return 6; // Default (e.g. planet level 3+)
        }

        /// <summary>Gems required to reach this level (same as max capacity for that level).</summary>
        public float GetGemsThresholdForLevel(int level)
        {
            return GetMaxGemsForLevel(level);
        }

        /// <summary>True when home planet is level 6 and has at least the gem capacity for level 6 (unlocks ship level 7 MEGA).</summary>
        public bool IsFullGemsForLevel7Unlock()
        {
            if (homePlanetLevel.Value < 6) return false;
            return currentGems.Value >= GetMaxGemsForLevel(6);
        }

        /// <summary>Gems still needed to fill current level capacity (and trigger level-up if not max level).</summary>
        public float GetGemsNeededForNextLevel()
        {
            int currentLevel = homePlanetLevel.Value;
            if (currentLevel >= 6) return 0f;
            float maxForLevel = GetMaxGemsForLevel(currentLevel);
            return Mathf.Max(0f, maxForLevel - currentGems.Value);
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
            // Notify all clients that a team was eliminated
            EliminateTeamClientRpc(eliminatedTeam);
            
            // Check win conditions
            CheckWinConditions();
        }

        [ClientRpc]
        private void EliminateTeamClientRpc(TeamManager.Team eliminatedTeam)
        {
            Debug.Log($"Team {eliminatedTeam} has been eliminated!");
        }

        private void CheckWinConditions()
        {
            // This would be handled by GameManager or a separate WinConditionManager
            // For now, just log
            Debug.Log("Checking win conditions...");
        }
    }
}
