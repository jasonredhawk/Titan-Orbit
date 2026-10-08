using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One mine the client should draw even though the owner ship ghost is off-screen.
    /// <see cref="MineVisualDriver"/> reads this list. A hide is queued so the mesh goes away
    /// without playing an explosion.
    /// </summary>
    public struct MineViewClientRow
    {
        /// <summary>Fields the mesh and fuse bar need.</summary>
        public DeployedMineElement Mine;

        /// <summary>1 while the server still says this mine is in view.</summary>
        public byte Active;
    }

    /// <summary>
    /// [NETCODE] Client-side list of mines replicated by <see cref="MineVisibleRpc"/> instead of
    /// the ship ghost. Fixed storage so a packet does not allocate.
    /// </summary>
    public static class MineViewClientCache
    {
        /// <summary>How many off-screen-owner mines one screen can hold.</summary>
        public const int Capacity = 64;

        /// <summary>Live rows.</summary>
        public static readonly MineViewClientRow[] Rows = new MineViewClientRow[Capacity];

        /// <summary>How many <see cref="Rows"/> are active.</summary>
        public static int Count;

        /// <summary>Hides waiting for the visual driver. Not an explosion.</summary>
        public static readonly int[] QuietOwner = new int[Capacity];

        /// <summary>Sequence paired with <see cref="QuietOwner"/>.</summary>
        public static readonly uint[] QuietSequence = new uint[Capacity];

        /// <summary>Place time paired with <see cref="QuietOwner"/>.</summary>
        public static readonly double[] QuietPlace = new double[Capacity];

        /// <summary>How many quiet hides are waiting.</summary>
        public static int QuietCount;

        /// <summary>[UNITY] Domain reload off: drop the previous match.</summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clear();

        /// <summary>Drops every row and quiet hide.</summary>
        public static void Clear()
        {
            Count = 0;
            QuietCount = 0;
        }

        /// <summary>Inserts or refreshes one visible mine.</summary>
        public static void Upsert(in DeployedMineElement mine)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Rows[i].Mine.OwnerNetworkId != mine.OwnerNetworkId || Rows[i].Mine.Sequence != mine.Sequence)
                    continue;
                if (math.abs(Rows[i].Mine.PlaceTime - mine.PlaceTime) > 0.0001)
                    continue;
                Rows[i].Mine = mine;
                Rows[i].Active = 1;
                return;
            }

            if (Count >= Capacity)
                return;

            Rows[Count++] = new MineViewClientRow { Mine = mine, Active = 1 };
        }

        /// <summary>Removes a mine and asks the visual driver not to play a blast.</summary>
        public static void Hide(int ownerNetworkId, uint sequence, double placeTime)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Rows[i].Mine.OwnerNetworkId != ownerNetworkId || Rows[i].Mine.Sequence != sequence)
                    continue;
                if (math.abs(Rows[i].Mine.PlaceTime - placeTime) > 0.0001)
                    continue;

                Rows[i] = Rows[Count - 1];
                Count--;
                break;
            }

            if (QuietCount >= Capacity)
                return;
            QuietOwner[QuietCount] = ownerNetworkId;
            QuietSequence[QuietCount] = sequence;
            QuietPlace[QuietCount] = placeTime;
            QuietCount++;
        }

        /// <summary>
        /// True once when this mine was hidden. The caller should delete the mesh without a blast.
        /// If the ship ghost still has the mine, call this only when the mesh is not alive from the ghost.
        /// </summary>
        public static bool ConsumeQuiet(int ownerNetworkId, uint sequence, double placeTime)
        {
            for (int i = 0; i < QuietCount; i++)
            {
                if (QuietOwner[i] != ownerNetworkId || QuietSequence[i] != sequence)
                    continue;
                if (math.abs(QuietPlace[i] - placeTime) > 0.0001)
                    continue;

                QuietOwner[i] = QuietOwner[QuietCount - 1];
                QuietSequence[i] = QuietSequence[QuietCount - 1];
                QuietPlace[i] = QuietPlace[QuietCount - 1];
                QuietCount--;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// [NETCODE] Client: applies <see cref="MineVisibleRpc"/> onto <see cref="MineViewClientCache"/>.
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MineVisibleClientSystem : ISystem
    {
        EntityQuery _query;

        /// <summary>Caches the RPC query so idle frames allocate nothing.</summary>
        public void OnCreate(ref SystemState state)
        {
            _query = state.GetEntityQuery(ComponentType.ReadOnly<MineVisibleRpc>());
        }

        /// <summary>Upserts or hides, then destroys the RPC entity.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_query.IsEmptyIgnoreFilter)
                return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, entity) in SystemAPI.Query<RefRO<MineVisibleRpc>>().WithEntityAccess())
            {
                MineVisibleRpc msg = rpc.ValueRO;
                if (msg.Visible == 0)
                {
                    MineViewClientCache.Hide(msg.OwnerNetworkId, msg.Sequence, msg.PlaceTime);
                }
                else
                {
                    MineViewClientCache.Upsert(new DeployedMineElement
                    {
                        Position = msg.Position,
                        OwnerTeam = msg.OwnerTeam,
                        OwnerNetworkId = msg.OwnerNetworkId,
                        ItemLevel = msg.ItemLevel,
                        Sequence = msg.Sequence,
                        ExpireTime = msg.ExpireTime,
                        PlaceTime = msg.PlaceTime,
                        MaxHealth = msg.MaxHealth,
                        Damage = msg.Damage,
                        VisualScale = msg.VisualScale,
                        ExplosionVfxScale = msg.ExplosionVfxScale,
                    });
                }

                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
