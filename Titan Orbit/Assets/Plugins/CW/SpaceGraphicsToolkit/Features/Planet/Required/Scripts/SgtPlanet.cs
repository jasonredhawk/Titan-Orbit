using System.Collections.Generic;
using UnityEngine;
using CW.Common;
using SpaceGraphicsToolkit.LightAndShadow;

namespace SpaceGraphicsToolkit
{
	/// <summary>This component allows you to render a planet that has been displaced with a heightmap, and has a dynamic water level.</summary>
	[ExecuteInEditMode]
	[HelpURL(SgtCommon.HelpUrlPrefix + "SgtPlanet")]
	[AddComponentMenu(SgtCommon.ComponentMenuPrefix + "Planet")]
	public class SgtPlanet : MonoBehaviour, IOverridableSharedMaterial
	{
		class Seam
		{
			public List<int> Indices = new List<int>();

			public static Stack<Seam> Pool = new Stack<Seam>();
		}

		class Geom
		{
			public List<Seam> Seams = new List<Seam>();
		}

		/// <summary>The sphere mesh used to render the planet.</summary>
		public Mesh Mesh { set { if (mesh != value) { mesh = value; DirtyMesh(); } } get { return mesh; } } [SerializeField] private Mesh mesh;

		/// <summary>If you want the generated mesh to have a matching collider, you can specify it here.</summary>
		public MeshCollider MeshCollider { set { if (meshCollider != value) { meshCollider = value; DirtyMesh(); } } get { return meshCollider; } } [SerializeField] private MeshCollider meshCollider;

		/// <summary>The radius of the planet in local space.</summary>
		public float Radius { set { if (radius != value) { radius = value; DirtyMesh(); } } get { return radius; } } [SerializeField] private float radius = 1.0f;

		/// <summary>The material used to render the planet. For best results, this should use the SGT Planet shader.</summary>
		public Material Material
		{
			set
			{
				if (material == value)
					return;

				// Replacing the draw material drops a WebGL instance we created so the asset is not mutated.
				if (webGlDrawMaterialReady && material != null)
					material = CwHelper.Destroy(material);

				material = value;
				webGlDrawMaterialReady = false;
				nightCached = false;
				sentWaterLevel = float.NaN;
				sentNightValid = false;

				// A new planet material drops uniforms that lived on the previous instance.
				// Shared asteroid materials stay shared; they do not take a private instance.
				if (lockSharedMesh || Application.platform != RuntimePlatform.WebGLPlayer)
					return;

				var gradient = GetComponent<SgtPlanetWaterGradient>();
				if (gradient != null)
				{
					gradient.DirtyTexture();
					gradient.DirtyScale();
				}

				var waterTexture = GetComponent<SgtPlanetWaterTexture>();
				if (waterTexture != null)
					waterTexture.InvalidateWebGlBlit();
			}
			get { return material; }
		} [SerializeField] private Material material;

		/// <summary>If you want to apply a shared material (e.g. atmosphere) to this terrain, then specify it here.</summary>
		public SgtSharedMaterial SharedMaterial { set { sharedMaterial = value; } get { return sharedMaterial; } } [SerializeField] private SgtSharedMaterial sharedMaterial;

		/// <summary>Should the planet cast shadows?</summary>
		public bool CastShadows { set { castShadows = value; } get { return castShadows; } } [SerializeField] private bool castShadows = true;

		/// <summary>Should the planet receive shadows?</summary>
		public bool ReceiveShadows { set { receiveShadows = value; } get { return receiveShadows; } } [SerializeField] private bool receiveShadows = true;

		/// <summary>The current water level.
		/// 0 = Radius.
		/// 1 = Radius + Displacement.</summary>
		public float WaterLevel { set { if (waterLevel != value) { waterLevel = value; DirtyMesh(); } } get { return waterLevel; } } [Range(-2.0f, 2.0f)] [SerializeField] private float waterLevel;

