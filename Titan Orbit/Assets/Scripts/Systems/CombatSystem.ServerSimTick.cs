using UnityEngine;
using Unity.Netcode;

namespace TitanOrbit.Systems
{
    public partial class CombatSystem
    {
        private void FixedUpdate()
        {
            if (!IsServer) return;
            EnsureSimulationInitialized();
            EnsureRocketPoolInitialized();
            EnsureMinePoolInitialized();
            EnsurePeopleTransportPoolInitialized();
            float dt = Time.fixedDeltaTime;
            float now = GetServerTimeNowSeconds();
            TickBulletGravityWells();
            TickServerRockets(dt, now);
            TickServerMines(now);
            TickServerPeopleTransports(dt, now);

            if (serverBullets != null && activeServerBulletCount > 0)
            {
                for (int i = 0; i < serverBullets.Length; i++)
                {
                    if (!serverBullets[i].Active) continue;
                    StepBullet(i, dt, now);
                }
            }
        }

        private void LateUpdate()
        {
            if (!IsServer) return;
            FlushPendingSpawnBatch();
            FlushPendingRocketBatch();
            FlushPendingMineBatch();
            FlushPendingPeopleTransportBatch();
            FlushPendingImpacts();
        }
    }
}
