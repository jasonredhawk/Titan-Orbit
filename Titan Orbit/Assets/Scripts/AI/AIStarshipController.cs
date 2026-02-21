using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.AI
{
    /// <summary>
    /// AI controller for enemy starships. Works regardless of debug mode.
    /// Plain MonoBehaviour (not NetworkBehaviour) so it runs regardless of network spawn state.
    /// Only runs on server - checked via NetworkManager.Singleton.IsServer.
    /// </summary>
    [RequireComponent(typeof(Starship))]
    [RequireComponent(typeof(Rigidbody))]
    [DefaultExecutionOrder(-100)] // Run before Starship so our movement is applied first
    public class AIStarshipController : MonoBehaviour
    {
        public enum AIBehaviorType
        {
            Mining,      // Mine asteroids and return gems to home planet
            Transport    // Load people from home planet and unload on nearby planets
        }

        [Header("AI Settings")]
        [SerializeField] private AIBehaviorType behaviorType = AIBehaviorType.Mining;
        [SerializeField] private float detectionRange = 300f;
        [SerializeField] private float arrivalDistance = 2f;
        [SerializeField] private float miningRange = 3f;
        [SerializeField] private float gemCollectionProximity = 30f; // Only collect gems within this range; otherwise return to asteroids
        [SerializeField] private float updateInterval = 0.5f; // Update AI decisions every 0.5 seconds
        [SerializeField] private float orbitSpeed = 0.8f;
        [SerializeField] private float attackRange = 3f; // Range at which to attack enemy ships (close proximity only - very short range)

        private Starship starship;
        private Rigidbody rb;
        private TeamManager.Team assignedTeam;
        private HomePlanet homePlanet;
        
        // State machine
        private enum AIState
        {
            Idle,
            MovingToTarget,
            ShootingAsteroid,   // At asteroid, shooting to destroy it
            CollectingGems,    // Asteroid destroyed; move toward gems to collect
            ReturningToHome,
            LoadingPeople,
            MovingToPlanet,
            UnloadingPeople,
            AttackingEnemy,    // Attacking an enemy ship while moving
            LevelingUp         // Mining and depositing gems to level up ship and attributes
        }
        private AIState currentState = AIState.Idle;
        
        private Vector3 targetPosition;
        private Asteroid targetAsteroid;
        private Planet targetPlanet;
        private Gem targetGem;  // Gem we're moving toward to collect
        private Starship targetEnemyShip;  // Enemy ship we're attacking
        private AIState previousState;  // Store previous state to return to after combat
        private float lastUpdateTime;
        private Vector3 moveDirection = Vector3.zero;
        
        // Combat movement randomization
        private float combatPatternChangeTime = 0f;
        private float combatPatternDuration = 2f; // Change pattern every 2 seconds
        private int currentCombatPattern = 0; // 0-3: different movement patterns
        private float strafeDirection = 1f; // -1 or 1 for left/right strafe
        private float preferredCombatDistance = 0f; // Randomized preferred distance
        private float combatCooldownUntil = 0f; // Don't re-engage combat until this time (reduces constant fighting)
        
        // Cached object lists to avoid expensive FindObjectsOfType calls every update
        private static float lastCacheRefreshTime = 0f;
        private static float cacheRefreshInterval = 1f; // Refresh cache every 1 second
        private static Asteroid[] cachedAsteroids = new Asteroid[0];
        private static Gem[] cachedGems = new Gem[0];
        private static Planet[] cachedPlanets = new Planet[0];
        private static Starship[] cachedShips = new Starship[0];

        public AIBehaviorType BehaviorType => behaviorType;
        public TeamManager.Team AssignedTeam => assignedTeam;

        private void Awake()
        {
            starship = GetComponent<Starship>();
            rb = GetComponent<Rigidbody>();
        }

        /// <summary>True when we should run AI (server only).</summary>
        private bool IsServerAuthority => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        /// <summary>Call after AddComponent when added post-spawn. Must be called from AIStarshipManager.</summary>
        public void InitFromServer(TeamManager.Team team, HomePlanet home)
        {
            if (!IsServerAuthority) return;
            assignedTeam = team;
            homePlanet = home;
            if (homePlanet == null) FindHomePlanet();
            targetPosition = rb != null ? rb.position : transform.position;
            currentState = AIState.Idle;
            previousState = AIState.Idle;
            lastUpdateTime = Time.time;
            // Spawn in orbit zone - triggers don't fire for objects that start inside, so set orbit manually
            TryEnterOrbitZoneIfInRange();
        }

        private void Update()
        {
            if (!IsServerAuthority) return;
            if (starship == null || starship.IsDead) return;

            // Handle health and energy regen for AI ships (they don't have an owner so Starship.Update won't do it)
            HandleHealthRegen();
            HandleEnergyRegen();
        }

        private void FixedUpdate()
        {
            if (!IsServerAuthority) return;
            if (starship == null) return;
            
            // Always sync debug data (even when dead, so text can show state transitions)
            var debugSync = GetComponent<AIStarshipDebugSync>();
            if (debugSync != null)
                debugSync.SetDebug(targetPosition, (int)currentState);
            
            if (starship.IsDead) return;

            // Ensure orbit zone is set if we're in it (triggers don't fire for objects that start inside)
            TryEnterOrbitZoneIfInRange();

            // Update AI decisions periodically
            if (Time.time - lastUpdateTime >= updateInterval)
            {
                UpdateAI();
                lastUpdateTime = Time.time;
            }

            // Handle shooting when at asteroid (shoot every FixedUpdate - FireAtTarget respects fire rate)
            // Works for both mining behavior and leveling behavior (both miners and transporters can mine when leveling)
            if (currentState == AIState.ShootingAsteroid && targetAsteroid != null && !targetAsteroid.IsDestroyed)
                HandleShootingAsteroid();

            // Handle attacking enemy ships (moves while shooting)
            if (currentState == AIState.AttackingEnemy && targetEnemyShip != null && !targetEnemyShip.IsDead)
                HandleAttackingEnemy();

            // Handle movement
            HandleAIMovement();
        }

        private void HandleHealthRegen()
        {
            // Health can regen even when at zero - regen is allowed
            // Only prevent regen when dead
            if (starship.IsDead) return;
            // AI ships regen health on server (same logic as Starship.HandleHealthRegen)
            if (starship.CurrentHealth < starship.MaxHealth)
            {
                float regenRate = 1f; // Base regen rate per second
                float regen = regenRate * Time.deltaTime;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode) regen *= 100f;
                
                // Access NetworkVariable via reflection (Starship doesn't expose it publicly)
                var healthField = typeof(Starship).GetField("currentHealth", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (healthField != null)
                {
                    var healthVar = healthField.GetValue(starship) as NetworkVariable<float>;
                    if (healthVar != null && IsServerAuthority)
                    {
                        float newHealth = healthVar.Value + regen;
                        healthVar.Value = Mathf.Min(newHealth, starship.MaxHealth);
                        // Safety check: clamp health to zero minimum
                        if (healthVar.Value < 0f)
                        {
                            healthVar.Value = 0f;
                        }
                    }
                }
            }
        }

        private void HandleEnergyRegen()
        {
            // AI ships regen energy on server (same logic as Starship.HandleEnergyRegen)
            if (starship.CurrentEnergy < starship.EnergyCapacity)
            {
                float regenRate = 5f; // Base regen rate per second
                float regen = regenRate * Time.deltaTime;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode) regen *= 100f;
                
                // Access NetworkVariable via reflection (Starship doesn't expose it publicly)
                var energyField = typeof(Starship).GetField("currentEnergy", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (energyField != null)
                {
                    var energyVar = energyField.GetValue(starship) as NetworkVariable<float>;
                    if (energyVar != null && IsServerAuthority)
                    {
                        energyVar.Value = Mathf.Min(energyVar.Value + regen, starship.EnergyCapacity);
                    }
                }
            }
        }

        private void UpdateAI()
        {
            if (homePlanet == null)
            {
                FindHomePlanet();
                if (homePlanet == null) return;
            }

            // --- PRIMARY BEHAVIOR FIRST ---
            // Run leveling/upgrade/primary action BEFORE combat check.
            // Combat only overrides when enemy is in range - don't let combat prevent primary actions.
            // Hierarchy: level up -> upgrade abilities -> primary action (mine/transport)
            if (CanLevelUpPotential())
            {
                UpdateLevelingBehavior();
            }
            else if (CanUpgradeAnyAttributePotential())
            {
                UpdateLevelingBehavior();
            }
            else if (IsFullyMaxedOut())
            {
                // Fully maxed: do primary action (mine for home, or transport people)
                if (currentState == AIState.ReturningToHome && targetAsteroid == null && targetGem == null)
                {
                    currentState = AIState.Idle;
                    targetAsteroid = null;
                    targetGem = null;
                }
                switch (behaviorType)
                {
                    case AIBehaviorType.Mining:
                        UpdateMiningBehavior();
                        break;
                    case AIBehaviorType.Transport:
                        UpdateTransportBehavior();
                        break;
                }
            }
            else
            {
                UpdateLevelingBehavior();
            }

            // --- COMBAT OVERRIDE (only when enemy in range) ---
            // If already in combat, verify target is still in range - exit if not (don't chase)
            if (currentState == AIState.AttackingEnemy && targetEnemyShip != null && !targetEnemyShip.IsDead)
            {
                float distToTarget = ToroidalMap.ToroidalDistance(rb.position, targetEnemyShip.transform.position);
                if (distToTarget > attackRange)
                {
                    combatCooldownUntil = Time.time + 3f;
                    targetEnemyShip = null;
                    currentState = previousState; // Resume primary task - don't clear targets
                    ExitOrbitIfInOrbit();
                }
            }
            else if (currentState == AIState.AttackingEnemy)
            {
                // Target dead or invalid - exit combat and resume primary task
                combatCooldownUntil = Time.time + 4f;
                targetEnemyShip = null;
                currentState = previousState; // Resume primary task - don't clear targets
                ExitOrbitIfInOrbit();
            }
            else
            {
                // Not in combat - check if enemy entered range (override primary action only when enemy is close)
                bool inCombatCooldown = Time.time < combatCooldownUntil;
                Starship nearestEnemy = inCombatCooldown ? null : FindNearestEnemyShip();
                if (nearestEnemy != null)
                {
                    previousState = currentState;
                    targetEnemyShip = nearestEnemy;
                    currentState = AIState.AttackingEnemy;
                    currentCombatPattern = Random.Range(0, 4);
                    strafeDirection = Random.value > 0.5f ? 1f : -1f;
                    preferredCombatDistance = attackRange * Random.Range(0.6f, 0.9f);
                    combatPatternDuration = Random.Range(1.5f, 3f);
                    combatPatternChangeTime = Time.time + combatPatternDuration;
                    ExitOrbitIfInOrbit();
                }
            }
        }

        private void UpdateMiningBehavior()
        {
            switch (currentState)
            {
                case AIState.Idle:
                case AIState.MovingToTarget:
                    // If gems = max gems, find nearest planet to deposit (home or captured)
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        // Check if home planet is fully maxed (level 6 is max for home planets)
                        bool homePlanetMaxed = homePlanet.PlanetLevel >= 6;
                        
                        // Check if already at a friendly planet - if so, deposit and then continue
                        Planet currentDepositPlanet = null;
                        float distanceToHome = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                        float homeOrbitRadius = homePlanet.PlanetSize * 0.85f;
                        
                        if (distanceToHome <= homeOrbitRadius)
                        {
                            // At home planet - deposit if not maxed, otherwise go to nearby planet
                            if (!homePlanetMaxed)
                            {
                                currentDepositPlanet = homePlanet;
                            }
                        }
                        else
                        {
                            // Check if at a captured planet
                            Planet nearestCaptured = FindNearestCapturedPlanet();
                            if (nearestCaptured != null)
                            {
                                float distToCaptured = ToroidalMap.ToroidalDistance(rb.position, nearestCaptured.transform.position);
                                float capturedOrbitRadius = nearestCaptured.PlanetSize * 0.85f;
                                if (distToCaptured <= capturedOrbitRadius)
                                {
                                    currentDepositPlanet = nearestCaptured;
                                }
                            }
                        }
                        
                        if (currentDepositPlanet != null)
                        {
                            // Already at a friendly planet - deposit gems
                            starship.SetWantToDepositGemsFromServer(true);
                            currentState = AIState.Idle; // Will check again next update
                        }
                        else
                        {
                            // Not at a friendly planet - choose target
                            Planet targetDepositPlanet = null;
                            
                            if (homePlanetMaxed)
                            {
                                // Home planet is maxed - bring gems to nearby captured planets to level them up
                                Planet nearestCaptured = FindNearestCapturedPlanet();
                                if (nearestCaptured != null)
                                {
                                    targetDepositPlanet = nearestCaptured;
                                }
                                else
                                {
                                    // No captured planets - still go to home (might level up later)
                                    targetDepositPlanet = homePlanet;
                                }
                            }
                            else
                            {
                                // Home planet not maxed - go to nearest (home or captured)
                                Planet nearestFriendly = FindNearestCapturedPlanet();
                                float distToHomeCheck = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                                float distToCapturedCheck = nearestFriendly != null ? ToroidalMap.ToroidalDistance(rb.position, nearestFriendly.transform.position) : float.MaxValue;
                                
                                targetDepositPlanet = (nearestFriendly != null && distToCapturedCheck < distToHomeCheck) ? nearestFriendly : homePlanet;
                            }
                            
                            if (targetDepositPlanet != null)
                            {
                                targetPosition = targetDepositPlanet.transform.position;
                                SetTargetInOrbitZone(targetDepositPlanet, ref targetPosition);
                                currentState = AIState.ReturningToHome;
                                ExitOrbitIfInOrbit();
                            }
                        }
                        break;
                    }
                    // Find nearest asteroid with gems - target orbit at miningRange (avoid bumping)
                    Asteroid nearestAsteroid = FindNearestMineableAsteroid();
                    if (nearestAsteroid != null)
                    {
                        targetAsteroid = nearestAsteroid;
                        Vector3 dirToShip = ToroidalMap.ToroidalDirection(nearestAsteroid.transform.position, rb.position);
                        targetPosition = nearestAsteroid.transform.position + dirToShip * (nearestAsteroid.AsteroidSize * 0.5f + miningRange * 0.9f);
                        targetPosition = ToroidalMap.WrapPosition(targetPosition);
                        currentState = AIState.MovingToTarget;
                        ExitOrbitIfInOrbit();
                    }
                    else
                    {
                        currentState = AIState.Idle;
                    }
                    break;

                case AIState.ShootingAsteroid:
                    // Asteroid destroyed? Transition to collecting gems
                    if (targetAsteroid == null || targetAsteroid.IsDestroyed)
                    {
                        targetAsteroid = null;
                        currentState = AIState.CollectingGems;
                        targetGem = FindNearestGemWithinRange(gemCollectionProximity);
                        if (targetGem != null)
                            targetPosition = ToroidalMap.WrapPosition(targetGem.transform.position);
                        break;
                    }

                    float distanceToAsteroid = ToroidalMap.ToroidalDistance(rb.position, targetAsteroid.transform.position);
                    if (distanceToAsteroid > miningRange)
                    {
                        // Move closer - target orbit point (not center) to avoid bumping
                        Vector3 dirToShip = ToroidalMap.ToroidalDirection(targetAsteroid.transform.position, rb.position);
                        targetPosition = targetAsteroid.transform.position + dirToShip * (targetAsteroid.AsteroidSize * 0.5f + miningRange * 0.9f);
                        targetPosition = ToroidalMap.WrapPosition(targetPosition);
                        currentState = AIState.MovingToTarget;
                    }
                    // Shooting happens in FixedUpdate via HandleShootingAsteroid
                    // If ship is full, return to nearest friendly planet to deposit
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        currentState = AIState.ReturningToHome;
                        targetAsteroid = null;
                        // Target will be set in ReturningToHome state
                    }
                    break;

                case AIState.CollectingGems:
                    // Ship full? Return to nearest friendly planet to deposit
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        currentState = AIState.ReturningToHome;
                        targetGem = null;
                        // Target will be set in ReturningToHome state
                        break;
                    }
                    // Find nearest gem within proximity only - otherwise return to asteroids
                    targetGem = FindNearestGemWithinRange(gemCollectionProximity);
                    if (targetGem != null)
                    {
                        targetPosition = ToroidalMap.WrapPosition(targetGem.transform.position);
                    }
                    else
                    {
                        // No gems nearby - go find next asteroid
                        targetGem = null;
                        currentState = AIState.Idle;
                    }
                    break;

                case AIState.ReturningToHome:
                    // Target orbit zone at deposit planet (home or captured) - determine which one we're going to
                    bool homePlanetMaxedMining = homePlanet.PlanetLevel >= 6; // Max level for home planets is 6
                    Planet depositTarget = homePlanet;
                    
                    if (homePlanetMaxedMining)
                    {
                        // Home planet is maxed - bring gems to nearby captured planets
                        Planet nearestCapturedForDeposit = FindNearestCapturedPlanet();
                        if (nearestCapturedForDeposit != null)
                        {
                            depositTarget = nearestCapturedForDeposit;
                        }
                        // Otherwise use home planet (fallback)
                    }
                    else
                    {
                        // Home planet not maxed - choose nearest (home or captured)
                        float distToHomeReturn = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                        Planet nearestCapturedForDeposit = FindNearestCapturedPlanet();
                        if (nearestCapturedForDeposit != null)
                        {
                            float distToCapturedReturn = ToroidalMap.ToroidalDistance(rb.position, nearestCapturedForDeposit.transform.position);
                            if (distToCapturedReturn < distToHomeReturn)
                            {
                                depositTarget = nearestCapturedForDeposit;
                            }
                        }
                    }
                    
                    SetTargetInOrbitZone(depositTarget, ref targetPosition);
                    float distToDepositTarget = ToroidalMap.ToroidalDistance(rb.position, depositTarget.transform.position);
                    float depositOrbitRadius = depositTarget.PlanetSize * 0.85f; // Use outer orbit band (0.5-0.85)
                    
                    if (distToDepositTarget <= depositOrbitRadius)
                    {
                        starship.SetWantToDepositGemsFromServer(true);
                        // After depositing, transition to Idle so ship can continue mining
                        // The Idle state will check if gems are full and either return home again or go mine
                        currentState = AIState.Idle;
                        // Clear any stale targets
                        targetAsteroid = null;
                        targetGem = null;
                    }
                    break;
            }
        }

        private void UpdateTransportBehavior()
        {
            switch (currentState)
            {
                case AIState.Idle:
                case AIState.LoadingPeople:
                    // If people = 0, find planet with most people (same team) to load from
                    if (starship.CurrentPeople < starship.PeopleCapacity - 0.1f)
                    {
                        Planet planetWithMostPeople = FindPlanetWithMostPeople();
                        if (planetWithMostPeople != null)
                        {
                            float distToLoadPlanet = ToroidalMap.ToroidalDistance(rb.position, planetWithMostPeople.transform.position);
                            float orbitRadius = Mathf.Max(planetWithMostPeople.PlanetSize * 1.1f, 6f);
                            
                            if (distToLoadPlanet <= orbitRadius)
                            {
                                starship.SetWantToLoadPeopleFromServer(true);
                                currentState = AIState.LoadingPeople;
                            }
                            else
                            {
                                targetPosition = planetWithMostPeople.transform.position;
                                SetTargetInOrbitZone(planetWithMostPeople, ref targetPosition);
                                currentState = AIState.MovingToTarget;
                                ExitOrbitIfInOrbit();
                            }
                        }
                        else
                        {
                            // No planets with people available - stay idle
                            currentState = AIState.Idle;
                        }
                    }
                    else if (starship.CurrentPeople >= starship.PeopleCapacity - 0.1f)
                    {
                        // People = max: target = closest neutral/enemy planet OR captured planet for unloading
                        Planet nearestNeutral = FindNearestPlanet();
                        Planet nearestCaptured = FindNearestCapturedPlanet();
                        
                        Planet targetUnloadPlanet = null;
                        if (nearestNeutral != null && nearestCaptured != null)
                        {
                            // Choose closest between neutral and captured
                            float distToNeutral = ToroidalMap.ToroidalDistance(rb.position, nearestNeutral.transform.position);
                            float distToCaptured = ToroidalMap.ToroidalDistance(rb.position, nearestCaptured.transform.position);
                            targetUnloadPlanet = distToNeutral < distToCaptured ? nearestNeutral : nearestCaptured;
                        }
                        else if (nearestNeutral != null)
                        {
                            targetUnloadPlanet = nearestNeutral;
                        }
                        else if (nearestCaptured != null)
                        {
                            targetUnloadPlanet = nearestCaptured;
                        }
                        
                        if (targetUnloadPlanet != null)
                        {
                            targetPlanet = targetUnloadPlanet;
                            targetPosition = targetUnloadPlanet.transform.position;
                            SetTargetInOrbitZone(targetUnloadPlanet, ref targetPosition);
                            currentState = AIState.MovingToPlanet;
                            ExitOrbitIfInOrbit();
                        }
                        else
                        {
                            // No planets to transport to - stay idle but keep checking
                            currentState = AIState.Idle;
                        }
                    }
                    break;

                case AIState.MovingToTarget:
                    // Transport going to planet with most people to load - check if we've arrived
                    Planet loadTargetPlanet = FindPlanetWithMostPeople();
                    if (loadTargetPlanet != null)
                    {
                        SetTargetInOrbitZone(loadTargetPlanet, ref targetPosition);
                        float distanceToLoadPlanet = ToroidalMap.ToroidalDistance(rb.position, loadTargetPlanet.transform.position);
                        float loadOrbitRadius = Mathf.Max(loadTargetPlanet.PlanetSize * 1.1f, 6f); // Extend past orbit zone; min 6 for small planets
                        if (distanceToLoadPlanet <= loadOrbitRadius)
                        {
                            starship.SetWantToLoadPeopleFromServer(true);
                            currentState = AIState.LoadingPeople;
                        }
                    }
                    else
                    {
                        // No planet with people found - go back to idle
                        currentState = AIState.Idle;
                    }
                    break;

                case AIState.MovingToPlanet:
                    if (targetPlanet == null)
                    {
                        currentState = AIState.Idle;
                        break;
                    }
                    SetTargetInOrbitZone(targetPlanet, ref targetPosition);

                    float distanceToPlanet = ToroidalMap.ToroidalDistance(rb.position, targetPlanet.transform.position);
                    float planetOrbitRadius = Mathf.Max(targetPlanet.PlanetSize * 1.1f, 6f); // Extend past orbit zone; min 6 for small planets
                    
                    if (distanceToPlanet <= planetOrbitRadius)
                    {
                        starship.SetWantToUnloadPeopleFromServer(true);
                        currentState = AIState.UnloadingPeople;
                    }
                    break;

                case AIState.UnloadingPeople:
                    // Done unloading - ship empty
                    if (starship.CurrentPeople <= 0.1f)
                    {
                        starship.SetWantToUnloadPeopleFromServer(false);
                        targetPlanet = null;
                        currentState = AIState.Idle;
                        ExitOrbitIfInOrbit(); // Leave target planet orbit, go back to home
                    }
                    // Planet captured - we now own it; leave even if ship has people left (planet may be full)
                    else if (targetPlanet != null && targetPlanet.TeamOwnership == assignedTeam)
                    {
                        starship.SetWantToUnloadPeopleFromServer(false);
                        targetPlanet = null;
                        currentState = AIState.Idle;
                        ExitOrbitIfInOrbit();
                    }
                    break;
            }
        }

        [Header("Movement Parameters")]
        [SerializeField] private float aiAcceleration = 20f;
        [SerializeField] private float aiMaxSpeed = 10f;
        [SerializeField] private float aiDeceleration = 8f;
        [SerializeField] private float aiRotationSpeed = 180f;

        private void HandleAIMovement()
        {
            if (rb == null || starship == null) return;
            
            // Dead ships cannot move - stop all movement
            if (starship.IsDead)
            {
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                vel = Vector3.MoveTowards(vel, Vector3.zero, aiDeceleration * Time.fixedDeltaTime);
                rb.linearVelocity = vel;
                moveDirection = Vector3.zero;
                return;
            }

            // When in orbit zone and idle/loading/unloading/depositing, orbit slowly
            // Don't orbit when attacking enemies
            if (currentState != AIState.AttackingEnemy && ShouldOrbit())
            {
                HandleAIOrbit();
                return;
            }

            // Skip normal movement logic when attacking - HandleAttackingEnemy sets moveDirection
            if (currentState == AIState.AttackingEnemy)
            {
                // Movement direction is set by HandleAttackingEnemy, just apply it
                // (Don't check arrival or set direction here)
            }
            else
            {
                // Use toroidal direction/distance (map wraps - raw Vector3 is wrong for distant targets)
                Vector3 pos = rb.position;
                pos.y = 0f;
                Vector3 toTarget = ToroidalMap.ToroidalDirection(pos, targetPosition);
                float distanceToTarget = ToroidalMap.ToroidalDistance(pos, targetPosition);

                // Check if we're leveling up - both miners and transporters mine asteroids when leveling
                bool isLevelingUp = !IsFullyMaxedOut();

                // Arrival threshold: use miningRange for asteroids, small value for gems (collect radius 0.6), arrivalDistance for planets
                float effectiveArrival = arrivalDistance;
                // Both miners and transporters can mine asteroids when leveling up
                if (currentState == AIState.MovingToTarget && targetAsteroid != null && 
                    (behaviorType == AIBehaviorType.Mining || isLevelingUp))
                    effectiveArrival = miningRange * 0.9f; // Stop at orbit point around asteroid
                else if (currentState == AIState.CollectingGems)
                    effectiveArrival = 0.5f; // Get close enough for gem collect (Gem.collectRadius = 0.6)

                // Transport: transition when within planet orbit radius - but skip this when leveling up
                if (behaviorType == AIBehaviorType.Transport && !isLevelingUp)
                {
                    if (currentState == AIState.MovingToTarget && homePlanet != null && targetAsteroid == null)
                    {
                        float distToHome = ToroidalMap.ToroidalDistance(pos, homePlanet.transform.position);
                        float orbitRadius = Mathf.Max(homePlanet.PlanetSize * 1.1f, 6f); // Extend past orbit zone; min 6 for small planets
                        if (distToHome <= orbitRadius)
                        {
                            starship.SetWantToLoadPeopleFromServer(true);
                            currentState = AIState.LoadingPeople;
                            moveDirection = Vector3.zero;
                            Vector3 vel = rb.linearVelocity;
                            vel.y = 0f;
                            vel = Vector3.MoveTowards(vel, Vector3.zero, aiDeceleration * Time.fixedDeltaTime);
                            rb.linearVelocity = vel;
                            return;
                        }
                    }
                    else if (currentState == AIState.MovingToPlanet && targetPlanet != null)
                    {
                        float distToPlanet = ToroidalMap.ToroidalDistance(pos, targetPlanet.transform.position);
                        float orbitRadius = Mathf.Max(targetPlanet.PlanetSize * 1.1f, 6f); // Extend past orbit zone; min 6 for small planets
                        if (distToPlanet <= orbitRadius)
                        {
                            starship.SetWantToUnloadPeopleFromServer(true);
                            currentState = AIState.UnloadingPeople;
                            moveDirection = Vector3.zero;
                            Vector3 vel = rb.linearVelocity;
                            vel.y = 0f;
                            vel = Vector3.MoveTowards(vel, Vector3.zero, aiDeceleration * Time.fixedDeltaTime);
                            rb.linearVelocity = vel;
                            return;
                        }
                    }
                }

                // Check if we've arrived at target - transition to ShootingAsteroid when we reach orbit point
                if (distanceToTarget <= effectiveArrival)
                {
                    // Reached orbit point around asteroid? Start shooting (both miners and transporters when leveling)
                    if (currentState == AIState.MovingToTarget && targetAsteroid != null && !targetAsteroid.IsDestroyed &&
                        (behaviorType == AIBehaviorType.Mining || isLevelingUp))
                        currentState = AIState.ShootingAsteroid;

                    moveDirection = Vector3.zero;
                    Vector3 vel = rb.linearVelocity;
                    vel.y = 0f;
                    vel = Vector3.MoveTowards(vel, Vector3.zero, aiDeceleration * Time.fixedDeltaTime);
                    rb.linearVelocity = vel;
                    return;
                }

                // Use toroidal direction (already normalized)
                if (distanceToTarget > 0.01f)
                {
                    moveDirection = toTarget;
                }
                else
                {
                    moveDirection = Vector3.zero;
                }
            }

            // Apply movement
            Vector3 currentVelocity = rb.linearVelocity;
            currentVelocity.y = 0f;

            if (moveDirection.magnitude > 0.1f)
            {
                currentVelocity += moveDirection * aiAcceleration * Time.fixedDeltaTime;
                
                if (currentVelocity.magnitude > aiMaxSpeed)
                    currentVelocity = currentVelocity.normalized * aiMaxSpeed;
            }
            else
            {
                currentVelocity = Vector3.MoveTowards(currentVelocity, Vector3.zero, aiDeceleration * Time.fixedDeltaTime);
            }

            // Rotate towards movement direction
            if (moveDirection.magnitude > 0.1f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(moveDirection);
                rb.MoveRotation(Quaternion.RotateTowards(
                    rb.rotation,
                    targetRotation,
                    aiRotationSpeed * Time.fixedDeltaTime
                ));
            }

            // Apply velocity
            currentVelocity.y = 0f;
            rb.linearVelocity = currentVelocity;

            // Update position with toroidal wrapping
            Vector3 newPosition = rb.position + currentVelocity * Time.fixedDeltaTime;
            newPosition.y = 0f;
            newPosition = ToroidalMap.WrapPosition(newPosition);
            rb.MovePosition(newPosition);
            transform.position = newPosition;
        }

        private bool ShouldOrbit()
        {
            if (starship == null || starship.CurrentOrbitPlanet == null) return false;
            // Never orbit when we have a movement target or are shooting/collecting/attacking
            if (currentState == AIState.MovingToTarget || currentState == AIState.MovingToPlanet || 
                currentState == AIState.CollectingGems || currentState == AIState.ShootingAsteroid ||
                currentState == AIState.AttackingEnemy || currentState == AIState.ReturningToHome)
            {
                starship.ExitOrbitZone(starship.CurrentOrbitPlanet);
                return false;
            }
            // Only orbit when idle/loading/unloading/depositing AND we don't have work to do
            // For miners: orbit only when depositing gems (wantToDepositGems)
            // When fully maxed, NEVER orbit at home idle - they should be mining
            if (behaviorType == AIBehaviorType.Mining)
            {
                if (starship.WantToDepositGems) return true;
                // Allow idle-at-home orbit ONLY when leveling (not fully maxed) - waiting for upgrade
                if (!IsFullyMaxedOut() && currentState == AIState.Idle && homePlanet != null)
                {
                    float distToHome = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                    float homeOrbitRadius = homePlanet.PlanetSize * 0.85f;
                    if (distToHome <= homeOrbitRadius && targetAsteroid == null && targetGem == null)
                    {
                        return true; // Idle at home while leveling - allow orbiting
                    }
                }
                return false;
            }
            // For transporters: orbit when loading/unloading people
            return currentState == AIState.LoadingPeople || currentState == AIState.UnloadingPeople;
        }

        private void HandleAIOrbit()
        {
            Planet orbitPlanet = starship.CurrentOrbitPlanet;
            if (orbitPlanet == null || rb == null) return;
            Vector3 planetPos = orbitPlanet.transform.position;
            Vector3 toShip = rb.position - planetPos;
            toShip.y = 0f;
            float dist = toShip.magnitude;
            if (dist < 0.01f) return;
            float innerWorld = orbitPlanet.PlanetSize * 0.5f;
            float outerWorld = orbitPlanet.PlanetSize * 0.85f;
            Vector3 radial = toShip / dist;
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);
            Vector3 orbitVelocity = tangent * orbitSpeed;
            if (dist < innerWorld)
                orbitVelocity += radial * 2f;
            else if (dist > outerWorld)
                orbitVelocity -= radial * 2f;
            orbitVelocity.y = 0f;
            rb.linearVelocity = orbitVelocity;
            Vector3 newPosition = rb.position + orbitVelocity * Time.fixedDeltaTime;
            newPosition.y = 0f;
            newPosition = ToroidalMap.WrapPosition(newPosition);
            rb.MovePosition(newPosition);
            transform.position = newPosition;
            transform.rotation = Quaternion.LookRotation(tangent);
        }

        private void HandleShootingAsteroid()
        {
            if (targetAsteroid == null || targetAsteroid.IsDestroyed || starship == null || rb == null) return;

            Vector3 dirToAsteroid = ToroidalMap.ToroidalDirection(rb.position, targetAsteroid.transform.position);
            dirToAsteroid.y = 0f;
            if (dirToAsteroid.sqrMagnitude < 0.01f) return;
            dirToAsteroid.Normalize();

            // Rotate toward asteroid
            Quaternion targetRotation = Quaternion.LookRotation(dirToAsteroid);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, targetRotation, aiRotationSpeed * Time.fixedDeltaTime));

            // Shoot (FireAtTarget respects fire rate and energy)
            starship.FireAtTarget(dirToAsteroid);
        }

        private void HandleAttackingEnemy()
        {
            if (targetEnemyShip == null || targetEnemyShip.IsDead || starship == null || rb == null) return;

            Vector3 myPos = rb.position;
            myPos.y = 0f;
            Vector3 enemyPos = targetEnemyShip.transform.position;
            enemyPos.y = 0f;
            
            // Calculate direction to enemy using toroidal distance
            Vector3 dirToEnemy = ToroidalMap.ToroidalDirection(myPos, enemyPos);
            float distanceToEnemy = ToroidalMap.ToroidalDistance(myPos, enemyPos);
            
            // Don't chase - if enemy left range, stop moving and shooting (UpdateAI will exit combat)
            if (distanceToEnemy > attackRange)
            {
                moveDirection = Vector3.zero;
                return;
            }
            
            if (dirToEnemy.sqrMagnitude < 0.01f) return;
            dirToEnemy.Normalize();

            // Change combat pattern periodically for more dynamic dogfights
            if (Time.time >= combatPatternChangeTime)
            {
                // Randomize combat pattern (0-3: different movement styles)
                currentCombatPattern = Random.Range(0, 4);
                // Randomize strafe direction (left or right)
                strafeDirection = Random.value > 0.5f ? 1f : -1f;
                // Randomize preferred combat distance (60% to 90% of attack range)
                preferredCombatDistance = attackRange * Random.Range(0.6f, 0.9f);
                // Set next pattern change time (1.5 to 3 seconds)
                combatPatternDuration = Random.Range(1.5f, 3f);
                combatPatternChangeTime = Time.time + combatPatternDuration;
            }

            // Rotate toward enemy (but add some randomness to rotation for more dynamic feel)
            Vector3 aimDirection = dirToEnemy;
            // Add slight random jitter to aim (5 degrees max)
            float aimJitter = Random.Range(-5f, 5f) * Mathf.Deg2Rad;
            Vector3 jitterAxis = Vector3.Cross(dirToEnemy, Vector3.up).normalized;
            if (jitterAxis.sqrMagnitude > 0.01f)
            {
                aimDirection = Quaternion.AngleAxis(aimJitter * Mathf.Rad2Deg, Vector3.up) * dirToEnemy;
            }
            
            Quaternion targetRotation = Quaternion.LookRotation(aimDirection);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, targetRotation, aiRotationSpeed * Time.fixedDeltaTime));

            // Shoot at enemy (FireAtTarget respects fire rate and energy)
            starship.FireAtTarget(dirToEnemy);

            // Dynamic combat movement patterns - different behaviors for variety
            Vector3 movementDir = Vector3.zero;
            
            switch (currentCombatPattern)
            {
                case 0: // Aggressive approach with strafe
                    if (distanceToEnemy > preferredCombatDistance)
                    {
                        // Approach while strafing
                        Vector3 tangent = new Vector3(-dirToEnemy.z * strafeDirection, 0f, dirToEnemy.x * strafeDirection);
                        movementDir = (dirToEnemy * 0.7f + tangent * 0.3f).normalized;
                    }
                    else
                    {
                        // Circle strafe
                        Vector3 tangent = new Vector3(-dirToEnemy.z * strafeDirection, 0f, dirToEnemy.x * strafeDirection);
                        movementDir = tangent;
                    }
                    break;
                    
                case 1: // Defensive circling
                    // Always circle, but vary distance
                    Vector3 tangent1 = new Vector3(-dirToEnemy.z * strafeDirection, 0f, dirToEnemy.x * strafeDirection);
                    if (distanceToEnemy < preferredCombatDistance * 0.8f)
                    {
                        // Too close - circle outward
                        movementDir = (tangent1 * 0.8f - dirToEnemy * 0.2f).normalized;
                    }
                    else
                    {
                        // Circle at distance
                        movementDir = tangent1;
                    }
                    break;
                    
                case 2: // Hit and run
                    if (distanceToEnemy > preferredCombatDistance)
                    {
                        // Approach quickly
                        movementDir = dirToEnemy;
                    }
                    else
                    {
                        // Retreat while strafing
                        Vector3 tangent2 = new Vector3(-dirToEnemy.z * strafeDirection, 0f, dirToEnemy.x * strafeDirection);
                        movementDir = (-dirToEnemy * 0.5f + tangent2 * 0.5f).normalized;
                    }
                    break;
                    
                case 3: // Erratic jinking
                    // Random movement with bias toward enemy
                    Vector3 tangent3 = new Vector3(-dirToEnemy.z * strafeDirection, 0f, dirToEnemy.x * strafeDirection);
                    float randomFactor = Random.Range(0f, 1f);
                    if (randomFactor < 0.4f)
                    {
                        // Move toward enemy
                        movementDir = dirToEnemy;
                    }
                    else if (randomFactor < 0.7f)
                    {
                        // Strafe
                        movementDir = tangent3;
                    }
                    else
                    {
                        // Diagonal approach
                        movementDir = (dirToEnemy * 0.6f + tangent3 * 0.4f).normalized;
                    }
                    break;
            }
            
            // Add random jitter to movement direction for more organic feel
            float jitterAmount = 0.15f; // 15% random variation
            Vector3 jitter = new Vector3(
                Random.Range(-jitterAmount, jitterAmount),
                0f,
                Random.Range(-jitterAmount, jitterAmount)
            );
            moveDirection = (movementDir + jitter).normalized;
        }

        /// <summary>Refresh cached object lists periodically to avoid expensive FindObjectsOfType calls.</summary>
        private static void RefreshObjectCache()
        {
            // Only refresh in play mode (Time.time is only available during play)
            if (!Application.isPlaying) return;
            
            if (Time.time - lastCacheRefreshTime >= cacheRefreshInterval)
            {
                // Refresh cache and filter out null/destroyed objects
                var asteroids = Object.FindObjectsByType<Asteroid>(FindObjectsSortMode.None);
                var gems = Object.FindObjectsByType<Gem>(FindObjectsSortMode.None);
                var planets = Object.FindObjectsByType<Planet>(FindObjectsSortMode.None);
                var ships = Object.FindObjectsByType<Starship>(FindObjectsSortMode.None);

                // Filter out null/destroyed objects
                System.Collections.Generic.List<Asteroid> validAsteroids = new System.Collections.Generic.List<Asteroid>();
                foreach (var a in asteroids) if (a != null && a.gameObject != null) validAsteroids.Add(a);
                cachedAsteroids = validAsteroids.ToArray();
                
                System.Collections.Generic.List<Gem> validGems = new System.Collections.Generic.List<Gem>();
                foreach (var g in gems) if (g != null && g.gameObject != null) validGems.Add(g);
                cachedGems = validGems.ToArray();
                
                System.Collections.Generic.List<Planet> validPlanets = new System.Collections.Generic.List<Planet>();
                foreach (var p in planets) if (p != null && p.gameObject != null) validPlanets.Add(p);
                cachedPlanets = validPlanets.ToArray();
                
                System.Collections.Generic.List<Starship> validShips = new System.Collections.Generic.List<Starship>();
                foreach (var s in ships) if (s != null && s.gameObject != null) validShips.Add(s);
                cachedShips = validShips.ToArray();
                
                lastCacheRefreshTime = Time.time;
            }
        }

        /// <summary>Find nearest gem within maxRange. Used for CollectingGems - only pursue gems in close proximity.</summary>
        private Gem FindNearestGemWithinRange(float maxRange)
        {
            RefreshObjectCache();
            
            Gem nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (var gem in cachedGems)
            {
                if (gem == null) continue;

                float distance = ToroidalMap.ToroidalDistance(rb.position, gem.transform.position);
                if (distance < maxRange && distance < nearestDistance)
                {
                    nearest = gem;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private Asteroid FindNearestMineableAsteroid()
        {
            RefreshObjectCache();
            
            Asteroid nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (var asteroid in cachedAsteroids)
            {
                if (asteroid == null || !asteroid.CanBeMined()) continue;
                if (asteroid.IsDestroyed) continue;

                float distance = ToroidalMap.ToroidalDistance(rb.position, asteroid.transform.position);
                if (distance < detectionRange && distance < nearestDistance)
                {
                    nearest = asteroid;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        /// <summary>Find nearest neutral or enemy planet (for capturing/unloading people).</summary>
        private Planet FindNearestPlanet()
        {
            RefreshObjectCache();
            
            Planet nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (var planet in cachedPlanets)
            {
                if (planet == null) continue;
                if (planet is HomePlanet) continue; // Skip home planets
                if (planet.TeamOwnership == assignedTeam) continue; // Skip already owned planets

                float distance = ToroidalMap.ToroidalDistance(rb.position, planet.transform.position);
                if (distance < detectionRange && distance < nearestDistance)
                {
                    nearest = planet;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        /// <summary>Find nearest captured planet (same team) for depositing gems or unloading people.</summary>
        private Planet FindNearestCapturedPlanet()
        {
            RefreshObjectCache();
            
            Planet nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (var planet in cachedPlanets)
            {
                if (planet == null) continue;
                if (planet is HomePlanet) continue; // Skip home planets (use home planet directly)
                if (planet.TeamOwnership != assignedTeam) continue; // Only captured planets (same team)

                float distance = ToroidalMap.ToroidalDistance(rb.position, planet.transform.position);
                if (distance < detectionRange && distance < nearestDistance)
                {
                    nearest = planet;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        /// <summary>Find planet with most people (same team) for loading people. Includes home planet.</summary>
        private Planet FindPlanetWithMostPeople()
        {
            RefreshObjectCache();
            
            Planet bestPlanet = null;
            float mostPeople = 0f;

            foreach (var planet in cachedPlanets)
            {
                if (planet == null) continue;
                
                // Check if planet belongs to our team
                bool isOurPlanet = false;
                if (planet is HomePlanet home)
                {
                    isOurPlanet = home.AssignedTeam == assignedTeam;
                }
                else
                {
                    isOurPlanet = planet.TeamOwnership == assignedTeam;
                }
                
                if (!isOurPlanet) continue; // Only our team's planets
                if (planet.CurrentPopulation <= 0f) continue; // Skip empty planets

                float population = planet.CurrentPopulation;
                if (population > mostPeople)
                {
                    mostPeople = population;
                    bestPlanet = planet;
                }
            }

            return bestPlanet;
        }

        private void FindHomePlanet()
        {
            RefreshObjectCache();
            
            // Filter cached planets for HomePlanets
            foreach (var planet in cachedPlanets)
            {
                if (planet is HomePlanet homePlanet)
                {
                    if (homePlanet.AssignedTeam == assignedTeam)
                    {
                        homePlanet = homePlanet;
                        return;
                    }
                }
            }
            
            // Fallback to FindObjectsOfType if cache doesn't have it (shouldn't happen)
            HomePlanet[] homePlanets = Object.FindObjectsByType<HomePlanet>(FindObjectsSortMode.None);
            foreach (var hp in homePlanets)
            {
                if (hp != null && hp.AssignedTeam == assignedTeam)
                {
                    homePlanet = hp;
                    return;
                }
            }
        }

        /// <summary>Check if ship is fully maxed out (level 7 + all attributes maxed).</summary>
        private bool IsFullyMaxedOut()
        {
            if (starship == null) return true;
            
            // Check if ship is at max level (7)
            if (starship.ShipLevel < 7) return false;
            
            // Check if all attributes are maxed (each can have up to ShipLevel upgrades)
            int maxUpgrades = starship.ShipLevel; // Level 7 = 7 upgrades per attribute
            foreach (AttributeUpgradeSystem.ShipAttributeType attr in System.Enum.GetValues(typeof(AttributeUpgradeSystem.ShipAttributeType)))
            {
                if (starship.GetAttributeLevel(attr) < maxUpgrades)
                    return false;
            }
            
            return true;
        }

        /// <summary>Check if ship can level up RIGHT NOW (has full gems and meets requirements).</summary>
        private bool CanLevelUp()
        {
            if (starship == null || homePlanet == null) return false;
            if (Systems.UpgradeSystem.Instance == null) return false;
            
            return Systems.UpgradeSystem.Instance.CanUpgradeStarshipLevel(starship);
        }

        /// <summary>Check if ship CAN level up (has potential - planet level allows it, just needs gems).</summary>
        private bool CanLevelUpPotential()
        {
            if (starship == null || homePlanet == null) return false;
            if (Systems.UpgradeSystem.Instance == null) return false;
            if (starship.ShipLevel >= 7) return false; // Already max level
            
            int nextLevel = starship.ShipLevel + 1;
            int planetLevel = homePlanet.HomePlanetLevel;
            
            // Check if planet level allows this ship level
            if (nextLevel == 7)
            {
                // Level 7 requires planet level 6 + full gems on planet
                if (planetLevel < 6 || !homePlanet.IsFullGemsForLevel7Unlock()) return false;
            }
            else if (nextLevel > planetLevel)
            {
                return false; // Planet level too low
            }
            
            // Check if upgrade tree has available upgrades
            var upgradeTree = Systems.UpgradeSystem.Instance.UpgradeTree;
            if (upgradeTree == null) return false;
            return upgradeTree.GetAvailableUpgrades(starship.ShipLevel, starship.BranchIndex).Count > 0;
        }

        /// <summary>Check if ship CAN upgrade any attribute (has potential - just needs gems).</summary>
        private bool CanUpgradeAnyAttributePotential()
        {
            if (starship == null || Systems.AttributeUpgradeSystem.Instance == null) return false;
            
            int currentLevel = starship.ShipLevel;
            int maxUpgrades = currentLevel; // Level N = N upgrades per attribute
            
            // Check if any attribute can still be upgraded (regardless of current gems)
            foreach (AttributeUpgradeSystem.ShipAttributeType attr in System.Enum.GetValues(typeof(AttributeUpgradeSystem.ShipAttributeType)))
            {
                if (starship.GetAttributeLevel(attr) < maxUpgrades)
                    return true; // Has potential to upgrade this attribute
            }
            
            return false;
        }

        /// <summary>Check if ship can upgrade any attribute (has enough gems).</summary>
        private bool CanUpgradeAnyAttribute()
        {
            if (starship == null || Systems.AttributeUpgradeSystem.Instance == null) return false;
            
            int currentLevel = starship.ShipLevel;
            int maxUpgrades = currentLevel; // Level N = N upgrades per attribute
            float upgradeCost = Systems.AttributeUpgradeSystem.Instance.GetUpgradeCost(currentLevel);
            
            // Check if any attribute can be upgraded
            foreach (AttributeUpgradeSystem.ShipAttributeType attr in System.Enum.GetValues(typeof(AttributeUpgradeSystem.ShipAttributeType)))
            {
                if (starship.GetAttributeLevel(attr) < maxUpgrades && starship.CurrentGems >= upgradeCost)
                    return true;
            }
            
            return false;
        }

        /// <summary>Upgrade the next available attribute (lowest level first).</summary>
        private void UpgradeNextAttribute()
        {
            if (starship == null || Systems.AttributeUpgradeSystem.Instance == null) return;
            if (!IsServerAuthority) return;
            
            int currentLevel = starship.ShipLevel;
            int maxUpgrades = currentLevel;
            float upgradeCost = Systems.AttributeUpgradeSystem.Instance.GetUpgradeCost(currentLevel);
            
            // Find the first attribute that can be upgraded
            foreach (AttributeUpgradeSystem.ShipAttributeType attr in System.Enum.GetValues(typeof(AttributeUpgradeSystem.ShipAttributeType)))
            {
                if (starship.GetAttributeLevel(attr) < maxUpgrades && starship.CurrentGems >= upgradeCost)
                {
                    var shipNetObj = starship.GetComponent<NetworkObject>();
                    if (shipNetObj != null)
                    {
                        Systems.AttributeUpgradeSystem.Instance.UpgradeAttributeServerRpc(shipNetObj.NetworkObjectId, attr);
                    }
                    return;
                }
            }
        }

        /// <summary>Attempt to level up the ship if possible.</summary>
        private void TryLevelUp()
        {
            if (starship == null || homePlanet == null) return;
            if (Systems.UpgradeSystem.Instance == null) return;
            if (!IsServerAuthority) return;
            
            if (!CanLevelUp()) return;
            
            // Get available upgrades for current level
            var upgradeTree = Systems.UpgradeSystem.Instance.UpgradeTree;
            if (upgradeTree == null) return;
            
            var availableUpgrades = upgradeTree.GetAvailableUpgrades(starship.ShipLevel, starship.BranchIndex);
            if (availableUpgrades.Count == 0) return;
            
            // Choose first available upgrade (or could randomize)
            int nextLevel = starship.ShipLevel + 1;
            int shipIndex = 0; // Choose first available ship variant
            
            var shipNetObj = starship.GetComponent<NetworkObject>();
            if (shipNetObj != null)
            {
                Systems.UpgradeSystem.Instance.UpgradeShipServerRpc(
                    shipNetObj.NetworkObjectId,
                    nextLevel,
                    starship.FocusType, // Keep same focus type
                    shipIndex
                );
            }
        }

        /// <summary>Update behavior when ship is leveling up - mine asteroids and use gems to upgrade (don't deposit).</summary>
        private void UpdateLevelingBehavior()
        {
            if (starship == null || homePlanet == null) return;
            
            // Don't update state if already shooting asteroid - let it finish
            if (currentState == AIState.ShootingAsteroid && targetAsteroid != null && !targetAsteroid.IsDestroyed)
            {
                // Check if ship is full - if so, return to home after asteroid is destroyed
                if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                {
                    // Will transition to ReturningToHome when asteroid is destroyed (handled below)
                }
                return;
            }
            
            // Check if we're at home planet
            float distanceToHome = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
            float homeOrbitRadius = homePlanet.PlanetSize * 0.85f;
            bool atHome = distanceToHome <= homeOrbitRadius;
            
            if (atHome)
            {
                // Priority 1: Try to level up ship (requires full gems)
                if (CanLevelUp())
                {
                    TryLevelUp();
                    // After leveling up, gems will be consumed
                    // Check if we can level up again or upgrade attributes - if so, continue leveling behavior
                    // Otherwise, will fall through to check if we need more gems
                    currentState = AIState.Idle;
                    targetAsteroid = null;
                    targetGem = null;
                    // Don't return - check if we can upgrade more or need more gems
                }
                
                // Priority 2: Try to upgrade attributes if we have enough gems
                if (CanUpgradeAnyAttribute())
                {
                    UpgradeNextAttribute();
                    // Stay at home to continue upgrading next update (might have more gems)
                    currentState = AIState.Idle;
                    return;
                }
                
                // Check if we need more gems
                bool needsFullGems = CanLevelUpPotential(); // Level up requires full gems
                bool needsSomeGems = CanUpgradeAnyAttributePotential(); // Attribute upgrade needs less
                bool needsMoreGems = false;
                
                if (needsFullGems && starship.CurrentGems < starship.GemCapacity * 0.95f)
                {
                    needsMoreGems = true; // Need full gems for level up
                }
                else if (needsSomeGems && !needsFullGems)
                {
                    float upgradeCost = Systems.AttributeUpgradeSystem.Instance.GetUpgradeCost(starship.ShipLevel);
                    if (starship.CurrentGems < upgradeCost)
                    {
                        needsMoreGems = true; // Need more gems for attribute upgrade
                    }
                }
                
                if (needsMoreGems)
                {
                    // Need more gems - leave home and go mine (fall through to mining logic)
                    currentState = AIState.Idle;
                    targetAsteroid = null;
                    targetGem = null;
                    // Continue to mining logic below
                }
                else
                {
                    // Full gems but can't upgrade (might need planet level up first) - wait at home
                    currentState = AIState.Idle;
                    return;
                }
            }
            
            // Not at home OR need to mine - handle mining states
            switch (currentState)
            {
                case AIState.Idle:
                case AIState.MovingToTarget:
                    // Validate current target asteroid - if it's destroyed or null, reset state
                    if (currentState == AIState.MovingToTarget && (targetAsteroid == null || targetAsteroid.IsDestroyed))
                    {
                        targetAsteroid = null;
                        currentState = AIState.Idle;
                    }
                    
                    // If gems = max gems, go to home planet to upgrade
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        targetPosition = homePlanet.transform.position;
                        SetTargetInOrbitZone(homePlanet, ref targetPosition);
                        currentState = AIState.ReturningToHome;
                        targetAsteroid = null; // Clear asteroid target when returning home
                        ExitOrbitIfInOrbit();
                        break;
                    }
                    
                    // Find nearest asteroid with gems
                    Asteroid nearestAsteroid = FindNearestMineableAsteroid();
                    if (nearestAsteroid != null)
                    {
                        // Only update target if we don't have one or it's different
                        if (targetAsteroid != nearestAsteroid)
                        {
                            targetAsteroid = nearestAsteroid;
                            Vector3 dirToShip = ToroidalMap.ToroidalDirection(nearestAsteroid.transform.position, rb.position);
                            targetPosition = nearestAsteroid.transform.position + dirToShip * (nearestAsteroid.AsteroidSize * 0.5f + miningRange * 0.9f);
                            targetPosition = ToroidalMap.WrapPosition(targetPosition);
                            currentState = AIState.MovingToTarget;
                            ExitOrbitIfInOrbit();
                        }
                        // If we already have this asteroid as target, keep moving toward it
                    }
                    else
                    {
                        // No asteroids found - clear target and go idle
                        targetAsteroid = null;
                        currentState = AIState.Idle;
                    }
                    break;

                case AIState.ShootingAsteroid:
                    // Asteroid destroyed? Transition to collecting gems
                    if (targetAsteroid == null || targetAsteroid.IsDestroyed)
                    {
                        targetAsteroid = null;
                        currentState = AIState.CollectingGems;
                        targetGem = FindNearestGemWithinRange(gemCollectionProximity);
                        if (targetGem != null)
                            targetPosition = ToroidalMap.WrapPosition(targetGem.transform.position);
                        break;
                    }

                    float distanceToAsteroidLeveling = ToroidalMap.ToroidalDistance(rb.position, targetAsteroid.transform.position);
                    if (distanceToAsteroidLeveling > miningRange)
                    {
                        // Move closer - target orbit point (not center) to avoid bumping
                        Vector3 dirToShipLeveling = ToroidalMap.ToroidalDirection(targetAsteroid.transform.position, rb.position);
                        targetPosition = targetAsteroid.transform.position + dirToShipLeveling * (targetAsteroid.AsteroidSize * 0.5f + miningRange * 0.9f);
                        targetPosition = ToroidalMap.WrapPosition(targetPosition);
                        currentState = AIState.MovingToTarget;
                    }
                    // Shooting happens in FixedUpdate via HandleShootingAsteroid
                    // If ship is full, return to home to upgrade
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        currentState = AIState.ReturningToHome;
                        targetAsteroid = null;
                    }
                    break;

                case AIState.CollectingGems:
                    // Ship full? Return to home to upgrade
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        currentState = AIState.ReturningToHome;
                        targetGem = null;
                        break;
                    }
                    // Find nearest gem within proximity
                    targetGem = FindNearestGemWithinRange(gemCollectionProximity);
                    if (targetGem != null)
                    {
                        targetPosition = ToroidalMap.WrapPosition(targetGem.transform.position);
                    }
                    else
                    {
                        targetGem = null;
                        currentState = AIState.Idle;
                    }
                    break;

                case AIState.ReturningToHome:
                    // Target orbit zone at home
                    SetTargetInOrbitZone(homePlanet, ref targetPosition);
                    float distToHomeReturn = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                    float homeOrbitRadiusReturn = homePlanet.PlanetSize * 0.85f;
                    
                    if (distToHomeReturn <= homeOrbitRadiusReturn)
                    {
                        // Arrived at home - check for upgrades (will be handled in atHome check next update)
                        // Reset state to Idle so the atHome check can process upgrades
                        currentState = AIState.Idle;
                        targetAsteroid = null;
                        targetGem = null;
                        // Next update will check atHome and handle upgrades
                    }
                    break;
            }
        }

        /// <summary>Find nearest enemy ship within attack range. Returns null if none found.</summary>
        private Starship FindNearestEnemyShip()
        {
            if (starship == null || assignedTeam == TeamManager.Team.None) return null;
            
            RefreshObjectCache();
            
            Vector3 myPos = rb != null ? rb.position : transform.position;
            Starship nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (var ship in cachedShips)
            {
                if (ship == null || ship == starship) continue; // Skip self
                if (ship.IsDead) continue;
                if (ship.ShipTeam == assignedTeam) continue; // Skip friendly ships

                Vector3 shipPos = ship.transform.position;
                shipPos.y = 0f;
                float dist = ToroidalMap.ToroidalDistance(myPos, shipPos);

                if (dist <= attackRange && dist < nearestDistance)
                {
                    nearest = ship;
                    nearestDistance = dist;
                }
            }

            return nearest;
        }

        /// <summary>Set target to a point in the planet's orbit zone (not center) so we don't fly into it.</summary>
        private void SetTargetInOrbitZone(Planet planet, ref Vector3 target)
        {
            if (planet == null) return;
            Vector3 dirToShip = ToroidalMap.ToroidalDirection(planet.transform.position, rb.position);
            float orbitDist = planet.PlanetSize * 0.65f; // Middle of orbit band 0.5-0.85
            target = planet.transform.position + dirToShip * orbitDist;
            target = ToroidalMap.WrapPosition(target);
        }

        /// <summary>Force exit orbit when we have a new movement target (like player right-click does).</summary>
        private void ExitOrbitIfInOrbit()
        {
            if (starship != null && starship.CurrentOrbitPlanet != null)
                starship.ExitOrbitZone(starship.CurrentOrbitPlanet);
        }

        /// <summary>Spawned objects start inside triggers - triggers don't fire. Manually set orbit if in range.
        /// Skip when AI has a movement target - like player right-click, we're intentionally leaving orbit.</summary>
        private void TryEnterOrbitZoneIfInRange()
        {
            if (starship == null || starship.CurrentOrbitPlanet != null) return;
            // Don't re-enter orbit when we're moving toward a distant target (same as player right-click - orbit has no effect)
            if (currentState == AIState.MovingToTarget || currentState == AIState.MovingToPlanet || currentState == AIState.CollectingGems)
            {
                float dist = ToroidalMap.ToroidalDistance(rb.position, targetPosition);
                if (dist > arrivalDistance + 2f) return; // We're leaving - don't trap us back in orbit
            }
            if (homePlanet != null && TryEnterOrbitZoneForPlanet(homePlanet)) return;
            if (targetPlanet != null && (currentState == AIState.MovingToPlanet || currentState == AIState.UnloadingPeople))
                TryEnterOrbitZoneForPlanet(targetPlanet);
        }

        private bool TryEnterOrbitZoneForPlanet(Planet planet)
        {
            if (planet == null || starship == null || starship.CurrentOrbitPlanet != null) return false;
            Vector3 toShip = rb.position - planet.transform.position;
            toShip.y = 0f;
            float dist = toShip.magnitude;
            float inner = planet.PlanetSize * 0.5f;
            float outer = planet.PlanetSize * 0.85f;
            if (dist >= inner && dist <= outer)
            {
                starship.EnterOrbitZone(planet);
                return true;
            }
            return false;
        }

        public void SetBehaviorType(AIBehaviorType type)
        {
            behaviorType = type;
        }
    }
}
