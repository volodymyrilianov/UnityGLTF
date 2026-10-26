Shader "Hidden/CubemapToEquirect"
{
    Properties
    {
        _Cube ("Cubemap", Cube) = "" {}
        _DynamicRange ("Dynamic Range", Range(1.0, 16.0)) = 8.0
        // HDR decode instructions of the SOURCE cubemap (ReflectionProbe.textureHDRDecodeValues).
        // Must be passed explicitly: unity_SpecCube0_HDR is only populated during scene
        // rendering with a bound probe — in an editor/export blit it is zero, which made
        // every exported probe equirect ALL BLACK.
        _CubeDecode ("Cube HDR Decode", Vector) = (1, 1, 0, 0)
        // Explicit mip level (SAMPLE_TEXTURECUBE_LOD) — what Unity's avatar shaders read; < 0 = the
        // plain sample the exporter has always used (ProbeMipDump sets it).
        _Lod ("LOD", Float) = -1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURECUBE(_Cube);
            SAMPLER(sampler_Cube);

            float _DynamicRange;
            float4 _CubeDecode;
            float _Lod;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float3 LatLongToDir(float2 uv)
            {
                // uv: [0..1]
                float phi = (uv.x * 2.0f - 1.0f) * PI;       // -pi..pi
                float theta = (1.0f - uv.y) * PI;           // 0..pi (top to bottom)
                float sinT = sin(theta);
                return float3(
                    sinT * sin(phi),
                    cos(theta),
                    sinT * cos(phi)
                );
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float3 dir = normalize(LatLongToDir(i.uv));
                float4 raw = _Lod < 0.0
                    ? SAMPLE_TEXTURECUBE(_Cube, sampler_Cube, dir)
                    : SAMPLE_TEXTURECUBE_LOD(_Cube, sampler_Cube, dir, _Lod);
                float3 col = DecodeHDREnvironment(raw, _CubeDecode) / _DynamicRange;
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
}