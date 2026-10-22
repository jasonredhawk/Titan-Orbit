using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Remembers the shared Colorize base materials on a ship proxy so retints
    /// can re-instance from the assets, not from a previous cached copy.
    /// Color1 comes from <see cref="Data.TeamColor1Palette"/> for <see cref="Team"/>.
    /// <para>
    /// Without this, a second paint would <c>SetColor</c> on an already-tinted
    /// instance and we could not return to the baked palette on Reset.
    /// </para>
    /// </summary>
    public sealed class ShipAccentTintState : MonoBehaviour
    {
        /// <summary>Per-renderer shared Colorize assets captured after the base swap.</summary>
        public Renderer[] Renderers;

        /// <summary>Parallel to <see cref="Renderers"/> — the Colorize assets, never cache instances.</summary>
        public Material[][] BaseSharedMaterials;

        /// <summary>
        /// Deep copies of <see cref="BaseSharedMaterials"/> taken before any accent tint.
        /// Default hull paint restores from these so a later recapture of <c>_Accent</c>
        /// instances cannot become the factory colors.
        /// </summary>
        public Material[][] PristineMaterials;

        /// <summary>Team whose Color1 is stamped from the palette. Preview can change this.</summary>
        public TeamId Team;

        /// <summary>Last applied paint key (team Color1 + accents), or 0 before the first tint.</summary>
        public int LastAccentKey;

        void OnDestroy()
        {
            DestroyPristine(PristineMaterials);
            PristineMaterials = null;
        }

        /// <summary>Releases runtime bake copies. Shared Colorize assets are left alone.</summary>
        public static void DestroyPristine(Material[][] pristine)
        {
            if (pristine == null)
                return;
            for (int i = 0; i < pristine.Length; i++)
            {
                Material[] row = pristine[i];
                if (row == null)
                    continue;
                for (int s = 0; s < row.Length; s++)
                {
                    if (row[s] != null)
                        Destroy(row[s]);
                }
            }
        }
    }
}
