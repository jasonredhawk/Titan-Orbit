using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Profiling;

namespace TitanOrbit.Diagnostics
{
    /// <summary>
    /// Logs WASM / managed memory every 30 seconds on a WebGL player.
    /// A climbing <c>allocatedMB</c> with a flat ghost count is a client leak.
    /// The browser heap does not shrink after C# GC.
    /// </summary>
    static class WebGlHeapHeartbeat
    {
        const float IntervalSeconds = 30f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            // Editor Play writes the probe file. WebGL player POSTs the same line.
            if (Application.platform != RuntimePlatform.WebGLPlayer && !Application.isEditor)
                return;

            var go = new GameObject(nameof(WebGlHeapHeartbeat));
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            float _next;
            bool _hasPrev;
            long _prevAllocated;
            long _prevReserved;
            long _prevMonoUsed;
            long _prevMonoHeap;
            long _prevGfx;
            int _prevMeshes;
            int _prevMaterials;
            int _prevTextures;
            int _prevRenderTextures;
            int _prevGameObjects;
            int _prevAudioClips;
            int _prevParticles;
            readonly Dictionary<string, int> _matNames = new Dictionary<string, int>(128);
            readonly Dictionary<string, int> _prevMatNames = new Dictionary<string, int>(128);
            readonly Dictionary<string, long> _texBytes = new Dictionary<string, long>(128);
            readonly Dictionary<string, long> _prevTexBytes = new Dictionary<string, long>(128);
            long _prevTexBytesTotal;

