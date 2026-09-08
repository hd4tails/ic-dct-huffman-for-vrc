// DCTH容量予測用のDCT係数を量子化し、Huffman容量計算に使うデータへ変換するシェーダー
// 構成: 3つのPassで構成し、DcthEncodeCommon.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHCapacityPrepassPostBlit"
{
    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Source Texture", 2D) = "white" {}
        // DCT係数へ適用する量子化table Texture
        _QuantTex ("Quant Table", 2D) = "white" {}
        // 量子化値の逆数を保持するlookup Texture
        _QuantReciprocalTex ("Quant Reciprocal LUT", 2D) = "white" {}
        // xyにDCT係数Textureの幅・高さを保持する入力値
        _CoeffSize ("Coefficient Size", Vector) = (512, 512, 0, 0)
        // xyに入力Textureの幅・高さを保持する入力値
        _SourceSize ("Source Size", Vector) = (512, 512, 0, 0)
        // 処理対象とするY・A・Cb・Crプレーンを選択するモード
        _PlaneMode ("Plane Mode", Float) = 0
        // 入力Textureを色ではなく未変換の格納値として読むかを示すフラグ
        _InputIsRawStorage ("Input Is Raw Storage", Float) = 0
        // 入力TextureがsRGB領域の値を保持しているかを示すフラグ
        _SourceTextureSrgb ("Source Texture sRGB", Float) = 0
        // 符号化するRGB値をsRGB領域として扱うかを示すフラグ
        _EncodeSrgb ("Encode sRGB", Float) = 1
        // 容量予測事前処理ブロックmapを保持する入力Texture
        _SampleBlockMapTex ("Capacity Prepass Block Map", 2D) = "black" {}
        // 容量予測事前処理前段DCを保持する入力Texture
        _SamplePreviousDcTex ("Capacity Prepass Previous DC", 2D) = "black" {}
        // xyに容量予測用ブロックmapの幅・高さを保持する入力値
        _SampleMapSize ("Capacity Prepass Map Size", Vector) = (128, 1, 0, 0)
        // xyに容量予測で参照するブロック範囲、zに合計ブロック数を保持する入力値
        _SampleBlockCount ("Capacity Prepass Block Count", Vector) = (16, 8, 128, 0)
        // xyに入力画像の横・縦方向のブロック数を保持する入力値
        _SourceBlockCount ("Source Block Count", Vector) = (64, 64, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #pragma target 3.5
        #include "DcthEncodeCommon.cginc"
        ENDCG
        Pass
        {
            Name "CapacityPrepassPreviousDcUnquantized"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_SampleBlockCount.xy, 1.0.xx);
                float2 sampleBlock = GetPixel(i.pos, blockCount);
                float sampleIndex = sampleBlock.y * blockCount.x + sampleBlock.x;
                float2 mapUv = (float2(sampleIndex, 0.0) + 0.5) / max(_SampleMapSize.xy, 1.0.xx);
                float4 packedMap = tex2D(_SampleBlockMapTex, mapUv);
                if (DecodeByte(packedMap.r) > 254.5 && DecodeByte(packedMap.g) > 254.5 && DecodeByte(packedMap.b) > 254.5 && DecodeByte(packedMap.a) > 254.5)
                {
                    return float4(0.0, 0.0, 0.0, 1.0);
                }
                float2 dcUv = (sampleBlock * 8.0 + 0.5) / max(_CoeffSize.xy, 1.0.xx);
                return float4(ICEncodeDctAccumRound(tex2D(_MainTex, dcUv).r), 0.0, 0.0, 1.0);
            }
            ENDCG
        }
        Pass
        {
            Name "CapacityPrepassQuantize"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings i) : SV_Target
            {
                float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
                float2 coeffPixel = GetPixel(i.pos, coeffSize);
                float2 coeff = coeffPixel - floor(coeffPixel / 8.0) * 8.0;
                float2 uv = (coeffPixel + 0.5) / coeffSize;
                float dct = ICEncodeDctAccumRound(tex2D(_MainTex, uv).r);
                return float4(QuantizeCapacityPrepassDct(dct, coeff), 0.0, 0.0, 1.0);
            }
            ENDCG
        }
        Pass
        {
            Name "CapacityPrepassDcDeltaQuantize"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_SampleBlockCount.xy, 1.0.xx);
                float2 sampleBlock = GetPixel(i.pos, blockCount);
                float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
                float2 currentUv = (sampleBlock * 8.0 + 0.5) / coeffSize;
                float2 previousUv = (sampleBlock + 0.5) / blockCount;
                float currentDct = ICEncodeDctAccumRound(tex2D(_MainTex, currentUv).r);
                float previousDct = ICEncodeDctAccumRound(tex2D(_SamplePreviousDcTex, previousUv).r);
                float currentDc = QuantizeCapacityPrepassDct(currentDct, float2(0.0, 0.0));
                float previousDc = QuantizeCapacityPrepassDct(previousDct, float2(0.0, 0.0));
                return PackSigned16(currentDc - previousDc);
            }
            ENDCG
        }
    }
    Fallback Off
}
