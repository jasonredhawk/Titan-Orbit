using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.AI
{
    /// <summary>
    /// AI controller for enemy starships in debug mode.
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
        [SerializeField] private float updateInterval = 0.5f; // Update AI decisions every 0.5 seconds
        [SerializeField] private float orbitSpeed = 0.8f;

        private Starship starship;
        private Rigidbody rb;
        private TeamManager.Team assignedTeam;
        private HomePlanet homePlanet;
        
        // State machine
        private enum AIState
        {
            Idle,
            MovingToTarget,
            Mining,
            ReturningToHome,
            LoadingPeople,
            MovingToPlanet,
            UnloadingPeople
        }
        private AIState currentState = AIState.Idle;
        
        private Vector3 targetPosition;
        private Asteroid targetAsteroid;
        private Planet targetPlanet;
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
            lastUpdateTime = Time.time;
            // Spawn in orbit zone - triggers don't fire for objects that start inside, so set orbit manually
            TryEnterOrbitZoneIfInRange();
        }

        private void Update()
        {
            if (!IsServerAuthority) return;
            if (starship == null || starship.IsDead) return;
            if (GameManager.Instance == null || !GameManager.Instance.DebugMode) return;

            // Handle health and energy regen for AI ships (they don't have an owner so Starship.Update won't do it)
            HandleHealthRegen();
            HandleEnergyRegen();
        }

        private void FixedUpdate()
        {
            if (!IsServerAuthority) return;
            if (starship == null || starship.IsDead) return;
            if (GameManager.Instance != null && !GameManager.Instance.DebugMode) return;

            // Ensure orbit zone is set if we're in it (triggers don't fire for objects that start inside)
            TryEnterOrbitZoneIfInRange();

            // Update AI decisions periodically
            if (Time.time - lastUpdateTime >= updateInterval)
            {
                UpdateAI();
                lastUpdateTime = Time.time;
            }

            // Handle movement
            HandleAIMovement();
        }

        private void HandleHealthRegen()
        {
            // AI ships regen health on server (same logic as Starship.HandleHealthRegen)
            if (starship.CurrentHealth < starship.MaxHealth)
            {
                float regenRate = 1f; // Base regen rate per second
                if (GameManager.Instance != null && GameManager.Instance.DebugMode) regenRate *= 100f;
                float regen = regenRate * Time.deltaTime;
                
                // Access NetworkVariable via reflection (Starship doesn't expose it publicly)
                var healthField = typeof(Starship).GetField("currentHealth", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (healthField != null)
                {
                    var healthVar = healthField.GetValue(starship) as NetworkVariable<float>;
                    if (healthVar != null && IsServerAuthority)
                    {
                        healthVar.Value = Mathf.Min(healthVar.Value + regen, starship.MaxHealth);
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
                if (GameManager.Instance != null && GameManager.Instance.DebugMode) regenRate *= 100f;
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

        private void UpdateMiningBehavior()
        {
            switch (currentState)
            {
                case AIState.Idle:
                case AIState.MovingToTarget:
                    // Find nearest asteroid with gems
                    Asteroid nearestAsteroid = FindNearestMineableAsteroid();
                    if (nearestAsteroid != null)
                    {
                        targetAsteroid = nearestAsteroid;
                        targetPosition = nearestAsteroid.transform.position;
                        currentState = AIState.MovingToTarget;
                    }
                    else
                    {
                        currentState = AIState.Idle;
                    }
                    break;

                case AIState.Mining:
                    // Check if asteroid is still valid and in range
                    if (targetAsteroid == null || !targetAsteroid.CanBeMined())
                    {
                        targetAsteroid = null;
                        currentState = AIState.Idle;
                        break;
                    }

                    float distanceToAsteroid = ToroidalMap.ToroidalDistance(rb.position, targetAsteroid.transform.position);
                    if (distanceToAsteroid > miningRange)
                    {
                        // Move closer
                        targetPosition = targetAsteroid.transform.position;
                        currentState = AIState.MovingToTarget;
                    }
                    else
                    {
                        // Mine the asteroid
                        if (MiningSystem.Instance != null)
                        {
                            float miningRate = starship.ShipLevel * 5f; // Base mining rate
                            MiningSystem.Instance.MineAsteroidServerRpc(
                                targetAsteroid.NetworkObjectId,
                                starship.NetworkObjectId,
                                miningRate
                            );
                        }

                        // If ship is full, return to home
                        if (starship.CurrentGems >= starship.GemCapacity * 0.95f)
                        {
                            currentState = AIState.ReturningToHome;
                            targetAsteroid = null;
                        }
                    }
                    break;

                case AIState.ReturningToHome:
                    // Check if at home planet
                    float distanceToHome = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                    float orbitRadius = homePlanet.PlanetSize * 0.7f;
                    
                    if (distanceToHome <= orbitRadius)
                    {
                        // Start depositing gems (use FromServer to bypass RPC ownership for AI)
                        starship.SetWantToDepositGemsFromServer(true);
                        currentState = AIState.Idle;
                    }
                    else
                    {
                        targetPosition = homePlanet.transform.position;
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
                    // Check if we need to load people
                    if (starship.CurrentPeople < starship.PeopleCapacity * 0.5f)
                    {
                        // Go to home planet to load
                        float distanceToHome = ToroidalMap.ToroidalDistance(rb.position, homePlanet.transform.position);
                        float orbitRadius = homePlanet.PlanetSize * 0.7f;
                        
                        if (distanceToHome <= orbitRadius)
                        {
                            // Start loading people (use FromServer to bypass RPC ownership for AI)
                            starship.SetWantToLoadPeopleFromServer(true);
                            currentState = AIState.LoadingPeople;
                        }
                        else
                        {
                            targetPosition = homePlanet.transform.position;
                            currentState = AIState.MovingToTarget;
                        }
                    }
                    else
                    {
                        // Find nearest planet to unload
                        Planet nearestPlanet = FindNearestPlanet();
                        if (nearestPlanet != null)
                        {
                            targetPlanet = nearestPlanet;
                            targetPosition = nearestPlanet.transform.position;
                            currentState = AIState.MovingToPlanet;
                        }
                    }
                    break;

                case AIState.MovingToPlanet:
                    if (targetPlanet == null)
                    {
                        currentState = AIState.Idle;
                        break;
                    }
                    targetPosition = targetPlanet.transform.position; // Keep target updated

                    float distanceToPlanet = ToroidalMap.ToroidalDistance(rb.position, targetPlanet.transform.position);
                    float planetOrbitRadius = targetPlanet.PlanetSize * 0.7f;
                    
                    if (distanceToPlanet <= planetOrbitRadius)
                    {
                        // Start unloading people (use FromServer to bypass RPC ownership for AI)
                        starship.SetWantToUnloadPeopleFromServer(true);
                        currentState = AIState.UnloadingPeople;
                    }
                    break;

                case AIState.UnloadingPeople:
                    // Check if done unloading
                    if (starship.CurrentPeople <= 0.1f)
                    {
                        starship.SetWantToUnloadPeopleFromServer(false);
                        targetPlanet = null;
                        currentState = AIState.Idle;
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
            if (ShouldOrbit())
            {
                HandleAIOrbit();
                return;
            }

            // Use toroidal direction/distance (map wraps - raw Vector3 is wrong for distant targets)
            Vector3 pos = rb.position;
            pos.y = 0f;
            Vector3 toTarget = ToroidalMap.ToroidalDirection(pos, targetPosition);
            float distanceToTarget = ToroidalMap.ToroidalDistance(pos, targetPosition);

            // Check if we've arrived at target
            if (distanceToTarget <= arrivalDistance)
            {
                moveDirection = Vector3.zero;
                // Decelerate instead of stopping immediately
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                vel = Vector3.MoveTowards(vel, Vector3.zero, aiDeceleration * Time.fixedDeltaTime);
                rb.linearVelocity = vel;
                
                // Transition to mining state if we're at an asteroid
                if (behaviorType == AIBehaviorType.Mining && currentState == AIState.MovingToTarget && targetAsteroid != null)
                {
                    if (distanceToTarget <= miningRange)
                    {
                        currentState = AIState.Mining;
                    }
                }
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
            // Orbit whenever we're in a planet's orbit zone (loading, unloading, depositing, idle)
            if (starship == null || starship.CurrentOrbitPlanet == null) return false;
            if (currentState == AIState.MovingToTarget || currentState == AIState.MovingToPlanet)
            {
                // When we have a distant target, leave orbit so we move toward it (don't rely on physics trigger)
                float dist = ToroidalMap.ToroidalDistance(rb.position, targetPosition);
                if (dist > arrivalDistance + 2f)
                {
                    starship.ExitOrbitZone(starship.CurrentOrbitPlanet);
                    return false;
                }
            }
            return true; // Orbit when in orbit zone
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

        /// <summary>Spawned objects start inside triggers - triggers don't fire. Manually set orbit if in range.</summary>
        private void TryEnterOrbitZoneIfInRange()
        {
            if (homePlanet == null || starship == null) return;
            if (starship.CurrentOrbitPlanet != null) return; // Already in orbit
            Vector3 toShip = rb.position - homePlanet.transform.position;
            toShip.y = 0f;
            float dist = toShip.magnitude;
            float inner = homePlanet.PlanetSize * 0.5f;
            float outer = homePlanet.PlanetSize * 0.85f;
            if (dist >= inner && dist <= outer)
                starship.EnterOrbitZone(homePlanet);
        }

        public void SetBehaviorType(AIBehaviorType type)
        {
            behaviorType = type;
        }
    }
}
