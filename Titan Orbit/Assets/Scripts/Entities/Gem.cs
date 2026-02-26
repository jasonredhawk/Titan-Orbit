using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Generation;
using System.IO;
using System.Text;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Gem pickup - spawned when asteroid is destroyed, explodes outward then stops. Collected by flying over.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Gem : NetworkBehaviour
    {
        private const string DEBUG_LOG_FILE = "debug-e62f68.log";
        private static string DebugLogPath
        {
            get
            {
                try
                {
                    string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                    if (!string.IsNullOrEmpty(projectRoot))
                        return Path.Combine(projectRoot, DEBUG_LOG_FILE);
                }
                catch { }
                return DEBUG_LOG_FILE;
            }
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static void DebugPerfLog(string runId, string hypothesisId, string location, string message, string dataJson)
        {
            try
            {
                string line =
                    "{\"sessionId\":\"e62f68\",\"runId\":\"" + EscapeJson(runId) +
                    "\",\"hypothesisId\":\"" + EscapeJson(hypothesisId) +
                    "\",\"location\":\"" + EscapeJson(location) +
                    "\",\"message\":\"" + EscapeJson(message) +
                    "\",\"data\":" + dataJson +
                    ",\"timestamp\":" + System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
                File.AppendAllText(DebugLogPath, line + "\n", Encoding.UTF8);
            }
            catch { }
        }

        public static int ActiveServerGems => activeServerGems;
        private static int activeServerGems = 0;
        private static int fixedTicksThisWindow = 0;
        private static int shipScanCallsThisWindow = 0;
        private static int shipsEnumeratedThisWindow = 0;
        private static float nextPerfLogTime = 0f;
        private static Starship[] cachedShips = new Starship[0];
        private static float nextShipCacheRefreshTime = 0f;
        private const float SHIP_CACHE_REFRESH_INTERVAL = 0.25f;

        [SerializeField] private float gemValue = 10f;
        [SerializeField] private float pickupRadius = 2f;
        [SerializeField] private float stopSpeedThreshold = 0.3f;
        [SerializeField] private float slowdownDrag = 4f;
        [SerializeField] private float baseScale = 0.48f; // Base visual scale; final scale = baseScale * value^(1/3) * ...
        [SerializeField] private float visualScaleMultiplier = 2.2f; // Global scale so value-1 gems are visible; value-50 is ~50x volume (not 50x radius)
        [SerializeField] private float lifetimeSeconds = 20f; // Time before gem expires and disappears
        [SerializeField] private float shrinkDuration = 3f; // Shrink from full to zero over this many seconds at end of life
        [SerializeField] private float magnetSpeed = 8f; // Speed when moving toward ship
        [SerializeField] private float collectRadius = 0.6f; // Collect when gem is this close to ship

        private NetworkVariable<float> value = new NetworkVariable<float>(10f);
        private NetworkVariable<float> gemSize = new NetworkVariable<float>(1f); // Size multiplier (affects visual scale and value)
        private NetworkVariable<float> asteroidPhysicalSize = new NetworkVariable<float>(0.5f); // Asteroid scale for "half asteroid" gem size
        private NetworkVariable<float> spawnTime = new NetworkVariable<float>(0f); // Server time when gem was spawned
        private NetworkVariable<ulong> expelledByShipId = new NetworkVariable<ulong>(0); // When non-zero: victim ship cannot collect for 3 sec
        private const float EXPELLED_UNCOLLECTABLE_DURATION = 3f;
        private Rigidbody rb;
        private float effectivePickupRadius; // Scaled pickup radius based on gem size

        public float Value => value.Value;
        public float GemSize => gemSize.Value;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            effectivePickupRadius = pickupRadius;
            var renderer = GetComponent<Renderer>();
            if (renderer != null)
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // Avoid overlapping shadow artifacts when gems cluster
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                value.Value = gemValue;
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
                if (rb != null) rb.linearDamping = slowdownDrag;
                activeServerGems++;
            }
            
            // Update visual scale based on gem size (client-side)
            gemSize.OnValueChanged += OnGemSizeChanged;
            UpdateVisualScale();
        }

        public override void OnNetworkDespawn()
        {
            gemSize.OnValueChanged -= OnGemSizeChanged;
            if (IsServer)
            {
                activeServerGems = Mathf.Max(0, activeServerGems - 1);
            }
            base.OnNetworkDespawn();
        }

        private void OnGemSizeChanged(float previousSize, float newSize)
        {
            UpdateVisualScale();
        }

        private void UpdateVisualScale()
        {
            // Shrink only in the last shrinkDuration seconds (e.g. 3 sec)
            float lifetimeRemaining = 1f;
            if (NetworkManager.Singleton != null)
            {
                float elapsedTime = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
                if (elapsedTime >= lifetimeSeconds - shrinkDuration)
                    lifetimeRemaining = Mathf.Clamp01((lifetimeSeconds - elapsedTime) / shrinkDuration);
            }
            
            // Scale by value^(1/3) so volume ∝ value (1-50)
            float valueScale = Mathf.Pow(Mathf.Max(1f, value.Value), 1f / 3f);
            float scale = baseScale * valueScale * asteroidPhysicalSize.Value * lifetimeRemaining * visualScaleMultiplier;
            // Cap so gem is never bigger than the asteroid it came from
            if (asteroidPhysicalSize.Value > 0.01f)
                scale = Mathf.Min(scale, asteroidPhysicalSize.Value * 0.85f);
            transform.localScale = Vector3.one * scale;
            
            // Pickup radius scales with value^(1/3) so bigger gems are easier to collect
            effectivePickupRadius = pickupRadius * valueScale * lifetimeRemaining;
        }

        public void Initialize(float gemValue, float sizeMultiplier = 1f, float asteroidScale = 0.5f)
        {
            if (IsServer)
            {
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = asteroidScale;
                value.Value = gemValue;
                expelledByShipId.Value = 0;
            }
        }

        /// <summary>Initialize gem expelled from a ship. Victim (expelledByShipNetworkId) cannot collect for 3 sec; enemies can collect immediately.</summary>
        public void InitializeFromShip(float gemValue, float sizeMultiplier, ulong expelledByShipNetworkId)
        {
            if (IsServer)
            {
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = 0.5f; // Default for ship gems
                value.Value = gemValue;
                expelledByShipId.Value = expelledByShipNetworkId;
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
            }
        }

        private void FixedUpdate()
        {
            // Update visual scale on all clients (for shrinking effect)
            UpdateVisualScale();

            // Never wrap gem position: world position can grow (e.g. 100, 310). ToroidalRenderer
            // displays at the copy closest to the local player's camera for a seamless view.

            if (!IsServer) return;
            if (value.Value <= 0) return;

            fixedTicksThisWindow++;

            // Check if gem has expired
            float elapsedTime = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
            if (elapsedTime >= lifetimeSeconds)
            {
                // Gem expired - despawn it
                var no = GetComponent<NetworkObject>();
                if (no != null) no.Despawn();
                return;
            }

            // Pickup radius and lifetime shrink (same as visual: full until last shrinkDuration sec)
            float lifetimeRemaining = 1f;
            if (elapsedTime >= lifetimeSeconds - shrinkDuration)
                lifetimeRemaining = Mathf.Clamp01((lifetimeSeconds - elapsedTime) / shrinkDuration);
            float currentPickupRadius = effectivePickupRadius;

            // Find nearest valid ship in range using toroidal distance (so pickup works across map edges)
            float elapsed = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
            ulong expelledId = expelledByShipId.Value;
            Vector3 gemPos = rb != null ? rb.position : transform.position;
            Starship nearestShip = null;
            float nearestDist = currentPickupRadius;
            Starship[] ships = GetCachedShipsForServer();
            foreach (Starship ship in ships)
            {
                if (ship.IsDead || ship.CurrentGems >= ship.GemCapacity) continue;
                if (expelledId != 0)
                {
                    var shipNo = ship.GetComponent<NetworkObject>();
                    if (shipNo != null && shipNo.NetworkObjectId == expelledId && elapsed < EXPELLED_UNCOLLECTABLE_DURATION)
                        continue; // Victim cannot collect yet
                }
                float dist = ToroidalMap.ToroidalDistance(gemPos, ship.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestShip = ship;
                }
            }

            if (nearestShip != null)
            {
                // Toroidal direction for magnetic pull (correct across wrap)
                Vector3 toShip = ToroidalMap.ToroidalDirection(gemPos, nearestShip.transform.position);
                toShip.y = 0f;
                if (toShip.sqrMagnitude < 0.0001f) toShip = Vector3.forward;
                else toShip.Normalize();
                float dist = nearestDist;

                // Collect when very close (toroidal distance)
                if (dist <= collectRadius)
                {
                    float toAdd = Mathf.Min(value.Value, nearestShip.GemCapacity - nearestShip.CurrentGems);
                    nearestShip.AddGemsServerRpc(toAdd);
                    value.Value -= toAdd;
                    if (value.Value <= 0)
                    {
                        var no = GetComponent<NetworkObject>();
                        if (no != null) no.Despawn();
                    }
                    return;
                }

                // Magnetic pull toward ship (XZ only, toroidal direction)
                if (rb != null)
                {
                    Vector3 targetVel = toShip * magnetSpeed;
                    rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, targetVel, magnetSpeed * Time.fixedDeltaTime * 4f);
                    rb.linearDamping = 0f;
                }
                return;
            }

            // No ship in range: apply drag so gem slows and stops
            if (rb != null)
            {
                rb.linearDamping = slowdownDrag;
                if (rb.linearVelocity.magnitude < stopSpeedThreshold)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.linearDamping = 0f;
                }
            }

            if (Time.unscaledTime >= nextPerfLogTime)
            {
                // #region agent log
                DebugPerfLog(
                    "initial",
                    "G1",
                    "Gem.cs:FixedUpdate",
                    "Gem perf aggregate",
                    "{\"activeServerGems\":" + activeServerGems +
                    ",\"fixedTicksWindow\":" + fixedTicksThisWindow +
                    ",\"shipScanCallsWindow\":" + shipScanCallsThisWindow +
                    ",\"shipsEnumeratedWindow\":" + shipsEnumeratedThisWindow + "}");
                // #endregion
                fixedTicksThisWindow = 0;
                shipScanCallsThisWindow = 0;
                shipsEnumeratedThisWindow = 0;
                nextPerfLogTime = Time.unscaledTime + 1f;
            }
        }

        private static Starship[] GetCachedShipsForServer()
        {
            if (Time.unscaledTime >= nextShipCacheRefreshTime || cachedShips == null)
            {
                cachedShips = FindObjectsByType<Starship>(FindObjectsSortMode.None);
                shipScanCallsThisWindow++;
                shipsEnumeratedThisWindow += cachedShips != null ? cachedShips.Length : 0;
                nextShipCacheRefreshTime = Time.unscaledTime + SHIP_CACHE_REFRESH_INTERVAL;
            }
            return cachedShips ?? System.Array.Empty<Starship>();
        }
    }
}
