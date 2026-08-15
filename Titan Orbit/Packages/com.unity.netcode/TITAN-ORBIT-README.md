# Titan Orbit — embedded Netcode for Entities

This folder is a **project-local copy** of `com.unity.netcode` 1.14.0.

`Packages/manifest.json` pins `"com.unity.netcode": "file:com.unity.netcode"`.

## Patch markers

| Area | Id / note |
|------|-----------|
| GhostSpawn | `GhostSpawnSystem.TitanOrbitGhostSpawnPatchId` → `TO_GhostSpawn_v16_requeueFailedInstantiate` |
| RpcSystem | `TO_RpcSystem_v1_skipUnknownHash` — skip unknown/mismatched RPCs (no `InvalidRpc` disconnect) |

## What the GhostSpawn patch does

1. No Burst on `OnUpdate` (managed Instantiates).
2. Safe SnapshotDataBuffer copy (`TryCopySnapshotBufferSafe`).
3. **16 Instantiates/frame** from delayed queues (ships/planets/nearby gems). Asteroids are seed-hydrated, not Instantiates.
4. **CreateEntity-all** placeholders (register every GhostID in `SpawnedGhostEntityMap`).
5. `TitanOrbitJoinLoadCounters` for loading UI (avoids asteroid `ToEntityArray`).

## What the RpcSystem patch does

Stock NetCode disconnects on unknown RPC hash / deserialize size mismatch. GCE headless with an older `PeopleTransportSpawnRpc` (hash `1026046134438292813`) kicked Windows clients to Main Menu on orbit people-load. Titan Orbit **skips** the payload and keeps the connection. After matching client+server rebuilds, the RPC resolves normally and VFX works.

## Why CreateEntity-cap+requeue is forbidden

Requeue without map registration → `baseline for a ghost we do not have` → Burst Crash!!! (2026-07-18). CreateEntity-all + Instantiates 16/frame is the safe pair for the live ghost set. Transform stays **ON**.

## Server companion (required for dense maps)

`TitanOrbitGhostDistanceImportanceBootstrapSystem.TileSizeWorld` must be ≪ map size (~48). TileSize 512 on a ~340 map made the whole asteroid field one chunk, so `MaxSendChunks=1` still sent dozens of spawns per tick. Gems use nearby relevancy + tractor pin — never all-map Instantiates.

## Verify

Windows client `Unity.NetCode.dll` must contain `TO_GhostSpawn_v16_requeueFailedInstantiate`.
`Player.log`: placeholders + Instantiates 16/frame, **no** native `Crash!!!`.
