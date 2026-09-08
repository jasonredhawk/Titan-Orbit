using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Entities;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Replaces host chassis children with cached meshes from another ship family,
    /// keeping the host slot pose. Used by the hybrid proxy applier and by covering-collider
    /// bake so the unique silhouette matches what the player sees.
    /// <para>
    /// Store extras remap cockpit / engine / wing / thruster / tail / hull. Weapons are
    /// owned by B-key <see cref="ShipLoadoutState.RuntimeBulletIndex"/> so purchase and
    /// cycle do not fight. Combat stats stay on Extra Level merge.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Original host parts are cloned under
    /// <see cref="ShipFamilyPartMatch.OriginalStashName"/> at proxy Bind. Discarding the
    /// purchased row from the Orbit Menu only removes the ghosted extra — this class
    /// puts the stashed (or host-prefab) mesh back on that hardpoint. We never leave
    /// an empty socket.
    /// </para>
    /// </summary>
    public static class ShipComponentVisualSwapLogic
    {
        static readonly List<ShipFamilyPartMatch.Slot> HostSlots = new List<ShipFamilyPartMatch.Slot>(32);
        static readonly List<ShipFamilyPartMatch.Slot> MatchSlots = new List<ShipFamilyPartMatch.Slot>(8);
        static readonly List<Transform> RestoreScratch = new List<Transform>(8);
        static readonly HashSet<string> KeepRemappedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the ghosted equipment buffer + bullet bank and remaps <paramref name="root"/>.
        /// When <paramref name="originalStash"/> is set, Bind-time originals are used to
        /// put the host mesh back after an Orbit Menu discard.
        /// </summary>
        /// <param name="stripColliders">True on live proxies; false on bake clones.</param>
        /// <param name="remapNonWeapons">
        /// False on B-key bank cycles: restore/swap weapons only so cockpit/engine/wing
        /// meshes and their already-applied grow stay untouched.
        /// </param>
        /// <returns>True when at least one child was replaced or restored.</returns>
        public static bool ApplyFromShip(
            Transform root,
            EntityManager em,
            Entity shipEntity,
            string hostFamilyPrefix,
            ShipFamilyDefinition hostFamily,
            TeamId team,
            Transform originalStash,
            bool stripColliders,
            bool remapNonWeapons = true)
        {
            if (root == null || shipEntity == Entity.Null || !em.Exists(shipEntity))
                return false;
            if (em.HasComponent<MegaShipState>(shipEntity)
                && em.GetComponentData<MegaShipState>(shipEntity).IsMega)
                return false;

            int shipLevel = 1;
            if (em.HasComponent<ShipState>(shipEntity))
                shipLevel = Mathf.Max(1, em.GetComponentData<ShipState>(shipEntity).ShipLevel);

            ShipComponentStoreVisualScaleLogic.CollectExtraComponentIds(em, shipEntity, out List<string> extras);

            int bankIndex = 0;
            bool healing = false;
            if (em.HasComponent<ShipLoadoutState>(shipEntity))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                bankIndex = loadout.RuntimeBulletIndex;
                healing = loadout.HealingBulletsActive;
            }

            return ApplyToHierarchy(
                root,
                hostFamilyPrefix,
                hostFamily,
                shipLevel,
                extras,
                bankIndex,
                healing,
                team,
                originalStash,
                stripColliders,
                remapNonWeapons);
        }

        /// <summary>
        /// True when extras or a foreign bullet bank would change meshes (bake must clone).
        /// </summary>
        public static bool WouldRemap(
            ShipFamilyDefinition hostFamily,
            IReadOnlyList<string> extraComponentIds,
            int runtimeBulletIndex,
            bool healingBullets)
        {
            if (HasNonWeaponExtra(extraComponentIds))
                return true;
            if (healingBullets)
                return false;
            if (!TryResolveBankFamily(runtimeBulletIndex, out ShipFamilyDefinition bankFamily)
                || bankFamily == null)
                return false;
            if (hostFamily == null || string.IsNullOrWhiteSpace(hostFamily.familyId))
                return true;
            return !string.Equals(bankFamily.familyId, hostFamily.familyId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Puts host originals back on slots the store / B-key no longer own, then applies
        /// remaining remaps. Pass <paramref name="remapNonWeapons"/> false on bank-only
        /// cycles so engines are not restored and re-swapped (that recaptured grown scale).
        /// </summary>
        public static bool ApplyToHierarchy(
            Transform root,
            string hostFamilyPrefix,
            ShipFamilyDefinition hostFamily,
            int shipLevel,
            IReadOnlyList<string> extraComponentIds,
            int runtimeBulletIndex,
            bool healingBullets,
            TeamId team,
            Transform originalStash,
            bool stripColliders,
            bool remapNonWeapons = true)
        {
            if (root == null)
                return false;

            bool changed = false;

            // --- Which non-weapon hardpoints extras still cover ---
            // [TITAN-ORBIT] Discard clears that id from the ghosted buffer. Slots not in
            // this set go back to the host mesh instead of being destroyed and left empty.
            KeepRemappedNames.Clear();
            if (remapNonWeapons && extraComponentIds != null)
                CollectRemappedHostNamesForExtras(root, hostFamilyPrefix, extraComponentIds, KeepRemappedNames);

            ShipFamilyDefinition bankFamily = null;
            bool foreignWeapons = !healingBullets
                && TryResolveBankFamily(runtimeBulletIndex, out bankFamily)
                && bankFamily != null
                && (hostFamily == null
                    || !string.Equals(bankFamily.familyId, hostFamily.familyId, StringComparison.OrdinalIgnoreCase));

            // --- Restore remapped slots the player no longer owns ---
            changed |= RestoreUnownedRemaps(
                root,
                hostFamilyPrefix,
                hostFamily,
                team,
                originalStash,
                stripColliders,
                remapNonWeapons,
                foreignWeapons);

            ShipFamilyPartMatch.CollectSlots(root, hostFamilyPrefix, HostSlots);

            if (remapNonWeapons && extraComponentIds != null)
            {
                for (int i = 0; i < extraComponentIds.Count; i++)
                    changed |= TryRemapStoreExtra(
                        root, hostFamilyPrefix, hostFamily, shipLevel, team,
                        extraComponentIds[i], originalStash, stripColliders);
            }

            if (foreignWeapons)
            {
                changed |= RemapAllWeapons(
                    root, hostFamilyPrefix, hostFamily, bankFamily, shipLevel, team,
                    originalStash, stripColliders);
            }

            return changed;
        }

        /// <summary>
        /// Copies every remappable host slot into <paramref name="originalStash"/> at the
        /// current (authored) pose. Call from proxy Bind before attribute grow so later
        /// B-key / store remaps restore the designed size, not an already-grown mesh.
        /// Skips slots that already have <see cref="ShipPartVisualSource"/> so a late
        /// Bind cannot cache a purchased mesh as the "original".
        /// </summary>
        public static void StashCurrentSlots(
            Transform root,
            string hostFamilyPrefix,
            Transform originalStash)
        {
            if (root == null || originalStash == null)
                return;

            ShipFamilyPartMatch.CollectSlots(root, hostFamilyPrefix, HostSlots);
            for (int i = 0; i < HostSlots.Count; i++)
            {
                Transform slot = HostSlots[i].Transform;
                if (slot == null)
                    continue;
                if (slot.GetComponent<ShipPartVisualSource>() != null)
                    continue;
                StashOriginalIfNeeded(root, slot, originalStash);
            }
        }

        /// <summary>Family that authored this default gun bank, or false when none.</summary>
        public static bool TryResolveBankFamily(int bankIndex, out ShipFamilyDefinition family)
        {
            family = null;
            var config = PlanetShipFamilyConfig.LoadDefault();
            return config != null && config.TryGetFamilyForDefaultBank(bankIndex, out family) && family != null;
        }

        static bool TryRemapStoreExtra(
            Transform root,
            string hostFamilyPrefix,
            ShipFamilyDefinition hostFamily,
            int shipLevel,
            TeamId team,
            string extraId,
            Transform originalStash,
            bool stripColliders)
        {
            if (string.IsNullOrWhiteSpace(extraId))
                return false;
            if (!ShipComponentStoreVisualScaleLogic.TryFindSourceComponent(extraId, out ShipFamilyComponentEntry entry)
                || entry == null)
                return false;
            if (!TryFindSourceFamily(extraId, out ShipFamilyDefinition sourceFamily) || sourceFamily == null)
                return false;

            if (!ShipFamilyPartMatch.TryParseSlotName(
                    entry.componentId, sourceFamily.familyId, out string partType, out _, out _))
                return false;
            if (ShipFamilyPartMatch.IsWeaponPartType(partType))
                return false;

            // Same family + exact suffix already on the hull — nothing to restyle.
            if (hostFamily != null
                && string.Equals(sourceFamily.familyId, hostFamily.familyId, StringComparison.OrdinalIgnoreCase)
                && HostHasExactSuffix(entry.componentId, sourceFamily.familyId))
                return false;

            GameObject template = ShipFamilyPartVisualCache.GetOrCreateTemplate(
                sourceFamily, entry.componentId, shipLevel);
            if (template == null)
                return false;

            ShipFamilyPartMatch.CollectSlots(root, hostFamilyPrefix, HostSlots);
            ShipFamilyPartMatch.CollectBestHostSlots(
                HostSlots, sourceFamily.familyId, entry.componentId, MatchSlots);
            return SwapSlots(
                root, MatchSlots, template, hostFamily, sourceFamily, team, originalStash, stripColliders);
        }

        static bool RemapAllWeapons(
            Transform root,
            string hostFamilyPrefix,
            ShipFamilyDefinition hostFamily,
            ShipFamilyDefinition sourceFamily,
            int shipLevel,
            TeamId team,
            Transform originalStash,
            bool stripColliders)
        {
            // Templates always pull L1 authored weapon scale; shipLevel is unused here.
            _ = shipLevel;
            ShipFamilyPartMatch.CollectSlots(root, hostFamilyPrefix, HostSlots);
            var weaponSlots = new List<ShipFamilyPartMatch.Slot>(8);
            for (int i = 0; i < HostSlots.Count; i++)
            {
                if (ShipFamilyPartMatch.IsWeaponPartType(HostSlots[i].PartType))
                    weaponSlots.Add(HostSlots[i]);
            }

            if (weaponSlots.Count == 0)
                return false;

            bool changed = false;
            // Snapshot the live host weapons now. SwapSlots DestroyImmediate those
            // transforms, then the replacement keeps source-family child names
            // (CosmicShark_Weapon_0). A second CollectSlots would treat those nested
            // children as extra host guns and destroy the inside of the barrel —
            // after enough B cycles the ship has no muzzles and looks unarmed.
            var doneIndices = new HashSet<int>();
            for (int i = 0; i < weaponSlots.Count; i++)
            {
                int index = weaponSlots[i].Index;
                if (!doneIndices.Add(index))
                    continue;

                string wanted = "Weapon_" + index;
                // Level 1 authored scale — attribute grow applies after the swap.
                GameObject template = ShipFamilyPartVisualCache.GetOrCreateTemplate(
                    sourceFamily, sourceFamily.familyId + "_" + wanted, shipLevel: 1);
                if (template == null)
                {
                    template = ShipFamilyPartVisualCache.GetOrCreateTemplate(
                        sourceFamily, wanted, shipLevel: 1);
                }

                if (template == null)
                    continue;

                MatchSlots.Clear();
                for (int s = 0; s < weaponSlots.Count; s++)
                {
                    if (weaponSlots[s].Index == index && weaponSlots[s].Transform != null)
                        MatchSlots.Add(weaponSlots[s]);
                }

                changed |= SwapSlots(
                    root, MatchSlots, template, hostFamily, sourceFamily, team, originalStash, stripColliders);
            }

            return changed;
        }

        static bool SwapSlots(
            Transform root,
            List<ShipFamilyPartMatch.Slot> slots,
            GameObject template,
            ShipFamilyDefinition hostFamily,
            ShipFamilyDefinition sourceFamily,
            TeamId team,
            Transform originalStash,
            bool stripColliders)
        {
            if (slots == null || slots.Count == 0 || template == null)
                return false;

            bool changed = false;
            for (int i = 0; i < slots.Count; i++)
            {
                Transform hostSlot = slots[i].Transform;
                if (hostSlot == null)
                    continue;

                if (originalStash != null)
                    StashOriginalIfNeeded(root, hostSlot, originalStash);

                GameObject clone = ShipFamilyPartVisualCache.InstantiateAtHostSlot(
                    template, hostSlot, stripColliders);
                if (clone == null)
                    continue;

                DestroyNow(hostSlot.gameObject);
                StampVisualSource(clone, sourceFamily);
                ApplyHostTeamMaterials(hostFamily, clone, team);
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Clones keep the host slot name — stamp the purchased family so jets can
        /// pick that family's flame prefab.
        /// </summary>
        static void StampVisualSource(GameObject clone, ShipFamilyDefinition sourceFamily)
        {
            if (clone == null || sourceFamily == null || string.IsNullOrWhiteSpace(sourceFamily.familyId))
                return;

            var marker = clone.GetComponent<ShipPartVisualSource>();
            if (marker == null)
                marker = clone.AddComponent<ShipPartVisualSource>();
            marker.sourceFamilyId = sourceFamily.familyId.Trim();
        }

        /// <summary>Stash / restore copies must not keep a purchased-family marker.</summary>
        static void StripVisualSource(GameObject root)
        {
            if (root == null)
                return;
            var leftover = root.GetComponent<ShipPartVisualSource>();
            if (leftover != null)
                DestroyNow(leftover);
        }

        /// <summary>
        /// Live jets are parented under the mount. Stashing the mesh would bake that
        /// flame into the "original" and leave the wrong family VFX after a delete.
        /// </summary>
        static void StripJetVfxInstances(GameObject root)
        {
            if (root == null)
                return;

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = transforms.Length - 1; i >= 0; i--)
            {
                Transform t = transforms[i];
                if (t == null || t == root.transform)
                    continue;
                if (t.name != ThrusterVfxBank.JetInstanceName)
                    continue;
                DestroyNow(t.gameObject);
            }
        }

        /// <summary>
        /// Host slot names that remaining extras would remap. Used so we restore only
        /// sockets the player just discarded — not every stashed wing on every apply.
        /// </summary>
        static void CollectRemappedHostNamesForExtras(
            Transform root,
            string hostFamilyPrefix,
            IReadOnlyList<string> extraComponentIds,
            HashSet<string> dest)
        {
            dest.Clear();
            if (root == null || extraComponentIds == null || dest == null)
                return;

            ShipFamilyPartMatch.CollectSlots(root, hostFamilyPrefix, HostSlots);
            for (int i = 0; i < extraComponentIds.Count; i++)
            {
                string extraId = extraComponentIds[i];
                if (string.IsNullOrWhiteSpace(extraId))
                    continue;
                if (!ShipComponentStoreVisualScaleLogic.TryFindSourceComponent(extraId, out ShipFamilyComponentEntry entry)
                    || entry == null)
                    continue;
                if (!TryFindSourceFamily(extraId, out ShipFamilyDefinition sourceFamily) || sourceFamily == null)
                    continue;
                if (!ShipFamilyPartMatch.TryParseSlotName(
                        entry.componentId, sourceFamily.familyId, out string partType, out _, out _))
                    continue;
                if (ShipFamilyPartMatch.IsWeaponPartType(partType))
                    continue;

                ShipFamilyPartMatch.CollectBestHostSlots(
                    HostSlots, sourceFamily.familyId, entry.componentId, MatchSlots);
                for (int s = 0; s < MatchSlots.Count; s++)
                {
                    if (MatchSlots[s].Transform != null)
                        dest.Add(MatchSlots[s].Transform.name);
                }
            }
        }

        /// <summary>
        /// Puts the host mesh back on remapped hardpoints the store / foreign bank no
        /// longer own. Instantiates the original first, then destroys the purchased
        /// clone so a failed restore cannot leave a hole.
        /// </summary>
        static bool RestoreUnownedRemaps(
            Transform root,
            string hostFamilyPrefix,
            ShipFamilyDefinition hostFamily,
            TeamId team,
            Transform originalStash,
            bool stripColliders,
            bool remapNonWeapons,
            bool foreignWeapons)
        {
            if (root == null)
                return false;

            ShipFamilyPartMatch.CollectSlots(root, hostFamilyPrefix, HostSlots);
            RestoreScratch.Clear();
            for (int i = 0; i < HostSlots.Count; i++)
            {
                Transform slot = HostSlots[i].Transform;
                if (slot == null)
                    continue;

                bool isWeapon = ShipFamilyPartMatch.IsWeaponPartType(HostSlots[i].PartType);

                // B-key only: leave cockpit / engine / wing (their grow is already applied).
                if (!remapNonWeapons && !isWeapon)
                    continue;

                // Foreign bank will replace every gun after this pass.
                if (isWeapon && foreignWeapons)
                    continue;

                // Still purchased — TryRemapStoreExtra will refresh the foreign mesh.
                if (!isWeapon && KeepRemappedNames.Contains(slot.name))
                    continue;

                if (!IsRemappedSlot(slot, hostFamily))
                    continue;

                RestoreScratch.Add(slot);
            }

            bool changed = false;
            for (int i = 0; i < RestoreScratch.Count; i++)
            {
                changed |= RestoreOneSlot(
                    root, RestoreScratch[i], hostFamily, team, originalStash, stripColliders);
            }

            return changed;
        }

        /// <summary>
        /// True when this live child is a purchased / B-cycled mesh (has a foreign
        /// <see cref="ShipPartVisualSource"/>). Unmarked parts are treated as host originals.
        /// </summary>
        static bool IsRemappedSlot(Transform slot, ShipFamilyDefinition hostFamily)
        {
            if (slot == null)
                return false;
            var marker = slot.GetComponent<ShipPartVisualSource>();
            if (marker == null || string.IsNullOrWhiteSpace(marker.sourceFamilyId))
                return false;
            if (hostFamily != null
                && string.Equals(marker.sourceFamilyId, hostFamily.familyId, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        /// <summary>
        /// Replaces one remapped live slot with the Bind-time stash copy, or the host
        /// family's prefab template when the stash missed (late Bind, missing prefix).
        /// </summary>
        static bool RestoreOneSlot(
            Transform root,
            Transform liveSlot,
            ShipFamilyDefinition hostFamily,
            TeamId team,
            Transform originalStash,
            bool stripColliders)
        {
            if (liveSlot == null)
                return false;
            if (IsUnderStash(liveSlot))
                return false;
            if (originalStash != null && IsAncestorOf(liveSlot, originalStash))
                return false;

            string hostName = liveSlot.name;
            Transform liveParent = liveSlot.parent != null ? liveSlot.parent : root;
            Vector3 pos = liveSlot.localPosition;
            Quaternion rot = liveSlot.localRotation;

            GameObject restored = null;
            ShipPartOriginalStashEntry stashEntry = FindStashEntry(originalStash, hostName);

            // --- Preferred: authored clone taken at Bind (correct chassis tier) ---
            if (stashEntry != null)
            {
                Transform stashParent = ResolveRelative(root, stashEntry.ParentPath);
                if (stashParent == null
                    || IsUnderStash(stashParent)
                    || stashParent == liveSlot)
                    stashParent = liveParent;

                restored = Object.Instantiate(stashEntry.gameObject, stashParent, false);
                var leftover = restored.GetComponent<ShipPartOriginalStashEntry>();
                if (leftover != null)
                    DestroyNow(leftover);
                StripVisualSource(restored);
                StripJetVfxInstances(restored);
                restored.name = hostName;
                restored.SetActive(true);
                restored.transform.localPosition = pos;
                restored.transform.localRotation = rot;
                restored.transform.SetSiblingIndex(
                    Mathf.Clamp(stashEntry.SiblingIndex, 0, stashParent.childCount - 1));
            }
            else if (hostFamily != null)
            {
                // --- Fallback: host-family prefab part (same cache as a store purchase) ---
                GameObject template = ShipFamilyPartVisualCache.GetOrCreateTemplate(
                    hostFamily, hostName, shipLevel: 1);
                if (template == null && !string.IsNullOrWhiteSpace(hostFamily.familyId))
                {
                    template = ShipFamilyPartVisualCache.GetOrCreateTemplate(
                        hostFamily, hostFamily.familyId + "_" + hostName, shipLevel: 1);
                }

                if (template != null)
                {
                    restored = ShipFamilyPartVisualCache.InstantiateAtHostSlot(
                        template, liveSlot, stripColliders);
                    if (restored != null)
                    {
                        StripVisualSource(restored);
                        StripJetVfxInstances(restored);
                        ApplyHostTeamMaterials(hostFamily, restored, team);
                    }
                }
            }

            if (restored == null)
                return false;

            // Destroy the purchased clone only after the original is in the hierarchy.
            DestroyNow(liveSlot.gameObject);
            return true;
        }

        /// <summary>
        /// Clones the live host slot under the hidden stash once. Later remaps skip so
        /// the copy stays the designed mesh, not a purchased one.
        /// </summary>
        static void StashOriginalIfNeeded(Transform root, Transform hostSlot, Transform stash)
        {
            if (stash == null || hostSlot == null)
                return;
            if (hostSlot.GetComponent<ShipPartVisualSource>() != null)
                return;
            if (FindStashEntry(stash, hostSlot.name) != null)
                return;

            GameObject copy = Object.Instantiate(hostSlot.gameObject, stash, false);
            copy.name = hostSlot.name;
            copy.SetActive(false);
            StripVisualSource(copy);
            StripJetVfxInstances(copy);
            var entry = copy.GetComponent<ShipPartOriginalStashEntry>();
            if (entry == null)
                entry = copy.AddComponent<ShipPartOriginalStashEntry>();
            entry.HostSlotName = hostSlot.name;
            entry.ParentPath = GetRelativePath(hostSlot.parent, root);
            entry.SiblingIndex = hostSlot.GetSiblingIndex();
        }

        /// <summary>Stash clone whose name or <see cref="ShipPartOriginalStashEntry.HostSlotName"/> matches.</summary>
        static ShipPartOriginalStashEntry FindStashEntry(Transform stash, string hostName)
        {
            if (stash == null || string.IsNullOrEmpty(hostName))
                return null;
            var entries = stash.GetComponentsInChildren<ShipPartOriginalStashEntry>(true);
            for (int i = 0; i < entries.Length; i++)
            {
                ShipPartOriginalStashEntry entry = entries[i];
                if (entry == null)
                    continue;
                if (string.Equals(entry.gameObject.name, hostName, StringComparison.Ordinal))
                    return entry;
                if (string.Equals(entry.HostSlotName, hostName, StringComparison.Ordinal))
                    return entry;
            }

            return null;
        }

        /// <summary>True when <paramref name="maybeAncestor"/> is <paramref name="descendant"/> or sits above it.</summary>
        static bool IsAncestorOf(Transform maybeAncestor, Transform descendant)
        {
            if (maybeAncestor == null || descendant == null)
                return false;
            Transform walk = descendant;
            while (walk != null)
            {
                if (walk == maybeAncestor)
                    return true;
                walk = walk.parent;
            }

            return false;
        }

        static bool HostHasExactSuffix(string componentId, string sourceFamilyId)
        {
            string suffix = ShipFamilyDefinition.GetComponentIdSuffix(sourceFamilyId, componentId);
            for (int i = 0; i < HostSlots.Count; i++)
            {
                if (string.Equals(HostSlots[i].NormalizedSuffix, suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static bool HasNonWeaponExtra(IReadOnlyList<string> extraComponentIds)
        {
            if (extraComponentIds == null)
                return false;
            for (int i = 0; i < extraComponentIds.Count; i++)
            {
                string id = extraComponentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (!ShipComponentStoreVisualScaleLogic.TryFindSourceComponent(id, out ShipFamilyComponentEntry entry)
                    || entry == null)
                    return true;
                string type = ShipFamilyPartTypes.Normalize(
                    ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(entry.componentId),
                    entry.componentId);
                if (!ShipFamilyPartMatch.IsWeaponPartType(type))
                    return true;
            }

            return false;
        }

        static bool TryFindSourceFamily(string componentId, out ShipFamilyDefinition family)
        {
            family = null;
            if (!ShipComponentStoreVisualScaleLogic.TryFindSourceComponent(componentId, out _))
                return false;

            var config = PlanetShipFamilyConfig.LoadDefault();
            if (config?.families == null)
                return false;

            string id = componentId.Trim();
            for (int i = 0; i < config.families.Count; i++)
            {
                ShipFamilyDefinition f = config.families[i]?.shipFamilyDefinition;
                if (f == null || string.IsNullOrWhiteSpace(f.familyId))
                    continue;
                if (id.StartsWith(f.familyId.Trim() + "_", StringComparison.OrdinalIgnoreCase)
                    && f.TryGetComponentEntry(id, out _))
                {
                    family = f;
                    return true;
                }
            }

            if (BulletBankProfileUtility.TryFindComponentInAnyFamily(componentId, out _))
            {
                for (int i = 0; i < config.families.Count; i++)
                {
                    ShipFamilyDefinition f = config.families[i]?.shipFamilyDefinition;
                    if (f != null && f.TryGetComponentEntry(componentId, out _))
                    {
                        family = f;
                        return true;
                    }
                }
            }

            return false;
        }

        static string GetRelativePath(Transform target, Transform root)
        {
            if (target == null || root == null || target == root)
                return string.Empty;

            var parts = new List<string>(4);
            Transform walk = target;
            while (walk != null && walk != root)
            {
                parts.Add(walk.name);
                walk = walk.parent;
            }

            if (walk != root)
                return string.Empty;
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Transform ResolveRelative(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path))
                return root;
            return root.Find(path);
        }

        /// <summary>Paints the swapped clone with the host family's team palette.</summary>
        static void ApplyHostTeamMaterials(ShipFamilyDefinition family, GameObject root, TeamId team)
        {
            if (family == null || root == null || team == TeamId.None)
                return;

            List<Material> teamMats = family.GetMaterialsForTeam(team);
            if (teamMats == null || teamMats.Count == 0)
                return;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;

                Material[] current = renderer.sharedMaterials;
                if (current == null || current.Length == 0)
                    continue;

                var replaced = new Material[current.Length];
                for (int s = 0; s < current.Length; s++)
                    replaced[s] = teamMats[s % teamMats.Count] != null
                        ? teamMats[s % teamMats.Count]
                        : current[s];
                renderer.sharedMaterials = replaced;
            }
        }

        static void DestroyNow(Object obj)
        {
            if (obj == null)
                return;
            Object.DestroyImmediate(obj);
        }

        static bool IsUnderStash(Transform t)
        {
            Transform walk = t;
            while (walk != null)
            {
                if (walk.name == ShipFamilyPartMatch.OriginalStashName)
                    return true;
                walk = walk.parent;
            }

            return false;
        }
    }
}
