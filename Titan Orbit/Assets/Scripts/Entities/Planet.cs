using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Data;
using TitanOrbit.Systems;
using TitanOrbit.UI;
using TMPro;
using SpaceGraphicsToolkit;
using SpaceGraphicsToolkit.Atmosphere;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Represents a planet in the game with population, ownership, and capture mechanics
    /// </summary>
    public class Planet : NetworkBehaviour
    {
        [Header("Planet Settings")]
        [Tooltip("Logical id for this planet used to link unique ship families and cards. 0 or negative = not bound to a specific family.")]
        [SerializeField] private int planetId = 0;
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
        [Tooltip("When set, shows world-space progress bars (Pop/Gems/Level) instead of population text. Created at runtime if missing.")]
        [SerializeField] private PlanetStatsDisplay planetStatsDisplay;
        [Tooltip("Tint intensity for regular planets (0 = no tint, 1 = full team color). Only applies to regular planets, not HomePlanets.")]
        [SerializeField] private float regularPlanetTintIntensity = 0.2f;

        [Header("Regular Planet: Water & Atmosphere (optional)")]
        [Tooltip("Optional. When set, regular planets get varying atmosphere (derived from material index). Leave empty for no atmosphere.")]
        [SerializeField] protected Material atmosphereSourceMaterial;
        [Tooltip("Optional. SGT atmosphere outer mesh (e.g. Geosphere40 from CW). Required if atmosphere is used.")]
        [SerializeField] protected Mesh atmosphereOuterMesh;

        [Header("Rotation")]
        [Tooltip("When enabled, the planet rotates around the same tilted axis used by its rings.")]
        [SerializeField] private bool enableSpin = true;
        [Tooltip("Spin speed in degrees/second. Clockwise when viewed from the positive ring axis.")]
        [SerializeField] private float spinDegreesPerSecond = 2f;

        /// <summary>Shared fallback materials for planets that don't have team materials assigned (e.g. regular Planet prefab). Populated from first planet that has them (e.g. HomePlanet).</summary>
        private static Material s_sharedNeutral, s_sharedTeamA, s_sharedTeamB, s_sharedTeamC;
        
        private MaterialPropertyBlock tintPropertyBlock;

        private const float PopulationDisplayInterval = 0.2f;
        private float lastPopulationDisplayUpdate = -999f;

        private NetworkVariable<TeamManager.Team> teamOwnership = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);
        private NetworkVariable<int> neutralMaterialIndex = new NetworkVariable<int>(-1);
        private NetworkVariable<float> currentPopulation = new NetworkVariable<float>(0f);
        private NetworkVariable<float> maxPopulation = new NetworkVariable<float>(100f);
        private NetworkVariable<float> growthRate = new NetworkVariable<float>(1f);
        private NetworkVariable<int> planetLevel = new NetworkVariable<int>(1);
        private NetworkVariable<float> currentGems = new NetworkVariable<float>(0f);
        private NetworkVariable<int> planetIdNet = new NetworkVariable<int>(0);

        /// <summary>
        /// Connection bonuses from planet‑to‑planet territory triangles.
        /// Values are fractional multipliers: 0.1 = +10% max population / growth.
        /// Server‑authored, but read on all clients via properties.
        /// </summary>
        private float connectionMaxPopulationBonusFraction = 0f;
        private float connectionGrowthBonusFraction = 0f;

        public int PlanetId => planetIdNet.Value;
        public TeamManager.Team TeamOwnership => teamOwnership.Value;

        /// <summary>
        /// Toroidal (canonical) position for this planet. Use for connection/triangle logic and consistent wrapping.
        /// Wraps transform position to the map tile; stable regardless of which display copy is visible.
        /// </summary>
        public Vector3 ToroidalPosition => ToroidalMap.WrapPosition(transform.position);

        protected void SetInitialTeamOwnership(TeamManager.Team team)
        {
            teamOwnership.Value = team;
        }

        /// <summary>
        /// Server-side setup helper: assign a stable logical id to this planet before it is spawned.
        /// MapGenerator uses this to give each neutral planet a unique id that can be matched by cards/chassis.
        /// </summary>
        public void SetTemplatePlanetId(int id)
        {
            if (IsSpawned) return; // Only allow setting before network spawn
            planetId = id;
        }
        public float CurrentPopulation => currentPopulation.Value;
        public float MaxPopulation => maxPopulation.Value * (1f + Mathf.Max(0f, connectionMaxPopulationBonusFraction));
        public float GrowthRate => GetGrowthRatePerSecond();
        public int PlanetLevel => planetLevel.Value;
        public float PlanetSize => planetSize;
        public float CaptureRadius => captureRadius;
        public float CurrentGems => currentGems.Value;
        /// <summary>Max gems at current level. Override GetMaxGemsForLevel in HomePlanet for different thresholds.</summary>
        public float MaxGems => GetMaxGemsForLevel(planetLevel.Value);

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
                // Sync logical planet id from template field so all clients see the same PlanetId.
                planetIdNet.Value = planetId;

                // Home planets: Team A = Tropical1, Team B = Tropical2, Team C = Tropical3 (WaterMaterials 0,1,2). Regular: random from Materials.
                if (materialPool != null)
                {
                    if (this is HomePlanet homePlanet)
                    {
                        var team = homePlanet.AssignedTeam;
                        int tropicalIndex = team == TeamManager.Team.TeamA ? 0 : team == TeamManager.Team.TeamB ? 1 : team == TeamManager.Team.TeamC ? 2 : 0;
                        neutralMaterialIndex.Value = tropicalIndex;
                    }
                    else
                    {
                        int idx = materialPool.GetRandomIndex(false);
                        if (idx >= 0)
                            neutralMaterialIndex.Value = idx;
                    }
                }

                // Initialize planet level (home planets start at 3, regular planets at 1)
                planetLevel.Value = GetInitialPlanetLevel();
                currentGems.Value = 0f;

                // Max population: regular planets 50-150 by size; home planets override to 100
                float potentialMax = GetMaxPopulationForPlanet();
                growthRate.Value = GetGrowthRatePerSecond();
                if (!(this is HomePlanet))
                    teamOwnership.Value = TeamManager.Team.None; // Neutral by default (home planets already set in InitForTeam for rings/color)
                // All planets (neutral and home) start at 100% population capacity.
                currentPopulation.Value = potentialMax;
                maxPopulation.Value = potentialMax;
            }

            if (populationText != null)
            {
                populationText.enabled = true;
                populationText.gameObject.SetActive(true);
                EnsurePopulationTextPosition();
                EnsurePopulationTextReadable();
            }

            EnsurePlanetStatsDisplay();

            EnsureBodyColliderSize();
            EnsureOrbitZoneExists();
            EnsureSpinTargetSetup();

            if (!(this is HomePlanet))
                EnsurePlanetRingsDrawer();

            ApplyRegularPlanetWaterAndAtmosphere();

            // Update visual on spawn
            UpdateVisual(teamOwnership.Value);
            UpdatePopulationDisplay();

            // When neutralMaterialIndex syncs from server (client may get it after spawn), refresh visual
            neutralMaterialIndex.OnValueChanged += OnNeutralMaterialIndexChanged;

            // Subscribe to ownership changes
            teamOwnership.OnValueChanged += OnOwnershipChanged;
            currentPopulation.OnValueChanged += (float oldVal, float newVal) => UpdatePopulationDisplay();
            planetLevel.OnValueChanged += OnPlanetLevelChanged;
        }

        public override void OnNetworkDespawn()
        {
            neutralMaterialIndex.OnValueChanged -= OnNeutralMaterialIndexChanged;
            teamOwnership.OnValueChanged -= OnOwnershipChanged;
            planetLevel.OnValueChanged -= OnPlanetLevelChanged;
        }

        private void OnNeutralMaterialIndexChanged(int previous, int current)
        {
            ApplyRegularPlanetWaterAndAtmosphere();
            UpdateVisual(teamOwnership.Value);
        }

        /// <summary>Regular planets only: set varying water level and optional atmosphere from deterministic seed (neutralMaterialIndex).</summary>
        private void ApplyRegularPlanetWaterAndAtmosphere()
        {
            if (this is HomePlanet) return;

            int seed = Mathf.Max(0, neutralMaterialIndex.Value);
            float waterLevel = (seed % 5) * 0.055f;
            float atmosphereHeight = (seed % 4) * 0.012f;

            GameObject visualTarget = GetPlanetVisualTargetObject();

            if (sgtPlanet != null)
                sgtPlanet.WaterLevel = waterLevel;

            if (waterLevel > 0.001f)
            {
                if (visualTarget.GetComponent<SgtPlanetWaterGradient>() == null)
                    visualTarget.AddComponent<SgtPlanetWaterGradient>();
                if (visualTarget.GetComponent<SgtPlanetWaterTexture>() == null)
                    visualTarget.AddComponent<SgtPlanetWaterTexture>();
            }

            if (atmosphereSourceMaterial != null && atmosphereOuterMesh != null && atmosphereHeight > 0.001f)
            {
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
                    sgtAtmosphere.OuterMeshRadius = 1f;
                    atmosphereObj.AddComponent<SgtAtmosphereDepthTex>();
                    atmosphereObj.AddComponent<SgtAtmosphereLightingTex>();
                    atmosphereObj.AddComponent<SgtAtmosphereScatteringTex>();
                }
                sgtAtmosphere.Height = atmosphereHeight;
            }
        }

        private void Update()
        {
            ApplyPlanetSpin();

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
                // Grow population over time if not at max (including territory bonuses).
                if (teamOwnership.Value != TeamManager.Team.None)
                {
                    float effectiveMax = MaxPopulation;
                    if (currentPopulation.Value < effectiveMax)
                    {
                        float growth = GetGrowthRatePerSecond() * Time.deltaTime;
                        if (GameManager.Instance != null && GameManager.Instance.DebugMode) growth *= 100f;
                        currentPopulation.Value = Mathf.Min(
                            currentPopulation.Value + growth,
                            effectiveMax
                        );
                    }
                }
            }
            
            // Update population display periodically (OnValueChanged handles immediate updates; this catches drift)
            if (Time.time - lastPopulationDisplayUpdate >= PopulationDisplayInterval)
            {
                lastPopulationDisplayUpdate = Time.time;
                UpdatePopulationDisplay();
            }
        }

        /// <summary>Rotate around the ring normal so body spin matches ring axis.</summary>
        private void ApplyPlanetSpin()
        {
            if (!enableSpin || Mathf.Approximately(spinDegreesPerSecond, 0f))
                return;

            Transform spinTarget = GetSpinTargetTransform();
            if (spinTarget == null)
                return;

            Vector3 axis = GetRingAxisWorld();
            spinTarget.RotateAround(transform.position, axis, spinDegreesPerSecond * Time.deltaTime);
        }

        /// <summary>
        /// Ensures visual planet rendering lives on a child transform, so spin does not rotate text/rings/orbit visuals.
        /// </summary>
        private void EnsureSpinTargetSetup()
        {
            if (sgtPlanet == null || sgtPlanet.transform != transform)
                return;

            SgtPlanet source = sgtPlanet;
            Transform spinTarget = transform.Find("PlanetVisualSpin");
            if (spinTarget == null)
            {
                GameObject visualObj = new GameObject("PlanetVisualSpin");
                spinTarget = visualObj.transform;
                spinTarget.SetParent(transform, false);
            }

            SgtPlanet target = spinTarget.GetComponent<SgtPlanet>();
            if (target == null)
                target = spinTarget.gameObject.AddComponent<SgtPlanet>();

            target.Mesh = source.Mesh;
            target.MeshCollider = source.MeshCollider;
            target.Radius = source.Radius;
            target.Material = source.Material;
            target.SharedMaterial = source.SharedMaterial;
            target.CastShadows = source.CastShadows;
            target.ReceiveShadows = source.ReceiveShadows;
            target.WaterLevel = source.WaterLevel;
            target.Displace = source.Displace;
            target.Displacement = source.Displacement;
            target.ClampWater = source.ClampWater;

            MoveWaterComponentsToSpinTarget(spinTarget.gameObject);

            sgtPlanet = target;
            Object.Destroy(source);
        }

        private void MoveWaterComponentsToSpinTarget(GameObject spinTargetObject)
        {
            SgtPlanetWaterGradient sourceGradient = GetComponent<SgtPlanetWaterGradient>();
            if (sourceGradient != null)
            {
                SgtPlanetWaterGradient targetGradient = spinTargetObject.GetComponent<SgtPlanetWaterGradient>();
                if (targetGradient == null)
                    targetGradient = spinTargetObject.AddComponent<SgtPlanetWaterGradient>();
                targetGradient.Shallow = sourceGradient.Shallow;
                targetGradient.Deep = sourceGradient.Deep;
                targetGradient.Ease = sourceGradient.Ease;
                targetGradient.Sharpness = sourceGradient.Sharpness;
                targetGradient.Scale = sourceGradient.Scale;
                Object.Destroy(sourceGradient);
            }

            SgtPlanetWaterTexture sourceTexture = GetComponent<SgtPlanetWaterTexture>();
            if (sourceTexture != null)
            {
                SgtPlanetWaterTexture targetTexture = spinTargetObject.GetComponent<SgtPlanetWaterTexture>();
                if (targetTexture == null)
                    targetTexture = spinTargetObject.AddComponent<SgtPlanetWaterTexture>();
                targetTexture.BaseTexture = sourceTexture.BaseTexture;
                targetTexture.Strength = sourceTexture.Strength;
                targetTexture.Speed = sourceTexture.Speed;
                Object.Destroy(sourceTexture);
            }
        }

        private Transform GetSpinTargetTransform()
        {
            if (sgtPlanet != null)
                return sgtPlanet.transform == transform ? null : sgtPlanet.transform;

            if (planetRenderer != null && planetRenderer.transform != transform)
                return planetRenderer.transform;

            return null;
        }

        protected GameObject GetPlanetVisualTargetObject()
        {
            if (sgtPlanet != null)
                return sgtPlanet.gameObject;
            if (planetRenderer != null)
                return planetRenderer.gameObject;
            return gameObject;
        }

        /// <summary>Gets ring axis from the active ring drawer; falls back to local up.</summary>
        private Vector3 GetRingAxisWorld()
        {
            PlanetRingsDrawer regularRings = GetComponentInChildren<PlanetRingsDrawer>(true);
            if (regularRings != null)
                return regularRings.GetRingAxisWorld();

            HomePlanetRingsDrawer homeRings = GetComponentInChildren<HomePlanetRingsDrawer>(true);
            if (homeRings != null)
                return homeRings.GetRingAxisWorld();

            return transform.up;
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

        /// <summary>Outline color for population text so it stays readable on any planet texture. Default: black (for white text). Override in HomePlanet for light outline (dark text).</summary>
        protected virtual Color GetPopulationTextOutlineColor() => Color.black;

        /// <summary>Render queue for population text so it draws on top of rings (all planets use Shapes for rings).</summary>
        protected virtual int GetPopulationTextRenderQueue() => (int)UnityEngine.Rendering.RenderQueue.Geometry + 100;

        /// <summary>One-time setup: outline so text reads on any planet, render queue so home-planet text isn't behind rings.</summary>
        private void EnsurePopulationTextReadable()
        {
            if (populationText == null) return;
            Material mat = populationText.fontMaterial;
            if (mat == null) return;
            mat.EnableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineColor")) mat.SetColor("_OutlineColor", GetPopulationTextOutlineColor());
            if (mat.HasProperty("_OutlineWidth")) mat.SetFloat("_OutlineWidth", 0.25f);
            if (mat.HasProperty("_OutlineSoftness")) mat.SetFloat("_OutlineSoftness", 0.1f);
            mat.renderQueue = GetPopulationTextRenderQueue();
        }

        private void UpdatePopulationDisplay()
        {
            if (planetStatsDisplay != null && planetStatsDisplay.isActiveAndEnabled)
            {
                planetStatsDisplay.Refresh();
                if (populationText != null)
                    populationText.gameObject.SetActive(false);
                return;
            }
            if (populationText == null) return;
            populationText.gameObject.SetActive(true);
            populationText.text = Mathf.RoundToInt(currentPopulation.Value).ToString();
            populationText.color = GetPopulationTextColor();
            populationText.enabled = true;
            var r = populationText.GetComponent<Renderer>();
            if (r != null) r.enabled = true;
            populationText.ForceMeshUpdate(true, false);
        }

        /// <summary>Add PlanetStatsDisplay at runtime so we show progress bars instead of single population text.</summary>
        private void EnsurePlanetStatsDisplay()
        {
            if (planetStatsDisplay == null)
                planetStatsDisplay = GetComponent<PlanetStatsDisplay>();
            if (planetStatsDisplay == null)
            {
                planetStatsDisplay = gameObject.AddComponent<PlanetStatsDisplay>();
            }
            planetStatsDisplay.Init(this);
        }

        /// <summary>Population per second. Override in HomePlanet for level-based (1 per 5 sec at level 3, doubles each level). Regular: uses stored growthRate (doubles on level up).</summary>
        protected virtual float GetGrowthRatePerSecond()
        {
            // Use stored growthRate.Value (which doubles on level up) instead of constant baseGrowthRate,
            // then apply any connection bonus from planet‑to‑planet triangles.
            float baseRate = growthRate.Value > 0f ? growthRate.Value : baseGrowthRate;
            float bonusFactor = 1f + Mathf.Max(0f, connectionGrowthBonusFraction);
            return baseRate * bonusFactor;
        }

        /// <summary>Server: update synced growth rate (e.g. when planet levels up).</summary>
        protected void SetGrowthRate(float rate)
        {
            if (IsServer)
                growthRate.Value = rate;
        }

        /// <summary>
        /// Server‑only: apply connection bonuses from territory triangles.
        /// Both arguments are fractional (e.g. 0.1 = +10%).
        /// </summary>
        public void SetConnectionBonuses(float maxPopBonusFraction, float growthBonusFraction)
        {
            if (!IsServer)
                return;

            connectionMaxPopulationBonusFraction = Mathf.Max(0f, maxPopBonusFraction);
            connectionGrowthBonusFraction = Mathf.Max(0f, growthBonusFraction);
        }

        /// <summary>Initial planet level. Override in HomePlanet to start at 3.</summary>
        protected virtual int GetInitialPlanetLevel() => 1;

        /// <summary>Max gems capacity for a given level. Override in HomePlanet for different thresholds. Regular planets: 200 * 2^(level-1).</summary>
        protected virtual float GetMaxGemsForLevel(int level)
        {
            // Regular planets: Level 1 = 200, Level 2 = 400, Level 3 = 800, etc.
            if (level < 1) return 0f;
            return 200f * Mathf.Pow(2f, level - 1);
        }

        /// <summary>Max level for this planet type. Override in HomePlanet for 6.</summary>
        protected virtual int GetMaxLevel() => 3; // Regular planets max level 3

        /// <summary>Server-only: apply gem deposit. Call this directly from server code (e.g. TickOrbitGemDeposit) instead of RPC to avoid RPC invocation issues when server calls itself.</summary>
        public void DepositGemsFromServer(float amount, TeamManager.Team depositingTeam, ulong depositingClientId)
        {
            if (!IsServer) return;
            // Only allow same team to deposit gems
            if (teamOwnership.Value == TeamManager.Team.None)
            {
                return;
            }
            if (teamOwnership.Value != depositingTeam)
            {
                return;
            }

            float maxGems = GetMaxGemsForLevel(planetLevel.Value);
            currentGems.Value = Mathf.Min(currentGems.Value + amount, maxGems);
            if (currentGems.Value >= maxGems - 0.001f)
                currentGems.Value = maxGems;

            CheckLevelUp();
        }

        [ServerRpc(RequireOwnership = false)]
        public void DepositGemsServerRpc(float amount, TeamManager.Team depositingTeam, ulong depositingClientId)
        {
            DepositGemsFromServer(amount, depositingTeam, depositingClientId);
        }

        private void CheckLevelUp()
        {
            if (!IsServer) return;

            int currentLevel = planetLevel.Value;
            if (currentLevel >= GetMaxLevel()) return;

            float maxForLevel = GetMaxGemsForLevel(currentLevel);
            // Level up when gems reach exact max capacity (e.g. 100/100). Use small epsilon for float precision.
            if (maxForLevel > 0f && currentGems.Value >= maxForLevel - 0.001f)
                LevelUpServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void LevelUpServerRpc()
        {
            if (planetLevel.Value >= GetMaxLevel()) return; // Max level

            int oldLevel = planetLevel.Value;
            planetLevel.Value++;
            currentGems.Value = 0f; // Reset gem count to 0 when leveling up

            // Recompute max population from new level (formula: size * level^1.5); double growth rate
            maxPopulation.Value = GetMaxPopulationForPlanet();
            float oldGrowthRate = growthRate.Value;
            SetGrowthRate(oldGrowthRate * 2f);

            LevelUpClientRpc(planetLevel.Value, transform.position, planetSize);
        }

        [ClientRpc]
        private void LevelUpClientRpc(int newLevel, Vector3 planetPosition, float effectPlanetSize)
        {
            Debug.Log($"Planet leveled up to level {newLevel}!");
            VisualEffectsManager.PlayLevelUpEffectStatic(planetPosition, effectPlanetSize);
        }

        protected virtual void OnPlanetLevelChanged(int previousLevel, int newLevel)
        {
            // Ships no longer auto-level. Players purchase level upgrades at the store (same cost as other ships of that tier).
        }

        /// <summary>Max population = planet size * level^1.5 (e.g. home size 20 level 3 ≈ 104; regular size 10 level 1 = 10).</summary>
        protected virtual float GetMaxPopulationForPlanet()
        {
            int level = Mathf.Max(1, planetLevel.Value);
            return planetSize * Mathf.Pow(level, 1.5f);
        }

        [ServerRpc(RequireOwnership = false)]
        public void AddPopulationServerRpc(float amount, TeamManager.Team sourceTeam)
        {
            // Same-team planet: add population (reinforce)
            if (teamOwnership.Value != TeamManager.Team.None && teamOwnership.Value == sourceTeam)
            {
                currentPopulation.Value = Mathf.Min(currentPopulation.Value + amount, MaxPopulation);
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
                
                // Blend neutral base color with team color (SGT Planet uses _Color, URP uses _BaseColor)
                Color neutralBaseColor = GetMaterialColor(neutralMat);
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
                        SetTintPropertyBlock(sgtRenderer, tintPropertyBlock, neutralMat, tintedColor);
                        sgtRenderer.SetPropertyBlock(tintPropertyBlock);
                    }
                    return;
                }
                
                Renderer renderer = planetRenderer != null ? planetRenderer : GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.material = neutralMat;
                    renderer.GetPropertyBlock(tintPropertyBlock);
                    SetTintPropertyBlock(renderer, tintPropertyBlock, neutralMat, tintedColor);
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
            return GetMaterialColor(teamMat);
        }

        /// <summary>SGT Planet uses _Color; URP uses _BaseColor. Try both.</summary>
        private static Color GetMaterialColor(Material mat)
        {
            if (mat == null) return Color.white;
            if (mat.HasProperty("_Color")) return mat.GetColor("_Color");
            if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
            return Color.white;
        }

        private static void SetTintPropertyBlock(Renderer r, MaterialPropertyBlock block, Material mat, Color tintedColor)
        {
            if (r == null || block == null) return;
            r.GetPropertyBlock(block);
            string prop = mat.HasProperty("_Color") ? "_Color" : (mat.HasProperty("_BaseColor") ? "_BaseColor" : null);
            if (prop != null) block.SetColor(prop, tintedColor);
            r.SetPropertyBlock(block);
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
                bool useTropicalList = this is HomePlanet;
                Material fromPool = materialPool.GetMaterial(neutralMaterialIndex.Value, useTropicalList);
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
