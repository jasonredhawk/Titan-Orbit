using UnityEngine;
using CW.Common;

namespace SpaceGraphicsToolkit
{
	/// <summary>This component can be added alongside the <b>SgtPlanet</b> component to give it an animated water surface texture.</summary>
	[ExecuteInEditMode]
	[RequireComponent(typeof(SgtPlanet))]
	[HelpURL(SgtCommon.HelpUrlPrefix + "SgtPlanetWaterTexture")]
	[AddComponentMenu(SgtCommon.ComponentMenuPrefix + "Planet Water Texture")]
	public class SgtPlanetWaterTexture : MonoBehaviour
	{
		/// <summary>The generated water texture will be based on this texture.
		/// NOTE: This should be a normal map.</summary>
		public Texture BaseTexture { set { baseTexture = value; } get { return baseTexture; } } [SerializeField] private Texture baseTexture;

		/// <summary>The strength of the normal map.</summary>
		public float Strength { set { strength = value; } get { return strength; } } [SerializeField] private float strength = 1.0f;

		/// <summary>The speed of the water animation.</summary>
		public float Speed { set { speed = value; } get { return speed; } } [SerializeField] private float speed = 5.0f;

		[System.NonSerialized]
		private SgtPlanet cachedPlanet;

		[System.NonSerialized]
		private RenderTexture generatedTexture;

		[SerializeField]
		private float age;

		[System.NonSerialized]
		private float nextWebGlBlitTime;

		private static Material cachedMaterial;

		private static int _MainTex        = Shader.PropertyToID("_MainTex");
		private static int _Age            = Shader.PropertyToID("_Age");
		private static int _NormalStrength = Shader.PropertyToID("_NormalStrength");
		private static int _WaterTexture   = Shader.PropertyToID("_WaterTexture");

		protected virtual void OnEnable()
		{
			cachedPlanet = GetComponent<SgtPlanet>();
		}

		/// <summary>Next WebGL update blits again. Used when the planet material instance is replaced.</summary>
		public void InvalidateWebGlBlit()
		{
			nextWebGlBlitTime = 0f;
		}

		protected virtual void OnDestroy()
		{
			if (generatedTexture != null)
			{
				generatedTexture = CwHelper.Destroy(generatedTexture);
			}
		}

		protected virtual void Update()
		{
			if (Application.isPlaying == true)
			{
				age += Time.deltaTime * speed;
			}

			if (baseTexture == null || cachedPlanet == null)
				return;

			bool webgl = Application.platform == RuntimePlatform.WebGLPlayer;

			if (generatedTexture == null)
			{
				int width = baseTexture.width;
				int height = baseTexture.height;
				// One full-size ARGB32 texture per planet was ~637 MB of ALLOC_GFX.
				if (webgl)
				{
					if (width > 256)
						width = 256;
					if (height > 256)
						height = 256;
				}

				generatedTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, 8);

				generatedTexture.wrapMode         = TextureWrapMode.Repeat;
				generatedTexture.useMipMap        = true;
				generatedTexture.autoGenerateMips = false;
				generatedTexture.filterMode       = FilterMode.Trilinear;
				generatedTexture.anisoLevel       = 8;
			}
			else if (webgl && Application.isPlaying && Time.unscaledTime < nextWebGlBlitTime)
			{
				return;
			}

			if (cachedMaterial == null)
			{
				cachedMaterial = CwHelper.CreateTempMaterial("PlanetWater (Generated)", SgtCommon.ShaderNamePrefix + "PlanetWater");
			}

			cachedMaterial.SetTexture(_MainTex, baseTexture);
			cachedMaterial.SetFloat(_Age, age);
			cachedMaterial.SetFloat(_NormalStrength, strength);

			Graphics.Blit(null, generatedTexture, cachedMaterial);

			generatedTexture.GenerateMips();

			if (webgl)
			{
				nextWebGlBlitTime = Time.unscaledTime + 0.1f;
				cachedPlanet.ApplyWebGlMaterialTexture(_WaterTexture, generatedTexture);
			}
			else
			{
				cachedPlanet.Properties.SetTexture(_WaterTexture, generatedTexture);
			}
		}
	}
}

#if UNITY_EDITOR
namespace SpaceGraphicsToolkit
{
	using UnityEditor;
	using TARGET = SgtPlanetWaterTexture;

	[CanEditMultipleObjects]
	[CustomEditor(typeof(TARGET))]
	public class SgtPlanetWaterTexture_Editor : CwEditor
	{
		protected override void OnInspector()
		{
			TARGET tgt; TARGET[] tgts; GetTargets(out tgt, out tgts);

			BeginError(Any(tgts, t => t.BaseTexture == null));
				Draw("baseTexture", "The generated water texture will be based on this texture.\n\nNOTE: This should be a normal map.");
			EndError();
			Draw("strength", "The strength of the normal map.");
			Draw("speed", "The speed of the water animation.");
		}
	}
}
#endif