            void Update()
            {
                if (Time.unscaledTime < _next)
                    return;

                _next = Time.unscaledTime + IntervalSeconds;
                long allocated = Profiler.GetTotalAllocatedMemoryLong();
                long reserved = Profiler.GetTotalReservedMemoryLong();
                long monoUsed = Profiler.GetMonoUsedSizeLong();
                long monoHeap = Profiler.GetMonoHeapSizeLong();
                long gfx = Profiler.GetAllocatedMemoryForGraphicsDriver();
                long monoGc = GC.GetTotalMemory(false);
                Debug.Log(
                    "[WebGlHeap] allocatedMB=" + (allocated / 1048576L) +
                    " reservedMB=" + (reserved / 1048576L) +
                    " monoMB=" + (monoGc / 1048576L));

                // #region agent log
                int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                Material[] materialAssets = Resources.FindObjectsOfTypeAll<Material>();
                int materials = materialAssets.Length;
                string matGrow = CollectMaterialGrowth(materialAssets);
                Texture2D[] textureAssets = Resources.FindObjectsOfTypeAll<Texture2D>();
                int textures = textureAssets.Length;
                string texGrow = CollectTextureGrowth(textureAssets, out long texBytes);
                int sprites = Resources.FindObjectsOfTypeAll<Sprite>().Length;
                int cubemaps = Resources.FindObjectsOfTypeAll<Cubemap>().Length;
                int renderTextures = Resources.FindObjectsOfTypeAll<RenderTexture>().Length;
                int gameObjects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
                int audioClips = Resources.FindObjectsOfTypeAll<AudioClip>().Length;
                int particles = Resources.FindObjectsOfTypeAll<ParticleSystem>().Length;
                long dAllocated = _hasPrev ? allocated - _prevAllocated : 0L;
                long dReserved = _hasPrev ? reserved - _prevReserved : 0L;
                long dMonoUsed = _hasPrev ? monoUsed - _prevMonoUsed : 0L;
                long dMonoHeap = _hasPrev ? monoHeap - _prevMonoHeap : 0L;
                long dGfx = _hasPrev ? gfx - _prevGfx : 0L;
                int dMeshes = _hasPrev ? meshes - _prevMeshes : 0;
                int dMaterials = _hasPrev ? materials - _prevMaterials : 0;
                int dTextures = _hasPrev ? textures - _prevTextures : 0;
                long dTexBytes = _hasPrev ? texBytes - _prevTexBytesTotal : 0L;
                int dRenderTextures = _hasPrev ? renderTextures - _prevRenderTextures : 0;
                int dGameObjects = _hasPrev ? gameObjects - _prevGameObjects : 0;
                int dAudioClips = _hasPrev ? audioClips - _prevAudioClips : 0;
                int dParticles = _hasPrev ? particles - _prevParticles : 0;
                _prevAllocated = allocated;
                _prevReserved = reserved;
                _prevMonoUsed = monoUsed;
                _prevMonoHeap = monoHeap;
                _prevGfx = gfx;
                _prevMeshes = meshes;
                _prevMaterials = materials;
                _prevTextures = textures;
                _prevTexBytesTotal = texBytes;
                _prevRenderTextures = renderTextures;
                _prevGameObjects = gameObjects;
                _prevAudioClips = audioClips;
                _prevParticles = particles;
                _hasPrev = true;

                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json =
                    "{\"sessionId\":\"caa453\",\"runId\":\"pre-fix\",\"hypothesisId\":\"sample\"," +
                    "\"location\":\"WebGlHeapHeartbeat.cs:Update\",\"message\":\"memtrace\"," +
                    "\"timestamp\":" + nowMs +
                    ",\"data\":{\"allocatedMB\":" + (allocated / 1048576L) +
                    ",\"reservedMB\":" + (reserved / 1048576L) +
                    ",\"monoUsedMB\":" + (monoUsed / 1048576L) +
                    ",\"monoHeapMB\":" + (monoHeap / 1048576L) +
                    ",\"monoGcMB\":" + (monoGc / 1048576L) +
                    ",\"gfxMB\":" + (gfx / 1048576L) +
                    ",\"dAllocatedMB\":" + (dAllocated / 1048576L) +
                    ",\"dReservedMB\":" + (dReserved / 1048576L) +
                    ",\"dMonoUsedMB\":" + (dMonoUsed / 1048576L) +
                    ",\"dMonoHeapMB\":" + (dMonoHeap / 1048576L) +
                    ",\"dGfxMB\":" + (dGfx / 1048576L) +
                    ",\"meshes\":" + meshes +
                    ",\"materials\":" + materials +
                    ",\"textures\":" + textures +
                    ",\"texBytesMB\":" + (texBytes / 1048576L) +
                    ",\"dTexBytesMB\":" + (dTexBytes / 1048576L) +
                    ",\"sprites\":" + sprites +
                    ",\"cubemaps\":" + cubemaps +
                    ",\"renderTextures\":" + renderTextures +
                    ",\"gameObjects\":" + gameObjects +
                    ",\"audioClips\":" + audioClips +
                    ",\"particles\":" + particles +
                    ",\"dMeshes\":" + dMeshes +
                    ",\"dMaterials\":" + dMaterials +
                    ",\"dTextures\":" + dTextures +
                    ",\"dRenderTextures\":" + dRenderTextures +
                    ",\"dGameObjects\":" + dGameObjects +
                    ",\"dAudioClips\":" + dAudioClips +
                    ",\"dParticles\":" + dParticles +
                    ",\"gc0\":" + GC.CollectionCount(0) +
                    ",\"matGrow\":{" + matGrow + "}" +
                    ",\"texGrow\":{" + texGrow + "}" +
                    ",\"applyColorCalls\":" + MaterialCloneProbe.ApplyColorCalls +
                    ",\"applyColorClones\":" + MaterialCloneProbe.ApplyColorClones +
                    ",\"applyColorRing\":" + MaterialCloneProbe.ApplyColorRingClones +
                    ",\"applyColorTrail\":" + MaterialCloneProbe.ApplyColorTrailClones +
                    ",\"trailReads\":" + MaterialCloneProbe.TrailReads +
                    ",\"trailClones\":" + MaterialCloneProbe.TrailClones +
                    ",\"fixClones\":" + MaterialCloneProbe.FixClones +
                    ",\"tracerShells\":" + MaterialCloneProbe.TracerShells +
                    ",\"oneShotShells\":" + MaterialCloneProbe.OneShotShells +
                    ",\"prepareCold\":" + MaterialCloneProbe.PrepareCold +
                    ",\"prepareWarm\":" + MaterialCloneProbe.PrepareWarm +
                    ",\"fontMatCalls\":" + MaterialCloneProbe.FontMaterialCalls +
                    ",\"fontMatClones\":" + MaterialCloneProbe.FontMaterialClones +
                    ",\"planetWebGl\":" + ReadPlanetWebGlCreates() +
                    "}}";
                Debug.Log("[MemTrace] " + json);
                WriteProbe(json);
                WriteAttribution(meshes, materials, textures, gameObjects, monoUsed, allocated, gfx);
                // #endregion
            }