		/// <summary>Should the planet mesh be displaced using the heightmap in the planet material?</summary>
		public bool Displace { set { if (displace != value) { displace = value; DirtyMesh(); } } get { return displace; } } [SerializeField] private bool displace;

		/// <summary>The maximum height displacement applied to the planet mesh when the heightmap alpha value is 1.</summary>
		public float Displacement { set { if (displacement != value) { displacement = value; DirtyMesh(); } } get { return displacement; } } [SerializeField] private float displacement = 0.1f;

		/// <summary>If you enable this then the water will not rise, instead the terrain will shrink down.</summary>
		public bool ClampWater { set { if (clampWater != value) { clampWater = value; DirtyMesh(); } } get { return clampWater; } } [SerializeField] private bool clampWater;

		public event SgtSharedMaterial.OverrideSharedMaterialSignature OnOverrideSharedMaterial;

		[System.NonSerialized]
		private Mesh generatedMesh;

		[System.NonSerialized]
		private List<Vector3> generatedPositions = new List<Vector3>();

		[System.NonSerialized]
		private List<Vector3> generatedNormals = new List<Vector3>();

		[System.NonSerialized]
		private List<Vector4> generatedTangents = new List<Vector4>();

		[System.NonSerialized]
		private SgtProperties properties = new SgtProperties();

		[System.NonSerialized]
		private bool dirtyMesh;

		// Same water level and night vector were pushed into the property block every
		// LateUpdate (once per planet and asteroid). On WebGL those native uploads are
		// not returned to the browser, so a long flight grows the WASM heap until OOM.
		[System.NonSerialized]
		private float sentWaterLevel = float.NaN;

		[System.NonSerialized]
		private bool nightCached;

		[System.NonSerialized]
		private bool hasNight;

		[System.NonSerialized]
		private Vector3 sentNightDir;

		[System.NonSerialized]
		private bool sentNightValid;

		[System.NonSerialized]
		private Texture2D lastHeightmap;

		// [TITAN-ORBIT] WebGL asteroid proxies share a handful of displaced meshes.
		// A private Geosphere50 copy per rock (~25k verts, plus the vertex lists below)
		// filled the WASM heap (~2 GB native + ~1 GB managed) and abort("OOM") froze the tab.
		[System.NonSerialized]
		private bool usesSharedGeneratedMesh;

		// Once set, LateUpdate and Rebuild must not Instantiate a private copy or drop the share.
		[System.NonSerialized]
		private bool lockSharedMesh;

		[System.NonSerialized]
		private int sharedMeshBucket = -1;

		// True when <see cref="material"/> is a per-planet instance used so WebGL draws
		// do not submit a MaterialPropertyBlock every camera.
		[System.NonSerialized]
		private bool webGlDrawMaterialReady;

		private static Dictionary<Mesh, int> sharedGeneratedUsers = new Dictionary<Mesh, int>();

		private static Dictionary<Mesh, Geom> meshToGeom = new Dictionary<Mesh, Geom>();

		private static Dictionary<Vector3, Seam> tempPoints = new Dictionary<Vector3, Seam>();

		private static int _HeightMap      = Shader.PropertyToID("_HeightMap");
		private static int _HasWater       = Shader.PropertyToID("_HasWater");
		private static int _WaterLevel     = Shader.PropertyToID("_WaterLevel");
		private static int _HasNight       = Shader.PropertyToID("_HasNight");
		private static int _NightDirection = Shader.PropertyToID("_NightDirection");

		public SgtProperties Properties
		{
			get
			{
				return properties;
			}
		}

		public Texture2D MaterialHeightmap
		{
			get
			{
				return material != null ? material.GetTexture(_HeightMap) as Texture2D : null;
			}
		}

		public bool MaterialHasWater
		{
			get
			{
				return material != null ? material.GetFloat(_HasWater) == 1.0f : false;
			}
		}

		public void DirtyMesh()
		{
			dirtyMesh = true;
		}

