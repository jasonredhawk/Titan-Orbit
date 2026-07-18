# Titan Orbit — embedded Netcode for Entities

This folder is a **project-local copy** of `com.unity.netcode` 1.14.0.

`Packages/manifest.json` pins `"com.unity.netcode": "file:com.unity.netcode"`.

## Patch marker

`GhostSpawnSystem.TitanOrbitGhostSpawnPatchId` → `TO_GhostSpawn_v12_joinLoadCounters`

## What the patch does

1. No Burst on `OnUpdate` (managed Instantiates).
2. Safe SnapshotDataBuffer copy (`TryCopySnapshotBufferSafe`).
3. **1 Instantiates/frame** from delayed queues.
4. **CreateEntity-all** placeholders (register every GhostID in `SpawnedGhostEntityMap`).
5. `TitanOrbitJoinLoadCounters` for loading UI (avoids asteroid `ToEntityArray`).

## Why CreateEntity-cap+requeue is forbidden

Requeue without map registration → `baseline for a ghost we do not have` → Burst Crash!!! (2026-07-18). CreateEntity-all + Instantiates 1/frame is the safe pair; project keeps Burst LTW / TransformSystemGroup off via TransformQuarantine.

## Server companion (required for dense maps)

`TitanOrbitGhostDistanceImportanceBootstrapSystem.TileSizeWorld` must be ≪ map size (~48). TileSize 512 on a ~340 map made the whole asteroid field one chunk, so `MaxSendChunks=1` still sent dozens of spawns per tick.

## Verify

Windows client `Unity.NetCode.dll` must contain `TO_GhostSpawn_v12_joinLoadCounters`.  
`Player.log`: placeholders + Instantiates 1/frame, Settling OFF, **no** `TransformSystemGroup RE-ENABLED`, **no** `Crash!!!`.
