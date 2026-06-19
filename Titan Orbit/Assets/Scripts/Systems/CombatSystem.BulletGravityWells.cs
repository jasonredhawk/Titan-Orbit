using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
    /// <summary>Server-side gravity-well fields spawned by bullet-bank GravityPull impacts.</summary>
    public partial class CombatSystem
    {
        private struct ActiveGravityWell
        {
            public Vector3 Center;
            public float Radius;
            public float PullForce;
            public float EndTime;
            public TeamManager.Team OwnerTeam;
            public ulong OwnerShipNetworkId;
        }

        private readonly List<ActiveGravityWell> activeGravityWells = new List<ActiveGravityWell>(24);

        /// <summary>Server: pull all enemy ships within radius toward center until the field expires.</summary>
        public void RegisterBulletGravityWell(
            Vector3 center,
            float radius,
            float pullForce,
            float durationSeconds,
            TeamManager.Team ownerTeam,
            ulong ownerShipNetworkId)
        {
            if (!IsServer || radius <= 0f || pullForce <= 0f || durationSeconds <= 0f) return;
            center.y = 0f;
            activeGravityWells.Add(new ActiveGravityWell
            {
                Center = center,
                Radius = Mathf.Max(0.5f, radius),
                PullForce = pullForce,
                EndTime = Time.time + durationSeconds,
                OwnerTeam = ownerTeam,
                OwnerShipNetworkId = ownerShipNetworkId,
            });
        }

        private void TickBulletGravityWells()
        {
            if (!IsServer || activeGravityWells.Count == 0) return;

            float dt = Time.fixedDeltaTime;
            for (int w = activeGravityWells.Count - 1; w >= 0; w--)
            {
                ActiveGravityWell well = activeGravityWells[w];
                if (Time.time >= well.EndTime)
                {
                    activeGravityWells.RemoveAt(w);
                    continue;
                }

                ApplyGravityWellForces(ref well, dt);
            }
        }

        private static void ApplyGravityWellForces(ref ActiveGravityWell well, float dt)
        {
            float radius = well.Radius;
            float radiusSq = radius * radius;

            for (int i = 0; i < Starship.AllStarships.Count; i++)
            {
                Starship ship = Starship.AllStarships[i];
                if (ship == null || ship.IsDead) continue;
                if (ship.ShipTeam == well.OwnerTeam) continue;

                NetworkObject shipNo = ship.NetworkObject;
                if (shipNo != null && shipNo.NetworkObjectId == well.OwnerShipNetworkId)
                    continue;

                Rigidbody shipRb = ship.GetComponent<Rigidbody>();
                if (shipRb == null) continue;

                Vector3 shipPos = shipRb.position;
                shipPos.y = 0f;
                float dist = ToroidalMap.ToroidalDistance(shipPos, well.Center);
                if (dist > radius) continue;

                Vector3 toCenter = ToroidalMap.ShortestWorldOffsetXZ(shipPos, well.Center);
                if (toCenter.sqrMagnitude < 0.0001f) continue;
                toCenter.Normalize();

                float falloff = 1f - Mathf.Clamp01(dist / radius);
                float strength = well.PullForce * falloff * Mathf.Max(0.01f, dt);
                shipRb.AddForce(toCenter * strength, ForceMode.Impulse);
            }
        }
    }
}
