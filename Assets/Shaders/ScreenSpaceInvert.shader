Shader "Hidden/Dashboard/ScreenSpaceInvert"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
        }

        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "InvertColors"

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            half4 Frag(Varyings input) : SV_Target
{
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            half4 sourceColor = SAMPLE_TEXTURE2D_X(
                _BlitTexture,
                sampler_LinearClamp,
                input.texcoord
            );

            float3 color = sourceColor.rgb;

            #if defined(UNITY_COLORSPACE_GAMMA)

                // In a Gamma project, values are already in perceptual space.
                color = 1.0 - saturate(color);

            #else

                // Convert the linear framebuffer value into display-oriented sRGB.
                float3 srgbColor = LinearToSRGB(saturate(color));

                // Perform the intuitive image-editor-style inversion.
                srgbColor = 1.0 - srgbColor;

                // Convert back because shader output is expected to be linear.
                color = SRGBToLinear(srgbColor);

            #endif

            return half4(color, sourceColor.a);
        }

            ENDHLSL
        }
    }

    Fallback Off
}