		/// <summary>The mesh currently drawn. Shared asteroid meshes are the same instance across a bucket.</summary>
		public Mesh GeneratedMesh
		{
			get { return generatedMesh; }
		}

		/// <summary>True when this body must keep the shared mesh and must not rebuild a private copy.</summary>
		public bool UsesLockedSharedMesh
		{
			get { return lockSharedMesh; }
		}

		/// <summary>WebGL asteroid shape bucket, or -1 when this body is not on a shared mesh.</summary>
		public int SharedMeshBucket
		{
			get { return sharedMeshBucket; }
		}

		public void BindSharedMeshBucket(int bucket)
		{
			sharedMeshBucket = bucket;
		}

		/// <summary>
		/// Copies authored mesh settings without property setters.
		/// Those setters call <see cref="DirtyMesh"/>, and LateUpdate would then Instantiate a private mesh.
		/// On WebGL the copy locks the mesh so that LateUpdate waits for the shared assign.
		/// </summary>
		public void CopyAuthoredSettingsFrom(SgtPlanet source)
		{
			if (source == null)
				return;

			mesh = source.mesh;
			meshCollider = source.meshCollider;
			radius = source.radius;
			material = source.material;
			sharedMaterial = source.sharedMaterial;
			castShadows = source.castShadows;
			receiveShadows = source.receiveShadows;
			waterLevel = source.waterLevel;
			displace = source.displace;
			displacement = source.displacement;
			clampWater = source.clampWater;
			dirtyMesh = false;
			nightCached = false;
			sentWaterLevel = float.NaN;
			sentNightValid = false;
			webGlDrawMaterialReady = false;

			if (Application.platform == RuntimePlatform.WebGLPlayer)
				lockSharedMesh = true;
		}

		/// <summary>Writes a texture onto the WebGL draw material. Shared asteroid meshes skip this.</summary>
		public void ApplyWebGlMaterialTexture(int nameId, Texture texture)
		{
			if (lockSharedMesh || texture == null)
				return;

			EnsureWebGlDrawMaterial();
			if (material != null)
				material.SetTexture(nameId, texture);
		}

		/// <summary>Writes a float onto the WebGL draw material. Shared asteroid meshes skip this.</summary>
		public void ApplyWebGlMaterialFloat(int nameId, float value)
		{
			if (lockSharedMesh)
				return;

			EnsureWebGlDrawMaterial();
			if (material != null)
				material.SetFloat(nameId, value);
		}

		/// <summary>
		/// Builds this planet's mesh once and records it as shared. Later proxies should call
		/// <see cref="AssignSharedGeneratedMesh"/> instead of <see cref="Rebuild"/>.
		/// </summary>
		public Mesh RebuildAsSharedMesh()
		{
			if (lockSharedMesh && generatedMesh != null)
			{
				dirtyMesh = false;
				return generatedMesh;
			}

			// The copy path locks before this build. Clear it so the one shared mesh can be created.
			lockSharedMesh = false;
			ReleaseSharedGeneratedMesh();
			Rebuild();
			if (generatedMesh == null)
				return null;

			usesSharedGeneratedMesh = true;
			lockSharedMesh = true;
			if (sharedGeneratedUsers.TryGetValue(generatedMesh, out int users))
				sharedGeneratedUsers[generatedMesh] = users + 1;
			else
				sharedGeneratedUsers[generatedMesh] = 1;

			// WebGL does not upload a DrawMesh-only mesh until the CPU copy is pushed.
			generatedMesh.UploadMeshData(false);
			return generatedMesh;
		}

