using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Placeable explosive mine. When an enemy ship enters trigger radius, explodes (damage + radius).
    /// Small vs large: different damage and radius.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    public class Mine : NetworkBehaviour
    {
        [Header("Mine")]
        [SerializeField] private float damage = 40f;
        [SerializeField] private float explosionRadius = 4f;
        [SerializeField] private float triggerRadius = 5f;
        [SerializeField] private float armTime = 0.5f;

        private TeamManager.Team ownerTeam;
        private float spawnTime;
        private const float FIXED_Y = 0f;

        public void Initialize(float mineDamage, float radius, TeamManager.Team team)
        {
            damage = mineDamage;
            explosionRadius = radius;
            triggerRadius = Mathf.Max(triggerRadius, radius);
            ownerTeam = team;
        }

        public override void OnNetworkSpawn()
        {
            var col = GetComponent<SphereCollider>();
            if (col != null) { col.isTrigger = true; col.radius = triggerRadius; }
            spawnTime = Time.time;
        }

        private void FixedUpdate()
        {
            Vector3 p = transform.position;
            p.y = FIXED_Y;
            transform.position = p;
            if (!IsServer) return;
            if (Time.time - spawnTime < armTime) return;

            Collider[] hits = Physics.OverlapSphere(transform.position, triggerRadius, ~0, QueryTriggerInteraction.Ignore);
            foreach (var c in hits)
            {
                Starship ship = c.GetComponent<Starship>();
                if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
                {
                    Explode();
                    return;
                }
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;
            if (Time.time - spawnTime < armTime) return;
            Starship ship = other.GetComponent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                Explode();
            }
        }

        private void Explode()
        {
            Vector3 pos = transform.position;
            Collider[] hits = Physics.OverlapSphere(pos, explosionRadius, ~0, QueryTriggerInteraction.Ignore);
            foreach (var c in hits)
            {
                float dist = Vector3.Distance(c.ClosestPoint(pos), pos);
                float falloff = 1f - (dist / explosionRadius) * 0.5f;
                float dmg = damage * Mathf.Clamp01(falloff);

                Starship ship = c.GetComponent<Starship>();
                if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
                    ship.TakeDamageServerRpc(dmg, ownerTeam);

                DroneBase drone = c.GetComponent<DroneBase>();
                if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
                    drone.TakeDamageServerRpc(dmg, ownerTeam);

                Asteroid ast = c.GetComponent<Asteroid>();
                if (ast != null && !ast.IsDestroyed)
                    ast.TakeDamageServerRpc(dmg);
            }

            var no = GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned) no.Despawn();
        }
    }
}
