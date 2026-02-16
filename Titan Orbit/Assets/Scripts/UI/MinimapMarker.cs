using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Temporary marker placed on the minimap to signal locations (defend/attack)
    /// </summary>
    public class MinimapMarker : NetworkBehaviour
    {
        public enum MarkerType
        {
            Defend,
            Attack
        }

        [SerializeField] private float lifetime = 12f; // 10 seconds visible + 2 seconds fade
        [SerializeField] private float fadeStartTime = 10f; // Start fading after 10 seconds
        [SerializeField] private float fadeDuration = 2f;
        
        private NetworkVariable<Vector3> worldPosition = new NetworkVariable<Vector3>();
        private NetworkVariable<MarkerType> markerType = new NetworkVariable<MarkerType>();
        private NetworkVariable<TeamManager.Team> markerTeam = new NetworkVariable<TeamManager.Team>();
        private NetworkVariable<float> spawnTime = new NetworkVariable<float>();
        
        // Public accessors for network variables
        public MarkerType Type => markerType.Value;
        public TeamManager.Team Team => markerTeam.Value;
        
        private SpriteRenderer spriteRenderer;
        private float startAlpha = 1f;
        private bool isInitialized = false;
        private float localSpawnTime = 0f; // Local spawn time for clients
        
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            
            // Set spawn time on server
            if (IsServer)
            {
                spawnTime.Value = Time.time;
                localSpawnTime = Time.time;
            }
            else
            {
                // For clients, use current time as spawn time (will be corrected when spawnTime syncs)
                localSpawnTime = Time.time;
            }
            
            // Subscribe to network variable changes
            markerType.OnValueChanged += OnMarkerTypeChanged;
            markerTeam.OnValueChanged += OnMarkerTeamChanged;
            spawnTime.OnValueChanged += OnSpawnTimeChanged;
            
            // Create visual immediately
            CreateVisual();
        }
        
        private void OnSpawnTimeChanged(float oldValue, float newValue)
        {
            // Update local spawn time when network variable syncs
            if (newValue > 0f)
            {
                localSpawnTime = newValue;
            }
        }
        
        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            markerType.OnValueChanged -= OnMarkerTypeChanged;
            markerTeam.OnValueChanged -= OnMarkerTeamChanged;
            spawnTime.OnValueChanged -= OnSpawnTimeChanged;
        }
        
        private void OnMarkerTypeChanged(MarkerType oldValue, MarkerType newValue)
        {
            UpdateVisual();
        }
        
        private void OnMarkerTeamChanged(TeamManager.Team oldValue, TeamManager.Team newValue)
        {
            UpdateVisual();
        }
        
        private void CreateVisual()
        {
            if (spriteRenderer != null) return; // Already created
            
            // Create a simple sprite renderer for the marker
            spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
            spriteRenderer.sortingOrder = 100; // Above other sprites
            
            // Set color based on team and type
            UpdateVisual();
        }
        
        private void Update()
        {
            if (!IsSpawned) return;
            
            // Update position if it changed
            if (worldPosition.Value != transform.position)
            {
                transform.position = worldPosition.Value;
            }
            
            // Handle fade-out - visible for 10 seconds, then fade over 2 seconds
            // Use localSpawnTime which is set correctly for both server and clients
            float elapsed = Time.time - localSpawnTime;
            
            // Only fade if spawnTime has synced (value > 0) or use local time
            if (spawnTime.Value > 0f)
            {
                elapsed = Time.time - spawnTime.Value;
            }
            
            if (elapsed > fadeStartTime)
            {
                float fadeProgress = Mathf.Clamp01((elapsed - fadeStartTime) / fadeDuration);
                float alpha = Mathf.Lerp(startAlpha, 0f, fadeProgress);
                
                if (spriteRenderer != null)
                {
                    Color color = spriteRenderer.color;
                    color.a = alpha;
                    spriteRenderer.color = color;
                }
            }
            else if (spriteRenderer != null)
            {
                // Ensure full alpha before fade starts
                Color color = spriteRenderer.color;
                color.a = startAlpha;
                spriteRenderer.color = color;
            }
            
            // Destroy when lifetime expires
            if (elapsed >= lifetime && IsServer)
            {
                GetComponent<NetworkObject>().Despawn();
            }
        }
        
        private void UpdateVisual()
        {
            if (spriteRenderer == null) return;
            
            Color teamColor = GetTeamColor(markerTeam.Value);
            Color markerColor = markerType.Value == MarkerType.Defend 
                ? new Color(0.2f, 0.8f, 0.2f, 1f) // Green for defend
                : new Color(0.8f, 0.2f, 0.2f, 1f); // Red for attack
            
            // Blend team color with marker color
            spriteRenderer.color = Color.Lerp(markerColor, teamColor, 0.3f);
            spriteRenderer.sprite = CreateMarkerSprite(markerType.Value);
        }
        
        private Color GetTeamColor(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return new Color(1f, 0.3f, 0.3f);
                case TeamManager.Team.TeamB: return new Color(0.3f, 0.5f, 1f);
                case TeamManager.Team.TeamC: return new Color(0.3f, 1f, 0.4f);
                default: return Color.white;
            }
        }
        
        private Sprite CreateMarkerSprite(MarkerType type)
        {
            int size = 32;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[size * size];
            float centerX = size / 2f;
            float centerY = size / 2f;
            
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - centerX;
                    float dy = y - centerY;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    
                    bool isInside = false;
                    
                    if (type == MarkerType.Defend)
                    {
                        // Shield shape for defend
                        float radius = size * 0.4f;
                        if (dist <= radius)
                        {
                            // Create shield shape (rounded top, pointed bottom)
                            float normalizedY = (y - centerY) / radius;
                            if (normalizedY > -0.3f) // Top half is circle
                            {
                                isInside = dist <= radius;
                            }
                            else // Bottom half tapers to point
                            {
                                float taper = Mathf.Abs(normalizedY + 0.3f) / 0.7f;
                                float adjustedRadius = radius * (1f - taper * 0.5f);
                                isInside = dist <= adjustedRadius;
                            }
                        }
                    }
                    else // Attack
                    {
                        // Arrow/sword shape pointing up
                        float halfWidth = size * 0.12f;
                        float shaftLength = size * 0.35f;
                        float headLength = size * 0.25f;
                        
                        // Shaft (rectangle)
                        if (Mathf.Abs(dx) <= halfWidth && dy >= -shaftLength * 0.2f && dy <= shaftLength)
                            isInside = true;
                        
                        // Arrow head (triangle pointing up)
                        if (dy > shaftLength * 0.7f && dy <= shaftLength + headLength)
                        {
                            float headProgress = (dy - shaftLength * 0.7f) / (shaftLength * 0.3f + headLength);
                            float headWidth = halfWidth * (1f + headProgress * 2f);
                            if (Mathf.Abs(dx) <= headWidth * (1f - headProgress))
                                isInside = true;
                        }
                    }
                    
                    pixels[y * size + x] = isInside ? Color.white : Color.clear;
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = type == MarkerType.Defend ? "DefendMarker" : "AttackMarker";
            return sprite;
        }
        
        public void Initialize(Vector3 position, MarkerType type, TeamManager.Team team)
        {
            if (IsServer)
            {
                worldPosition.Value = position;
                markerType.Value = type;
                markerTeam.Value = team;
                // Set spawn time when initializing (after spawn)
                if (spawnTime.Value == 0f)
                {
                    spawnTime.Value = Time.time;
                }
                localSpawnTime = Time.time;
            }
            transform.position = position;
            isInitialized = true;
            
            // Create visual immediately if already spawned
            if (IsSpawned)
            {
                CreateVisual();
            }
        }
    }
}
