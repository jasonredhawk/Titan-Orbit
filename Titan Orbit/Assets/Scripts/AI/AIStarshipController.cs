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
        [SerializeField] private float attackRange = 10f; // Range at which to attack enemy ships (matches bullet range)

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
            AttackingEnemy     // Attacking an enemy ship while moving
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
            // AI ships regen health on server (same logic as Starship.HandleHealthRegen, but without debug mode multiplier)
            if (starship.CurrentHealth < starship.MaxHealth)
            {
                float regenRate = 1f; // Base regen rate per second (enemy ships ignore debug mode)
                float regen = regenRate * Time.deltaTime;
                
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
            // AI ships regen energy on server (same logic as Starship.HandleEnergyRegen, but without debug mode multiplier)
            if (starship.CurrentEnergy < starship.EnergyCapacity)
            {
                float regenRate = 5f; // Base regen rate per second (enemy ships ignore debug mode)
                float regen = regenRate * Time.deltaTime;
                
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

            // Check for enemy ships first - combat takes priority over all other actions
            Starship nearestEnemy = FindNearestEnemyShip();
            if (nearestEnemy != null)
            {
                // Enemy in range - switch to attack mode
                if (currentState != AIState.AttackingEnemy)
                {
                    previousState = currentState; // Save current state to return to later
                }
                targetEnemyShip = nearestEnemy;
                currentState = AIState.AttackingEnemy;
                ExitOrbitIfInOrbit(); // Exit orbit when engaging enemy
            }
            else if (currentState == AIState.AttackingEnemy)
            {
                // No enemies in range - return to previous behavior
                targetEnemyShip = null;
                AIState stateToReturnTo = previousState;
                
                // If previous state was invalid or was also AttackingEnemy, reset to Idle
                if (stateToReturnTo == AIState.AttackingEnemy)
                {
                    stateToReturnTo = AIState.Idle;
                }
                
                currentState = stateToReturnTo;
                
                // Force behavior update to ensure proper state transitions
                // This ensures ships don't get stuck when returning from attack
                if (currentState == AIState.Idle || currentState == AIState.ReturningToHome || 
                    currentState == AIState.LoadingPeople || currentState == AIState.UnloadingPeople)
                {
                    // Reset any stale targets that might prevent proper behavior
                    if (currentState == AIState.Idle)
                    {
                        targetAsteroid = null;
                        targetPlanet = null;
                        targetGem = null;
                    }
                }
            }

            // Only update behavior if not attacking
            if (currentState != AIState.AttackingEnemy)
            {
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
        }

        private void UpdateMiningBehavior()
        {
            switch (currentState)
            {
                case AIState.Idle:
                case AIState.MovingToTarget:
                    // If gems = max gems, target = home planet
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        // Check if already at home - if so, deposit and then continue
                        float distanceToHome = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                        float homeOrbitRadius = homePlanet.PlanetSize * 0.85f;
                        if (distanceToHome <= homeOrbitRadius)
                        {
                            // Already at home - deposit gems
                            starship.SetWantToDepositGemsFromServer(true);
                            currentState = AIState.Idle; // Will check again next update
                        }
                        else
                        {
                            // Not at home - go to home planet
                            targetPosition = homePlanet.transform.position;
                            SetTargetInOrbitZone(homePlanet, ref targetPosition);
                            currentState = AIState.ReturningToHome;
                            ExitOrbitIfInOrbit();
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
                    // If ship is full, return to home
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        currentState = AIState.ReturningToHome;
                        targetAsteroid = null;
                    }
                    break;

                case AIState.CollectingGems:
                    // Ship full? Return home
                    if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                    {
                        currentState = AIState.ReturningToHome;
                        targetGem = null;
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
                    // Target orbit zone at home (not center)
                    SetTargetInOrbitZone(homePlanet, ref targetPosition);
                    float distToHomeReturn = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                    float homeOrbitRadiusReturn = homePlanet.PlanetSize * 0.85f; // Use outer orbit band (0.5-0.85)
                    
                    if (distToHomeReturn <= homeOrbitRadiusReturn)
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
                    // If people = 0, target = home planet; if in orbit, load people
                    if (starship.CurrentPeople < starship.PeopleCapacity - 0.1f)
                    {
                        float distanceToHome = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                        if (distanceToHome <= Mathf.Max(homePlanet.PlanetSize * 1.1f, 6f))
                        {
                            starship.SetWantToLoadPeopleFromServer(true);
                            currentState = AIState.LoadingPeople;
                        }
                        else
                        {
                            targetPosition = homePlanet.transform.position;
                            SetTargetInOrbitZone(homePlanet, ref targetPosition);
                            currentState = AIState.MovingToTarget;
                            ExitOrbitIfInOrbit();
                        }
                    }
                    else if (starship.CurrentPeople >= starship.PeopleCapacity - 0.1f)
                    {
                        // People = max: target = closest neutral planet
                        Planet nearestPlanet = FindNearestPlanet();
                        if (nearestPlanet != null)
                        {
                            targetPlanet = nearestPlanet;
                            targetPosition = nearestPlanet.transform.position;
                            SetTargetInOrbitZone(nearestPlanet, ref targetPosition);
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
                    // Transport going to home to load people - check if we've arrived
                    SetTargetInOrbitZone(homePlanet, ref targetPosition);
                    float distanceToHomeTransport = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                    float homeOrbitRadiusTransport = Mathf.Max(homePlanet.PlanetSize * 1.1f, 6f); // Extend past orbit zone; min 6 for small planets
                    if (distanceToHomeTransport <= homeOrbitRadiusTransport)
                    {
                        starship.SetWantToLoadPeopleFromServer(true);
                        currentState = AIState.LoadingPeople;
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

                // Arrival threshold: use miningRange for asteroids, small value for gems (collect radius 0.6), arrivalDistance for planets
                float effectiveArrival = arrivalDistance;
                if (behaviorType == AIBehaviorType.Mining && currentState == AIState.MovingToTarget && targetAsteroid != null)
                    effectiveArrival = miningRange * 0.9f; // Stop at orbit point around asteroid
                else if (currentState == AIState.CollectingGems)
                    effectiveArrival = 0.5f; // Get close enough for gem collect (Gem.collectRadius = 0.6)

                // Transport: transition when within planet orbit radius - smaller planets need larger relative radius
                if (behaviorType == AIBehaviorType.Transport)
                {
                    if (currentState == AIState.MovingToTarget && homePlanet != null)
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
                    // Reached orbit point around asteroid? Start shooting
                    if (behaviorType == AIBehaviorType.Mining && currentState == AIState.MovingToTarget && targetAsteroid != null && !targetAsteroid.IsDestroyed)
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
            // For miners: orbit only if gems are being deposited
            if (behaviorType == AIBehaviorType.Mining)
            {
                // Orbit only if depositing gems (wantToDepositGems is true)
                return starship.WantToDepositGems;
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
            
            if (dirToEnemy.sqrMagnitude < 0.01f) return;
            dirToEnemy.Normalize();

            // Rotate toward enemy
            Quaternion targetRotation = Quaternion.LookRotation(dirToEnemy);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, targetRotation, aiRotationSpeed * Time.fixedDeltaTime));

            // Shoot at enemy (FireAtTarget respects fire rate and energy)
            starship.FireAtTarget(dirToEnemy);

            // Continue moving toward enemy while attacking (don't stop)
            // Maintain a preferred distance - move closer if too far, circle if too close
            float preferredDistance = attackRange * 0.7f; // Stay at 70% of attack range
            if (distanceToEnemy > attackRange)
            {
                // Too far - move closer
                moveDirection = dirToEnemy;
            }
            else if (distanceToEnemy < preferredDistance)
            {
                // Too close - strafe around enemy (perpendicular movement)
                Vector3 tangent = new Vector3(-dirToEnemy.z, 0f, dirToEnemy.x);
                moveDirection = tangent;
            }
            else
            {
                // Good distance - maintain position with slight movement
                Vector3 tangent = new Vector3(-dirToEnemy.z, 0f, dirToEnemy.x);
                moveDirection = (dirToEnemy * 0.3f + tangent * 0.7f).normalized; // Mostly strafe, slight forward
            }
        }

        /// <summary>Find nearest gem within maxRange. Used for CollectingGems - only pursue gems in close proximity.</summary>
        private Gem FindNearestGemWithinRange(float maxRange)
        {
            Gem nearest = null;
            float nearestDistance = float.MaxValue;

            Gem[] gems = Object.FindObjectsOfType<Gem>();
            foreach (var gem in gems)
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
            Asteroid nearest = null;
            float nearestDistance = float.MaxValue;

            Asteroid[] asteroids = Object.FindObjectsOfType<Asteroid>();
            foreach (var asteroid in asteroids)
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

        private Planet FindNearestPlanet()
        {
            Planet nearest = null;
            float nearestDistance = float.MaxValue;

            Planet[] planets = Object.FindObjectsOfType<Planet>();
            foreach (var planet in planets)
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

        private void FindHomePlanet()
        {
            HomePlanet[] homePlanets = Object.FindObjectsOfType<HomePlanet>();
            foreach (var hp in homePlanets)
            {
                if (hp != null && hp.AssignedTeam == assignedTeam)
                {
                    homePlanet = hp;
                    return;
                }
            }
        }

        /// <summary>Find nearest enemy ship within attack range. Returns null if none found.</summary>
        private Starship FindNearestEnemyShip()
        {
            if (starship == null || assignedTeam == TeamManager.Team.None) return null;
            
            Vector3 myPos = rb != null ? rb.position : transform.position;
            Starship nearest = null;
            float nearestDistance = float.MaxValue;
            float attackRangeSq = attackRange * attackRange;

            Starship[] ships = Object.FindObjectsByType<Starship>(FindObjectsSortMode.None);
            foreach (var ship in ships)
            {
                if (ship == null || ship.IsDead) continue;
                if (ship.ShipTeam == assignedTeam || ship.ShipTeam == TeamManager.Team.None) continue; // Skip friendly ships and neutral

                float distance = ToroidalMap.ToroidalDistance(myPos, ship.transform.position);
                if (distance <= attackRange && distance < nearestDistance)
                {
                    nearest = ship;
                    nearestDistance = distance;
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
