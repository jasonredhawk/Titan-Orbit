using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
    public struct PeopleTransportSpawnPayload : INetworkSerializable
    {
        public Vector3 SpawnPosition;
        public Vector3 Velocity;
        public float Amount;
        public ulong TargetNetworkObjectId;
        public ulong SourcePlanetNetworkObjectId;
        public ulong SpawningShipNetworkObjectId;
        public uint Sequence;
        public float ServerSpawnTime;
        public float CruiseSpeed;
        public byte TeamByte;
        public byte IsLoadFlag;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref SpawnPosition);
            serializer.SerializeValue(ref Velocity);
            serializer.SerializeValue(ref Amount);
            serializer.SerializeValue(ref TargetNetworkObjectId);
            serializer.SerializeValue(ref SourcePlanetNetworkObjectId);
            serializer.SerializeValue(ref SpawningShipNetworkObjectId);
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref ServerSpawnTime);
            serializer.SerializeValue(ref CruiseSpeed);
            serializer.SerializeValue(ref TeamByte);
            serializer.SerializeValue(ref IsLoadFlag);
        }
    }

    public partial class CombatSystem
    {
        private const int MaxPeopleTransportBatch = 16;
        private const int DefaultPeopleTransportPool = 128;
        private const float PeopleTransportRadius = 0.25f;

        private struct ServerPeopleTransport
        {
            public bool Active;
            public Vector3 Position;
            public Vector3 Velocity;
            public float Amount;
            public float SpawnTime;
            public Vector3 SpawnPosition;
            public float CruiseSpeed;
            public ulong TargetNetworkObjectId;
            public ulong SourcePlanetNetworkObjectId;
            public ulong SpawningShipNetworkObjectId;
            public TeamManager.Team Team;
            public bool IsLoad;
            public uint Sequence;
        }

        private ServerPeopleTransport[] serverPeopleTransports;
        private int activePeopleTransportCount;
        private uint nextPeopleTransportSequence = 1;
        private readonly System.Collections.Generic.List<PeopleTransportSpawnPayload> pendingPeopleTransportBatch =
            new System.Collections.Generic.List<PeopleTransportSpawnPayload>(MaxPeopleTransportBatch);
        private readonly System.Collections.Generic.List<uint> pendingPeopleTransportDespawn =
            new System.Collections.Generic.List<uint>(MaxPeopleTransportBatch);

        public bool TrySpawnServerPeopleTransport(
            Vector3 fromPos,
            Vector3 toPos,
            float amount,
            ulong targetNetworkObjectId,
            bool isLoad,
            TeamManager.Team team,
            ulong spawningShipNetworkObjectId,
            ulong sourcePlanetNetworkObjectId)
        {
            if (!IsServer || amount <= 0f) return false;
            EnsurePeopleTransportPoolInitialized();

            Vector3 dir = ToroidalMap.ToroidalDirection(fromPos, toPos);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            else dir.Normalize();

            Vector3 pos = fromPos;
            pos.y = 0f;
            if (isLoad)
                pos += dir * 0.2f;

            float cruiseSpeed = PeopleTransportProjectile.ComputeCruiseSpeed(fromPos, toPos, isLoad);
            float initialSpeed = cruiseSpeed * (isLoad ? 0.55f : 0.3f);
            Vector3 velocity = dir * initialSpeed;

            int slot = AcquirePeopleTransportSlot();
            if (slot < 0) return false;

            uint seq = nextPeopleTransportSequence++;
            if (nextPeopleTransportSequence == 0) nextPeopleTransportSequence = 1;
            float spawnTime = GetServerTimeNowSeconds();

            ref ServerPeopleTransport t = ref serverPeopleTransports[slot];
            t.Active = true;
            t.Position = pos;
            t.Velocity = velocity;
            t.Amount = amount;
            t.SpawnTime = spawnTime;
            t.SpawnPosition = pos;
            t.CruiseSpeed = cruiseSpeed;
            t.TargetNetworkObjectId = targetNetworkObjectId;
            t.SourcePlanetNetworkObjectId = sourcePlanetNetworkObjectId;
            t.SpawningShipNetworkObjectId = spawningShipNetworkObjectId;
            t.Team = team;
            t.IsLoad = isLoad;
            t.Sequence = seq;
            activePeopleTransportCount++;

            var payload = new PeopleTransportSpawnPayload
            {
                SpawnPosition = pos,
                Velocity = velocity,
                Amount = amount,
                TargetNetworkObjectId = targetNetworkObjectId,
                SourcePlanetNetworkObjectId = sourcePlanetNetworkObjectId,
                SpawningShipNetworkObjectId = spawningShipNetworkObjectId,
                Sequence = seq,
                ServerSpawnTime = spawnTime,
                CruiseSpeed = cruiseSpeed,
                TeamByte = (byte)team,
                IsLoadFlag = (byte)(isLoad ? 1 : 0),
            };
            pendingPeopleTransportBatch.Add(payload);
            return true;
        }

        private void EnsurePeopleTransportPoolInitialized()
        {
            if (serverPeopleTransports != null) return;
            serverPeopleTransports = new ServerPeopleTransport[DefaultPeopleTransportPool];
        }

        private int AcquirePeopleTransportSlot()
        {
            if (serverPeopleTransports == null) return -1;
            for (int i = 0; i < serverPeopleTransports.Length; i++)
                if (!serverPeopleTransports[i].Active) return i;
            return -1;
        }

        private void ReleasePeopleTransportSlot(int slot, uint sequence)
        {
            if (slot < 0 || serverPeopleTransports == null || !serverPeopleTransports[slot].Active) return;
            pendingPeopleTransportDespawn.Add(sequence);
            serverPeopleTransports[slot].Active = false;
            activePeopleTransportCount = Mathf.Max(0, activePeopleTransportCount - 1);
        }

        private void TickServerPeopleTransports(float dt, float now)
        {
            if (serverPeopleTransports == null || activePeopleTransportCount == 0) return;
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            for (int i = 0; i < serverPeopleTransports.Length; i++)
            {
                if (!serverPeopleTransports[i].Active) continue;
                StepPeopleTransport(i, dt, now, nm);
            }
        }

        private void StepPeopleTransport(int slot, float dt, float now, NetworkManager nm)
        {
            ref ServerPeopleTransport t = ref serverPeopleTransports[slot];
            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(t.TargetNetworkObjectId, out NetworkObject targetObj))
                return;

            Vector3 myPos = t.Position;
            myPos.y = 0f;

            if (t.IsLoad)
            {
                Starship ship = targetObj.GetComponent<Starship>();
                if (ship == null) return;

                if (ship.IsDead || !PeopleTransportProjectile.IsShipEligibleForLoadFromSourcePlanet(ship, t.SourcePlanetNetworkObjectId))
                {
                    if (PeopleTransportProjectile.TryResolvePlanet(t.SourcePlanetNetworkObjectId, out Planet sourcePlanet))
                    {
                        Vector3 surfaceTarget = PeopleTransportProjectile.GetSurfacePointToward(sourcePlanet, myPos);
                        ApplyPeopleMagnet(ref t, myPos, surfaceTarget, dt);
                        if (PeopleTransportProjectile.CanCompleteReturnToSourcePlanet(
                                t.Position, t.SpawnPosition, sourcePlanet, now, t.SpawnTime))
                        {
                            ReturnPeopleLoadToPlanet(ref t, ship, sourcePlanet);
                            ReleasePeopleTransportSlot(slot, t.Sequence);
                        }
                    }
                }
                else
                {
                    if (PeopleTransportProjectile.CanDeliverLoadToShip(myPos, ship, PeopleTransportRadius)
                        && PeopleTransportProjectile.HasBriefTravelBeforeLoad(myPos, t.SpawnPosition, now, t.SpawnTime))
                    {
                        DeliverPeopleLoad(ref t, ship);
                        ReleasePeopleTransportSlot(slot, t.Sequence);
                        return;
                    }

                    Vector3 shipTarget = PeopleTransportProjectile.GetShipMagnetTarget(ship, myPos);
                    ApplyPeopleMagnet(ref t, myPos, shipTarget, dt);
                }

                if (TryDestroyPeopleTransportOnForeignPlanet(ref t, slot, null, now, nm))
                    return;
            }
            else
            {
                Planet planet = targetObj.GetComponent<Planet>();
                if (planet == null) return;

                Vector3 magnetTarget = PeopleTransportProjectile.GetSurfacePointToward(planet, myPos);
                ApplyPeopleMagnet(ref t, myPos, magnetTarget, dt);

                if (PeopleTransportProjectile.CanCompleteUnloadDelivery(myPos, t.SpawnPosition, planet, now, t.SpawnTime))
                {
                    DeliverPeopleUnload(ref t, planet, nm);
                    ReleasePeopleTransportSlot(slot, t.Sequence);
                    return;
                }

                TryDestroyPeopleTransportOnForeignPlanet(ref t, slot, planet, now, nm);
            }
        }

        private static void ApplyPeopleMagnet(ref ServerPeopleTransport t, Vector3 myPos, Vector3 target, float dt)
        {
            t.Velocity = PeopleTransportProjectile.SteerMagnetVelocity(myPos, target, t.Velocity, dt, t.CruiseSpeed);
            t.Velocity.y = 0f;
            t.Position = myPos + t.Velocity * dt;
            t.Position.y = 0f;
        }

        private static void ReturnPeopleLoadToPlanet(ref ServerPeopleTransport t, Starship ship, Planet sourcePlanet)
        {
            sourcePlanet.AddPopulationFromServer(t.Amount, t.Team);
            if (ship != null)
                ship.ReleasePeopleInTransit(t.Amount);
        }

        private bool TryDestroyPeopleTransportOnForeignPlanet(
            ref ServerPeopleTransport t,
            int slot,
            Planet intendedTargetPlanet,
            float now,
            NetworkManager nm)
        {
            for (int i = 0; i < Planet.AllPlanets.Count; i++)
            {
                Planet candidate = Planet.AllPlanets[i];
                if (candidate == null || candidate == intendedTargetPlanet) continue;

                if (t.IsLoad
                    && PeopleTransportProjectile.TryResolvePlanet(t.SourcePlanetNetworkObjectId, out Planet sourcePlanet)
                    && candidate == sourcePlanet)
                    continue;

                if (!PeopleTransportProjectile.HitsForeignPlanetSurface(t.Position, candidate, now, t.SpawnTime))
                    continue;

                if (t.IsLoad
                    && nm.SpawnManager.SpawnedObjects.TryGetValue(t.TargetNetworkObjectId, out NetworkObject shipObj))
                {
                    Starship ship = shipObj.GetComponent<Starship>();
                    if (ship != null)
                        ship.ReleasePeopleInTransit(t.Amount);
                }

                ReleasePeopleTransportSlot(slot, t.Sequence);
                return true;
            }

            return false;
        }

        private static void DeliverPeopleLoad(ref ServerPeopleTransport t, Starship ship)
        {
            float space = ship.PeopleCapacity - ship.CurrentPeople;
            float toAdd = Mathf.Min(t.Amount, space);
            if (toAdd > 0f)
            {
                ship.AddPeopleFromServer(toAdd);
                ship.OnPeopleLoadArrivedFromProjectile(toAdd, t.Team, PeopleTransportProjectile.GetShipMagnetTarget(ship, t.Position));
                ship.ReleasePeopleInTransit(toAdd);
                if (ScoreSystem.Instance != null)
                    ScoreSystem.Instance.AwardFriendlyLoad(ship, toAdd);
            }
            else
                ship.ReleasePeopleInTransit(t.Amount);
        }

        private static void DeliverPeopleUnload(ref ServerPeopleTransport t, Planet planet, NetworkManager nm)
        {
            planet.AddPopulationFromServer(t.Amount, t.Team);
            Vector3 feedbackPos = PeopleTransportProjectile.GetSurfacePointToward(planet, t.Position);
            if (PeopleTransportProjectile.TryResolveShip(t.SpawningShipNetworkObjectId, out Starship ship))
                ship.OnPeopleUnloadArrivedFromProjectile(t.Amount, t.Team, feedbackPos, planet);
            else if (VisualEffectsManager.Instance != null)
            {
                feedbackPos.y = 0f;
                VisualEffectsManager.Instance.SpawnFloatingCountFromServerAuthority(
                    feedbackPos,
                    FloatingCountChannel.PeopleUnload,
                    -t.Amount,
                    t.Team);
            }
        }

        private void FlushPendingPeopleTransportBatch()
        {
            if (pendingPeopleTransportBatch.Count > 0)
            {
                int total = pendingPeopleTransportBatch.Count;
                for (int start = 0; start < total; start += MaxPeopleTransportBatch)
                {
                    int chunk = Mathf.Min(MaxPeopleTransportBatch, total - start);
                    PeopleTransportSpawnPayload[] arr = new PeopleTransportSpawnPayload[chunk];
                    pendingPeopleTransportBatch.CopyTo(start, arr, 0, chunk);
                    if (IsClient)
                        SpawnPeopleTransportBatchLocal(arr);
                    SpawnPeopleTransportBatchClientRpc(arr);
                }
                pendingPeopleTransportBatch.Clear();
            }

            if (pendingPeopleTransportDespawn.Count > 0)
            {
                for (int i = 0; i < pendingPeopleTransportDespawn.Count; i++)
                {
                    uint seq = pendingPeopleTransportDespawn[i];
                    if (IsClient)
                        ClientPeopleTransportTracer.DespawnBySequence(seq);
                    DespawnPeopleTransportClientRpc(seq);
                }
                pendingPeopleTransportDespawn.Clear();
            }
        }

        private static void SpawnPeopleTransportBatchLocal(PeopleTransportSpawnPayload[] payloads)
        {
            if (payloads == null) return;
            for (int i = 0; i < payloads.Length; i++)
                ClientPeopleTransportTracer.Spawn(payloads[i]);
        }

        [ClientRpc]
        private void SpawnPeopleTransportBatchClientRpc(PeopleTransportSpawnPayload[] payloads)
        {
            if (IsServer) return;
            SpawnPeopleTransportBatchLocal(payloads);
        }

        [ClientRpc]
        private void DespawnPeopleTransportClientRpc(uint sequence)
        {
            if (IsServer) return;
            ClientPeopleTransportTracer.DespawnBySequence(sequence);
        }
    }
}
