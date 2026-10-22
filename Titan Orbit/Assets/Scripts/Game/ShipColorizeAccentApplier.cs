using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Applies Colorize colors on ship proxies without MaterialPropertyBlock
    /// (WebGL often draws nothing for URP + MPB).
    /// Color1 always comes from <see cref="TeamColor1Palette"/> — players cannot change it.
    /// Color2 / Color3 / Emission1–3 come from <see cref="ShipAccentColors"/> when custom.
    /// <para>
    /// Instances are cached by (base material, team Color1, packed accents) so two
    /// ships that share a palette share one copy. Apply happens on spawn / paint
    /// change — never every frame.
    /// </para>
    /// </summary>
    public static class ShipColorizeAccentApplier
    {
        static readonly int Color1Id = Shader.PropertyToID("_Color1");
        static readonly int Color2Id = Shader.PropertyToID("_Color2");
        static readonly int Color3Id = Shader.PropertyToID("_Color3");
        static readonly int SecondaryId = Shader.PropertyToID("_SecondaryColor");
        static readonly int TertiaryId = Shader.PropertyToID("_TertiaryColor");
        static readonly int TretiaryId = Shader.PropertyToID("_TretiaryColor");
        static readonly int Emission1Id = Shader.PropertyToID("_Emission1");
        static readonly int Emission2Id = Shader.PropertyToID("_Emission2");
        static readonly int Emission3Id = Shader.PropertyToID("_Emission3");
        static readonly int GlowId = Shader.PropertyToID("_GlowColor");
        static readonly int HighlightId = Shader.PropertyToID("_HighlightColor");

        static readonly Dictionary<AccentCacheKey, Material> Cache =
            new Dictionary<AccentCacheKey, Material>(32);

        struct AccentCacheKey : System.IEquatable<AccentCacheKey>
        {
            public int BaseId;
            public uint Color1;
            public uint Color2;
            public uint Color3;
            public uint Emission;
            public uint Emission2;
            public uint Emission3;
            public byte HasCustom;

            public bool Equals(AccentCacheKey other) =>
                BaseId == other.BaseId
                && Color1 == other.Color1
                && Color2 == other.Color2
                && Color3 == other.Color3
                && Emission == other.Emission
                && Emission2 == other.Emission2
                && Emission3 == other.Emission3
                && HasCustom == other.HasCustom;

            public override bool Equals(object obj) => obj is AccentCacheKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = BaseId;
                    hash = (hash * 397) ^ (int)Color1;
                    hash = (hash * 397) ^ HasCustom;
                    hash = (hash * 397) ^ (int)Color2;
                    hash = (hash * 397) ^ (int)Color3;
                    hash = (hash * 397) ^ (int)Emission;
                    hash = (hash * 397) ^ (int)Emission2;
                    hash = (hash * 397) ^ (int)Emission3;
                    return hash;
                }
            }
        }

        /// <summary>[UNITY] Domain reload / Play Mode: drop cached instances so we do not leak.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Cache.Clear();
        }

        /// <summary>
        /// Snapshots the current shared Colorize assets, then stamps team Color1
        /// and optional player accents onto cached instances.
        /// </summary>
        public static void CaptureBaseAndApply(GameObject root, in ShipAccentColors accents, TeamId team)
        {
            if (root == null)
                return;

            var state = root.GetComponent<ShipAccentTintState>();
            if (state == null)
                state = root.AddComponent<ShipAccentTintState>();
            state.Team = team == TeamId.None ? TeamId.TeamA : team;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Material[][] previousBases = state.BaseSharedMaterials;
            Material[][] previousPristine = state.PristineMaterials;
            state.Renderers = renderers;
            state.BaseSharedMaterials = new Material[renderers.Length][];
            state.PristineMaterials = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;

                Material[] current = renderer.sharedMaterials;
                if (current == null || current.Length == 0)
                    continue;

                var copy = new Material[current.Length];
                var pristine = new Material[current.Length];
                Material[] oldBases = SlotRow(previousBases, i);
                Material[] oldPristine = SlotRow(previousPristine, i);
                for (int s = 0; s < current.Length; s++)
                {
                    // A part-swap recapture runs after paint. The renderer is already
                    // wearing an _Accent instance — keeping that as the "base" makes
                    // Default clone the last skin and skip the color write.
                    Material incoming = current[s];
                    if (IsAccentInstance(incoming)
                        && oldBases != null
                        && s < oldBases.Length
                        && oldBases[s] != null)
                    {
                        copy[s] = oldBases[s];
                        pristine[s] = oldPristine != null && s < oldPristine.Length
                            ? oldPristine[s]
                            : null;
                        continue;
                    }

                    copy[s] = incoming;
                    pristine[s] = incoming != null
                        ? new Material(incoming)
                        {
                            name = incoming.name + "_Baked",
                            hideFlags = HideFlags.HideAndDontSave,
                        }
                        : null;
                }

                state.BaseSharedMaterials[i] = copy;
                state.PristineMaterials[i] = pristine;
            }

            DestroyUnusedPristine(previousPristine, state.PristineMaterials);
            state.LastAccentKey = int.MinValue;
            ApplyFromCapturedBase(state, accents, state.Team);
        }

        /// <summary>
        /// Re-tints an existing proxy from the captured Colorize assets. No destroy/recreate.
        /// </summary>
        public static void ApplyFromCapturedBase(GameObject root, in ShipAccentColors accents, TeamId team)
        {
            if (root == null)
                return;
            var state = root.GetComponent<ShipAccentTintState>();
            if (state == null)
            {
                CaptureBaseAndApply(root, accents, team);
                return;
            }

            state.Team = team == TeamId.None ? TeamId.TeamA : team;
            ApplyFromCapturedBase(state, accents, state.Team);
        }

        static void ApplyFromCapturedBase(ShipAccentTintState state, in ShipAccentColors accents, TeamId team)
        {
            if (state == null || state.Renderers == null || state.BaseSharedMaterials == null)
                return;

            Color color1 = TeamColor1Palette.GetColor1(team);
            uint color1Packed = ShipAccentColors.Pack(Opaque(color1));
            int key = unchecked((int)color1Packed * 397) ^ accents.CacheKey;
            // Default (HasCustom 0) must still run. The key can match a previous
            // factory apply while the renderer is wearing a later skin.
            if (accents.IsCustom && state.LastAccentKey == key && state.LastAccentKey != 0)
                return;
            state.LastAccentKey = key;

            for (int i = 0; i < state.Renderers.Length; i++)
            {
                var renderer = state.Renderers[i];
                Material[] bases = i < state.BaseSharedMaterials.Length
                    ? state.BaseSharedMaterials[i]
                    : null;
                if (renderer == null || bases == null || bases.Length == 0)
                    continue;

                Material[] pristine = SlotRow(state.PristineMaterials, i);
                var replaced = new Material[bases.Length];
                for (int s = 0; s < bases.Length; s++)
                {
                    Material source = bases[s];
                    Material baked = pristine != null && s < pristine.Length ? pristine[s] : null;
                    replaced[s] = source != null
                        ? GetOrCreateTinted(source, baked, accents, color1Packed, color1)
                        : null;
                }

                renderer.SetPropertyBlock(null);
                renderer.sharedMaterials = replaced;
            }
        }

        /// <summary>
        /// Reads the preview hull's captured Colorize team mats. Colorize assets store
        /// <c>a = 0</c>, so we force opaque bytes for the studio wells.
        /// </summary>
        public static bool TryReadBakedAccentsFromRoot(
            GameObject root,
            out Color32 color2,
            out Color32 color3,
            out Color32 emission1,
            out Color32 emission2,
            out Color32 emission3)
        {
            AssignFallbackAccents(out color2, out color3, out emission1, out emission2, out emission3);
            if (root == null)
                return false;

            var state = root.GetComponent<ShipAccentTintState>();
            if (state != null && TryReadBakedFromRows(state.PristineMaterials, out color2, out color3, out emission1, out emission2, out emission3))
                return true;
            if (state != null && TryReadBakedFromRows(state.BaseSharedMaterials, out color2, out color3, out emission1, out emission2, out emission3))
                return true;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;
                Material[] mats = renderer.sharedMaterials;
                if (mats == null)
                    continue;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (TryReadBakedAccents(mats[s], out color2, out color3, out emission1, out emission2, out emission3))
                        return true;
                }
            }

            return false;
        }

        /// <summary>Reads baked Color2 / Color3 / Emission1–3 from a Colorize material.</summary>
        public static void ReadBakedAccents(
            Material material,
            out Color32 color2,
            out Color32 color3,
            out Color32 emission1,
            out Color32 emission2,
            out Color32 emission3)
        {
            TryReadBakedAccents(material, out color2, out color3, out emission1, out emission2, out emission3);
        }

        static bool TryReadBakedAccents(
            Material material,
            out Color32 color2,
            out Color32 color3,
            out Color32 emission1,
            out Color32 emission2,
            out Color32 emission3)
        {
            AssignFallbackAccents(out color2, out color3, out emission1, out emission2, out emission3);
            if (material == null)
                return false;

            bool any = false;
            if (material.HasProperty(Color2Id))
            {
                color2 = Opaque(material.GetColor(Color2Id));
                any = true;
            }
            else if (material.HasProperty(SecondaryId))
            {
                color2 = Opaque(material.GetColor(SecondaryId));
                any = true;
            }

            if (material.HasProperty(Color3Id))
            {
                color3 = Opaque(material.GetColor(Color3Id));
                any = true;
            }
            else if (material.HasProperty(TertiaryId))
            {
                color3 = Opaque(material.GetColor(TertiaryId));
                any = true;
            }
            else if (material.HasProperty(TretiaryId))
            {
                color3 = Opaque(material.GetColor(TretiaryId));
                any = true;
            }

            if (material.HasProperty(Emission1Id))
            {
                emission1 = Opaque(material.GetColor(Emission1Id));
                any = true;
            }

            if (material.HasProperty(Emission2Id))
            {
                emission2 = Opaque(material.GetColor(Emission2Id));
                any = true;
            }

            if (material.HasProperty(Emission3Id))
            {
                emission3 = Opaque(material.GetColor(Emission3Id));
                any = true;
            }

            return any;
        }

        static void AssignFallbackAccents(
            out Color32 color2,
            out Color32 color3,
            out Color32 emission1,
            out Color32 emission2,
            out Color32 emission3)
        {
            color2 = new Color32(39, 39, 39, 255);
            color3 = new Color32(116, 116, 116, 255);
            emission1 = new Color32(0, 16, 63, 255);
            emission2 = new Color32(31, 146, 218, 255);
            emission3 = new Color32(130, 243, 243, 255);
        }

        /// <summary>
        /// Colorize .mat files store <c>a = 0</c>. UI Image uses that alpha, so wells
        /// look empty unless we force opaque. HDR values above 1 are clamped.
        /// </summary>
        public static Color32 Opaque(Color color)
        {
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f),
                255);
        }

        static Material GetOrCreateTinted(
            Material source,
            Material bakedSource,
            in ShipAccentColors accents,
            uint color1Packed,
            Color color1)
        {
            Material copyFrom = bakedSource != null ? bakedSource : source;
            var key = new AccentCacheKey
            {
                BaseId = source.GetInstanceID(),
                Color1 = color1Packed,
                HasCustom = accents.HasCustom,
                Color2 = accents.Color2Packed,
                Color3 = accents.Color3Packed,
                Emission = accents.EmissionPacked,
                Emission2 = accents.Emission2Packed,
                Emission3 = accents.Emission3Packed,
            };
            if (Cache.TryGetValue(key, out Material cached) && cached != null)
            {
                if (!accents.IsCustom)
                    RestoreBakedAccents(cached, copyFrom, color1);
                return cached;
            }

            var instance = new Material(copyFrom)
            {
                name = source.name + "_Accent",
            };
            WriteAccentColors(instance, accents, color1, copyFrom);
            Cache[key] = instance;
            return instance;
        }

        /// <summary>
        /// Factory hull (HasCustom 0) must put the Colorize Color2 / Color3 / glow
        /// slots back. Leaving them untouched keeps whatever skin was last written
        /// onto the material we cloned.
        /// </summary>
        static void WriteAccentColors(Material material, in ShipAccentColors accents, Color color1, Material bakedSource)
        {
            TrySet(material, Color1Id, color1);

            if (!accents.IsCustom)
            {
                RestoreBakedAccents(material, bakedSource, color1);
                return;
            }

            Color color2 = (Color)accents.Color2;
            Color color3 = (Color)accents.Color3;
            Color emission1 = (Color)accents.Emission;
            Color emission2 = (Color)accents.Emission2;
            Color emission3 = (Color)accents.Emission3;

            TrySet(material, Color2Id, color2);
            TrySet(material, SecondaryId, color2);
            TrySet(material, Color3Id, color3);
            TrySet(material, TertiaryId, color3);
            TrySet(material, TretiaryId, color3);
            TrySet(material, Emission1Id, emission1);
            TrySet(material, Emission2Id, emission2);
            TrySet(material, Emission3Id, emission3);
            TrySet(material, GlowId, emission1);
            TrySet(material, HighlightId, emission2);
        }

        static void RestoreBakedAccents(Material dest, Material baked, Color color1)
        {
            if (dest == null || baked == null || dest == baked)
            {
                TrySet(dest, Color1Id, color1);
                return;
            }

            dest.CopyPropertiesFromMaterial(baked);
            TrySet(dest, Color1Id, color1);
        }

        static bool IsAccentInstance(Material material)
        {
            return material != null && material.name.EndsWith("_Accent");
        }

        static Material[] SlotRow(Material[][] rows, int index)
        {
            if (rows == null || index < 0 || index >= rows.Length)
                return null;
            return rows[index];
        }

        static bool TryReadBakedFromRows(
            Material[][] rows,
            out Color32 color2,
            out Color32 color3,
            out Color32 emission1,
            out Color32 emission2,
            out Color32 emission3)
        {
            AssignFallbackAccents(out color2, out color3, out emission1, out emission2, out emission3);
            if (rows == null)
                return false;
            for (int i = 0; i < rows.Length; i++)
            {
                Material[] mats = rows[i];
                if (mats == null)
                    continue;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (TryReadBakedAccents(mats[s], out color2, out color3, out emission1, out emission2, out emission3))
                        return true;
                }
            }

            return false;
        }

        static void DestroyUnusedPristine(Material[][] previous, Material[][] kept)
        {
            if (previous == null)
                return;
            for (int i = 0; i < previous.Length; i++)
            {
                Material[] row = previous[i];
                if (row == null)
                    continue;
                for (int s = 0; s < row.Length; s++)
                {
                    Material mat = row[s];
                    if (mat == null || ContainsMaterial(kept, mat))
                        continue;
                    UnityEngine.Object.Destroy(mat);
                }
            }
        }

        static bool ContainsMaterial(Material[][] rows, Material mat)
        {
            if (rows == null || mat == null)
                return false;
            for (int i = 0; i < rows.Length; i++)
            {
                Material[] row = rows[i];
                if (row == null)
                    continue;
                for (int s = 0; s < row.Length; s++)
                {
                    if (row[s] == mat)
                        return true;
                }
            }

            return false;
        }

        static void TrySet(Material material, int id, Color color)
        {
            if (material != null && material.HasProperty(id))
                material.SetColor(id, color);
        }
    }
}
