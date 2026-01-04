Shader "Custom/FogOfWarFlow"
{
    Properties
    {
        _MainTex ("Fog Mask", 2D) = "white" {}
        _NoiseTex ("Noise Texture", 2D) = "white" {}
        
        _Color ("Cloud Color", Color) = (1, 1, 1, 1)
        _ShadowColor ("Shadow Color", Color) = (0.5, 0.6, 0.8, 1)
        
        // [核心修改] 现在这个值代表"世界单位" (米)
        // 建议设为 3.0 到 5.0，表示边缘会有 3-5 米的随机扭曲
        _DistortStrength ("Distortion (Meters)", Float) = 5.0
        
        // [自动传入] 不需要手动调，由脚本传入
        _MapSize ("Map Size", Float) = 100.0
        
        _CoreSolidity ("Core Solidity", Range(1.0, 10.0)) = 5.0
        _EdgeFading ("Edge Fading", Range(0.0, 1.0)) = 0.5
        
        _ShadowScale ("Shadow Scale", Range(0, 1)) = 0.4
        _ShadowHardness ("Shadow Hardness", Range(0.01, 1)) = 0.3
        _Scale ("Noise Scale", Float) = 1.2
        _Speed ("Scroll Speed", Vector) = (0.015, 0.005, 0, 0)
    }
    SubShader
    {
        // 保持最强遮挡模式
        Tags { "Queue"="Transparent+2000" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        ZTest Always
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float2 noiseUV : TEXCOORD1;
            };

            sampler2D _MainTex; float4 _MainTex_ST;
            sampler2D _NoiseTex; float4 _NoiseTex_ST;
            
            fixed4 _Color;
            fixed4 _ShadowColor;
            float _CoreSolidity;
            float _EdgeFading;
            float _DistortStrength;
            float _MapSize; // 新增：地图尺寸
            float _ShadowScale;
            float _ShadowHardness;
            float _Scale;
            float4 _Speed;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                // 优化：让噪波纹理也随地图大小自动缩放，保证纹理密度一致
                // 这样大地图和小地图的云朵大小看起来是一样的
                o.noiseUV = v.uv * _Scale * (_MapSize / 128.0); 
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 flow = _Time.y * _Speed.xy;
                fixed noise1 = tex2D(_NoiseTex, i.noiseUV + flow).r;
                fixed noise2 = tex2D(_NoiseTex, i.noiseUV * 0.6 - flow * 0.5).r;
                fixed cloudNoise = (noise1 + noise2) * 0.5;

                // [核心修复] 
                // 将"米"转换为"UV单位"。
                // 假设地图宽1000米，你想偏移5米。 UV偏移量 = 5 / 1000 = 0.005
                float realDistortion = _DistortStrength / _MapSize;
                
                float2 distortOffset = (float2(noise1, noise2) - 0.5) * realDistortion;
                fixed maskVal = tex2D(_MainTex, saturate(i.uv + distortOffset)).a;

                // 虚边实心逻辑
                float alpha = maskVal - (1.0 - cloudNoise) * _EdgeFading;
                alpha = saturate(alpha * _CoreSolidity);

                float colorMix = smoothstep(_ShadowScale, _ShadowScale + _ShadowHardness, cloudNoise);
                fixed4 finalCol = lerp(_ShadowColor, _Color, colorMix);

                finalCol.a = alpha;
                finalCol.a *= smoothstep(0.0, 0.2, maskVal);

                return finalCol;
            }
            ENDCG
        }
    }
}