// 量子化済みDCT係数のDC値を直前ブロックとの差分へ変換し、Huffman符号化しやすくするシェーダー
// 構成: 2つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHDcDeltaBlit"
{
    // DC prediction stage: blockごとのDC値をdelta化し、Huffmanへ渡す値を小さくする
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Source Texture", 2D) = "black" {}
        // xyにDCT係数Textureの幅・高さを保持する入力値
        _CoeffSize ("Coefficient Size", Vector) = (512, 512, 0, 0)
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        CGINCLUDE
        #pragma target 3.0
        #include "UnityCG.cginc"
        #include "DcthHalfRound.cginc"

        // 現在のBlitで基準入力として読むTexture
        sampler2D _MainTex;
        // xyにDCT係数Textureの幅・高さを保持する入力値
        float4 _CoeffSize;
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;

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

        float2 GetPixel(float4 screenPos, float2 size)
        {
            return min(floor(screenPos.xy), max(size - 1.0, 0.0));
        }

        float4 PackSigned16(float value)
        {
            // DC deltaは整数dataとしてHuffman化する。実数境界判定は量子化passで確定済みなので、
            // encode境界処理をここで再適用しない
            float clamped = ICQuantizedDctClampSigned16(value);
            float encoded = clamped + 32768.0;
            float lo = fmod(encoded, 256.0);
            float hi = floor(encoded / 256.0);
            return float4(lo / 255.0, hi / 255.0, 0.0, 1.0);
        }

        float UnpackSigned16(float4 packed)
        {
            float lo = floor(saturate(packed.r) * 255.0 + 0.5);
            float hi = floor(saturate(packed.g) * 255.0 + 0.5);
            return lo + hi * 256.0 - 32768.0;
        }

            float ReadDcFromCoeff(float2 block)
            {
                float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
                float2 uv = (block * 8.0 + 0.5) / coeffSize;
                // coeff RT内のDCは量子化済み整数係数。広いencode境界処理はdct/quant step専用なので、
                // 通常の安定丸めで読む
                return ICQuantizedDctRoundToInt(tex2D(_MainTex, uv).r);
            }

        float ReadDcFromPacked(float2 block)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            float2 uv = (block + 0.5) / blockCount;
            return UnpackSigned16(tex2D(_MainTex, uv));
        }
        ENDCG

        Pass
        {
            Name "DcExtract"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 block = GetPixel(i.pos, blockCount);
                return PackSigned16(ReadDcFromCoeff(block));
            }
            ENDCG
        }

        Pass
        {
            Name "DcDelta"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 block = GetPixel(i.pos, blockCount);
                float blockIndex = block.y * blockCount.x + block.x;
                float currentDc = ReadDcFromPacked(block);
                float previousDc = 0.0;

                // JPEG と同じ raster order の直前 block を参照し、先頭 block は 0 との差分にする
                if (blockIndex > 0.5)
                {
                    float previousIndex = blockIndex - 1.0;
                    float2 previousBlock = float2(fmod(previousIndex, blockCount.x), floor(previousIndex / blockCount.x));
                    previousDc = ReadDcFromPacked(previousBlock);
                }

                return PackSigned16(currentDc - previousDc);
            }
            ENDCG
        }
    }
}
