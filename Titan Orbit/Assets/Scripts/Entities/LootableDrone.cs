using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Drone loot dropped when a ship dies. Synced over the network, tractor-pullable like gems,
    /// and collected into an empty equipment slot. Despawns when debris lifetime expires.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class LootableDrone : NetworkBehaviour
    {
        public static readonly List<LootableDrone> AllLootableDrones = new List<LootableDrone>();

        private NetworkVariable<int> itemTypeNet = new NetworkVariable<int>((int)StoreItemType.FighterDrone);
        private NetworkVariable<int> remainingHpNet = new NetworkVariable<int>(30);
        private NetworkVariable<int> ownerTeamNet = new NetworkVariable<int>((int)TeamManager.Team.None);
        private NetworkVariable<float> spawnServerTimeNet = new NetworkVariable<float>(0f);
        private NetworkVariable<float> lifetimeNet = new NetworkVariable<float>(5f);
        private NetworkVariable<ulong> sourceShipNetworkIdNet = new NetworkVariable<ulong>(0ul);

        [SerializeField] private float collectRadius = 1.2f;
        [SerializeField] private float shipProximitySlop = 0.35f;

        private Rigidbody rb;
        private DroneBody droneBody;
        private bool registered;

        public StoreItemType ItemType => (StoreItemType)itemTypeNet.Value;
        public int RemainingHp => remainingHpNet.Value;
        public TeamManager.Team OwnerTeam => (TeamManager.Team)ownerTeamNet.Value;
        public bool IsDestroyed => remainingHpNet.Value <= 0;
        public ulong SourceShipNetworkId => sourceShipNetworkIdNet.Value;

        public static LootableDrone SpawnFromShipDeath(
            GameObject networkPrefab,
            StoreItemType itemType,
            int remainingHp,
            TeamManager.Team ownerTeam,
            ulong sourceShipNetworkId,
            Vector3 worldPosition,
            Vector3 explosionCenter,
            float lifetimeSeconds)
        {
            if (networkPrefab == null || !StoreItemData.IsDrone(itemType)) return null;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return null;

            worldPosition.y = DroneSwarmLogic.FixedY;
            GameObject go = Object.Instantiate(networkPrefab, worldPosition, Quaternion.identity);

            var loot = go.GetComponent<LootableDrone>();
            if (loot == null)
                loot = go.AddComponent<LootableDrone>();

            loot.ConfigureVisualChild(itemType);
            loot.ServerInitializeBeforeSpawn(itemType, remainingHp, ownerTeam, sourceShipNetworkId, lifetimeSeconds);

            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezePositionY;
                Vector3 dir = worldPosition - explosionCenter;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f)
                {
                    Vector2 c = Random.insideUnitCircle.normalized;
                    dir = new Vector3(c.x, 0f, c.y);
                }
                else
                    dir.Normalize();

                CombatSystem combat = CombatSystem.Instance;
                float minImpulse = combat != null ? combat.DeathDebrisMinImpulse : 1f;
                float maxImpulse = combat != null ? combat.DeathDebrisMaxImpulse : 3f;
                float speed = Random.Range(minImpulse, maxImpulse);
                rb.linearVelocity = dir * speed;
                rb.angularVelocity = Random.insideUnitSphere * 4f;
                rb.linearDamping = combat != null ? combat.DeathDebrisLinearDamping : 0f;
            }

            var no = go.GetComponent<NetworkObject>();
            if (no != null)
                no.Spawn();

            return loot;
        }

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.useGravity = false;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
        }

        public override void OnNetworkSpawn()
        {
            if (!registered)
            {
                AllLootableDrones.Add(this);
                registered = true;
            }

            EnsureDroneBody();
            droneBody?.InitializeAsLoot(this);
        }

        public override void OnNetworkDespawn()
        {
            if (registered)
            {
                AllLootableDrones.Remove(this);
                registered = false;
            }
        }

        private void ServerInitializeBeforeSpawn(
            StoreItemType itemType,
            int remainingHp,
            TeamManager.Team ownerTeam,
            ulong sourceShipNetworkId,
            float lifetimeSeconds)
        {
            itemTypeNet.Value = (int)itemType;
            remainingHpNet.Value = Mathf.Max(1, remainingHp);
            ownerTeamNet.Value = (int)ownerTeam;
            sourceShipNetworkIdNet.Value = sourceShipNetworkId;
            lifetimeNet.Value = Mathf.Max(0.5f, lifetimeSeconds);
            if (NetworkManager.Singleton != null)
                spawnServerTimeNet.Value = (float)NetworkManager.Singleton.ServerTime.Time;
        }

        private void ConfigureVisualChild(StoreItemType itemType)
        {
            var store = HomePlanetStoreSystem.Instance;
            if (store == null) return;

            GameObject visualPrefab = itemType switch
            {
                StoreItemType.FighterDrone => store.FighterDronePrefab,
                StoreItemType.ShieldDrone => store.ShieldDronePrefab,
                StoreItemType.MiningDrone => store.MiningDronePrefab,
                _ => null
            };
            if (visualPrefab == null) return;

            GameObject visual = Object.Instantiate(visualPrefab, transform);
            visual.name = "DroneVisual";
            DroneSwarmController.SanitizeLootVisualInstance(visual);
        }

        private void EnsureDroneBody()
        {
            droneBody = GetComponent<DroneBody>();
            if (droneBody == null)
                droneBody = gameObject.AddComponent<DroneBody>();
        }

        public bool IsEnemyTeam(TeamManager.Team attackerTeam)
        {
            return attackerTeam != TeamManager.Team.None && attackerTeam != OwnerTeam;
        }

        public bool CanBeCollectedBy(Starship ship)
        {
            if (!IsServer || ship == null || !ship.IsSpawned || ship.IsDead) return false;
            if (IsDestroyed) return false;
            if (!ship.HasEmptyEquipmentSlot) return false;
            if (ship.ShipTeam == TeamManager.Team.None) return false;
            return true;
        }

        public void ApplyDamageFromBullet(float damage, TeamManager.Team attackerTeam)
        {
            if (!IsServer || IsDestroyed) return;
            if (!IsEnemyTeam(attackerTeam)) return;
            remainingHpNet.Value = Mathf.Max(0, remainingHpNet.Value - Mathf.RoundToInt(damage));
            if (remainingHpNet.Value <= 0)
                DespawnLoot();
        }

        private void FixedUpdate()
        {
            if (!IsServer || IsDestroyed) return;

            float elapsed = (float)NetworkManager.Singleton.ServerTime.Time - spawnServerTimeNet.Value;
            if (elapsed >= lifetimeNet.Value)
            {
                DespawnLoot();
                return;
            }

            if (LootableDroneTractorUtility.IsPulledByAnyShip(this))
            {
                TryProximityCollect();
                return;
            }

            TryProximityCollect();
        }

        private void TryProximityCollect()
        {
            if (!CanBeCollectedByAny(out Starship collector)) return;
            CollectToShip(collector);
        }

        private bool CanBeCollectedByAny(out Starship ship)
        {
            ship = null;
            foreach (var candidate in Starship.AllStarships)
            {
                if (!CanBeCollectedBy(candidate)) continue;
                if (!IsWithinCollectDistance(candidate)) continue;
                ship = candidate;
                return true;
            }
            return false;
        }

        private bool IsWithinCollectDistance(Starship ship)
        {
            Vector3 pos = rb != null ? rb.position : transform.position;
            Vector3 shipPos = ship.transform.position;
            shipPos.y = 0f;
            pos.y = 0f;
            float dist = ToroidalMap.ToroidalDistance(pos, shipPos);
            float hullRadius = shipProximitySlop;
            Collider shipCollider = ship.GetComponent<Collider>();
            if (shipCollider != null && shipCollider.enabled)
            {
                Vector3 e = shipCollider.bounds.extents;
                float colliderRadius = Mathf.Sqrt(e.x * e.x + e.z * e.z);
                if (colliderRadius > 0.01f)
                    hullRadius = Mathf.Max(hullRadius, colliderRadius * 0.45f);
            }
            return dist <= collectRadius + hullRadius;
        }

        private void CollectToShip(Starship ship)
        {
            if (!IsServer || ship == null) return;
            if (!ship.AddEquipmentFromServer(ItemType, remainingHpNet.Value))
                return;
            DespawnLoot();
        }

        private void DespawnLoot()
        {
            if (!IsServer) return;
            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned)
                no.Despawn();
            else
                Destroy(gameObject);
        }
    }
}
