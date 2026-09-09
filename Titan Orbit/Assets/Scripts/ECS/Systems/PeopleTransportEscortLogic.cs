using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server escort bookkeeping. Each load capsule keeps its packed Amount (a +36 stays +36).
    /// Follow sits around the hull. Unload calls an in-position capsule to ship center (ready),
    /// then launches it one-way at the old cadence.
    /// </summary>
    public static class PeopleTransportEscortLogic
    {
        /// <summary>True when this ship should dump escorts (hostile/neutral, or friendly below half-cap).</summary>
        public static bool ShouldUnloadEscorts(
            in ShipState ship,
            in PlanetState planet,
            int halfCap)
        {
            if (ship.IsDead || ship.AwaitingTeamSelection || ship.CurrentPeople <= 0)
                return false;
            bool friendly = ship.Team != TeamId.None && planet.Ownership == ship.Team;
            if (!friendly)
                return true;
            return planet.Population < halfCap;
        }

        /// <summary>True when the landing latch is aimed at a live planet.</summary>
        public static bool IsLanding(in PeopleEscortLandingState landing) =>
            landing.PlanetId != 0;

        /// <summary>
        /// World covering-ellipse radii (X = beam, Z = length). Formation uses the
        /// ellipse at each slot angle — not an average of the axes.
        /// </summary>
        public static void ResolveEscortShipExtents(
            EntityManager em,
            Entity shipEntity,
            in LocalTransform transform,
            out float extX,
            out float extZ)
        {
            float scale = math.max(0.25f, transform.Scale);
            extX = 0f;
            extZ = 0f;
            if (em.Exists(shipEntity) && em.HasComponent<ShipHullColliderState>(shipEntity))
            {
                var hull = em.GetComponentData<ShipHullColliderState>(shipEntity);
                float3 ext = ShipHullColliderLogic.GetCachedCoveringExtents(hull);
                extX = ext.x * scale;
                extZ = ext.z * scale;
            }

            if (extX <= BodyCollisionMath.MinShipHullRadiusWorld &&
                em.Exists(shipEntity) && em.HasComponent<PhysicsCollider>(shipEntity))
            {
                var collider = em.GetComponentData<PhysicsCollider>(shipEntity);
                if (collider.Value.IsCreated)
                {
                    Aabb aabb = collider.Value.Value.CalculateAabb(RigidTransform.identity);
                    float2 he = (aabb.Max.xz - aabb.Min.xz) * 0.5f * scale;
                    extX = he.x;
                    extZ = he.y;
                }
            }

            if (extX <= BodyCollisionMath.MinShipHullRadiusWorld)
            {
                float fallback = PeopleTransportMath.GetShipHullRadius(transform.Scale);
                extX = fallback;
                extZ = fallback;
            }

            if (extZ <= BodyCollisionMath.MinShipHullRadiusWorld)
                extZ = extX;
        }

        /// <summary>Characteristic hull size (max of X/Z) for ready-center slack.</summary>
        public static float ResolveEscortShipRadius(
            EntityManager em,
            Entity shipEntity,
            in LocalTransform transform)
        {
            ResolveEscortShipExtents(em, shipEntity, in transform, out float extX, out float extZ);
            return math.max(extX, extZ);
        }

        /// <summary>
        /// Keeps slot Amounts in sync with <see cref="ShipState.CurrentPeople"/> without
        /// re-packing into unload chunks. A delivered +36 stays one slot.
        /// </summary>
        public static void SyncSlotAmounts(
            EntityManager em,
            Entity shipEntity,
            in ShipState ship,
            in LocalTransform shipTransform,
            float mapW,
            float mapH)
        {
            if (!em.HasBuffer<PeopleEscortSlot>(shipEntity) ||
                !em.HasComponent<PeopleEscortLandingState>(shipEntity))
                return;

            var slots = em.GetBuffer<PeopleEscortSlot>(shipEntity);
            if (ship.IsDead || ship.AwaitingTeamSelection)
            {
                ClearNonLaunchedSlots(ref slots);
                var landingClear = em.GetComponentData<PeopleEscortLandingState>(shipEntity);
                landingClear.PlanetId = 0;
                em.SetComponentData(shipEntity, landingClear);
                return;
            }

            if (ship.CurrentPeople <= 0)
            {
                ClearNonLaunchedSlots(ref slots);
                if (slots.Length == 0)
                {
                    var landingClear = em.GetComponentData<PeopleEscortLandingState>(shipEntity);
                    landingClear.PlanetId = 0;
                    em.SetComponentData(shipEntity, landingClear);
                }

                return;
            }

            int people = ship.CurrentPeople;
            int sum = SumCargoAmounts(slots);
            if (sum == people)
                return;

            if (sum < people)
            {
                int combineMax = ResolveCombineMax(em, shipEntity, in ship);
                int netId = ResolveShipNetworkId(em, shipEntity);
                ResolveEscortShipExtents(em, shipEntity, in shipTransform, out float ax, out float az);
                TryAppendSlot(
                    ref slots, people - sum, in shipTransform, ax, az,
                    mapW, mapH, combineMax, netId);
                return;
            }

            ShrinkFromEnd(ref slots, sum - people);
        }

        /// <summary>
        /// Adds people to escorts. Partial loads grow an existing capsule until
        /// <paramref name="combineMax"/> (ship × planet), then a new one starts.
        /// </summary>
        public static void TryAppendEscortSlot(
            EntityManager em,
            Entity shipEntity,
            float amount,
            in LocalTransform shipTransform,
            float mapW,
            float mapH,
            int combineMax,
            int shipNetworkId)
        {
            if (amount <= 0.01f || !em.HasBuffer<PeopleEscortSlot>(shipEntity))
                return;
            var slots = em.GetBuffer<PeopleEscortSlot>(shipEntity);
            ResolveEscortShipExtents(em, shipEntity, in shipTransform, out float ax, out float az);
            TryAppendSlot(
                ref slots, amount, in shipTransform, ax, az,
                mapW, mapH, combineMax, shipNetworkId);
        }

        /// <summary>
        /// Integrates formation follow and ready-at-center. Launched capsules are skipped.
        /// </summary>
        public static void StepFollowSlots(
            EntityManager em,
            Entity shipEntity,
            in LocalTransform shipTransform,
            float dt,
            float mapW,
            float mapH)
        {
            if (!em.HasBuffer<PeopleEscortSlot>(shipEntity))
                return;

            var slots = em.GetBuffer<PeopleEscortSlot>(shipEntity);
            int count = slots.Length;
            if (count <= 0)
                return;

            ResolveEscortShipExtents(em, shipEntity, in shipTransform, out float extX, out float extZ);
            int netId = ResolveShipNetworkId(em, shipEntity);
            float3 shipPos = shipTransform.Position;
            shipPos.y = 0f;
            float3 shipDelta = float3.zero;
            if (em.HasComponent<ShipKinematics>(shipEntity))
            {
                shipDelta = em.GetComponentData<ShipKinematics>(shipEntity).Velocity * dt;
                shipDelta.y = 0f;
            }

            for (int i = 0; i < count; i++)
            {
                var slot = slots[i];
                if (slot.InFlight != 0)
                    continue;

                bool ready = slot.Ready != 0;
                float3 target = ready
                    ? shipPos
                    : PeopleTransportMath.EvaluateEscortSlotPose(
                        shipTransform.Position, shipTransform.Rotation, extX, extZ,
                        i, count, slot.Amount, netId, mapW, mapH);
                float3 pos = slot.Position;
                float3 vel = slot.Velocity;
                bool riding = slot.Riding != 0;
                PeopleTransportMath.IntegrateEscortFollow(
                    ref pos, ref vel, ref riding, target,
                    ready ? shipDelta : float3.zero,
                    slot.Amount, dt, mapW, mapH, ready);
                slot.Position = pos;
                slot.Velocity = vel;
                slot.Riding = riding ? (byte)1 : (byte)0;
                slots[i] = slot;
            }
        }

        /// <summary>Latches the unload planet. Does not launch until escorts have gathered.</summary>
        public static void BeginLandingWave(
            EntityManager em,
            Entity shipEntity,
            in ShipState ship,
            in LocalTransform shipTransform,
            int planetId,
            float mapW,
            float mapH)
        {
            if (planetId == 0 || !em.HasBuffer<PeopleEscortSlot>(shipEntity))
                return;

            SyncSlotAmounts(em, shipEntity, in ship, in shipTransform, mapW, mapH);
            if (!em.HasComponent<PeopleEscortLandingState>(shipEntity))
                return;

            var landing = em.GetComponentData<PeopleEscortLandingState>(shipEntity);
            if (landing.PlanetId == planetId)
                return;
            landing.PlanetId = planetId;
            em.SetComponentData(shipEntity, landing);
        }

        /// <summary>
        /// Calls one in-position follow capsule to ship center. Enroute followers are skipped.
        /// Only one ready preload at a time.
        /// </summary>
        public static bool TryPromoteReadySlot(
            ref DynamicBuffer<PeopleEscortSlot> slots,
            in LocalTransform shipTransform,
            float extX,
            float extZ,
            int shipNetworkId,
            float mapW,
            float mapH)
        {
            int count = slots.Length;
            for (int i = 0; i < count; i++)
            {
                if (slots[i].Ready != 0 && slots[i].InFlight == 0)
                    return false;
            }

            for (int i = count - 1; i >= 0; i--)
            {
                var slot = slots[i];
                if (slot.InFlight != 0 || slot.Ready != 0)
                    continue;
                if (!PeopleTransportMath.IsEscortGatheredAtShip(
                        slot.Position, shipTransform.Position, shipTransform.Rotation,
                        extX, extZ, i, count, slot.Amount, shipNetworkId, mapW, mapH))
                    continue;

                slot.Ready = 1;
                slot.Riding = 0;
                slot.CruiseSpeed = 0f;
                slots[i] = slot;
                return true;
            }

            return false;
        }

        /// <summary>True when the preload capsule is stopped on ship center.</summary>
        public static bool IsReadySlotParkedAtShipCenter(
            in DynamicBuffer<PeopleEscortSlot> slots,
            float3 shipPos,
            float hullRadius,
            float mapW,
            float mapH)
        {
            shipPos.y = 0f;
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot.InFlight != 0 || slot.Ready == 0)
                    continue;
                return PeopleTransportMath.IsEscortParkedAtShipCenter(
                    slot.Position, slot.Velocity, shipPos, hullRadius, mapW, mapH);
            }

            return false;
        }

        /// <summary>
        /// Launches the ready capsule if it has reached ship center. One-way — does not come back.
        /// </summary>
        public static bool TryLaunchReadySlot(
            ref DynamicBuffer<PeopleEscortSlot> slots,
            in LocalTransform shipTransform,
            float hullRadius,
            int planetId,
            float3 planetPos,
            float planetSize,
            float mapW,
            float mapH,
            out int launchedAmount)
        {
            launchedAmount = 0;
            if (planetId == 0)
                return false;

            float hull = hullRadius;
            float3 shipPos = shipTransform.Position;
            shipPos.y = 0f;
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot.InFlight != 0 || slot.Ready == 0)
                    continue;
                if (!PeopleTransportMath.IsEscortParkedAtShipCenter(
                        slot.Position, slot.Velocity, shipPos, hull, mapW, mapH))
                    return false;

                float3 pos = slot.Position;
                pos.y = 0f;
                float3 target = PeopleTransportMath.GetPlanetSurfaceToward(
                    planetPos, planetSize, pos, mapW, mapH);
                float cruise = PeopleTransportMath.GetEscortCruise(
                    slot.Amount,
                    PeopleTransportMath.ComputeCruiseSpeed(pos, target, isLoad: false, mapW, mapH));
                slot.Position = pos;
                slot.SpawnPosition = pos;
                slot.CruiseSpeed = cruise;
                slot.Velocity = float3.zero;
                slot.InFlight = 1;
                slot.Ready = 0;
                slot.TargetPlanetId = planetId;
                slot.FlightElapsed = 0f;
                slots[i] = slot;
                launchedAmount = math.max(0, (int)slot.Amount);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Stops new ready / launches. Launched one-way capsules keep flying.
        /// Un-launched ready slots return to follow.
        /// </summary>
        public static void AbortLandingWave(EntityManager em, Entity shipEntity)
        {
            if (em.HasBuffer<PeopleEscortSlot>(shipEntity))
            {
                var slots = em.GetBuffer<PeopleEscortSlot>(shipEntity);
                for (int i = 0; i < slots.Length; i++)
                {
                    var slot = slots[i];
                    if (slot.InFlight != 0)
                        continue;
                    if (slot.Ready == 0)
                        continue;
                    slot.Ready = 0;
                    slot.Riding = 0;
                    slot.CruiseSpeed = 0f;
                    slots[i] = slot;
                }
            }

            if (!em.HasComponent<PeopleEscortLandingState>(shipEntity))
                return;
            var landing = em.GetComponentData<PeopleEscortLandingState>(shipEntity);
            if (landing.PlanetId == 0)
                return;
            landing.PlanetId = 0;
            em.SetComponentData(shipEntity, landing);
        }

        /// <summary>
        /// Integrates one in-flight slot toward the planet. Returns true when it may apply people.
        /// </summary>
        public static bool StepLandingSlot(
            ref PeopleEscortSlot slot,
            float dt,
            float3 planetPos,
            float planetSize,
            float mapW,
            float mapH)
        {
            slot.FlightElapsed += dt;
            float3 target = PeopleTransportMath.GetPlanetSurfaceToward(
                planetPos, planetSize, slot.Position, mapW, mapH);
            float cruise = slot.CruiseSpeed;
            if (cruise < 0.12f)
            {
                cruise = PeopleTransportMath.GetEscortCruise(
                    slot.Amount,
                    PeopleTransportMath.ComputeCruiseSpeed(
                        slot.Position, target, isLoad: false, mapW, mapH));
                slot.CruiseSpeed = cruise;
            }

            slot.Velocity = PeopleTransportMath.SteerEscortVelocity(
                slot.Position, target, slot.Velocity, dt, cruise, slot.Amount, mapW, mapH);
            float3 pos = slot.Position + slot.Velocity * dt;
            pos.y = 0f;
            if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);
            slot.Position = pos;

            return PeopleTransportMath.CanCompleteEscortUnload(
                slot.Position, slot.SpawnPosition, planetPos, planetSize, slot.FlightElapsed, mapW, mapH);
        }

        /// <summary>
        /// Applies bullet damage to one escort slot. Cargo kills debit
        /// <see cref="ShipState.CurrentPeople"/>. In-flight kills do not (already left the ship).
        /// Dead in-flight slots stay one tick at 0 HP so <see cref="ShipEscortVitals"/> can show it.
        /// </summary>
        public static int ApplyDamageToSlot(
            EntityManager em,
            Entity shipEntity,
            int slotIndex,
            float damage)
        {
            if (!em.HasBuffer<PeopleEscortSlot>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity))
                return 0;

            var slots = em.GetBuffer<PeopleEscortSlot>(shipEntity);
            if (slotIndex < 0 || slotIndex >= slots.Length)
                return 0;

            var slot = slots[slotIndex];
            slot.Health -= math.max(0f, damage);
            if (slot.Health > 0f)
            {
                slots[slotIndex] = slot;
                return 0;
            }

            int lost = math.max(0, (int)slot.Amount);
            if (slot.InFlight != 0)
            {
                slot.Health = 0f;
                slots[slotIndex] = slot;
                return 0;
            }

            slots.RemoveAt(slotIndex);
            if (lost <= 0)
                return 0;

            var ship = em.GetComponentData<ShipState>(shipEntity);
            ship.CurrentPeople = math.max(0, ship.CurrentPeople - lost);
            em.SetComponentData(shipEntity, ship);
            return lost;
        }

        /// <summary>Writes packed escort HP for client nameplates. Call after slot mutations.</summary>
        public static void WriteEscortVitals(EntityManager em, Entity shipEntity)
        {
            if (!em.HasComponent<ShipEscortVitals>(shipEntity) ||
                !em.HasBuffer<PeopleEscortSlot>(shipEntity))
                return;

            var slots = em.GetBuffer<PeopleEscortSlot>(shipEntity);
            var vitals = new ShipEscortVitals();
            int n = math.min(ShipEscortVitals.MaxSlots, slots.Length);
            vitals.Count = (byte)n;
            ulong health = 0;
            ulong amount = 0;
            byte flying = 0;
            for (int i = 0; i < n; i++)
            {
                var slot = slots[i];
                byte hp = (byte)math.clamp((int)math.round(math.max(0f, slot.Health)), 0, 255);
                byte amt = (byte)math.clamp((int)math.round(math.max(0f, slot.Amount)), 0, 255);
                health |= ((ulong)hp) << (i * 8);
                amount |= ((ulong)amt) << (i * 8);
                if (slot.InFlight != 0)
                    flying |= (byte)(1 << i);
            }

            vitals.HealthPacked = health;
            vitals.AmountPacked = amount;
            vitals.InFlightMask = flying;
            em.SetComponentData(shipEntity, vitals);
        }

        static int SumCargoAmounts(DynamicBuffer<PeopleEscortSlot> slots)
        {
            int sum = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].InFlight != 0)
                    continue;
                sum += math.max(0, (int)slots[i].Amount);
            }

            return sum;
        }

        static int ResolveCombineMax(EntityManager em, Entity shipEntity, in ShipState ship)
        {
            int fallback = PeopleTransportMath.GetTransferChunk(math.max(1, ship.ShipLevel), 1);
            if (!em.HasComponent<ShipPeopleTransferState>(shipEntity))
                return fallback;
            int stored = em.GetComponentData<ShipPeopleTransferState>(shipEntity).LastLoadCombineMax;
            return stored > 0 ? stored : fallback;
        }

        static int ResolveShipNetworkId(EntityManager em, Entity shipEntity)
        {
            if (em.HasComponent<GhostOwner>(shipEntity))
                return em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            return 0;
        }

        static void TryAppendSlot(
            ref DynamicBuffer<PeopleEscortSlot> slots,
            float amount,
            in LocalTransform shipTransform,
            float extX,
            float extZ,
            float mapW,
            float mapH,
            int combineMax,
            int shipNetworkId)
        {
            if (amount <= 0.01f)
                return;

            combineMax = math.max(1, combineMax);
            amount = FillExistingSlots(ref slots, amount, combineMax);
            if (amount <= 0.01f)
                return;

            if (slots.Length >= PeopleTransportMath.MaxEscortVisualSlots)
            {
                int last = slots.Length - 1;
                var merge = slots[last];
                merge.Amount += amount;
                merge.Health = PeopleTransportMath.ComputeMaxHealth(merge.Amount);
                slots[last] = merge;
                return;
            }

            float3 pos = PeopleTransportMath.EvaluateEscortSlotPose(
                shipTransform.Position, shipTransform.Rotation, extX, extZ,
                slots.Length, slots.Length + 1, amount, shipNetworkId, mapW, mapH);
            slots.Add(new PeopleEscortSlot
            {
                Position = pos,
                Velocity = float3.zero,
                Amount = amount,
                Health = PeopleTransportMath.ComputeMaxHealth(amount),
                CruiseSpeed = 0f,
                SpawnPosition = pos,
                InFlight = 0,
                Ready = 0,
                TargetPlanetId = 0,
                FlightElapsed = 0f,
                Riding = 0,
            });
        }

        static float FillExistingSlots(
            ref DynamicBuffer<PeopleEscortSlot> slots,
            float amount,
            int combineMax)
        {
            for (int i = slots.Length - 1; i >= 0 && amount > 0.01f; i--)
            {
                var slot = slots[i];
                if (slot.InFlight != 0)
                    continue;
                float room = combineMax - slot.Amount;
                if (room <= 0.01f)
                    continue;
                float add = math.min(amount, room);
                slot.Amount += add;
                slot.Health = PeopleTransportMath.ComputeMaxHealth(slot.Amount);
                slots[i] = slot;
                amount -= add;
            }

            return amount;
        }

        static void ClearNonLaunchedSlots(ref DynamicBuffer<PeopleEscortSlot> slots)
        {
            for (int i = slots.Length - 1; i >= 0; i--)
            {
                if (slots[i].InFlight != 0)
                    continue;
                slots.RemoveAt(i);
            }
        }

        static void ShrinkFromEnd(ref DynamicBuffer<PeopleEscortSlot> slots, int deficit)
        {
            for (int i = slots.Length - 1; i >= 0 && deficit > 0; i--)
            {
                var slot = slots[i];
                if (slot.InFlight != 0)
                    continue;
                int have = math.max(0, (int)slot.Amount);
                if (have <= deficit)
                {
                    deficit -= have;
                    slots.RemoveAt(i);
                    continue;
                }

                slot.Amount = have - deficit;
                slot.Health = PeopleTransportMath.ComputeMaxHealth(slot.Amount);
                slots[i] = slot;
                deficit = 0;
            }
        }
    }
}