            /// <summary>
            /// Names of materials that increased since the previous sample.
            /// Instance suffix is stripped so clones of one asset group together.
            /// </summary>
            string CollectMaterialGrowth(Material[] materials)
            {
                _matNames.Clear();
                for (int i = 0; i < materials.Length; i++)
                {
                    Material mat = materials[i];
                    if (mat == null)
                        continue;
                    string key = mat.name ?? string.Empty;
                    const string suffix = " (Instance)";
                    if (key.EndsWith(suffix, StringComparison.Ordinal))
                        key = key.Substring(0, key.Length - suffix.Length);
                    if (key.Length > 48)
                        key = key.Substring(0, 48);
                    _matNames.TryGetValue(key, out int n);
                    _matNames[key] = n + 1;
                }

                string bestKey = string.Empty;
                int bestDelta = 0;
                string secondKey = string.Empty;
                int secondDelta = 0;
                if (_hasPrev)
                {
                    foreach (KeyValuePair<string, int> pair in _matNames)
                    {
                        _prevMatNames.TryGetValue(pair.Key, out int prev);
                        int delta = pair.Value - prev;
                        if (delta > bestDelta)
                        {
                            secondKey = bestKey;
                            secondDelta = bestDelta;
                            bestKey = pair.Key;
                            bestDelta = delta;
                        }
                        else if (delta > secondDelta)
                        {
                            secondKey = pair.Key;
                            secondDelta = delta;
                        }
                    }
                }

                _prevMatNames.Clear();
                foreach (KeyValuePair<string, int> pair in _matNames)
                    _prevMatNames[pair.Key] = pair.Value;

                if (bestDelta <= 0)
                    return "\"none\":0";
                string json = "\"" + Escape(bestKey) + "\":" + bestDelta;
                if (secondDelta > 0)
                    json += ",\"" + Escape(secondKey) + "\":" + secondDelta;
                return json;
            }

            /// <summary>
            /// Texture names whose runtime bytes increased since the previous sample.
            /// Values are megabytes. Pixel data on WebGL sits in the WASM heap.
            /// </summary>
            string CollectTextureGrowth(Texture2D[] textures, out long totalBytes)
            {
                _texBytes.Clear();
                totalBytes = 0L;
                for (int i = 0; i < textures.Length; i++)
                {
                    Texture2D tex = textures[i];
                    if (tex == null)
                        continue;
                    long bytes = Profiler.GetRuntimeMemorySizeLong(tex);
                    totalBytes += bytes;
                    string key = tex.name ?? string.Empty;
                    const string suffix = " (Instance)";
                    if (key.EndsWith(suffix, StringComparison.Ordinal))
                        key = key.Substring(0, key.Length - suffix.Length);
                    if (key.Length > 48)
                        key = key.Substring(0, 48);
                    _texBytes.TryGetValue(key, out long n);
                    _texBytes[key] = n + bytes;
                }

                string bestKey = string.Empty;
                long bestDelta = 0L;
                string secondKey = string.Empty;
                long secondDelta = 0L;
                if (_hasPrev)
                {
                    foreach (KeyValuePair<string, long> pair in _texBytes)
                    {
                        _prevTexBytes.TryGetValue(pair.Key, out long prev);
                        long delta = pair.Value - prev;
                        if (delta > bestDelta)
                        {
                            secondKey = bestKey;
                            secondDelta = bestDelta;
                            bestKey = pair.Key;
                            bestDelta = delta;
                        }
                        else if (delta > secondDelta)
                        {
                            secondKey = pair.Key;
                            secondDelta = delta;
                        }
                    }
                }

                _prevTexBytes.Clear();
                foreach (KeyValuePair<string, long> pair in _texBytes)
                    _prevTexBytes[pair.Key] = pair.Value;

                if (bestDelta <= 0L)
                    return "\"none\":0";
                string json = "\"" + Escape(bestKey) + "\":" + (bestDelta / 1048576L);
                if (secondDelta > 0L)
                    json += ",\"" + Escape(secondKey) + "\":" + (secondDelta / 1048576L);
                return json;
            }

