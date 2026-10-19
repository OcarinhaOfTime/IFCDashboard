Shader "Hidden/IFCDashboard/RainOnLens"
{
    Properties
    {
        _RainAmount ("Rain Amount", Range(0, 1)) = 0.75

        _DistortionStrength ("Distortion Strength", Range(0, 1)) = 0.2

        _DropDensity ("Drop Density", Range(0, 1)) = .5
        _DropSize ("Drop Size", Range(0.05, 0.45)) = 0.18

        _Speed ("Fall Speed", Range(0, 3)) = 0.55

        _TrailLength ("Trail Length", Range(0.05, 0.8)) = 0.42
        _TrailWidth ("Trail Width", Range(0.005, 0.15)) = 0.035

        _HighlightStrength ("Water Highlight", Range(0, 0.25)) = 0.055
        
        _Size ("Size", Range(0, 10 )) = 4.0
        _Blur("Blur", Range(0, 1)) = 0.8
        _T ("Time", Float) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "RenderPipeline"="UniversalPipeline"
        }

        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "RainOnLens"

            HLSLPROGRAM
            #define S(a, b, t) smoothstep(a, b, t)

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            CBUFFER_START(UnityPerMaterial)

                float _RainAmount;
                float _DistortionStrength;

                float _DropDensity;
                float _DropSize;

                float _Speed;

                float _TrailLength;
                float _TrailWidth;

                float _HighlightStrength;
                float _T;
                float _Size;
                float _Blur;
            CBUFFER_END

            float N21(float2 p){
                p = frac(p*float2(123.34, 456.21));
                p += dot(p, p+45.32);
                return frac(p.x*p.y);
            }

            float3 SampleBlurred(float2 uv, float blur)
            {
                blur = saturate(blur);

                // Maximum blur radius, in pixels.
                float radius = blur * 8.0;

                float2 texel = radius / _ScreenParams.xy;

                // 3x3 Gaussian kernel:
                //
                // 1  2  1
                // 2  4  2
                // 1  2  1
                //
                // Sum = 16

                float3 col = 0.0;

                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv + texel * float2(-1, -1)
                ).rgb * 1.0;

                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv + texel * float2( 0, -1)
                ).rgb * 2.0;

                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv + texel * float2( 1, -1)
                ).rgb * 1.0;


                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv + texel * float2(-1, 0)
                ).rgb * 2.0;

                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv
                ).rgb * 4.0;

                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv + texel * float2(1, 0)
                ).rgb * 2.0;


                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv + texel * float2(-1, 1)
                ).rgb * 1.0;

                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv + texel * float2(0, 1)
                ).rgb * 2.0;

                col += SAMPLE_TEXTURE2D(
                    _BlitTexture, sampler_LinearClamp,
                    uv + texel * float2(1, 1)
                ).rgb * 1.0;

                return col / 16.0;
            }

            float3 RainLayer(float2 ouv, float t, float density = .5, float seed = 0){
                float2 aspect = float2(2, 1);
                float haspect = _ScreenParams.x / _ScreenParams.y;

                float2 uv = ouv;

                uv.x *= haspect;
                uv *= _Size;
                uv *= aspect;
                uv.y += t * .25;

                float2 gv = frac(uv) - .5;                
                float2 id = floor(uv);

                float n = N21(id);
                float n2 = N21(id + float2(313.6, 123.4) * seed);
                t += n * 14.28;

                float w = ouv.y * 10;
                float x = (n - .5) * .8;
                x += (.4 - abs(x)) * sin(3*w) * pow(sin(w), 6) * .45;
                float y = -sin(t + sin(t+sin(t) * .5)) * .45;
                y -= (gv.x - x) * (gv.x - x); // parabola drop shape

                float2 dropPos = (gv - float2(x, y)) / aspect;
                float drop = S(.05, .03, length(dropPos));

                float2 trailPos = (gv - float2(x, t * .25)) / aspect; // keeps the trail with the same speed as the grid, as if it sticks to the grid
                trailPos.y = (frac(trailPos.y * 8) - .5) / 8;
                float trail = S(.03, .01, length(trailPos));
                float fogTrail = S(-.05, .05, dropPos.y);
                fogTrail *= S(.5, y, gv.y);
                trail *= S(-.05, .05, dropPos.y); //Clips the trail to be above the drop
                trail *= S(.5, y, gv.y); //fades the trail, being 0 at the top of the grid and 1 near the drop
                fogTrail *= S(.05, .04, abs(dropPos.x)); 

                float2 offs = drop * dropPos + trail * trailPos + fogTrail * trailPos.x * .25;

                

                return float3(offs, fogTrail) * step(n2, density);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                
                //float _Size = 4;
                float t = fmod(_T, 7200);
                t = _Time.y * _Speed;
                t = fmod(t, 7200);
                
                half3 col = 0;
                float2 ouv = input.texcoord;
                
                float density = _DropDensity;
                float3 drops = RainLayer(ouv, t, density);
                drops += RainLayer(ouv * 1.78 + 7.49, t + 6.323, density, 313.6);
                drops += RainLayer(ouv * 2.1 + 78.49, t + 129.6, density, 12.4);
                drops += RainLayer(ouv * 3.41 + 66.7649, t + 1234.5466, density, 33.6);
                drops += RainLayer(ouv * 4.27 + 888.4335433, t + 5645.68886, density, 23.4);

                float2 offs = drops.xy;
                float fogTrail = drops.z;

                col = SampleBlurred(ouv + offs * -_DistortionStrength, 0);
                //col = offs.x + offs.y;

                //if (gv.x > 0.48 || gv.y > 0.49) col = half3(1, 0, 0); //Debugging
                
                return half4(
                    col,
                    1.0
                );
            }

            ENDHLSL
        }
    }
}