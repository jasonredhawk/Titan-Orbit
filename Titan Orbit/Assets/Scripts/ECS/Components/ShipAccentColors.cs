using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Player-chosen Colorize accents on a ship ghost. Color1 stays on the
    /// team material (Red / Blue / Green / Orange / Purple). These fields override
    /// Color2, Color3, and the three emission slots so teammates stay readable.
    /// <para>
    /// Must be baked on <see cref="Authoring.StarshipGhostAuthoring"/>. A runtime-only
    /// <c>AddComponent</c> does not register GhostFields, so remotes would never see
    /// the paint — the same trap as <see cref="ShipLoadoutState"/>.
    /// </para>
    /// Packed RGBAs are 4 bytes each. NetCode only dirties the snapshot when the
    /// player changes a well. Adding GhostFields changes the ghost layout — rebuild
    /// the Linux headless server together with the client.
    /// </summary>
    public struct ShipAccentColors : IComponentData
    {
        /// <summary>0 = use the Colorize material's baked Color2 / Color3 / Emissions.</summary>
        [GhostField] public byte HasCustom;

        /// <summary>Packed RGBA for shader <c>_Color2</c> / <c>_SecondaryColor</c>.</summary>
        [GhostField] public uint Color2Packed;

        /// <summary>Packed RGBA for shader <c>_Color3</c> / <c>_TertiaryColor</c>.</summary>
        [GhostField] public uint Color3Packed;

        /// <summary>Packed RGBA for shader <c>_Emission1</c>.</summary>
        [GhostField] public uint EmissionPacked;

        /// <summary>Packed RGBA for shader <c>_Emission2</c>.</summary>
        [GhostField] public uint Emission2Packed;

        /// <summary>Packed RGBA for shader <c>_Emission3</c>.</summary>
        [GhostField] public uint Emission3Packed;

        /// <summary>True when the player has painted accents (or loaded a saved custom palette).</summary>
        public bool IsCustom => HasCustom != 0;

        /// <summary>Color2 as a Unity <see cref="Color32"/>. Alpha is forced opaque for UI wells.</summary>
        public Color32 Color2 => Unpack(Color2Packed);

        /// <summary>Color3 as a Unity <see cref="Color32"/>.</summary>
        public Color32 Color3 => Unpack(Color3Packed);

        /// <summary>Emission1 as a Unity <see cref="Color32"/>.</summary>
        public Color32 Emission => Unpack(EmissionPacked);

        /// <summary>Emission2 as a Unity <see cref="Color32"/>.</summary>
        public Color32 Emission2 => Unpack(Emission2Packed);

        /// <summary>Emission3 as a Unity <see cref="Color32"/>.</summary>
        public Color32 Emission3 => Unpack(Emission3Packed);

        /// <summary>Empty palette — presentation leaves the shared Colorize assets alone.</summary>
        public static ShipAccentColors Default => default;

        /// <summary>Builds a custom palette from the five paintable Colorize slots.</summary>
        public static ShipAccentColors FromCustom(
            Color32 color2,
            Color32 color3,
            Color32 emission1,
            Color32 emission2,
            Color32 emission3)
        {
            return new ShipAccentColors
            {
                HasCustom = 1,
                Color2Packed = Pack(color2),
                Color3Packed = Pack(color3),
                EmissionPacked = Pack(emission1),
                Emission2Packed = Pack(emission2),
                Emission3Packed = Pack(emission3),
            };
        }

        /// <summary>
        /// Packs RGB into a little-endian uint. Alpha is always 255 — Colorize assets
        /// store <c>a = 0</c>, which would make UI wells invisible if we copied it.
        /// </summary>
        public static uint Pack(Color32 color)
        {
            return (uint)color.r
                | ((uint)color.g << 8)
                | ((uint)color.b << 16)
                | (255u << 24);
        }

        /// <summary>Unpacks a GhostField uint back to opaque RGBA bytes.</summary>
        public static Color32 Unpack(uint packed)
        {
            return new Color32(
                (byte)packed,
                (byte)(packed >> 8),
                (byte)(packed >> 16),
                255);
        }

        /// <summary>
        /// One integer for "did this ship's paint change?" checks on the visualizer.
        /// Includes the flag so Reset (HasCustom 0) is distinct from a black custom palette.
        /// </summary>
        public int CacheKey
        {
            get
            {
                unchecked
                {
                    int hash = HasCustom;
                    hash = (hash * 397) ^ (int)Color2Packed;
                    hash = (hash * 397) ^ (int)Color3Packed;
                    hash = (hash * 397) ^ (int)EmissionPacked;
                    hash = (hash * 397) ^ (int)Emission2Packed;
                    hash = (hash * 397) ^ (int)Emission3Packed;
                    return hash;
                }
            }
        }
    }
}