		/// <summary>
		/// Points this body at <paramref name="shared"/> and skips <see cref="Rebuild"/>.
		/// Destroying one proxy does not destroy the mesh while other proxies still use it.
		/// </summary>
		public void AssignSharedGeneratedMesh(Mesh shared)
		{
			if (shared == null)
				return;

			if (usesSharedGeneratedMesh && generatedMesh == shared)
			{
				dirtyMesh = false;
				lockSharedMesh = true;
				return;
			}

			ReleaseSharedGeneratedMesh();
			if (generatedMesh != null && generatedMesh != shared)
				generatedMesh = CwHelper.Destroy(generatedMesh);

			generatedMesh = shared;
			dirtyMesh = false;
			usesSharedGeneratedMesh = true;
			lockSharedMesh = true;
			if (sharedGeneratedUsers.TryGetValue(shared, out int users))
				sharedGeneratedUsers[shared] = users + 1;
			else
				sharedGeneratedUsers[shared] = 1;
		}

		private void ReleaseSharedGeneratedMesh()
		{
			if (usesSharedGeneratedMesh == false)
				return;

			usesSharedGeneratedMesh = false;
			lockSharedMesh = false;
			Mesh shared = generatedMesh;
			generatedMesh = null;
			dirtyMesh = false;
			if (shared == null)
				return;

			if (sharedGeneratedUsers.TryGetValue(shared, out int users) == false)
				return;

			users--;
			if (users <= 0)
			{
				sharedGeneratedUsers.Remove(shared);
				CwHelper.Destroy(shared);
			}
			else
			{
				sharedGeneratedUsers[shared] = users;
			}
		}

		public void RegisterSharedMaterialOverride(SgtSharedMaterial.OverrideSharedMaterialSignature e)
		{
			OnOverrideSharedMaterial += e;
		}

		public void UnregisterSharedMaterialOverride(SgtSharedMaterial.OverrideSharedMaterialSignature e)
		{
			OnOverrideSharedMaterial -= e;
		}

		/// <summary>This method causes the planet mesh to update based on the current settings. You should call this after you finish modifying them.</summary>
		[ContextMenu("Rebuild")]
		public void Rebuild()
		{
			if (lockSharedMesh)
			{
				dirtyMesh = false;
				return;
			}

			dirtyMesh = false;
			if (usesSharedGeneratedMesh == true)
				ReleaseSharedGeneratedMesh();
			else
				generatedMesh = CwHelper.Destroy(generatedMesh);

			if (mesh != null)
			{
				generatedMesh = Instantiate(mesh);

				if (displace == true)
				{
					var count = generatedMesh.vertexCount;

					lastHeightmap = MaterialHeightmap;

					generatedMesh.GetVertices(generatedPositions);

					for (var i = 0; i < count; i++)
					{
						var vector = generatedPositions[i].normalized;

						generatedPositions[i] = vector * Sample(vector);
					}

					generatedMesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius + displacement) * 2.0f);

					generatedMesh.SetVertices(generatedPositions);

					generatedMesh.RecalculateNormals();
					generatedMesh.RecalculateTangents();

					generatedMesh.GetNormals(generatedNormals);
					generatedMesh.GetTangents(generatedTangents);

					// Fix seams
					var geom = GetGeom(mesh);

					foreach (var seam in geom.Seams)
					{
						if (seam.Indices.Count > 1)
						{
							var averageNormal  = default(Vector3);
							var averageTangent = default(Vector4);

							for (var i = 0; i < seam.Indices.Count; i++)
							{
								var index = seam.Indices[i];

								averageNormal  += generatedNormals[index];
								averageTangent += generatedTangents[index];
							}

							averageNormal  /= seam.Indices.Count;
							averageTangent /= seam.Indices.Count;

							for (var i = 0; i < seam.Indices.Count; i++)
							{
								var index = seam.Indices[i];

								generatedNormals[index] = averageNormal;
								generatedTangents[index] = averageTangent;
							}
						}
					}

					generatedMesh.SetNormals(generatedNormals);
					generatedMesh.SetTangents(generatedTangents);
				}
				else
				{
					generatedMesh.GetVertices(generatedPositions);

					var count = generatedMesh.vertexCount;
					var scale = radius / generatedPositions[0].magnitude;

					for (var i = 0; i < count; i++)
					{
						generatedPositions[i] *= scale;
					}

					generatedMesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius + displacement) * 2.0f);

