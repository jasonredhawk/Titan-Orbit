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

Marker: `GhostSpawnSystem.TitanOrbitGhostSpawnPatchId` → `TO_GhostSpawn_v9_transformsAlwaysOn`

Changes:

1. No Burst on `OnUpdate` (managed Instantiates).
2. Re-fetch `GhostCollectionPrefabSerializer` after structural changes.
3. Cap delayed Instantiates to **1 per frame**.
4. Placeholders drain **stock-style** (do not defer/re-queue — that broke the ghost map in v7).
5. **Safe SnapshotDataBuffer copy** — rebuild buffer instead of `ResizeUninitialized` on Instantiated prefab headers.
6. Predicted ghosts use delayed Instantiates via placeholders (registered same frame).
7. Player.log when creating ≥16 placeholders.
8. **Project companion (v9):** never disable `TransformSystemGroup` during join settle — Instantiates=1 keeps LocalToWorld warm; disable→re-enable after ~700 Instantiates hard-crashes Windows Burst LTW.

## Companion project systems (outside this package)

| Area | Type |
|------|------|
| Server stream | `TitanOrbitGhostSendTuneSystem`, `TitanOrbitGhostDistanceImportanceBootstrapSystem` |
| Client settle | `TitanOrbitClientJoinTransformGateSystem`, `ClientJoinSettleState` (hybrid/UI gate only) |
| Hybrid Instantiates | `EcsWorldVisualizer` + `ClientJoinSettleCache` |

Canonical backup: `tools/netcode-patches/GhostSpawnSystem.cs`

Menu: **Titan Orbit → NetCode → Re-apply GhostSpawnSystem patch**

## Post-build verification (Windows client)

Search UTF-16 strings in:

- `BuildOutput/Client/windows/TitanOrbit_Data/Managed/Unity.NetCode.dll` → `TO_GhostSpawn_v9_transformsAlwaysOn`
- `…/TitanOrbit.ECS.dll` → `JoinSettle` / `ClientJoinSettle`

Join `Player.log` should show:

1. `[JoinSettle] Settling ON (hybrid/UI gate; TransformSystemGroup stays enabled)…`
2. `[MapSessionMeta] Client latched totals…`
3. Optional `[TO_GhostSpawn] Created N placeholders…`
4. `[JoinSettle] Settling OFF…` (no `Crash!!!`, and **no** `TransformSystemGroup RE-ENABLED`)

## Do not

- Switch manifest back to registry `1.14.0` without re-applying the patch.
- “Upgrade” NetCode by deleting this folder without porting the GhostSpawn changes.
- Disable `TransformSystemGroup` for join Instantiates.
