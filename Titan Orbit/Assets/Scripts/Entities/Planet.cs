using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Data;
using TMPro;
using SpaceGraphicsToolkit;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Represents a planet in the game with population, ownership, and capture mechanics
    /// </summary>
    public class Planet : NetworkBehaviour
    {
        [Header("Planet Settings")]
        [SerializeField] private float baseMaxPopulation = 100f;
        [SerializeField] private float baseGrowthRate = 1f / 30f; // Regular planets: 1 person per 30 sec (override in subclasses for home)
        [SerializeField] private float planetSize = 1f;
        [SerializeField] private float captureRadius = 5f;

        [Header("Visual")]
        [SerializeField] private Renderer planetRenderer;
        [Tooltip("When set, planet is drawn by SGT Planet (CW asset); team materials are applied to this.")]
        [SerializeField] private SgtPlanet sgtPlanet;
        [Tooltip("Optional. When set, neutral material is chosen at random from this pool at spawn.")]
        [SerializeField] private PlanetMaterialPool materialPool;
        [SerializeField] private Material neutralMaterial;
        [SerializeField] private Material teamAMaterial;
        [SerializeField] private Material teamBMaterial;
        [SerializeField] private Material teamCMaterial;
        [SerializeField] private TextMeshPro populationText;
        [Tooltip("Tint intensity for regular planets (0 = no tint, 1 = full team color). Only applies to regular planets, not HomePlanets.")]
        [SerializeField] private float regularPlanetTintIntensity = 0.2f;

        /// <summary>Shared fallback materials for planets that don't have team materials assigned (e.g. regular Planet prefab). Populated from first planet that has them (e.g. HomePlanet).</summary>
        private static Material s_sharedNeutral, s_sharedTeamA, s_sharedTeamB, s_sharedTeamC;
        
        private MaterialPropertyBlock tintPropertyBlock;

        private NetworkVariable<TeamManager.Team> teamOwnership = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);
        private NetworkVariable<int> neutralMaterialIndex = new NetworkVariable<int>(-1);
        private NetworkVariable<float> currentPopulation = new NetworkVariable<float>(0f);
        private NetworkVariable<float> maxPopulation = new NetworkVariable<float>(100f);
        private NetworkVariable<float> growthRate = new NetworkVariable<float>(1f);
        private NetworkVariable<int> planetLevel = new NetworkVariable<int>(1);

        public TeamManager.Team TeamOwnership => teamOwnership.Value;
        
        protected void SetInitialTeamOwnership(TeamManager.Team team)
        {
            teamOwnership.Value = team;
        }
        public float CurrentPopulation => currentPopulation.Value;
        public float MaxPopulation => maxPopulation.Value;
        public float GrowthRate => growthRate.Value;
        public int PlanetLevel => planetLevel.Value;
        public float PlanetSize => planetSize;
        public float CaptureRadius => captureRadius;

        private const float FIXED_Y_POSITION = 0f;

        public override void OnNetworkSpawn()
        {
            tintPropertyBlock = new MaterialPropertyBlock();
            
            // Lock Y position to 0
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;
            
            // Update planetSize from actual transform scale (MapGenerator sets scale directly)
            // Use average of x, y, z scale components
            float actualSize = (transform.localScale.x + transform.localScale.y + transform.localScale.z) / 3f;
            if (actualSize > 0.1f) // Only update if scale is valid
            {
                planetSize = actualSize;
            }
            
            if (IsServer)
            {
                // Random neutral material from pool (home planets use water materials if available)
                if (materialPool != null)
                {
                    bool useWater = this is HomePlanet;
                    int idx = materialPool.GetRandomIndex(useWater);
                    if (idx >= 0)
                        neutralMaterialIndex.Value = idx;
                }

                // Max population: regular planets 50-150 by size; home planets override to 100
                float potentialMax = GetMaxPopulationForPlanet();
                growthRate.Value = GetGrowthRatePerSecond();
                teamOwnership.Value = TeamManager.Team.None; // Neutral by default
                // For neutral (regular) planets only: starting population is also the max (display as e.g. 18 of 18). Home planets keep full cap.
                if (!(this is HomePlanet))
                {
                    float startingPopulation = potentialMax * 0.25f; // Start with 25% population
                    currentPopulation.Value = startingPopulation;
                    maxPopulation.Value = startingPopulation; // Max equals starting value for neutral planets
                }
                else
                {
                    currentPopulation.Value = potentialMax * 0.25f; // Start with 25% population
                    maxPopulation.Value = potentialMax;
                }
            }

            if (populationText != null)
            {
                populationText.enabled = true;
                populationText.gameObject.SetActive(true);
                EnsurePopulationTextPosition();
            }

            EnsureBodyColliderSize();
            EnsureOrbitZoneExists();

            if (!(this is HomePlanet))
                EnsurePlanetRingsDrawer();

            // Update visual on spawn
            UpdateVisual(teamOwnership.Value);
            UpdatePopulationDisplay();

            // When neutralMaterialIndex syncs from server (client may get it after spawn), refresh visual
            neutralMaterialIndex.OnValueChanged += OnNeutralMaterialIndexChanged;

            // Subscribe to ownership changes
            teamOwnership.OnValueChanged += OnOwnershipChanged;
            currentPopulation.OnValueChanged += (float oldVal, float newVal) => UpdatePopulationDisplay();
        }

        public override void OnNetworkDespawn()
        {
            neutralMaterialIndex.OnValueChanged -= OnNeutralMaterialIndexChanged;
            teamOwnership.OnValueChanged -= OnOwnershipChanged;
        }

        private void OnNeutralMaterialIndexChanged(int previous, int current)
        {
            UpdateVisual(teamOwnership.Value);
        }

        private void Update()
        {
            // Always lock Y position (prevents drift)
            Vector3 pos = transform.position;
            if (Mathf.Abs(pos.y - FIXED_Y_POSITION) > 0.01f)
            {
                pos.y = FIXED_Y_POSITION;
                transform.position = pos;
            }
            
            // Position is set by ToroidalRenderer in LateUpdate (display copy closest to camera).
            // Do not wrap here or entities will disappear at edges.

            if (IsServer)
            {
                // Grow population over time if not at max
                if (currentPopulation.Value < maxPopulation.Value && teamOwnership.Value != TeamManager.Team.None)
                {
                    float growth = growthRate.Value * Time.deltaTime;
                    if (GameManager.Instance != null && GameManager.Instance.DebugMode) growth *= 100f;
                    currentPopulation.Value = Mathf.Min(
                        currentPopulation.Value + growth,
                        maxPopulation.Value
                    );
                }
            }
            
            // Update population display every frame (handles client-side updates)
            UpdatePopulationDisplay();
        }
        
        /// <summary>Override in HomePlanet to place text above the ring (e.g. 0.8).</summary>
        protected virtual Vector3 GetPopulationTextLocalPosition() => new Vector3(0f, 0.55f, 0f);

        /// <summary>
        /// Positions population text just above planet surface. Negative X scale so text is readable (not mirrored).
        /// </summary>
        private void EnsurePopulationTextPosition()
        {
            if (populationText == null) return;
            Transform t = populationText.transform;
            t.localPosition = GetPopulationTextLocalPosition();
            t.localScale = new Vector3(0.04f, -0.04f, 0.04f); // +X: not mirrored, -Y: right-side up
        }

        /// <summary>
        /// Body collider = planet sphere (Unity default sphere radius 0.5 local). Orbit zone = band from surface to +10% diameter (radius 0.5 to 0.6).
        /// </summary>
        private void EnsureBodyColliderSize()
        {
            SphereCollider body = GetComponent<SphereCollider>();
            if (body != null)
            {
                body.radius = 0.5f; // Match Unity primitive sphere (diameter 1)
                body.isTrigger = false;
            }
        }

        /// <summary>
        /// Orbit zone: surface (0.5) to outer (0.85 local). Ships orbit at whatever radius they enter; farther = slower.
        /// </summary>
        private void EnsureOrbitZoneExists()
        {
            PlanetOrbitZone existing = GetComponentInChildren<PlanetOrbitZone>();
            if (existing != null)
            {
                var col = existing.GetComponent<SphereCollider>();
                if (col != null) col.radius = 0.85f;
                EnsureOrbitZoneVisual(existing.gameObject);
                return;
            }
            GameObject orbitZoneObj = new GameObject("OrbitZone");
            orbitZoneObj.transform.SetParent(transform);
            orbitZoneObj.transform.localPosition = Vector3.zero;
            orbitZoneObj.transform.localScale = Vector3.one;
            SphereCollider orbitCollider = orbitZoneObj.AddComponent<SphereCollider>();
            orbitCollider.isTrigger = true;
            orbitCollider.radius = 0.85f;
            PlanetOrbitZone zone = orbitZoneObj.AddComponent<PlanetOrbitZone>();
            zone.SetPlanet(this);
            EnsureOrbitZoneVisual(orbitZoneObj);
        }

        private void EnsureOrbitZoneVisual(GameObject orbitZoneObj)
        {
            var shapesVisual = orbitZoneObj.GetComponent<OrbitZoneShapesVisual>();
            if (shapesVisual == null)
                orbitZoneObj.AddComponent<OrbitZoneShapesVisual>();
        }

        /// <summary>Regular planets only: remove legacy cylinder ring and use Shapes to draw one tilted ring.</summary>
        private void EnsurePlanetRingsDrawer()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name == "Ring" || child.name.StartsWith("Ring"))
                    Object.Destroy(child.gameObject);
            }
            if (GetComponentInChildren<PlanetRingsDrawer>(true) != null) return;
            GameObject ringsObj = new GameObject("PlanetRings");
            ringsObj.transform.SetParent(transform);
            ringsObj.transform.localPosition = Vector3.zero;
            ringsObj.transform.localRotation = Quaternion.identity;
            ringsObj.transform.localScale = Vector3.one;
            ringsObj.AddComponent<PlanetRingsDrawer>();
        }

        /// <summary>Override in HomePlanet to use a color that contrasts with the white ring.</summary>
        protected virtual Color GetPopulationTextColor() => Color.white;

        private void UpdatePopulationDisplay()
        {
            if (populationText != null)
            {
                int pop = Mathf.RoundToInt(currentPopulation.Value);
                populationText.text = pop.ToString();
                populationText.color = GetPopulationTextColor();
                populationText.enabled = true;
            }
        }

        /// <summary>Population per second. Override in HomePlanet for 1 per 5 sec. Regular: flat 1 per 30 sec.</summary>
        protected virtual float GetGrowthRatePerSecond()
        {
            return baseGrowthRate;
        }

        /// <summary>Max population: regular planets 50-150 by size (min 4, max 12). Override in HomePlanet for 100.</summary>
        protected virtual float GetMaxPopulationForPlanet()
        {
            const float minSize = 4f, maxSize = 12f;
            float t = Mathf.Clamp01((planetSize - minSize) / (maxSize - minSize));
            return Mathf.Lerp(50f, 150f, t);
        }

        [ServerRpc(RequireOwnership = false)]
        public void AddPopulationServerRpc(float amount, TeamManager.Team sourceTeam)
        {
            // Same-team planet: add population (reinforce)
            if (teamOwnership.Value != TeamManager.Team.None && teamOwnership.Value == sourceTeam)
            {
                currentPopulation.Value = Mathf.Min(currentPopulation.Value + amount, maxPopulation.Value);
                return;
            }
            // Neutral or enemy: unload decreases their population (capture attempt)
            currentPopulation.Value -= amount;
            if (currentPopulation.Value <= 0)
            {
                CapturePlanetServerRpc(sourceTeam);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemovePopulationServerRpc(float amount)
        {
            currentPopulation.Value = Mathf.Max(0f, currentPopulation.Value - amount);
        }

        [ServerRpc(RequireOwnership = false)]
        public void CapturePlanetServerRpc(TeamManager.Team newTeam)
        {
            teamOwnership.Value = newTeam;
            currentPopulation.Value = 0f; // Reset population after capture
            maxPopulation.Value = GetMaxPopulationForPlanet(); // New owner gets full cap (e.g. 50-150)
            CapturePlanetClientRpc(newTeam);
        }

        [ClientRpc]
        private void CapturePlanetClientRpc(TeamManager.Team newTeam)
        {
            UpdateVisual(newTeam);
            Debug.Log($"Planet captured by {newTeam}");
        }

        private void OnOwnershipChanged(TeamManager.Team previousTeam, TeamManager.Team newTeam)
        {
            UpdateVisual(newTeam);
            UpdatePopulationDisplay();
        }

        private void UpdateVisual(TeamManager.Team? teamOverride = null)
        {
            if (tintPropertyBlock == null)
                tintPropertyBlock = new MaterialPropertyBlock();
                
            EnsureSharedMaterialsRegistered();
            TeamManager.Team team = teamOverride ?? teamOwnership.Value;
            
            // For regular planets (not HomePlanet), apply a faint tint overlay instead of swapping materials
            bool isRegularPlanet = !(this is HomePlanet);
            bool hasTeam = team != TeamManager.Team.None;
            
            if (isRegularPlanet && hasTeam)
            {
                // Use neutral material with faint team color tint
                Material neutralMat = GetNeutralMaterial();
                if (neutralMat == null) return;
                
                // Get team color from team material
                Material teamMat = GetTeamMaterial(team);
                Color teamColor = GetTeamColorFromMaterial(teamMat);
                
                // Blend neutral base color with team color
                Color neutralBaseColor = neutralMat.GetColor("_BaseColor");
                Color tintedColor = Color.Lerp(neutralBaseColor, teamColor, regularPlanetTintIntensity);
                
                if (sgtPlanet != null)
                {
                    sgtPlanet.Material = neutralMat;
                    // Note: SgtPlanet may not support MaterialPropertyBlock, so we might need to create a material instance
                    // For now, try applying via property block if possible
                    var sgtRenderer = sgtPlanet.GetComponent<Renderer>();
                    if (sgtRenderer != null)
                    {
                        sgtRenderer.GetPropertyBlock(tintPropertyBlock);
                        tintPropertyBlock.SetColor("_BaseColor", tintedColor);
                        sgtRenderer.SetPropertyBlock(tintPropertyBlock);
                    }
                    return;
                }
                
                Renderer renderer = planetRenderer != null ? planetRenderer : GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.material = neutralMat;
                    renderer.GetPropertyBlock(tintPropertyBlock);
                    tintPropertyBlock.SetColor("_BaseColor", tintedColor);
                    renderer.SetPropertyBlock(tintPropertyBlock);
                }
            }
            else
            {
                // HomePlanets or neutral planets: use full material swap (existing behavior)
                Material materialToUse = GetEffectiveMaterialForPlanetSurface(team);
                if (materialToUse == null) return;

                if (sgtPlanet != null)
                {
                    sgtPlanet.Material = materialToUse;
                    // Clear any property block for home planets by setting an empty one
                    var sgtRenderer = sgtPlanet.GetComponent<Renderer>();
                    if (sgtRenderer != null)
                    {
                        sgtRenderer.SetPropertyBlock(null);
                    }
                    return;
                }
                Renderer renderer = planetRenderer != null ? planetRenderer : GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.material = materialToUse;
                    // Clear any property block by setting to null
                    renderer.SetPropertyBlock(null);
                }
            }
        }
        
        private Material GetTeamMaterial(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return teamAMaterial ?? s_sharedTeamA;
                case TeamManager.Team.TeamB: return teamBMaterial ?? s_sharedTeamB;
                case TeamManager.Team.TeamC: return teamCMaterial ?? s_sharedTeamC;
                default: return null;
            }
        }
        
        private Color GetTeamColorFromMaterial(Material teamMat)
        {
            if (teamMat == null) return Color.white;
            return teamMat.GetColor("_BaseColor");
        }

        /// <summary>Material used for the planet surface. Home planets always use tropical (neutral); others use team color.</summary>
        protected virtual Material GetEffectiveMaterialForPlanetSurface(TeamManager.Team team)
        {
            return GetEffectiveMaterial(team);
        }

        /// <summary>When this planet has no team materials (e.g. regular prefab), copy from a HomePlanet so captured planets can change colour.</summary>
        private void EnsureSharedMaterialsRegistered()
        {
            if (s_sharedTeamA != null) return;
            // Prefer HomePlanet (always has team materials assigned in prefab)
            foreach (var hp in Object.FindObjectsOfType<HomePlanet>())
            {
                if (hp != null && hp.teamAMaterial != null)
                {
                    s_sharedNeutral = hp.neutralMaterial;
                    s_sharedTeamA = hp.teamAMaterial;
                    s_sharedTeamB = hp.teamBMaterial;
                    s_sharedTeamC = hp.teamCMaterial;
                    return;
                }
            }
            foreach (var p in Object.FindObjectsOfType<Planet>())
            {
                if (p != null && p.teamAMaterial != null)
                {
                    s_sharedNeutral = p.neutralMaterial;
                    s_sharedTeamA = p.teamAMaterial;
                    s_sharedTeamB = p.teamBMaterial;
                    s_sharedTeamC = p.teamCMaterial;
                    return;
                }
            }
        }

        private Material GetEffectiveMaterial(TeamManager.Team team)
        {
            Material neutral = GetNeutralMaterial();
            switch (team)
            {
                case TeamManager.Team.TeamA: return teamAMaterial ?? s_sharedTeamA ?? neutral;
                case TeamManager.Team.TeamB: return teamBMaterial ?? s_sharedTeamB ?? neutral;
                case TeamManager.Team.TeamC: return teamCMaterial ?? s_sharedTeamC ?? neutral;
                default: return neutral;
            }
        }

        protected Material GetNeutralMaterial()
        {
            if (materialPool != null && neutralMaterialIndex.Value >= 0)
            {
                bool useWaterList = this is HomePlanet;
                Material fromPool = materialPool.GetMaterial(neutralMaterialIndex.Value, useWaterList);
                if (fromPool != null) return fromPool;
            }
            return neutralMaterial ?? s_sharedNeutral;
        }

        public virtual bool CanBeCapturedBy(TeamManager.Team team)
        {
            return teamOwnership.Value == TeamManager.Team.None || teamOwnership.Value != team;
        }

        public float GetPopulationNeededToCapture(TeamManager.Team attackingTeam)
        {
            if (teamOwnership.Value == TeamManager.Team.None)
            {
                return currentPopulation.Value + 1f; // Need 1 more than neutral
            }
            else if (teamOwnership.Value == attackingTeam)
            {
                return 0f; // Already owned
            }
            else
            {
                return currentPopulation.Value + 1f; // Need 1 more than current
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, captureRadius);
        }
    }
}
