# Titan Orbit — embedded Netcode for Entities

This folder is a **project-local copy** of `com.unity.netcode` 1.14.0.

`Packages/manifest.json` pins `"com.unity.netcode": "file:com.unity.netcode"`.

## Patch marker

`GhostSpawnSystem.TitanOrbitGhostSpawnPatchId` → `TO_GhostSpawn_v10_placeholderCap1`

## What the patch does

1. No Burst on `OnUpdate` (managed Instantiates).
2. Safe SnapshotDataBuffer copy (`TryCopySnapshotBufferSafe`).
3. **1 Instantiates/frame** from delayed queues.
4. **1 CreateEntity placeholder/frame** — requeue leftovers; register each CreateEntity in the ghost map.
5. Never disable `TransformSystemGroup` during join (project settle code).

## Why CreateEntity must be capped

With LTW always on, CreateEntity×56 in one frame hard-crashed Windows Burst (`Crash!!!` immediately after the placeholder log). Soft try/catch cannot catch that — native AV.

## Server companion (required for dense maps)

`TitanOrbitGhostDistanceImportanceBootstrapSystem.TileSizeWorld` must be ≪ map size. TileSize 512 on a ~340 map made the whole asteroid field one chunk, so `MaxSendChunks=1` still sent dozens of spawns per tick.

## Verify

Windows client `Unity.NetCode.dll` must contain `TO_GhostSpawn_v10_placeholderCap1`.  
`Player.log`: `[TO_GhostSpawn] Placeholder cap: created 1/frame, re-queued N…` then join completes with **no** `Crash!!!`.
