using UnityEngine;
using UnityEngine.Rendering;
#if SHAPES_URP
using System;
using UnityEngine.Rendering.Universal;
#elif SHAPES_HDRP
using System.Collections.Generic;
using UnityEngine.Rendering.HighDefinition;
#endif
#if SHAPES_URP && UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
using CommandBufferType = UnityEngine.Rendering.RasterCommandBuffer;

#else
using CommandBufferType = UnityEngine.Rendering.CommandBuffer;
#endif

// Shapes © Freya Holmér - https://twitter.com/FreyaHolmer/
// Website & Documentation - https://acegikmo.com/shapes/
namespace Shapes {

	#if SHAPES_URP
	internal class ShapesRenderPass : ScriptableRenderPass {
		DrawCommand drawCommand;
		readonly CommandBuffer cmdBuf = new CommandBuffer();

		public ShapesRenderPass Init( DrawCommand drawCommand ) {
			this.drawCommand = drawCommand;
			renderPassEvent = drawCommand.camEvt;
			return this;
		}


		#if UNITY_6000_0_OR_NEWER
		private class PassData {
			public DrawCommand drawCommand;
		}

		public override void RecordRenderGraph( RenderGraph renderGraph, ContextContainer frameData ) {
			// cameraColor exists only when URP allocates an intermediate target. Cameras that
			// draw straight into a RenderTexture (join-load warmup) leave it empty.
			// SetRenderAttachment then NREs, the graph never executes, and Forward+
			// leaves ZBinningJob unfinished so every later camera render fails.
			// Color and depth must stay a pair: a color-only attachment drops the camera
			// depth buffer, and Shapes' default ZTest (LessEqual) then discards every
			// fragment. Planets still draw in the opaque pass; territory fills and the
			// lines between them do not.
			UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
			TextureHandle color = resourceData.activeColorTexture;
			TextureHandle depth = resourceData.activeDepthTexture;
			if( !color.IsValid() ) {
				color = resourceData.cameraColor;
				depth = resourceData.cameraDepth;
			}
			if( !color.IsValid() ) {
				color = resourceData.backBufferColor;
				depth = resourceData.backBufferDepth;
			}
			if( !color.IsValid() )
				return;

			using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<PassData>( "Render Shapes", out PassData data );
			data.drawCommand = drawCommand;
			builder.AllowPassCulling( false );
			builder.SetRenderAttachment( color, 0, AccessFlags.Write );
			// Read-only: territory and orbit shapes use ZWrite Off. Binding depth is what
			// lets LessEqual keep fills under hulls and in front of the starfield floor.
			if( depth.IsValid() )
				builder.SetRenderAttachmentDepth( depth, AccessFlags.Read );
			builder.SetRenderFunc(
				( PassData dataParam, RasterGraphContext context ) => {
					dataParam.drawCommand.AppendToBuffer( context.cmd );
				}
			);
		}
		#endif

		#if !UNITY_6000_0_OR_NEWER
		[Obsolete( "This rendering path is for compatibility mode only (when Render Graph is disabled)", false )]
		public override void Execute( ScriptableRenderContext context, ref RenderingData renderingData ) {
			drawCommand.AppendToBuffer( cmdBuf );
			context.ExecuteCommandBuffer( cmdBuf );
			cmdBuf.Clear();
		}
		#endif

		public override void FrameCleanup( CommandBuffer cmd ) {
			DrawCommand.OnCommandRendered( drawCommand );
			drawCommand = null;
			ObjectPool<ShapesRenderPass>.Free( this );
		}
	}
	#elif SHAPES_HDRP
	public class ShapesRenderPass : CustomPass {
		protected override void Setup( ScriptableRenderContext renderContext, CommandBuffer cmd ) => this.name = "Shapes Render Pass";
		// HDRP doesn't have ScriptableRenderPass stuff, so we use *one* custom pass per injection point, but branch inside of it instead
		// this does mean there will be redundancy/overhead in the way this is done, but, can't do much about it for now I think
		static readonly List<DrawCommand> executingCommands = new List<DrawCommand>();
			#if !UNITY_6000_0_OR_NEWER
		protected override void Execute( ScriptableRenderContext context, CommandBuffer cmd, HDCamera hdCamera, CullingResults cullingResult ) {
			#else
		protected override void Execute( CustomPassContext passContext ) {
			ScriptableRenderContext context = passContext.renderContext;
			CommandBuffer cmd = passContext.cmd;
			HDCamera hdCamera = passContext.hdCamera;
			#endif

			if( DrawCommand.cBuffersRendering.TryGetValue( hdCamera.camera, out List<DrawCommand> cmds ) ) {
				for( int i = 0; i < cmds.Count; i++ ) {
					if( cmds[i].camEvt == injectionPoint ) {
						executingCommands.Add( cmds[i] );
						cmds[i].AppendToBuffer( cmd );
					}
				}
			}

			// if we added commands, execute them immediately
			if( executingCommands.Count > 0 ) {
				context.ExecuteCommandBuffer( cmd ); // we have to execute it because OnCommandRendered might want to destroy used materials
				cmd.Clear();
				foreach( DrawCommand drawCommand in executingCommands )
					DrawCommand.OnCommandRendered( drawCommand ); // deletes cached assets
			}
			executingCommands.Clear();
		}
	}
	#endif

}