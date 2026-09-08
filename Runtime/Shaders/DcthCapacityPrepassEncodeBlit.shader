// DCTHの本圧縮前に容量を予測するため、入力画像へ水平DCTを行う事前処理用シェーダー
// 構成: 1つのPassで構成し、DcthEncodeCommon.cgincを読み込んで共通の頂点処理・補助関数を利用する
Shader "HDAssets/IC/DCTH/DCTHCapacityPrepassEncodeBlit"
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
        // 水平分割処理の前段結果を保持するTexture
        _HorizontalPreviousTex ("Horizontal Previous", 2D) = "black" {}
        // 水平処理サンプルオフセット
        _HorizontalSampleOffset ("Horizontal Sample Offset", Float) = 0
        // 水平処理前段が利用可能かを示すフラグ
        _HasHorizontalPrevious ("Has Horizontal Previous", Float) = 0
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
            Name "CapacityPrepassDctHorizontal"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings i) : SV_Target
            {
                float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
                float2 workPixel = GetPixel(i.pos, coeffSize);
                float2 sampleBlock = floor(workPixel / 8.0);
                float2 local = workPixel - sampleBlock * 8.0;
                float2 sourceBlock = ReadCapacityPrepassSourceBlock(sampleBlock);
                float2 workUv = (workPixel + 0.5) / coeffSize;
                float sum = _HasHorizontalPrevious > 0.5
                    ? ICEncodeDctAccumRound(tex2D(_HorizontalPreviousTex, workUv).r)
                    : 0.0;
                int sampleOffset = (int)floor(_HorizontalSampleOffset + 0.5);
                [unroll(2)]
                for (int lane = 0; lane < 2; lane++)
                {
                    int sx = sampleOffset + lane;
                    float2 samplePixel = sourceBlock * 8.0 + float2(sx, local.y);
                    sum = ICEncodeDctAccumMadd(ReadY255(samplePixel), ReadDctBasisConst(sx, local.x), sum);
                }
                return float4(ICEncodeDctAccumRound(sum), 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
