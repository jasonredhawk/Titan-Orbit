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
    /// cycle do not fight: every barrel restyles to the family that owns that bank
    /// (purchased extra, else the planet that rolled it). Combat stats stay on Extra Level merge.
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
        static readonly List<Transform> StashEntryScratch = new List<Transform>(32);
        static readonly HashSet<string> KeepRemappedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> LiveSlotNames = new HashSet<string>(StringComparer.Ordinal);

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
                remapNonWeapons,
                em,
                shipEntity);
        }

        /// <summary>
        /// True when store extras would change non-weapon meshes (bake must clone).
        /// B-key weapon restyle is presentation-only and does not dirty covering hulls.
        /// </summary>
        public static bool WouldRemap(
            ShipFamilyDefinition hostFamily,
            IReadOnlyList<string> extraComponentIds,
            int runtimeBulletIndex,
            bool healingBullets,
            EntityManager em,
            Entity shipEntity)
        {
            // B-key weapon restyle is presentation-only. Covering colliders stay
            // host + store extras so a bank cycle does not Instantiates/Destroy
            // weapon children during the sim tick (that aborted B-key writes).
            _ = hostFamily;
            _ = runtimeBulletIndex;
            _ = healingBullets;
            _ = em;
            _ = shipEntity;
            return HasNonWeaponExtra(extraComponentIds);
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
            bool remapNonWeapons = true,
            EntityManager em = default,
            Entity shipEntity = default)
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
            bool resolvedFamily = !healingBullets
                && TryResolveBankFamily(runtimeBulletIndex, em, shipEntity, out bankFamily)
                && bankFamily != null;
            bool hostWeapons = resolvedFamily
                && hostFamily != null
                && string.Equals(bankFamily.familyId, hostFamily.familyId, StringComparison.OrdinalIgnoreCase);
            bool foreignWeapons = resolvedFamily && !hostWeapons;

            // --- Restore remapped slots the player no longer owns ---
            // Weapons restore only when we know this is the hull gun. A failed
            // family resolve must not put host guns back (that looked like B
            // snapping to the default type).
            changed |= RestoreUnownedRemaps(
                root,
                hostFamilyPrefix,
                hostFamily,
                team,
                originalStash,
                stripColliders,
                remapNonWeapons,
                restoreHostWeapons: hostWeapons);

            // A swap that destroyed its own socket (nested DestroyImmediate abort)
            // leaves no live marker to restore onto. Put that Bind-time part back
            // before the next extra tries to match an empty hull.
            changed |= ResurrectMissingStashedSlots(root, hostFamily, team, originalStash);

            CollectHostSlots(root, hostFamilyPrefix, HostSlots);

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
                    root, hostFamilyPrefix, hostFamily, bankFamily, extraComponentIds,
                    shipLevel, team, originalStash, stripColliders);
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

            CollectHostSlots(root, hostFamilyPrefix, HostSlots);
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

        /// <summary>
        /// Family whose guns belong on this bank. Prefers owned extras + hull stamp
        /// (every family asset now defaults to Laserbolt, so unique-default lookup
        /// almost never finds a owner).
        /// </summary>
        public static bool TryResolveBankFamily(
            int bankIndex,
            EntityManager em,
            Entity shipEntity,
            out ShipFamilyDefinition family)
        {
            if (BulletBankOwnership.TryResolveFamilyForBank(em, shipEntity, bankIndex, out family)
                && family != null)
                return true;

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

            CollectHostSlots(root, hostFamilyPrefix, HostSlots);
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
            IReadOnlyList<string> extraComponentIds,
            int shipLevel,
            TeamId team,
            Transform originalStash,
            bool stripColliders)
        {
            // Templates always pull L1 authored weapon scale; shipLevel is unused here.
            _ = shipLevel;
            CollectHostSlots(root, hostFamilyPrefix, HostSlots);
            var weaponSlots = new List<ShipFamilyPartMatch.Slot>(8);
            for (int i = 0; i < HostSlots.Count; i++)
            {
                if (ShipFamilyPartMatch.IsWeaponPartType(HostSlots[i].PartType))
                    weaponSlots.Add(HostSlots[i]);
            }

            if (weaponSlots.Count == 0)
                return false;

            // Store extras remap one host assembly. Nested barrel / muzzle
            // children also parse as weapons — DestroyImmediate of the parent
            // then the child asserted `t.GetParent() == nullptr` and aborted
            // the tick, which snapped RuntimeBulletIndex back to the hull gun.
            for (int i = weaponSlots.Count - 1; i >= 0; i--)
            {
                if (IsNestedUnderAnotherSlot(weaponSlots[i].Transform, weaponSlots))
                    weaponSlots.RemoveAt(i);
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

                GameObject template = ResolveWeaponTemplate(sourceFamily, index, extraComponentIds);
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

        /// <summary>
        /// Template for one host barrel index: purchased extra of this family first
        /// (same pick as a Gear-tab buy), then that family's catalog weapon at the
        /// same index, then <c>Family_Weapon_N</c>.
        /// </summary>
        static GameObject ResolveWeaponTemplate(
            ShipFamilyDefinition sourceFamily,
            int hostIndex,
            IReadOnlyList<string> extraComponentIds)
        {
            if (sourceFamily == null)
                return null;

            string extraId = FindWeaponComponentId(sourceFamily, hostIndex, extraComponentIds);
            if (!string.IsNullOrEmpty(extraId))
            {
                GameObject fromExtra = ShipFamilyPartVisualCache.GetOrCreateTemplate(
                    sourceFamily, extraId, shipLevel: 1);
                if (fromExtra != null)
                    return fromExtra;
            }

            string catalogId = FindWeaponComponentId(sourceFamily, hostIndex, extras: null);
            if (!string.IsNullOrEmpty(catalogId))
            {
                GameObject fromCatalog = ShipFamilyPartVisualCache.GetOrCreateTemplate(
                    sourceFamily, catalogId, shipLevel: 1);
                if (fromCatalog != null)
                    return fromCatalog;
            }

            string wanted = "Weapon_" + hostIndex;
            GameObject template = ShipFamilyPartVisualCache.GetOrCreateTemplate(
                sourceFamily, sourceFamily.familyId + "_" + wanted, shipLevel: 1);
            if (template != null)
                return template;
            return ShipFamilyPartVisualCache.GetOrCreateTemplate(sourceFamily, wanted, shipLevel: 1);
        }

        /// <summary>
        /// Best weapon component id on <paramref name="sourceFamily"/> for this barrel.
        /// Exact index, else closest at or below, else the highest (same rule as
        /// <see cref="ShipFamilyPartMatch.CollectBestHostSlotsForType"/>).
        /// When <paramref name="extras"/> is set, only purchased ids from that family.
        /// </summary>
        static string FindWeaponComponentId(
            ShipFamilyDefinition sourceFamily,
            int hostIndex,
            IReadOnlyList<string> extras)
        {
            if (sourceFamily == null || string.IsNullOrWhiteSpace(sourceFamily.familyId))
                return null;

            string exact = null;
            string bestAtOrBelow = null;
            int bestAtOrBelowIdx = -1;
            string bestAny = null;
            int bestAnyIdx = -1;

            if (extras != null)
            {
                for (int i = 0; i < extras.Count; i++)
                    ConsiderWeaponId(extras[i], sourceFamily, hostIndex, requireFamilyOwner: true,
                        ref exact, ref bestAtOrBelow, ref bestAtOrBelowIdx, ref bestAny, ref bestAnyIdx);
            }
            else if (sourceFamily.components != null)
            {
                for (int i = 0; i < sourceFamily.components.Count; i++)
                {
                    ShipFamilyComponentEntry entry = sourceFamily.components[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                        continue;
                    ConsiderWeaponId(entry.componentId, sourceFamily, hostIndex, requireFamilyOwner: false,
                        ref exact, ref bestAtOrBelow, ref bestAtOrBelowIdx, ref bestAny, ref bestAnyIdx);
                }
            }

            if (!string.IsNullOrEmpty(exact))
                return exact;
            if (!string.IsNullOrEmpty(bestAtOrBelow))
                return bestAtOrBelow;
            return bestAny;
        }

        static void ConsiderWeaponId(
            string componentId,
            ShipFamilyDefinition sourceFamily,
            int hostIndex,
            bool requireFamilyOwner,
            ref string exact,
            ref string bestAtOrBelow,
            ref int bestAtOrBelowIdx,
            ref string bestAny,
            ref int bestAnyIdx)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return;
            if (requireFamilyOwner)
            {
                if (!TryFindSourceFamily(componentId, out ShipFamilyDefinition owner) || owner == null)
                    return;
                if (!string.Equals(owner.familyId, sourceFamily.familyId, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            if (!ShipFamilyPartMatch.TryParseSlotName(
                    componentId, sourceFamily.familyId, out string partType, out int index, out _))
                return;
            if (!ShipFamilyPartMatch.IsWeaponPartType(partType))
                return;

            if (index == hostIndex && exact == null)
                exact = componentId;
            if (index <= hostIndex && index > bestAtOrBelowIdx)
            {
                bestAtOrBelowIdx = index;
                bestAtOrBelow = componentId;
            }

            if (index > bestAnyIdx)
            {
                bestAnyIdx = index;
                bestAny = componentId;
            }
        }

        /// <summary>
        /// True when <paramref name="slot"/> sits under another collected slot
        /// (inner barrel of a Weapon assembly).
        /// </summary>
        static bool IsNestedUnderAnotherSlot(Transform slot, List<ShipFamilyPartMatch.Slot> slots)
        {
            if (slot == null || slots == null)
                return false;

            Transform walk = slot.parent;
            while (walk != null)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i].Transform == walk)
                        return true;
                }

                walk = walk.parent;
            }

            return false;
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

            // Parent assembly and a nested barrel of the same type both match.
            // DestroyImmediate of the parent, then the child, asserts and aborts
            // the apply — the socket is gone and later discards have nothing to restore.
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                if (IsNestedUnderAnotherSlot(slots[i].Transform, slots))
                    slots.RemoveAt(i);
            }

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

            CollectHostSlots(root, hostFamilyPrefix, HostSlots);
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
        /// Puts the host mesh back on remapped hardpoints the store / hull bank no
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
            bool restoreHostWeapons)
        {
            if (root == null)
                return false;

            CollectHostSlots(root, hostFamilyPrefix, HostSlots);
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

                // Leave guns unless this is the confirmed hull bank (restore).
                if (isWeapon && !restoreHostWeapons)
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
        /// True when this live child was swapped in by a purchase or B-cycle
        /// (<see cref="ShipPartVisualSource"/>). Same-family gear still stamps the
        /// marker — discard must restore those sockets too. Unmarked parts are host originals.
        /// </summary>
        static bool IsRemappedSlot(Transform slot, ShipFamilyDefinition hostFamily)
        {
            _ = hostFamily;
            if (slot == null)
                return false;
            var marker = slot.GetComponent<ShipPartVisualSource>();
            return marker != null && !string.IsNullOrWhiteSpace(marker.sourceFamilyId);
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

            // A bad parent path can nest the original under the mesh we are about to delete.
            if (restored.transform.IsChildOf(liveSlot))
                restored.transform.SetParent(liveParent, false);

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
            // Children of a purchased mesh keep source-family names and must not
            // be cached as the hull's original (that overwrite is how deletes
            // put the bought part back).
            if (IsUnderRemappedAncestor(hostSlot))
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

            List<Material> teamMats = family.GetColorizeBaseMaterials();
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

        /// <summary>
        /// Host slots for remap / restore. Children inside an already purchased mesh
        /// keep source-family names and must not be matched again — a later gear buy
        /// was destroying those guts until the hull had no parts left.
        /// </summary>
        static void CollectHostSlots(Transform root, string familyPrefix, List<ShipFamilyPartMatch.Slot> dest)
        {
            ShipFamilyPartMatch.CollectSlots(root, familyPrefix, dest);
            if (dest == null)
                return;

            for (int i = dest.Count - 1; i >= 0; i--)
            {
                Transform t = dest[i].Transform;
                if (t != null && IsUnderRemappedAncestor(t))
                    dest.RemoveAt(i);
            }
        }

        /// <summary>True when a parent (not this transform) carries a purchase / B-cycle marker.</summary>
        static bool IsUnderRemappedAncestor(Transform t)
        {
            if (t == null)
                return false;

            Transform walk = t.parent;
            while (walk != null)
            {
                var marker = walk.GetComponent<ShipPartVisualSource>();
                if (marker != null && !string.IsNullOrWhiteSpace(marker.sourceFamilyId))
                    return true;
                if (walk.name == ShipFamilyPartMatch.OriginalStashName)
                    return false;
                walk = walk.parent;
            }

            return false;
        }

        /// <summary>
        /// Puts Bind-time originals back when the live socket is gone entirely
        /// (destroyed as a child of a swapped part, or lost when a nested destroy aborted).
        /// Shallow parents first so a restored engine brings its own guns and we
        /// do not also drop a second copy of those guns.
        /// </summary>
        static bool ResurrectMissingStashedSlots(
            Transform root,
            ShipFamilyDefinition hostFamily,
            TeamId team,
            Transform originalStash)
        {
            if (root == null || originalStash == null)
                return false;

            StashEntryScratch.Clear();
            for (int i = 0; i < originalStash.childCount; i++)
            {
                Transform child = originalStash.GetChild(i);
                if (child != null && child.GetComponent<ShipPartOriginalStashEntry>() != null)
                    StashEntryScratch.Add(child);
            }

            for (int i = 1; i < StashEntryScratch.Count; i++)
            {
                Transform key = StashEntryScratch[i];
                int keyDepth = PathDepth(key.GetComponent<ShipPartOriginalStashEntry>().ParentPath);
                int j = i - 1;
                while (j >= 0)
                {
                    int depth = PathDepth(StashEntryScratch[j].GetComponent<ShipPartOriginalStashEntry>().ParentPath);
                    if (depth <= keyDepth)
                        break;
                    StashEntryScratch[j + 1] = StashEntryScratch[j];
                    j--;
                }

                StashEntryScratch[j + 1] = key;
            }

            CaptureLiveSlotNames(root);
            bool changed = false;
            for (int i = 0; i < StashEntryScratch.Count; i++)
            {
                ShipPartOriginalStashEntry entry = StashEntryScratch[i].GetComponent<ShipPartOriginalStashEntry>();
                if (entry == null)
                    continue;

                string hostName = string.IsNullOrEmpty(entry.HostSlotName)
                    ? entry.gameObject.name
                    : entry.HostSlotName;
                if (string.IsNullOrEmpty(hostName) || LiveSlotNames.Contains(hostName))
                    continue;

                Transform parent = ResolveRelative(root, entry.ParentPath);
                if (parent == null || IsUnderStash(parent))
                    parent = root;

                // Still under a purchased assembly — that stash copy includes this
                // part and comes back when the gear is discarded. Dropping it now
                // would stack a second set on the bought mesh.
                if (parent != root
                    && (IsRemappedSlot(parent, hostFamily) || IsUnderRemappedAncestor(parent)))
                    continue;

                GameObject restored = Object.Instantiate(entry.gameObject, parent, false);
                var leftover = restored.GetComponent<ShipPartOriginalStashEntry>();
                if (leftover != null)
                    DestroyNow(leftover);
                StripVisualSource(restored);
                StripJetVfxInstances(restored);
                restored.name = hostName;
                restored.SetActive(true);
                restored.transform.localPosition = entry.transform.localPosition;
                restored.transform.localRotation = entry.transform.localRotation;
                restored.transform.SetSiblingIndex(
                    Mathf.Clamp(entry.SiblingIndex, 0, parent.childCount - 1));
                ApplyHostTeamMaterials(hostFamily, restored, team);
                LiveSlotNames.Add(hostName);
                RememberHierarchyNames(restored.transform);
                changed = true;
            }

            return changed;
        }

        static int PathDepth(string path)
        {
            if (string.IsNullOrEmpty(path))
                return 0;
            int depth = 1;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/')
                    depth++;
            }

            return depth;
        }

        static void CaptureLiveSlotNames(Transform root)
        {
            LiveSlotNames.Clear();
            RememberHierarchyNames(root);
            LiveSlotNames.Remove(root.name);
        }

        static void RememberHierarchyNames(Transform t)
        {
            if (t == null)
                return;

            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (child == null || child.name == ShipFamilyPartMatch.OriginalStashName)
                    continue;
                LiveSlotNames.Add(child.name);
                RememberHierarchyNames(child);
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
