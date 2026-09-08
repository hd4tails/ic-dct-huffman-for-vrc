// DCTHのCb/Crプレーンを半解像度で符号化する前に、入力色を事前フィルターして縮小するシェーダー
// 構成: 1つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
Shader "HDAssets/IC/DCTH/DCTHColorDownsampleBlit"
{
    // half-size Cb/CrのDCT encode前にsource colorをpre-filterする
    // 出力はUnity linear色空間のまま保持し、DCT encodeがsource Textureとして読む

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Source Texture", 2D) = "white" {}
        // xyに入力Textureの幅・高さを保持する入力値
        _SourceSize ("Source Size", Vector) = (512, 512, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "ColorDownsample2x2"

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // 現在のBlitで基準入力として読むTexture
            sampler2D _MainTex;
            // xyに入力Textureの幅・高さを保持する入力値
            float4 _SourceSize;

            struct Varyings
            {
                // clip空間へ変換した頂点位置
                float4 pos : SV_POSITION;
                // 頂点処理で受け渡すUV座標
                float2 uv : TEXCOORD0;
            };

            Varyings vert(appdata_img v)
            {
                Varyings o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float4 SampleSource(float2 pixel, float2 sourceSize)
            {
                float2 clampedPixel = clamp(pixel, float2(0.0, 0.0), sourceSize - 1.0);
                return tex2D(_MainTex, (clampedPixel + 0.5) / sourceSize);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 sourceSize = max(_SourceSize.xy, 1.0.xx);
                float2 targetPixel = floor(i.pos.xy);
                float2 sourcePixel = targetPixel * 2.0;
                float4 c00 = SampleSource(sourcePixel, sourceSize);
                float4 c10 = SampleSource(sourcePixel + float2(1.0, 0.0), sourceSize);
                float4 c01 = SampleSource(sourcePixel + float2(0.0, 1.0), sourceSize);
                float4 c11 = SampleSource(sourcePixel + float2(1.0, 1.0), sourceSize);
                return (c00 + c10 + c01 + c11) * 0.25;
            }
            ENDCG
        }
    }
}
