using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Data;
using TitanOrbit.Systems;
using TitanOrbit.UI;
using TitanOrbit.Audio;
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
        /// <summary>All active Planet instances. Updated on network spawn/despawn.</summary>
        public static readonly System.Collections.Generic.List<Planet> AllPlanets = new System.Collections.Generic.List<Planet>();
        [Header("Planet Settings")]
        [Tooltip("Logical id for this planet used to link unique ship families and cards. 0 or negative = not bound to a specific family.")]
        [SerializeField] private int planetId = 0;
        [SerializeField] private float baseMaxPopulation = 100f;
        [SerializeField] private float baseGrowthRate = 1f / 30f; // Regular planets: 1 person per 30 sec (override in subclasses for home)
        [Tooltip("Seconds after the last hostile unload (people dropped on this planet) before passive population growth resumes.")]
        [SerializeField] private float populationGrowthPauseAfterAttackSeconds = 1f;
        [SerializeField] private float planetSize = 1f;
        [SerializeField] private float captureRadius = 5f;

        [Header("Regular Planet Level Settings")]
        [Tooltip("Minimum starting level for neutral regular planets (inclusive).")]
        [SerializeField] private int minStartingLevel = 1;
        [Tooltip("Maximum starting level for neutral regular planets (inclusive). Regular planets can still level up to the global max level.")]
        [SerializeField] private int maxStartingLevel = 3;
        [Tooltip("When enabled, neutral regular planets roll a random starting level in [minStartingLevel, maxStartingLevel]. When disabled they start at level 1.")]
        [SerializeField] private bool randomizeNeutralStartingLevel = true;

        /// <summary>When set before network spawn (e.g. by MapGenerator), overrides random starting level.</summary>
        private int templateStartingLevel = -1;

        public bool RandomizeNeutralStartingLevel => randomizeNeutralStartingLevel;
        public int MinStartingLevel => minStartingLevel;
        public int MaxStartingLevel => maxStartingLevel;

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
        [SerializeField] private Material teamDMaterial;
        [SerializeField] private Material teamEMaterial;
        [SerializeField] private TextMeshPro populationText;
        [Tooltip("When set, shows world-space population number instead of population text. Created at runtime if missing.")]
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

        [Header("Gem moon matrix shield VFX")]
        [Tooltip("Runtime-created GemMoon has no prefab asset, so MatrixShield references must be assigned on the planet (or they stay null in builds).")]
        [SerializeField] private GameObject gemMoonMatrixShieldRedPrefab;
        [SerializeField] private GameObject gemMoonMatrixShieldBluePrefab;
        [SerializeField] private GameObject gemMoonMatrixShieldGreenPrefab;
        [SerializeField] private GameObject gemMoonMatrixShieldModularPrefab;
        [Tooltip("World-space gem moon stats UI: icon beside moon gem counts (defaults assigned on planet prefabs).")]
        [SerializeField] private Sprite gemMoonHudGemIcon;
        [Tooltip("World-space gem moon stats UI: icon beside shield point counts.")]
        [SerializeField] private Sprite gemMoonHudShieldIcon;

        /// <summary>Icons for <see cref="GemMoonStatsDisplay"/> on the gem moon; optional.</summary>
        public Sprite GemMoonHudGemIcon => gemMoonHudGemIcon;
        /// <summary>Icons for <see cref="GemMoonStatsDisplay"/> on the gem moon; optional.</summary>
        public Sprite GemMoonHudShieldIcon => gemMoonHudShieldIcon;

        /// <summary>Outer radius of orbit zone in local space at level 1 (1.5x original 0.85, then scaled to 75% of that). Grows 5% per planet level.</summary>
        private const float OrbitZoneBaseOuterRadiusLocal = 0.85f * 1.5f * 0.75f;
        private const float OrbitZoneGrowthPerLevel = 0.05f;

        /// <summary>Reference planet scale for gem-moon sizing: home planets use this size; smaller worlds get larger moons inversely (20/PlanetSize).</summary>
        private const float GemMoonReferencePlanetSize = 20f;
        /// <summary>Caps inverse ratio so tiny planets do not get absurd moons.</summary>
        private const float GemMoonInversePlanetSizeCap = 10f;
        /// <summary>Must stay in sync with <see cref="PlanetRingsDrawer"/> / <see cref="HomePlanetRingsDrawer"/> ring layout.</summary>
        private const float GemMoonRingsInnerRadiusLocal = 0.68f;
        private const float GemMoonRingThicknessLocal = 0.06f;
        private const float GemMoonRingGapLocal = 0.015f;

        /// <summary>Shared fallback materials for planets that don't have team materials assigned (e.g. regular Planet prefab). Populated from first planet that has them (e.g. HomePlanet).</summary>
        private static Material s_sharedNeutral, s_sharedTeamA, s_sharedTeamB, s_sharedTeamC, s_sharedTeamD, s_sharedTeamE;

        /// <summary>Orbit zone outer radius in planet-local space. Base is 1.5x original (0.85); +5% per planet level.</summary>
        public float GetOrbitZoneOuterRadiusLocal()
        {
            int level = Mathf.Max(1, planetLevel.Value);
            return OrbitZoneBaseOuterRadiusLocal * Mathf.Pow(1f + OrbitZoneGrowthPerLevel, level - 1);
        }

        /// <summary>
        /// Outer edge of decorative Saturn rings in planet-local XZ units (matches ring drawer: one band per level, max 6).
        /// </summary>
        public float GetRingsOuterEdgeRadiusLocal(int level)
        {
            int n = Mathf.Clamp(level, 1, 6);
            float step = GemMoonRingThicknessLocal + GemMoonRingGapLocal;
            float lastCenter = GemMoonRingsInnerRadiusLocal + (n - 1) * step;
            return lastCenter + GemMoonRingThicknessLocal * 0.5f;
        }

        /// <summary>
        /// World-space radius from planet center to the farthest of orbit-zone outer edge or outermost ring (for moon clearance).
        /// </summary>
        public float GetGemMoonStructuralOuterRadiusWorld()
        {
            int level = Mathf.Max(1, PlanetLevel);
            float ringsOuterLocal = GetRingsOuterEdgeRadiusLocal(level);
            float zoneOuterLocal = GetOrbitZoneOuterRadiusLocal();
            return PlanetSize * Mathf.Max(ringsOuterLocal, zoneOuterLocal);
        }

        /// <summary>
        /// Standard clockwise orbit linear speed at a world-space radius (matches Starship outer-band tuning; no per-ship territory bonus).
        /// </summary>
        public float GetStandardOrbitSpeedAtRadiusWorld(float radiusWorld)
        {
            float innerWorld = PlanetSize * 0.5f;
            float outerWorld = PlanetSize * GetOrbitZoneOuterRadiusLocal();
            if (outerWorld <= innerWorld + 0.001f) return 0.8f;
            float clampedRadius = Mathf.Clamp(radiusWorld, innerWorld, outerWorld);
            float radiusFactor = Mathf.InverseLerp(outerWorld, innerWorld, clampedRadius);
            const float minSize = 9f;
            const float maxSize = 18f;
            float sizeNorm = Mathf.Clamp01((PlanetSize - minSize) / (maxSize - minSize));
            float sizeMultiplier = Mathf.Lerp(0.8f, 1.4f, sizeNorm);
            float radiusMultiplier = Mathf.Lerp(0.7f, 1.6f, radiusFactor);
            const float baseOrbitSpeed = 0.8f;
            return baseOrbitSpeed * sizeMultiplier * radiusMultiplier;
        }

        /// <summary>Orbit speed at the outer edge of the orbit band (where the gem moon runs).</summary>
        public float GetStandardOrbitSpeedAtOuterOrbit()
        {
            float r = PlanetSize * GetOrbitZoneOuterRadiusLocal();
            return GetStandardOrbitSpeedAtRadiusWorld(r);
        }

        private PlanetGemMoon gemMoon;

        /// <summary>Gem deposit moon for this planet (outer orbit, clockwise). Null before spawn setup.</summary>
        public PlanetGemMoon GemMoon => gemMoon;

        /// <summary>World position of the gem moon for AI navigation (falls back to planet center if missing).</summary>
        public Vector3 GetGemMoonWorldPosition()
        {
            return gemMoon != null ? gemMoon.transform.position : transform.position;
        }

        /// <summary>Updates the orbit zone SphereCollider radius when level or setup changes.</summary>
        protected virtual void RefreshOrbitZoneRadius()
        {
            var oz = GetComponent<PlanetOrbitZone>();
            if (oz == null)
                oz = GetComponentInChildren<PlanetOrbitZone>(true);
            if (oz != null)
            {
                foreach (var col in oz.GetComponents<SphereCollider>())
                {
                    if (col.isTrigger)
                    {
                        col.radius = GetOrbitZoneOuterRadiusLocal();
                        break;
                    }
                }
            }
            RefreshGemMoonDockTriggerRadius();
            ApplyGemMoonVisualScale();
        }

        private void RefreshGemMoonDockTriggerRadius()
        {
            if (gemMoon == null) return;
            SphereCollider[] cols = gemMoon.GetComponents<SphereCollider>();
            if (cols == null || cols.Length == 0) return;

            // IMPORTANT:
            // GemMoonVisual is a primitive sphere scaled on the moon's child transform.
            // SphereCollider.radius is in the moon object's local space and scales like: (primitive sphere radius 0.5) * visualLocalScale.
            // Use the intended visual scale, not whatever might currently be set on the child.
            // (Refresh order can matter during spawn/setup.)
            float visualLocalScale = Mathf.Abs(GetGemMoonVisualUniformScale());
            float bodyLocalRadius = Mathf.Max(0.01f, 0.5f * visualLocalScale);
            // Moon dock / orbit zone visual radius (1.5× prior 1.3× body).
            float dockLocalRadius = bodyLocalRadius * 1.95f;
            float shieldLocalRadius = dockLocalRadius * gemMoon.GetMoonShieldBarrierRadiusMultiplierFromDockRadius();

            // There can be multiple SphereColliders (older versions, prefab duplicates, etc.).
            // If we have multiple triggers, treat:
            // - smallest trigger = dock/landing trigger
            // - largest trigger = shield barrier trigger
            SphereCollider minTrigger = null;
            SphereCollider maxTrigger = null;
            float minTriggerRadius = float.MaxValue;
            float maxTriggerRadius = 0f;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null || !c.isTrigger) continue;
                if (c.radius < minTriggerRadius)
                {
                    minTriggerRadius = c.radius;
                    minTrigger = c;
                }
                if (c.radius > maxTriggerRadius)
                {
                    maxTriggerRadius = c.radius;
                    maxTrigger = c;
                }
            }

            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null) continue;

                if (!c.isTrigger)
                {
                    c.radius = bodyLocalRadius;
                    continue;
                }

                // Single-trigger moons (older scenes) keep that trigger as dock radius.
                if (minTrigger != null && maxTrigger != null && minTrigger != maxTrigger)
                {
                    if (c == minTrigger) c.radius = dockLocalRadius;
                    else if (c == maxTrigger) c.radius = shieldLocalRadius;
                    else c.radius = dockLocalRadius;
                }
                else
                {
                    c.radius = dockLocalRadius;
                }
            }
        }
        
        private MaterialPropertyBlock tintPropertyBlock;

        private const float PopulationDisplayInterval = 0.2f;
        private float lastPopulationDisplayUpdate = -999f;
        /// <summary>Server Time.time when hostile unload last reduced population (capture pressure). Growth waits until pause elapses.</summary>
        private float lastHostilePopulationImpactServerTime = -999f;

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

        /// <summary>
        /// Server-side setup helper: assign a starting level before network spawn.
        /// MapGenerator uses this to spread neutral planet levels evenly across the map.
        /// </summary>
        public void SetTemplateStartingLevel(int level)
        {
            if (IsSpawned) return;
            templateStartingLevel = level;
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
            if (!AllPlanets.Contains(this))
                AllPlanets.Add(this);
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
                        int tropicalIndex = (Mathf.Max(0, (int)team - 1)) % 3;
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
                // Gem moon orbit phase is now derived deterministically per peer in
                // PlanetGemMoon.OnEnable (NetworkObjectId % 6283UL * 0.001f); no replication needed.
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
            EnsureGemMoon();
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

            if (!IsServer)
            {
                var mapNetObj = GetComponent<NetworkObject>();
                MapGenerator.Active?.HandleClientMapEntitySpawned(mapNetObj);
            }
        }

        public override void OnNetworkDespawn()
        {
            neutralMaterialIndex.OnValueChanged -= OnNeutralMaterialIndexChanged;
            teamOwnership.OnValueChanged -= OnOwnershipChanged;
            planetLevel.OnValueChanged -= OnPlanetLevelChanged;

            AllPlanets.Remove(this);
        }

        private void OnNeutralMaterialIndexChanged(int previous, int current)
        {
            ApplyRegularPlanetWaterAndAtmosphere();
            UpdateVisual(teamOwnership.Value);
        }

        /// <summary>Regular planets only: set varying water level from deterministic seed (neutralMaterialIndex). Atmosphere is disabled.</summary>
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

            // Atmosphere visuals have been removed from regular planets.
            // Clean up any legacy Atmosphere child that might still exist on old prefabs/scenes.
            Transform existingAtmosphere = transform.Find("Atmosphere");
            if (existingAtmosphere != null)
            {
                Destroy(existingAtmosphere.gameObject);
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
                // Grow population over time for all planets (owned and neutral) up to cap.
                float effectiveMax = MaxPopulation;
                if (currentPopulation.Value < effectiveMax
                    && Time.time >= lastHostilePopulationImpactServerTime + populationGrowthPauseAfterAttackSeconds)
                {
                    float growth = GetGrowthRatePerSecond() * Time.deltaTime;
                    if (GameManager.Instance != null && GameManager.Instance.DebugMode) growth *= 100f;
                    currentPopulation.Value = Mathf.Min(
                        currentPopulation.Value + growth,
                        effectiveMax
                    );
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

        /// <summary>Public spin axis accessor so satellites/moons can match planet tilt spin.</summary>
        public Vector3 GetSpinAxisWorld()
        {
            return GetRingAxisWorld();
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
        /// Orbit zone: surface (0.5) to outer (scaled by level: 1.5x base, +5% per level). Ships orbit at whatever radius they enter; farther = slower.
        /// Trigger + <see cref="PlanetOrbitZone"/> live on the planet root (second SphereCollider). Legacy <c>OrbitZone</c> child objects are removed.
        /// </summary>
        private void EnsureOrbitZoneExists()
        {
            Transform legacy = transform.Find("OrbitZone");
            if (legacy != null)
                DestroyImmediate(legacy.gameObject);

            PlanetOrbitZone zone = GetComponent<PlanetOrbitZone>();
            if (zone == null)
            {
                SphereCollider orbitCollider = gameObject.AddComponent<SphereCollider>();
                orbitCollider.isTrigger = true;
                orbitCollider.radius = GetOrbitZoneOuterRadiusLocal();
                zone = gameObject.AddComponent<PlanetOrbitZone>();
                zone.SetPlanet(this);
            }

            RefreshOrbitZoneRadius();
        }

        /// <summary>One gem moon per planet: orbits at the outer orbit radius; ships dock here to deposit gems and open the orbit station UI.</summary>
        private void EnsureGemMoon()
        {
            if (gemMoon != null) return;
            var existing = GetComponentInChildren<PlanetGemMoon>(true);
            if (existing != null)
            {
                gemMoon = existing;
                RefreshGemMoonDockTriggerRadius();
                ApplyGemMoonVisualScale();
                RefreshGemMoonVisualMaterial();
                InjectGemMoonMatrixShieldPrefabs();
                return;
            }

            GameObject go = new GameObject("GemMoon");
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one;
            go.layer = gameObject.layer;

            // Moon needs collisions so ships can land on top of it.
            Rigidbody moonRb = go.AddComponent<Rigidbody>();
            moonRb.isKinematic = true;
            moonRb.useGravity = false;
            moonRb.constraints = RigidbodyConstraints.FreezeRotation;

            // Docking trigger (ignored by projectile SphereCasts; used for gem-moon docking state).
            var dockCol = go.AddComponent<SphereCollider>();
            dockCol.isTrigger = true;

            // Shield barrier trigger (keeps enemy ships out while shieldPoints > 0).
            var shieldCol = go.AddComponent<SphereCollider>();
            shieldCol.isTrigger = true;
            // Ensure this collider starts larger than the dock trigger so "dock = smallest trigger" remains stable.
            shieldCol.radius = Mathf.Max(0.01f, dockCol.radius * 1.5f);

            // Physics body collider (used for bullet hits and ship collision).
            var bodyCol = go.AddComponent<SphereCollider>();
            bodyCol.isTrigger = false;

            gemMoon = go.AddComponent<PlanetGemMoon>();

            RefreshGemMoonDockTriggerRadius();

            GameObject vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(vis.GetComponent<Collider>());
            vis.name = "GemMoonVisual";
            vis.transform.SetParent(go.transform, false);
            ApplyGemMoonVisualScale();
            var renderer = vis.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                ApplyGemMoonSurfaceToRenderer(renderer);
            }

            InjectGemMoonMatrixShieldPrefabs();
        }

        private void InjectGemMoonMatrixShieldPrefabs()
        {
            if (gemMoon == null) return;
            gemMoon.InjectMatrixShieldPrefabsIfMissing(
                gemMoonMatrixShieldRedPrefab,
                gemMoonMatrixShieldBluePrefab,
                gemMoonMatrixShieldGreenPrefab,
                gemMoonMatrixShieldModularPrefab);
        }

        /// <summary>SGT planet shader: disable water so the moon shows dry terrain only.</summary>
        private static void StripWaterFromGemMoonMaterial(Material m)
        {
            if (m == null) return;
            if (m.HasProperty("_HasWater")) m.SetFloat("_HasWater", 0f);
            if (m.HasProperty("_WaterLevel")) m.SetFloat("_WaterLevel", -2f);
        }

        /// <summary>Moon uses the same surface material family as this planet (home / neutral / team), never water.</summary>
        protected virtual void ApplyGemMoonSurfaceToRenderer(Renderer renderer)
        {
            if (renderer == null) return;
            EnsureSharedMaterialsRegistered();
            TeamManager.Team team = teamOwnership.Value;
            Material src;

            bool isRegular = !(this is HomePlanet);
            if (isRegular && team != TeamManager.Team.None)
            {
                Material neutralMat = GetNeutralMaterial();
                Material teamMat = GetTeamMaterial(team);
                if (neutralMat != null && teamMat != null)
                {
                    src = new Material(neutralMat);
                    Color neutralBase = GetMaterialColor(neutralMat);
                    Color teamColor = GetTeamColorFromMaterial(teamMat);
                    Color tinted = Color.Lerp(neutralBase, teamColor, regularPlanetTintIntensity);
                    if (src.HasProperty("_Color")) src.SetColor("_Color", tinted);
                    else if (src.HasProperty("_BaseColor")) src.SetColor("_BaseColor", tinted);
                }
                else
                {
                    Material nm = GetNeutralMaterial();
                    src = nm != null ? new Material(nm) : null;
                }
            }
            else
            {
                Material baseMat = GetEffectiveMaterialForPlanetSurface(team);
                src = baseMat != null ? new Material(baseMat) : null;
            }

            if (src == null) return;
            StripWaterFromGemMoonMaterial(src);
            renderer.material = src;
        }

        private void RefreshGemMoonVisualMaterial()
        {
            if (gemMoon == null) return;
            Transform vis = gemMoon.transform.Find("GemMoonVisual");
            if (vis == null) return;
            var r = vis.GetComponent<Renderer>();
            ApplyGemMoonSurfaceToRenderer(r);
        }

        /// <summary>
        /// Extra scale for home-planet gem moons only (regular planets use 1). Home moons are 1.5× the inverse-scaled baseline.
        /// </summary>
        protected virtual float GetGemMoonHomeVisualScaleMultiplier() => 1f;

        /// <summary>
        /// Uniform local scale for GemMoonVisual: baseline as if planet were <see cref="GemMoonReferencePlanetSize"/>, then × (20/PlanetSize), capped.
        /// </summary>
        private float GetGemMoonVisualUniformScale()
        {
            float baseAtRef = Mathf.Clamp(GemMoonReferencePlanetSize * 0.0035f, 0.02f, 0.1f) * 2.5f;
            float inv = GemMoonReferencePlanetSize / Mathf.Max(0.01f, PlanetSize);
            inv = Mathf.Min(inv, GemMoonInversePlanetSizeCap);
            float s = baseAtRef * inv * GetGemMoonHomeVisualScaleMultiplier();
            return Mathf.Clamp(s, 0.02f, 1.25f);
        }

        private void ApplyGemMoonVisualScale()
        {
            if (gemMoon == null) return;
            Transform vis = gemMoon.transform.Find("GemMoonVisual");
            if (vis == null) return;
            vis.localScale = Vector3.one * GetGemMoonVisualUniformScale();
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

            var allDrawers = GetComponentsInChildren<PlanetRingsDrawer>(true);
            PlanetRingsDrawer keep = null;
            foreach (var d in allDrawers)
            {
                if (d != null && d.transform.name == "PlanetRings")
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

        /// <summary>Add PlanetStatsDisplay at runtime for world-space population number (hides population text when active).</summary>
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

        /// <summary>
        /// Initial planet level.
        /// Home planets override this; regular neutral planets can start at a randomized level range.
        /// </summary>
        protected virtual int GetInitialPlanetLevel()
        {
            // Only regular planets use this implementation; HomePlanet overrides.
            if (templateStartingLevel >= 1)
                return Mathf.Clamp(templateStartingLevel, 1, GetMaxLevel());

            if (!randomizeNeutralStartingLevel)
                return 1;

            int maxLevel = GetMaxLevel();
            int clampedMin = Mathf.Clamp(minStartingLevel, 1, maxLevel);
            int clampedMax = Mathf.Clamp(maxStartingLevel, clampedMin, maxLevel);

            // Unity's int Random.Range is max‑exclusive, so add 1 to include clampedMax.
            int rolledLevel = Random.Range(clampedMin, clampedMax + 1);
            return Mathf.Max(1, Mathf.Min(rolledLevel, maxLevel));
        }

        /// <summary>Max gems capacity for a given level. Override in HomePlanet for different thresholds. Regular planets: 200 * 2^(level-1).</summary>
        protected virtual float GetMaxGemsForLevel(int level)
        {
            // Regular planets: Level 1 = 200, Level 2 = 400, Level 3 = 800, etc.
            if (level < 1) return 0f;
            return 200f * Mathf.Pow(2f, level - 1);
        }

        /// <summary>
        /// Max level for this planet type.
        /// Regular planets now share the same maximum (6) as home planets so neutral planets can be leveled up fully.
        /// </summary>
        protected virtual int GetMaxLevel() => 6;

        /// <summary>Server-only: apply gem deposit. Call this directly from server code (e.g. TickOrbitGemDeposit) instead of RPC to avoid RPC invocation issues when server calls itself.</summary>
        /// <param name="popupWorldPosition">Optional: where to show the floating gem count (e.g. gem moon when docking). Defaults to planet center.</param>
        public void DepositGemsFromServer(float amount, TeamManager.Team depositingTeam, ulong depositingClientId, Vector3? popupWorldPosition = null)
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
            float before = currentGems.Value;
            currentGems.Value = Mathf.Min(currentGems.Value + amount, maxGems);
            if (currentGems.Value >= maxGems - 0.001f)
                currentGems.Value = maxGems;

            // Feedback popup: show only the actual amount that increased gems (clamped by max).
            float delta = currentGems.Value - before;
            if (delta > 0.0001f && VisualEffectsManager.Instance != null)
            {
                Vector3 popupPos = popupWorldPosition ?? transform.position;
                popupPos.y = 0f;
                VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                    popupPos,
                    (int)FloatingCountChannel.GemDeposit,
                    delta,
                    (int)depositingTeam
                );
            }
            if (delta > 0.0001f)
            {
                PlayGemDepositSoundClientRpc(delta);
            }

            CheckLevelUp();
        }

        /// <summary>Server-only: drain gems without leveling down (can't decrease planet level).</summary>
        public void DrainGemsFromServer(float amount)
        {
            if (!IsServer) return;
            if (amount <= 0f) return;
            currentGems.Value = Mathf.Max(0f, currentGems.Value - amount);
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
                LevelUpFromServer();
        }

        /// <summary>Server-only level-up. Must not be a ServerRpc — deposits run on the server and NGO does not reliably execute self-invoked ServerRpcs.</summary>
        private void LevelUpFromServer()
        {
            if (!IsServer) return;
            if (planetLevel.Value >= GetMaxLevel()) return;

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

        /// <summary>
        /// Client-sync for the gem moon's matrix shield (deplete on hit; clients extrapolate regen from server time).
        /// </summary>
        [ClientRpc]
        public void GemMoonShieldClientRpc(float currentShieldPoints, float maxShieldPoints, float lastHitServerTime, float currentMoonGemPoints)
        {
            if (gemMoon == null) return;
            gemMoon.ApplyShieldClientSync(currentShieldPoints, maxShieldPoints, lastHitServerTime, currentMoonGemPoints);
        }

        protected virtual void OnPlanetLevelChanged(int previousLevel, int newLevel)
        {
            RefreshOrbitZoneRadius();
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
            AddPopulationFromServer(amount, sourceTeam);
        }

        /// <summary>Server-only: apply population from people transport (reinforce or hostile unload).</summary>
        public void AddPopulationFromServer(float amount, TeamManager.Team sourceTeam)
        {
            if (!IsServer || amount <= 0f) return;

            // Same-team planet: add population (reinforce)
            if (teamOwnership.Value != TeamManager.Team.None && teamOwnership.Value == sourceTeam)
            {
                currentPopulation.Value = Mathf.Min(currentPopulation.Value + amount, MaxPopulation);
                return;
            }

            // Neutral or enemy: unload decreases their population (capture attempt)
            lastHostilePopulationImpactServerTime = Time.time;
            currentPopulation.Value -= amount;
            if (currentPopulation.Value <= 0)
                CapturePlanetFromServer(sourceTeam);
        }

        private void CapturePlanetFromServer(TeamManager.Team newTeam)
        {
            if (!IsServer) return;
            teamOwnership.Value = newTeam;
            currentPopulation.Value = 0f;
            maxPopulation.Value = GetMaxPopulationForPlanet();
            CapturePlanetClientRpc(newTeam);
        }

        /// <summary>Server-only: remove population when crew loads onto a ship (avoids nested ServerRpc from server orbit transfer).</summary>
        public void RemovePopulationFromServer(float amount)
        {
            if (!IsServer || amount <= 0f) return;
            currentPopulation.Value = Mathf.Max(0f, currentPopulation.Value - amount);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemovePopulationServerRpc(float amount)
        {
            RemovePopulationFromServer(amount);
        }

        [ServerRpc(RequireOwnership = false)]
        public void CapturePlanetServerRpc(TeamManager.Team newTeam)
        {
            CapturePlanetFromServer(newTeam);
        }

        [ClientRpc]
        private void CapturePlanetClientRpc(TeamManager.Team newTeam)
        {
            UpdateVisual(newTeam);
            Debug.Log($"Planet captured by {newTeam}");
        }

        [ClientRpc]
        private void PlayGemDepositSoundClientRpc(float amount)
        {
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayGemDepositSound(amount);
        }

        private void OnOwnershipChanged(TeamManager.Team previousTeam, TeamManager.Team newTeam)
        {
            UpdateVisual(newTeam);
            UpdatePopulationDisplay();
            if (gemMoon != null)
                gemMoon.RefreshMatrixShieldForPlanetTeam();
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

            RefreshGemMoonVisualMaterial();
        }
        
        private Material GetTeamMaterial(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return teamAMaterial ?? s_sharedTeamA;
                case TeamManager.Team.TeamB: return teamBMaterial ?? s_sharedTeamB;
                case TeamManager.Team.TeamC: return teamCMaterial ?? s_sharedTeamC;
                case TeamManager.Team.TeamD: return teamDMaterial ?? s_sharedTeamD;
                case TeamManager.Team.TeamE: return teamEMaterial ?? s_sharedTeamE;
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
            foreach (var hp in HomePlanet.AllHomePlanets)
            {
                if (hp != null && hp.teamAMaterial != null)
                {
                    s_sharedNeutral = hp.neutralMaterial;
                    s_sharedTeamA = hp.teamAMaterial;
                    s_sharedTeamB = hp.teamBMaterial;
                    s_sharedTeamC = hp.teamCMaterial;
                    s_sharedTeamD = hp.teamDMaterial;
                    s_sharedTeamE = hp.teamEMaterial;
                    return;
                }
            }
            foreach (var p in AllPlanets)
            {
                if (p != null && p.teamAMaterial != null)
                {
                    s_sharedNeutral = p.neutralMaterial;
                    s_sharedTeamA = p.teamAMaterial;
                    s_sharedTeamB = p.teamBMaterial;
                    s_sharedTeamC = p.teamCMaterial;
                    s_sharedTeamD = p.teamDMaterial;
                    s_sharedTeamE = p.teamEMaterial;
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
                case TeamManager.Team.TeamD: return teamDMaterial ?? s_sharedTeamD ?? teamCMaterial ?? s_sharedTeamC ?? neutral;
                case TeamManager.Team.TeamE: return teamEMaterial ?? s_sharedTeamE ?? teamAMaterial ?? s_sharedTeamA ?? neutral;
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
