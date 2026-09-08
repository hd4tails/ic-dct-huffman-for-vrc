// 水平DCTの中間係数へ垂直DCTと量子化を行い、整数係数Textureを作るシェーダー
// 構成: 1つのPassで構成し、DcthEncodeCommon.cgincを読み込んで共通の頂点処理・補助関数を利用する
Shader "HDAssets/IC/DCTH/DCTHVerticalQuantizeBlit"
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
            Name "EncodeDcthVerticalQuantize"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings i) : SV_Target
            {
                float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
                float2 coeffPixel = GetPixel(i.pos, coeffSize);
                float quotient = ComputeQuantizedDctQuotient(coeffPixel);
                float coeff = ICEncodeDctRoundToIntStable(quotient);
                return float4(coeff, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