            static int ReadPlanetWebGlCreates()
            {
                Type planet = Type.GetType("SpaceGraphicsToolkit.SgtPlanet, SpaceGraphicsToolkit");
                if (planet == null)
                    return -1;
                FieldInfo field = planet.GetField(
                    "DebugWebGlMaterialCreates",
                    BindingFlags.Public | BindingFlags.Static);
                if (field == null)
                    return -1;
                return (int)field.GetValue(null);
            }

            /// <summary>
            /// One forced collection plus live counts. Compares rooted managed memory
            /// with store refreshes, proxies, and mesh bytes.
            /// </summary>
            static void WriteAttribution(
                int meshes,
                int materials,
                int textures,
                int gameObjects,
                long monoBefore,
                long allocatedBefore,
                long gfxBefore)
            {
                // #region agent log
                long meshBytes = SumRuntimeBytes<Mesh>();
                GC.Collect();
                long monoAfter = Profiler.GetMonoUsedSizeLong();
                long allocatedAfter = Profiler.GetTotalAllocatedMemoryLong();
                long gfxAfter = Profiler.GetAllocatedMemoryForGraphicsDriver();
                int popups = CountType("TitanOrbit.Game.FloatingCountPopup, TitanOrbit.Game");
                int proxies = ReadStaticInt(
                    "TitanOrbit.Game.EcsWorldVisualizer, TitanOrbit.Game",
                    "WorldBodyProxyCount");
                int parkedAsteroids = ReadDictionaryCount(
                    "TitanOrbit.Game.AsteroidVisualPool, TitanOrbit.Game",
                    "HiddenBySlot");
                string worlds = ReadWorldEntityCounts();
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json =
                    "{\"sessionId\":\"caa453\",\"runId\":\"pre-fix\",\"hypothesisId\":\"attr\"," +
                    "\"location\":\"WebGlHeapHeartbeat.cs:WriteAttribution\",\"message\":\"attribution\"," +
                    "\"timestamp\":" + nowMs +
                    ",\"data\":{\"monoBeforeMB\":" + (monoBefore / 1048576L) +
                    ",\"monoAfterGcMB\":" + (monoAfter / 1048576L) +
                    ",\"reclaimMB\":" + ((monoBefore - monoAfter) / 1048576L) +
                    ",\"allocatedBeforeMB\":" + (allocatedBefore / 1048576L) +
                    ",\"allocatedAfterGcMB\":" + (allocatedAfter / 1048576L) +
                    ",\"gfxBeforeMB\":" + (gfxBefore / 1048576L) +
                    ",\"gfxAfterGcMB\":" + (gfxAfter / 1048576L) +
                    ",\"meshBytesMB\":" + (meshBytes / 1048576L) +
                    ",\"meshes\":" + meshes +
                    ",\"materials\":" + materials +
                    ",\"textures\":" + textures +
                    ",\"gameObjects\":" + gameObjects +
                    ",\"popups\":" + popups +
                    ",\"proxies\":" + proxies +
                    ",\"parkedAsteroids\":" + parkedAsteroids +
                    ",\"storeRefreshes\":" + MaterialCloneProbe.StoreRefreshes +
                    ",\"storeLabelSets\":" + MaterialCloneProbe.StoreLabelSets +
                    ",\"worlds\":{" + worlds + "}}}";
                Debug.Log("[MemAttr] " + json);
                WriteProbe(json);
                // #endregion
            }

