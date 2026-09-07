Shader "Hidden/Dashboard/ScreenSpaceGodRays"
{
    Properties
    {
        [HDR] _RayColor(
            "Ray Color",
            Color
        ) = (1.0, 0.82, 0.55, 1.0)

        _Intensity(
            "Intensity",
            Range(0.0, 4.0)
        ) = 1.0

        _Decay(
            "Decay",
            Range(0.8, 0.999)
        ) = 0.96

        _Density(
            "Density",
            Range(0.1, 1.5)
        ) = 1.0

        _Weight(
            "Sample Weight",
            Range(0.001, 0.2)
        ) = 0.03

        _SunRadius(
            "Sun Influence Radius",
            Range(0.01, 0.6)
        ) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Name "ScreenSpaceGodRays"

            ZWrite Off
            ZTest Always
            Cull Off

            // Replace the camera target with the result returned by Frag.
            // The rays themselves are added manually inside the shader.
            Blend One Zero

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            /*
             * Good starting point for desktop.
             *
             * For WebGL, 24 samples will be cheaper.
             * For testing, 64 samples will appear smoother.
             */
            #define GOD_RAY_SAMPLES 32

            CBUFFER_START(UnityPerMaterial)

                float4 _RayColor;

                /*
                 * XY: projected sun viewport position.
                 * W: visibility/fade calculated by the controller.
                 */
                float4 _SunScreenPosition;

                float _Intensity;
                float _Decay;
                float _Density;
                float _Weight;
                float _SunRadius;
                float _CameraAspect;

            CBUFFER_END

            bool IsInsideScreen(float2 uv)
            {
                return
                    uv.x >= 0.0 &&
                    uv.x <= 1.0 &&
                    uv.y >= 0.0 &&
                    uv.y <= 1.0;
            }

            /*
             * Returns:
             *
             * 1 for sky/background pixels
             * 0 for opaque geometry
             */
            half GetSkyMask(float2 uv)
            {
                if (!IsInsideScreen(uv))
                {
                    return 0.0h;
                }

                float rawDepth = SampleSceneDepth(uv);

                /*
                 * Convert the platform-specific depth value into a normalized
                 * linear depth:
                 *
                 * near plane = 0
                 * far plane  = 1
                 */
                float linearDepth = Linear01Depth(
                    rawDepth,
                    _ZBufferParams
                );

                /*
                 * Skybox/background pixels retain far-plane depth.
                 */
                return step(0.999h, linearDepth);
            }

            /*
             * Creates a synthetic circular light source around the projected
             * directional-light position and clips it against scene geometry.
             */
            half GetSunSourceMask(float2 uv)
            {
                half skyMask = GetSkyMask(uv);

                float2 offset =
                    uv - _SunScreenPosition.xy;

                /*
                 * Correct horizontal distance so the source stays circular on
                 * non-square screens.
                 */
                offset.x *= max(
                    _CameraAspect,
                    0.0001
                );

                float distanceFromSun = length(offset);

                /*
                 * Soft circular source.
                 *
                 * The inner 25% is fully bright, followed by a smooth falloff.
                 */
                half sunMask =
                    1.0h -
                    smoothstep(
                        _SunRadius * 0.25,
                        _SunRadius,
                        distanceFromSun
                    );

                return skyMask * sunMask;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;

                /*
                 * Fetch the camera color supplied by the Full Screen Pass
                 * Renderer Feature.
                 */
                half4 sceneColor = SAMPLE_TEXTURE2D_X(
                    _BlitTexture,
                    sampler_LinearClamp,
                    uv
                );

                /*
                 * The controller sets W to zero when the sun is not visible,
                 * behind the camera, or sufficiently far outside the screen.
                 */
                if (_SunScreenPosition.w <= 0.0001)
                {
                    return sceneColor;
                }

                float2 directionToSun =
                    _SunScreenPosition.xy - uv;

                float2 sampleStep =
                    directionToSun *
                    (
                        _Density /
                        (float) GOD_RAY_SAMPLES
                    );

                float2 sampleUV = uv;

                half illuminationDecay = 1.0h;
                half accumulatedLight = 0.0h;

                [unroll]
                for (
                    int sampleIndex = 0;
                    sampleIndex < GOD_RAY_SAMPLES;
                    sampleIndex++
                )
                {
                    sampleUV += sampleStep;

                    half sourceMask =
                        GetSunSourceMask(sampleUV);

                    accumulatedLight +=
                        sourceMask *
                        illuminationDecay *
                        (half) _Weight;

                    illuminationDecay *=
                        (half) _Decay;
                }

                half rayStrength =
                    accumulatedLight *
                    (half) _Intensity *
                    (half) _SunScreenPosition.w;

                /*
                 * Add the scattered-light contribution in linear HDR space.
                 * Bloom and tonemapping can process it afterward.
                 */
                sceneColor.rgb +=
                    (half3) _RayColor.rgb *
                    rayStrength;

                return sceneColor;
            }

            ENDHLSL
        }
    }

    Fallback Off
}