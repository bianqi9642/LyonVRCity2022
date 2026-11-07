// TargetHoverOutline.shader
//
// Role: the outline pass of the Part 1 / Part 3 target-building hover highlight (see InteractableHoverFeedback.cs).
// Technique: "inverted hull". The SAME mesh as the building is drawn a second time with front faces culled and every vertex pushed outwards along its
// normal; only the rim that sticks out beyond the building's silhouette stays visible, as a flat-colour outline. The building's own material is never touched.
//
// Properties (all set per outline renderer through a MaterialPropertyBlock by InteractableHoverFeedback; the material itself is never edited at runtime):
//   _OutlineColor     outline colour (default warm amber).
//   _OutlineWidth     world-space extrusion per metre of camera distance (0.004 ~ 3 px in a Quest eye buffer), so the outline keeps a roughly constant
//                     on-screen thickness whatever the viewing distance - this is what "consistent thickness in VR" needs.
//   _OutlineMinWidth  lower clamp of the world-space extrusion in metres (keeps the outline visible when the participant is close).
//   _OutlineMaxWidth  upper clamp in metres (stops a far building from getting an absurdly fat outline).
//
// Design notes
//   - URP 14 forward, unlit, opaque queue, depth-tested and depth-written: occluders hide the outline exactly like the building itself.
//   - Single-pass instanced (Quest) safe: stereo instancing macros as in URP's own Unlit shader; the camera position comes from GetCameraPositionWS()
//     (per-eye in XR), so both eyes extrude with their own distance.
//   - No post-processing, no extra camera, no stencil, no renderer-feature edit. One extra draw per building submesh, only while the building is hovered.
//   - Hard-edged (box-like) meshes have split vertex normals, so at a convex corner the extruded faces do not touch; the outline then shows a tiny notch
//     exactly at the corner. At the widths used here (2-4 px) this is not noticeable and the outline stays continuous along every edge.
//   - Hidden/ shader living in a Resources folder: it is included in builds and found with Shader.Find, so no scene or graphics-settings edit is needed.
//
// All comments in English per project convention.

Shader "Hidden/Lyon/TargetHoverOutline"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (1.0, 0.72, 0.10, 1.0)
        _OutlineWidth ("Outline Width (m per m of distance)", Range(0.0, 0.02)) = 0.004
        _OutlineMinWidth ("Outline Min Width (m)", Float) = 0.03
        _OutlineMaxWidth ("Outline Max Width (m)", Float) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry+1"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "HoverOutline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZWrite On
            ZTest LEqual
            Blend Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                float _OutlineWidth;
                float _OutlineMinWidth;
                float _OutlineMaxWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                float distanceToCamera = length(positionWS - GetCameraPositionWS());
                float width = clamp(distanceToCamera * _OutlineWidth, _OutlineMinWidth, _OutlineMaxWidth);

                output.positionCS = TransformWorldToHClip(positionWS + normalWS * width);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return half4(_OutlineColor.rgb, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