            static long SumRuntimeBytes<T>() where T : UnityEngine.Object
            {
                T[] assets = Resources.FindObjectsOfTypeAll<T>();
                long total = 0L;
                for (int i = 0; i < assets.Length; i++)
                {
                    if (assets[i] != null)
                        total += Profiler.GetRuntimeMemorySizeLong(assets[i]);
                }

                return total;
            }

            static int CountType(string assemblyQualifiedName)
            {
                Type type = Type.GetType(assemblyQualifiedName);
                if (type == null)
                    return -1;
                return Resources.FindObjectsOfTypeAll(type).Length;
            }

            static int ReadStaticInt(string assemblyQualifiedName, string propertyName)
            {
                Type type = Type.GetType(assemblyQualifiedName);
                if (type == null)
                    return -1;
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                if (property == null)
                    return -1;
                object value = property.GetValue(null);
                return value is int n ? n : -1;
            }

            static int ReadDictionaryCount(string assemblyQualifiedName, string fieldName)
            {
                Type type = Type.GetType(assemblyQualifiedName);
                if (type == null)
                    return -1;
                FieldInfo field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                    return -1;
                object value = field.GetValue(null);
                if (value is System.Collections.IDictionary dictionary)
                    return dictionary.Count;
                return -1;
            }

            static string ReadWorldEntityCounts()
            {
                Type worldType = Type.GetType("Unity.Entities.World, Unity.Entities");
                if (worldType == null)
                    return "\"missing\":-1";
                PropertyInfo all = worldType.GetProperty("All", BindingFlags.Public | BindingFlags.Static);
                if (all == null || all.GetValue(null) is not System.Collections.IEnumerable worlds)
                    return "\"missing\":-1";

                var sb = new StringBuilder(128);
                int written = 0;
                foreach (object world in worlds)
                {
                    if (world == null)
                        continue;
                    Type type = world.GetType();
                    PropertyInfo nameProp = type.GetProperty("Name");
                    PropertyInfo emProp = type.GetProperty("EntityManager");
                    PropertyInfo createdProp = type.GetProperty("IsCreated");
                    if (createdProp != null && createdProp.GetValue(world) is bool created && !created)
                        continue;
                    string name = nameProp?.GetValue(world) as string ?? "world";
                    int count = -1;
                    object em = emProp?.GetValue(world);
                    if (em != null)
                    {
                        PropertyInfo query = em.GetType().GetProperty("UniversalQuery");
                        object universal = query?.GetValue(em);
                        MethodInfo calculate = universal?.GetType().GetMethod("CalculateEntityCount");
                        if (calculate != null && calculate.Invoke(universal, null) is int n)
                            count = n;
                    }

                    if (written > 0)
                        sb.Append(',');
                    sb.Append('"').Append(Escape(name)).Append("\":").Append(count);
                    written++;
                    if (written >= 4)
                        break;
                }

                if (written == 0)
                    return "\"none\":0";
                return sb.ToString();
            }

            static void WriteProbe(string json)
            {
                // #region agent log
                try
                {
                    File.AppendAllText(
                        @"c:\Users\jason\Documents\repo\Titan-Orbit\debug-caa453.log",
                        json + "\n");
                }
                catch (Exception)
                {
                    // WebGL has no workspace filesystem. The POST below is the browser path.
                }

                var request = new UnityWebRequest(
                    "http://127.0.0.1:7774/ingest/30ccdc0d-4064-42d7-ab07-612840f5e6a2",
                    UnityWebRequest.kHttpVerbPOST);
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("X-Debug-Session-Id", "caa453");
                request.SendWebRequest();
                // #endregion
            }

            static string Escape(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return string.Empty;
                return value.Replace("\\", " ").Replace("\"", "'");
            }
        }
    }
}
