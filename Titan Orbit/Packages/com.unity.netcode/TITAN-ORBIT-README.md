# Titan Orbit — embedded Netcode for Entities

This folder is a **project-local copy** of `com.unity.netcode` 1.14.0.

## Why it is embedded

Editing `Library/PackageCache/...` is wiped whenever Unity restores packages.
That caused the Windows client GhostSpawn crash to keep coming back after “fixes.”

`Packages/manifest.json` pins:

```json
"com.unity.netcode": "file:com.unity.netcode"
```

So this tree is the source of truth checked into git.

## Titan Orbit patch (`GhostSpawnSystem.cs`)

File: `Runtime/Snapshot/GhostSpawnSystem.cs`

Marker: `GhostSpawnSystem.TitanOrbitGhostSpawnPatchId` → `TO_GhostSpawn_v4_safeSnapshotCopy`

Changes:

1. No Burst on `OnUpdate` (managed Instantiates).
2. Re-fetch `GhostCollectionPrefabSerializer` after structural changes.
3. Cap delayed Instantiates per frame (placeholders stay queued).
4. **Safe SnapshotDataBuffer copy** — rebuild buffer instead of `ResizeUninitialized` on Instantiated prefab headers (fixes Windows `FreeTracked` / `Crash!!!` in `TrySpawnFromDelayedQueue`).

Canonical backup also lives at:

`tools/netcode-patches/GhostSpawnSystem.cs`

Menu: **Titan Orbit → NetCode → Re-apply GhostSpawnSystem patch**
(copies tools → this embedded package).

## Do not

- Switch manifest back to registry `1.14.0` without re-applying the patch.
- “Upgrade” NetCode by deleting this folder without porting the GhostSpawn changes.
