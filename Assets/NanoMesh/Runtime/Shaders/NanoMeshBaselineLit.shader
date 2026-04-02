Shader "NanoMesh/BaselineLitURP"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _BaseMap("Base Map", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct ClusterRecordGpu
            {
                int vertexDataOffsetBytes;
                int vertexCount;
                int indexDataOffsetBytes;
                int indexCount;
                int materialRangeIndex;
                int hierarchyNodeIndex;
                int hierarchyLevel;
                float geometricError;
                float3 positionOrigin;
                float padding0;
                float3 positionExtent;
                float padding1;
            };

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
            };

            ByteAddressBuffer _NanoMeshVertexPayloadBuffer;
            ByteAddressBuffer _NanoMeshIndexPayloadBuffer;
            StructuredBuffer<ClusterRecordGpu> _NanoMeshClusterBuffer;

            int _NanoMeshClusterIndex;
            float4 _NanoMeshUvDecode;
            float _NanoMeshDebugClusters;

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            CBUFFER_END

            uint LoadUInt16(ByteAddressBuffer buffer, uint byteOffset)
            {
                uint alignedOffset = byteOffset & ~3u;
                uint packed = buffer.Load(alignedOffset);
                uint shift = (byteOffset & 2u) * 8u;
                return (packed >> shift) & 0xFFFFu;
            }

            int UnpackSigned16(uint packed, bool useHighWord)
            {
                int value = useHighWord ? (int)(packed >> 16) : (int)(packed & 0xFFFFu);
                return (value << 16) >> 16;
            }

            float DecodeSnorm16(int value)
            {
                return max((float)value / 32767.0f, -1.0f);
            }

            float3 ComputeClusterDebugColor(uint clusterIndex)
            {
                float3 seed = float3(clusterIndex, clusterIndex + 17u, clusterIndex + 37u);
                return 0.25f + 0.75f * frac(sin(seed * float3(12.9898f, 78.233f, 37.719f)) * 43758.5453f);
            }

            Varyings Vert(Attributes input)
            {
                ClusterRecordGpu cluster = _NanoMeshClusterBuffer[_NanoMeshClusterIndex];
                uint indexByteOffset = (uint)cluster.indexDataOffsetBytes + input.vertexID * 2u;
                uint vertexIndex = LoadUInt16(_NanoMeshIndexPayloadBuffer, indexByteOffset);
                uint vertexByteOffset = (uint)cluster.vertexDataOffsetBytes + vertexIndex * 16u;
                uint4 packedVertex = _NanoMeshVertexPayloadBuffer.Load4(vertexByteOffset);

                uint positionX = packedVertex.x & 0xFFFFu;
                uint positionY = packedVertex.x >> 16;
                uint positionZ = packedVertex.y & 0xFFFFu;
                int normalX = UnpackSigned16(packedVertex.y, true);
                int normalY = UnpackSigned16(packedVertex.z, false);
                int normalZ = UnpackSigned16(packedVertex.z, true);
                uint uvX = packedVertex.w & 0xFFFFu;
                uint uvY = packedVertex.w >> 16;

                float3 positionOS = cluster.positionOrigin + cluster.positionExtent * (float3(positionX, positionY, positionZ) / 65535.0f);
                float3 normalOS = normalize(float3(DecodeSnorm16(normalX), DecodeSnorm16(normalY), DecodeSnorm16(normalZ)));
                float2 uv = _NanoMeshUvDecode.xy + _NanoMeshUvDecode.zw * (float2(uvX, uvY) / 65535.0f);
                uv = uv * _BaseMap_ST.xy + _BaseMap_ST.zw;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(positionOS);
                float3 normalWS = TransformObjectToWorldNormal(normalOS);

                Varyings output;
                output.positionCS = positionInputs.positionCS;
                output.uv = uv;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalWS;
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                output.shadowCoord = GetShadowCoord(positionInputs);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                if (_NanoMeshDebugClusters > 0.5f)
                {
                    return half4(ComputeClusterDebugColor((uint)_NanoMeshClusterIndex), 1.0h);
                }

                float3 normalWS = normalize(input.normalWS);
                half3 lighting = SampleSH(normalWS) * albedo.rgb;

                Light mainLight = GetMainLight(input.shadowCoord);
                half mainNdotL = saturate(dot(normalWS, mainLight.direction));
                lighting += albedo.rgb * mainLight.color * (mainNdotL * mainLight.shadowAttenuation * mainLight.distanceAttenuation);

                #if defined(_ADDITIONAL_LIGHTS)
                uint additionalLightCount = GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < additionalLightCount; lightIndex++)
                {
                    Light additionalLight = GetAdditionalLight(lightIndex, input.positionWS);
                    half additionalNdotL = saturate(dot(normalWS, additionalLight.direction));
                    lighting += albedo.rgb * additionalLight.color * (additionalNdotL * additionalLight.distanceAttenuation * additionalLight.shadowAttenuation);
                }
                #endif

                lighting = MixFog(lighting, input.fogFactor);
                return half4(lighting, albedo.a);
            }
            ENDHLSL
        }
    }
}
