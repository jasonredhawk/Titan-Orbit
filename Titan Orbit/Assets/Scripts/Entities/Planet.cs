using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TMPro;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Represents a planet in the game with population, ownership, and capture mechanics
    /// </summary>
    public class Planet : NetworkBehaviour
    {
        [Header("Planet Settings")]
        [SerializeField] private float baseMaxPopulation = 100f;
        [SerializeField] private float baseGrowthRate = 1f; // Population per second
        [SerializeField] private float planetSize = 1f;
        [SerializeField] private float captureRadius = 5f;

        [Header("Visual")]
        [SerializeField] private Renderer planetRenderer;
        [SerializeField] private Material neutralMaterial;
        [SerializeField] private Material teamAMaterial;
        [SerializeField] private Material teamBMaterial;
        [SerializeField] private Material teamCMaterial;
        [SerializeField] private TextMeshPro populationText;

        private NetworkVariable<TeamManager.Team> teamOwnership = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);
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
                // Initialize planet based on size
                float sizeMultiplier = planetSize;
                maxPopulation.Value = baseMaxPopulation * sizeMultiplier;
                currentPopulation.Value = maxPopulation.Value * 0.1f; // Start with 10% population
                growthRate.Value = baseGrowthRate * sizeMultiplier;
                teamOwnership.Value = TeamManager.Team.None; // Neutral by default
            }

            // Ensure population text is visible
            if (populationText != null)
            {
                populationText.enabled = true;
                populationText.color = Color.white;
                populationText.gameObject.SetActive(true);
            }

            // Update visual on spawn
            UpdateVisual();
            UpdatePopulationDisplay();
            
            // Subscribe to ownership changes
            teamOwnership.OnValueChanged += OnOwnershipChanged;
            currentPopulation.OnValueChanged += (float oldVal, float newVal) => UpdatePopulationDisplay();
        }

        public override void OnNetworkDespawn()
        {
            teamOwnership.OnValueChanged -= OnOwnershipChanged;
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
            
            // Wrap position toroidally (same as starship)
            Vector3 wrappedPos = ToroidalMap.WrapPosition(pos);
            if (Vector3.SqrMagnitude(wrappedPos - pos) > 0.0001f)
            {
                transform.position = wrappedPos;
            }
            
            if (IsServer)
            {
                // Grow population over time if not at max
                if (currentPopulation.Value < maxPopulation.Value && teamOwnership.Value != TeamManager.Team.None)
                {
                    currentPopulation.Value = Mathf.Min(
                        currentPopulation.Value + growthRate.Value * Time.deltaTime,
                        maxPopulation.Value
                    );
                }
            }
            
            // Update population display every frame (handles client-side updates)
            UpdatePopulationDisplay();
        }
        
        private void UpdatePopulationDisplay()
        {
            if (populationText != null)
            {
                int pop = Mathf.RoundToInt(currentPopulation.Value);
                populationText.text = pop.ToString();
                // Ensure text is visible - make it brighter and ensure it's enabled
                populationText.color = Color.white;
                populationText.enabled = true;
                // Text rotation is static (set once) - no LookAt, always readable from top-down
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void AddPopulationServerRpc(float amount, TeamManager.Team sourceTeam)
        {
            if (teamOwnership.Value == TeamManager.Team.None || teamOwnership.Value == sourceTeam)
            {
                // Add to same team or neutral planet
                currentPopulation.Value = Mathf.Min(currentPopulation.Value + amount, maxPopulation.Value);
            }
            else
            {
                // Different team - attempt capture
                currentPopulation.Value -= amount;
                
                if (currentPopulation.Value <= 0)
                {
                    // Captured!
                    CapturePlanetServerRpc(sourceTeam);
                }
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
            CapturePlanetClientRpc(newTeam);
        }

        [ClientRpc]
        private void CapturePlanetClientRpc(TeamManager.Team newTeam)
        {
            UpdateVisual();
            Debug.Log($"Planet captured by {newTeam}");
        }

        private void OnOwnershipChanged(TeamManager.Team previousTeam, TeamManager.Team newTeam)
        {
            UpdateVisual();
            UpdatePopulationDisplay();
        }

        private void UpdateVisual()
        {
            if (planetRenderer == null) return;

            Material materialToUse = neutralMaterial;
            
            switch (teamOwnership.Value)
            {
                case TeamManager.Team.TeamA:
                    materialToUse = teamAMaterial ?? neutralMaterial;
                    break;
                case TeamManager.Team.TeamB:
                    materialToUse = teamBMaterial ?? neutralMaterial;
                    break;
                case TeamManager.Team.TeamC:
                    materialToUse = teamCMaterial ?? neutralMaterial;
                    break;
            }

            if (materialToUse != null)
            {
                planetRenderer.material = materialToUse;
            }
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
