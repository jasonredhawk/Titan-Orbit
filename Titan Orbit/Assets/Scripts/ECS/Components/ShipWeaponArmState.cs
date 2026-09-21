using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Ghosted on/off mask for every weapon mount on a ship. The player can mute one
    /// barrel or a whole class (all missiles, all lasers) from the in-flight arsenal HUD.
    /// Fire planners skip muted mounts so they do not spend energy or spawn shots.
    /// <para>
    /// [NETCODE] Must be baked on the starship ghost (<see cref="Authoring.StarshipGhostAuthoring"/>).
    /// Runtime <c>AddComponent</c> covers older SubScenes locally, but GhostFields do not
    /// replicate until the prefab is rebaked. The mount pose buffer itself is not ghosted —
    /// this compact mask is the networked enable state.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Four 32-bit words cover 128 barrels (MEGA hulls can exceed 40).
    /// New ships start <see cref="AllOn"/> so existing loadouts keep firing until the
    /// player clicks the HUD. A player who mutes every barrel writes all zeros — that
    /// is a legal “hold fire” loadout, not an uninitialized default.
    /// </para>
    /// Paired with <see cref="ShipWeaponArmSystem"/> (predicted apply) and
    /// <see cref="ShipWeaponFireLogic"/> (skip muted barrels).
    /// </summary>
    public struct ShipWeaponArmState : IComponentData
    {
        /// <summary>How many barrels this mask can name (4 × 32 bits).</summary>
        public const int MaxTrackedMounts = 128;

        /// <summary>[NETCODE] HUD click sets one mount. <see cref="ShipInput.WeaponArmIndex"/> is the buffer index.</summary>
        public const byte ModeMount = 0;

        /// <summary>[NETCODE] HUD click sets every mount of one <see cref="ShipWeaponKind"/>.</summary>
        public const byte ModeKind = 1;

        /// <summary>Mounts 0–31. Bit i = 1 means that barrel may fire.</summary>
        [GhostField] public uint Mask0;

        /// <summary>Mounts 32–63.</summary>
        [GhostField] public uint Mask1;

        /// <summary>Mounts 64–95.</summary>
        [GhostField] public uint Mask2;

        /// <summary>Mounts 96–127.</summary>
        [GhostField] public uint Mask3;

        /// <summary>
        /// Bake / ensure default — every bit on. Missing component on an old ghost
        /// should be treated the same way (see <see cref="Resolve"/>).
        /// </summary>
        public static ShipWeaponArmState AllOn => new ShipWeaponArmState
        {
            Mask0 = uint.MaxValue,
            Mask1 = uint.MaxValue,
            Mask2 = uint.MaxValue,
            Mask3 = uint.MaxValue,
        };

        /// <summary>
        /// Reads the ghosted mask, or <see cref="AllOn"/> when the component is missing
        /// (older predicted ghosts before rebake).
        /// </summary>
        /// <param name="em">World that owns <paramref name="ship"/>.</param>
        /// <param name="ship">Ship entity (local predicted or server authority).</param>
        public static ShipWeaponArmState Resolve(EntityManager em, Entity ship)
        {
            if (ship == Entity.Null || !em.HasComponent<ShipWeaponArmState>(ship))
                return AllOn;
            return em.GetComponentData<ShipWeaponArmState>(ship);
        }

        /// <summary>
        /// True when barrel <paramref name="mountIndex"/> may spend energy / fire.
        /// Indices past <see cref="MaxTrackedMounts"/> stay armed so an oversized
        /// MEGA does not mute mystery barrels.
        /// </summary>
        public static bool IsArmed(in ShipWeaponArmState arm, int mountIndex)
        {
            if (mountIndex < 0)
                return false;
            if (mountIndex >= MaxTrackedMounts)
                return true;

            int word = mountIndex >> 5;
            int bit = mountIndex & 31;
            uint mask = ReadWord(in arm, word);
            return (mask & (1u << bit)) != 0;
        }

        /// <summary>Turns one barrel on or off. No-op when the index is out of the 128-bit window.</summary>
        public static void SetMount(ref ShipWeaponArmState arm, int mountIndex, bool enabled)
        {
            if (mountIndex < 0 || mountIndex >= MaxTrackedMounts)
                return;

            int word = mountIndex >> 5;
            int bit = mountIndex & 31;
            uint flag = 1u << bit;
            uint current = ReadWord(in arm, word);
            uint next = enabled ? (current | flag) : (current & ~flag);
            WriteWord(ref arm, word, next);
        }

        /// <summary>
        /// Turns every mount whose <see cref="ShipWeaponMountElement.WeaponKind"/> matches
        /// <paramref name="kind"/> on or off. Regular family barrels are kind 0 (gun).
        /// </summary>
        public static void SetKind(
            ref ShipWeaponArmState arm,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            byte kind,
            bool enabled)
        {
            if (!mounts.IsCreated)
                return;

            int count = mounts.Length;
            for (int i = 0; i < count; i++)
            {
                if (mounts[i].WeaponKind != kind)
                    continue;
                SetMount(ref arm, i, enabled);
            }
        }

        /// <summary>True when at least one mount of <paramref name="kind"/> is armed.</summary>
        public static bool IsAnyKindArmed(
            in ShipWeaponArmState arm,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            byte kind)
        {
            if (!mounts.IsCreated)
                return false;

            int count = mounts.Length;
            for (int i = 0; i < count; i++)
            {
                if (mounts[i].WeaponKind != kind)
                    continue;
                if (IsArmed(in arm, i))
                    return true;
            }

            return false;
        }

        /// <summary>How many mounts of <paramref name="kind"/> sit on this hull.</summary>
        public static int CountKind(DynamicBuffer<ShipWeaponMountElement> mounts, byte kind)
        {
            if (!mounts.IsCreated)
                return 0;

            int n = 0;
            int count = mounts.Length;
            for (int i = 0; i < count; i++)
            {
                if (mounts[i].WeaponKind == kind)
                    n++;
            }

            return n;
        }

        /// <summary>Reads one 32-bit word of the mask (0–3).</summary>
        static uint ReadWord(in ShipWeaponArmState arm, int word)
        {
            switch (word)
            {
                case 0: return arm.Mask0;
                case 1: return arm.Mask1;
                case 2: return arm.Mask2;
                default: return arm.Mask3;
            }
        }

        /// <summary>Writes one 32-bit word of the mask (0–3).</summary>
        static void WriteWord(ref ShipWeaponArmState arm, int word, uint value)
        {
            switch (word)
            {
                case 0:
                    arm.Mask0 = value;
                    return;
                case 1:
                    arm.Mask1 = value;
                    return;
                case 2:
                    arm.Mask2 = value;
                    return;
                default:
                    arm.Mask3 = value;
                    return;
            }
        }
    }
}