					generatedMesh.SetVertices(generatedPositions);
				}

				if (meshCollider != null)
				{
					meshCollider.sharedMesh = null;
					meshCollider.sharedMesh = generatedMesh;
				}
			}
		}

		private static Geom GetGeom(Mesh mesh)
		{
			var geom = default(Geom);

			if (meshToGeom.TryGetValue(mesh, out geom) == false)
			{
				geom = new Geom();

				tempPoints.Clear();

				var positions = mesh.vertices;

				for (var i = 0; i < positions.Length; i++)
				{
					var point = positions[i];
					var seam  = default(Seam);

					if (tempPoints.TryGetValue(point, out seam) == false)
					{
						seam = Seam.Pool.Count > 0 ? Seam.Pool.Pop() : new Seam();

						tempPoints.Add(point, seam);
					}

					seam.Indices.Add(i);
				}

				foreach (var pair in tempPoints)
				{
					var seam = pair.Value;

					if (seam.Indices.Count > 1)
					{
						geom.Seams.Add(pair.Value);
					}
					else
					{
						seam.Indices.Clear();

						Seam.Pool.Push(seam);
					}
				}

				tempPoints.Clear();

				meshToGeom.Add(mesh, geom);
			}

			return geom;
		}

		public static SgtPlanet Create(int layer = 0, Transform parent = null)
		{
			return Create(layer, parent, Vector3.zero, Quaternion.identity, Vector3.one);
		}

		public static SgtPlanet Create(int layer, Transform parent, Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
		{
			return CwHelper.CreateGameObject("Planet", layer, parent, localPosition, localRotation, localScale).AddComponent<SgtPlanet>();
		}

		protected virtual void OnEnable()
		{
			SgtCamera.OnCameraDraw        += HandleCameraDraw;
			SgtCommon.OnCalculateDistance += HandleCalculateDistance;
		}

		protected virtual void OnDisable()
		{
			SgtCamera.OnCameraDraw        -= HandleCameraDraw;
			SgtCommon.OnCalculateDistance -= HandleCalculateDistance;
		}

		protected virtual void LateUpdate()
		{
			// A locked share must survive Displacement / WaterLevel dirties. Rebuilding
			// would Release the share and Instantiate a private Geosphere per rock.
			if (lockSharedMesh)
				dirtyMesh = false;
			else if (generatedMesh == null || dirtyMesh == true)
				Rebuild();

			if (generatedMesh == null || material == null)
				return;

			// Asteroids share materials. Per-body water and night uploads would write one
			// material from every rock, and the property block is not drawn on WebGL.
			if (lockSharedMesh && Application.platform == RuntimePlatform.WebGLPlayer)
				return;

			if (Application.platform == RuntimePlatform.WebGLPlayer)
			{
				ApplyWebGlPlanetUniforms();
				return;
			}

			// Skip the upload when nothing changed. Bodies do not move, so a constant
			// water level and light direction must not allocate a new block each frame.
			if (sentWaterLevel != waterLevel)
			{
				Properties.SetFloat(_WaterLevel, waterLevel);
				sentWaterLevel = waterLevel;
			}

			if (nightCached == false)
			{
				hasNight = material.GetFloat(_HasNight) == 1.0f;
				nightCached = true;
			}

			if (hasNight == false)
				return;

			var mask   = 1 << gameObject.layer;
			var lights = SgtLight.Find(mask, transform.position);

			SgtLight.FilterOut(transform.position);

			if (lights.Count > 0)
			{
				var position  = Vector3.zero;
				var direction = Vector3.forward;
				var color     = Color.white;
				var intensity = 0.0f;

				SgtLight.Calculate(lights[0], transform.position, 0.0f, default(Transform), default(Transform), ref position, ref direction, ref color, ref intensity);

				Vector3 night = -direction;
				if (sentNightValid && (night - sentNightDir).sqrMagnitude < 0.00000001f)
					return;

				properties.SetVector(_NightDirection, night);
				sentNightDir = night;
				sentNightValid = true;
			}
		}

		protected virtual void OnDidApplyAnimationProperties()
		{
			DirtyMesh();
		}

		private void HandleCameraDraw(Camera camera)
		{
			if (SgtCommon.CanDraw(gameObject, camera) == false) return;

			//var layer = SgtHelper.GetRenderingLayers(gameObject, renderingLayer);
			var layer = gameObject.layer;

			// DrawMesh copies a MaterialPropertyBlock into native memory. On WebGL that
			// copy is not returned to the browser, so every camera and every body grows the heap.
			MaterialPropertyBlock block = null;
			if (Application.platform != RuntimePlatform.WebGLPlayer)
				block = properties;

			Graphics.DrawMesh(generatedMesh, transform.localToWorldMatrix, material, layer, camera, 0, block, castShadows, receiveShadows);

			var finalSharedMaterial = sharedMaterial;

			if (OnOverrideSharedMaterial != null)
			{
				OnOverrideSharedMaterial.Invoke(ref finalSharedMaterial, camera);
			}

			if (CwHelper.Enabled(finalSharedMaterial) == true && finalSharedMaterial.Material != null)
			{
				Graphics.DrawMesh(generatedMesh, transform.localToWorldMatrix, finalSharedMaterial.Material, layer, camera, 0, block);
			}
		}

		void EnsureWebGlDrawMaterial()
		{
			if (webGlDrawMaterialReady || material == null || lockSharedMesh)
				return;
			if (Application.platform != RuntimePlatform.WebGLPlayer)
				return;

			var instance = new Material(material);
			instance.name = material.name + " (WebGL)";
			material = instance;
			webGlDrawMaterialReady = true;
		}

		void ApplyWebGlPlanetUniforms()
		{
			EnsureWebGlDrawMaterial();
			if (material == null)
				return;

			if (sentWaterLevel != waterLevel)
			{
				material.SetFloat(_WaterLevel, waterLevel);
				sentWaterLevel = waterLevel;
			}

			if (nightCached == false)
			{
				hasNight = material.GetFloat(_HasNight) == 1.0f;
				nightCached = true;
			}

			if (hasNight == false)
				return;

			var mask = 1 << gameObject.layer;
			var lights = SgtLight.Find(mask, transform.position);

			SgtLight.FilterOut(transform.position);

			if (lights.Count == 0)
				return;

			var position = Vector3.zero;
			var direction = Vector3.forward;
			var color = Color.white;
			var intensity = 0.0f;

			SgtLight.Calculate(lights[0], transform.position, 0.0f, default(Transform), default(Transform), ref position, ref direction, ref color, ref intensity);

			Vector3 night = -direction;
			if (sentNightValid && (night - sentNightDir).sqrMagnitude < 0.00000001f)
				return;

			material.SetVector(_NightDirection, night);
			sentNightDir = night;
			sentNightValid = true;
		}

		protected virtual void OnDestroy()
		{
			if (webGlDrawMaterialReady && material != null)
			{
				material = CwHelper.Destroy(material);
				webGlDrawMaterialReady = false;
			}

			if (usesSharedGeneratedMesh == true)
			{
				ReleaseSharedGeneratedMesh();
				return;
			}

			CwHelper.Destroy(generatedMesh);
		}

		private void HandleCalculateDistance(Vector3 worldPosition, ref float distance)
		{
			var localPosition = transform.InverseTransformPoint(worldPosition);

			localPosition = localPosition.normalized * Sample(localPosition);

			var surfacePosition = transform.TransformPoint(localPosition);
			var thisDistance    = Vector3.Distance(worldPosition, surfacePosition);

			if (thisDistance < distance)
			{
				distance = thisDistance;
			}
		}

		private float Sample(Vector3 vector)
		{
			var final = radius;

			if (lastHeightmap != null)
			{
				var uv   = SgtCommon.CartesianToPolarUV(vector);
				var land = lastHeightmap.GetPixelBilinear(uv.x, uv.y).a;

				if (clampWater == true)
				{
					final += displacement * Mathf.InverseLerp(Mathf.Clamp01(waterLevel), 1.0f, land);
				}
				else
				{
					final += displacement * Mathf.Max(land, waterLevel);
				}
			}

			return final;
		}
	}
}

