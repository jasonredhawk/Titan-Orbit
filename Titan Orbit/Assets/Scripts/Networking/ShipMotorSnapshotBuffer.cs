using UnityEngine;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Server motor sample used for remote-player snapshot interpolation (Starblast-style render buffer).
    /// </summary>
    public struct ShipMotorSnapshot
    {
        public uint Tick;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public bool Thrust;
        public Vector2 AimWorldXZ;
    }

    /// <summary>
    /// Ring buffer of authoritative motor snapshots keyed by server tick.
    /// </summary>
    public sealed class ShipMotorSnapshotBuffer
    {
        private const int Capacity = 48;
        private readonly ShipMotorSnapshot[] _ring = new ShipMotorSnapshot[Capacity];
        private int _count;
        private int _start;

        public int Count => _count;
        public uint LatestTick => _count > 0 ? _ring[(_start + _count - 1) % Capacity].Tick : 0;

        public void Clear()
        {
            _count = 0;
            _start = 0;
        }

        public void Push(in ShipMotorSnapshot snapshot)
        {
            if (_count > 0)
            {
                uint latest = LatestTick;
                if (snapshot.Tick < latest)
                    return;
                if (snapshot.Tick == latest)
                {
                    _ring[(_start + _count - 1) % Capacity] = snapshot;
                    return;
                }
            }

            if (_count < Capacity)
            {
                _ring[(_start + _count) % Capacity] = snapshot;
                _count++;
            }
            else
            {
                _start = (_start + 1) % Capacity;
                _ring[(_start + _count - 1) % Capacity] = snapshot;
            }
        }

        /// <summary>
        /// Sample pose at fractional server tick. Extrapolates briefly when render time is ahead of newest sample.
        /// </summary>
        public bool TrySample(
            float renderTickF,
            float fixedDeltaTime,
            float maxExtrapolationSeconds,
            out Vector3 position,
            out Quaternion rotation,
            out Vector3 velocity)
        {
            position = default;
            rotation = Quaternion.identity;
            velocity = default;

            if (_count <= 0 || fixedDeltaTime <= 0f)
                return false;

            if (_count == 1)
            {
                ShipMotorSnapshot only = _ring[_start];
                float extraSec = Mathf.Max(0f, (renderTickF - only.Tick) * fixedDeltaTime);
                extraSec = Mathf.Min(extraSec, maxExtrapolationSeconds);
                position = only.Position + only.Velocity * extraSec;
                rotation = only.Rotation;
                velocity = only.Velocity;
                return true;
            }

            ShipMotorSnapshot oldest = _ring[_start];
            if (renderTickF <= oldest.Tick)
            {
                position = oldest.Position;
                rotation = oldest.Rotation;
                velocity = oldest.Velocity;
                return true;
            }

            ShipMotorSnapshot newest = _ring[(_start + _count - 1) % Capacity];
            if (renderTickF >= newest.Tick)
            {
                float extraSec = (renderTickF - newest.Tick) * fixedDeltaTime;
                extraSec = Mathf.Min(Mathf.Max(0f, extraSec), maxExtrapolationSeconds);
                position = newest.Position + newest.Velocity * extraSec;
                rotation = newest.Rotation;
                velocity = newest.Velocity;
                return true;
            }

            for (int i = 0; i < _count - 1; i++)
            {
                ShipMotorSnapshot a = _ring[(_start + i) % Capacity];
                ShipMotorSnapshot b = _ring[(_start + i + 1) % Capacity];
                if (renderTickF < a.Tick || renderTickF > b.Tick)
                    continue;

                float span = b.Tick - a.Tick;
                float t = span > 0.0001f ? Mathf.Clamp01((renderTickF - a.Tick) / span) : 0f;
                position = Vector3.Lerp(a.Position, b.Position, t);
                rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                velocity = Vector3.Lerp(a.Velocity, b.Velocity, t);
                return true;
            }

            position = newest.Position;
            rotation = newest.Rotation;
            velocity = newest.Velocity;
            return true;
        }
    }
}
