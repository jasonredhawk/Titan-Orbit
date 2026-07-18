# NetCode GhostSpawnSystem patch (Titan Orbit)

## Problem

On dedicated Relay late-join, the Windows client hard-crashes in Burst `GhostSpawnSystem` right after go-in-game. Stock NetCode drains the entire delayed-interpolate Instantiates queue in one frame when `ClientSpawnTick` values are already in the past (map generated before the client joined). Hundreds of asteroid Instantiates in one Burst update → native `Crash!!!`.

`GhostSendSystemData.MaxSendChunks` is **not** enough: it limits *chunks*, and one asteroid archetype chunk can still hold dozens/hundreds of entities.

## Patch

File: `Library/PackageCache/com.unity.netcode@6437771c174a/Runtime/Snapshot/GhostSpawnSystem.cs`

- Cap delayed Instantiates to **12 per frame** (leave the rest queued).
- Bounds-check `GhostType` before indexing prefab / serializer buffers.

Canonical copy: `tools/netcode-patches/GhostSpawnSystem.cs`

## Re-apply after PackageCache restore

Unity menu: **Titan Orbit → NetCode → Re-apply GhostSpawnSystem rate-limit patch**

Then rebuild the **Windows client** (this system is client-only). Headless server rebuild is not required for the Instantiates cap alone, but client/server EntityScenes must still match for ghost hashes.