#if UNITY_EDITOR
namespace SpaceGraphicsToolkit
{
	using UnityEditor;
	using TARGET = SgtPlanet;

	[CanEditMultipleObjects]
	[CustomEditor(typeof(TARGET))]
	public class SgtPlanet_Editor : CwEditor
	{
		protected override void OnInspector()
		{
			TARGET tgt; TARGET[] tgts; GetTargets(out tgt, out tgts);

			var dirtyMesh = false;

			BeginError(Any(tgts, t => t.Mesh == null));
				Draw("mesh", ref dirtyMesh, "The sphere mesh used to render the planet.");
			EndError();
			//Draw("renderingLayer", "The rendering layer used to render the planet.");
			Draw("meshCollider", ref dirtyMesh, "If you want the generated mesh to have a matching collider, you can specify it here.");
			BeginError(Any(tgts, t => t.Radius <= 0.0f));
				Draw("radius", ref dirtyMesh, "The radius of the planet in local space.");
			EndError();

			Separator();

			BeginError(Any(tgts, t => t.Material == null));
				Draw("material", "The material used to render the planet. For best results, this should use the SGT Planet shader.");
			EndError();
			Draw("sharedMaterial", "If you want to apply a shared material (e.g. atmosphere) to this terrain, then specify it here.");
			Draw("castShadows", "Should the planet cast shadows?");
			Draw("receiveShadows", "Should the planet receive shadows?");

			Separator();

			if (Any(tgts, t => t.MaterialHeightmap != null))
			{
				if (Any(tgts, t => t.MaterialHasWater == true))
				{
					Draw("waterLevel", ref dirtyMesh, "The current water level.\n\n0 = Radius.\n\n1 = Radius + Displacement.");
				}
				Draw("displace", ref dirtyMesh, "Should the planet mesh be displaced using the heightmap in the planet material?");
				if (Any(tgts, t => t.Displace == true))
				{
					BeginIndent();
						BeginError(Any(tgts, t => t.Displacement == 0.0f));
							Draw("displacement", ref dirtyMesh, "The maximum height displacement applied to the planet mesh when the heightmap alpha value is 1.");
						EndError();
						Draw("clampWater", ref dirtyMesh, "If you enable this then the water will not rise, instead the terrain will shrink down.");
					EndIndent();
				}
			}

			if (Any(tgts, t => t.MaterialHasWater == true && t.GetComponent<SgtPlanetWaterGradient>() == null))
			{
				Separator();

				if (HelpButton("This material has water, but you have no WaterGradient component.", UnityEditor.MessageType.Info, "Fix", 50) == true)
				{
					Each(tgts, t => CwHelper.GetOrAddComponent<SgtPlanetWaterGradient>(t.gameObject));
				}
			}

			if (Any(tgts, t => t.MaterialHasWater == true && t.GetComponent<SgtPlanetWaterTexture>() == null))
			{
				Separator();

				if (HelpButton("This material has water, but you have no WaterTexture component.", UnityEditor.MessageType.Info, "Fix", 50) == true)
				{
					Each(tgts, t => CwHelper.GetOrAddComponent<SgtPlanetWaterTexture>(t.gameObject));
				}
			}

			if (dirtyMesh == true) Each(tgts, t => t.DirtyMesh(), true, true);
		}

		[MenuItem(SgtCommon.GameObjectMenuPrefix + "Planet", false, 10)]
		public static void CreateMenuItem()
		{
			var parent   = CwHelper.GetSelectedParent();
			var instance = SgtPlanet.Create(parent != null ? parent.gameObject.layer : 0, parent);

			CwHelper.SelectAndPing(instance);
		}
	}
}
#endif