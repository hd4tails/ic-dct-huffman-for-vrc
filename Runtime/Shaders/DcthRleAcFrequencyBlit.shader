// RLEシンボルを種類別に数え、AC用Huffmanテーブルの出現頻度を作るシェーダー
// 構成: 2つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHRleAcFrequencyBlit"
{
    // Huffman table準備: RLE symbolからAC symbolのrow別frequencyを作る
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("RLE Symbol Texture", 2D) = "black" {}
        // 前段までの行別frequencyを保持するTexture
        _PreviousFrequencyRowsTex ("Previous Frequency Rows", 2D) = "black" {}
        // xyにシンボルTextureの幅・高さを保持する入力値
        _SymbolSize ("Symbol Size", Vector) = (512, 512, 0, 0)
        // xyにfrequency Textureの幅・高さを保持する入力値
        _FrequencySize ("Frequency Size", Vector) = (256, 64, 0, 0)
        // 今回処理するブロック列の開始位置
        _BlockColumnStart ("Block Column Start", Float) = 0
        // 今回処理するブロック列数
        _BlockColumnCount ("Block Column Count", Float) = 64
        // ACスロット開始位置
        _AcSlotStart ("AC Slot Start", Float) = 1
        // ACスロット個数
        _AcSlotCount ("AC Slot Count", Float) = 63
        // 前段までの行別frequencyが利用可能かを示すフラグ
        _HasPreviousFrequencyRows ("Has Previous Frequency Rows", Float) = 0
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

        // 現在のBlitで基準入力として読むTexture
        sampler2D _MainTex;
        // 前段までの行別frequencyを保持するTexture
        sampler2D _PreviousFrequencyRowsTex;
        // xyにシンボルTextureの幅・高さを保持する入力値
        float4 _SymbolSize;
        // xyにfrequency Textureの幅・高さを保持する入力値
        float4 _FrequencySize;
        // 今回処理するブロック列の開始位置
        float _BlockColumnStart;
        // 今回処理するブロック列数
        float _BlockColumnCount;
        // ACスロット開始位置
        float _AcSlotStart;
        // ACスロット個数
        float _AcSlotCount;
        // 前段までの行別frequencyが利用可能かを示すフラグ
        float _HasPreviousFrequencyRows;

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

        float DecodeUInt24(float4 packed)
        {
            float r = floor(saturate(packed.r) * 255.0 + 0.5);
            float g = floor(saturate(packed.g) * 255.0 + 0.5);
            float b = floor(saturate(packed.b) * 255.0 + 0.5);
            return r + g * 256.0 + b * 65536.0;
        }

        float4 EncodeUInt24(float value)
        {
            float clamped = min(floor(value + 0.5), 16777215.0);
            float r = fmod(clamped, 256.0);
            float g = fmod(floor(clamped / 256.0), 256.0);
            float b = floor(clamped / 65536.0);
            return float4(r / 255.0, g / 255.0, b / 255.0, 1.0);
        }

        float UnpackSigned16(float4 packed)
        {
            float lo = floor(saturate(packed.r) * 255.0 + 0.5);
            float hi = floor(saturate(packed.g) * 255.0 + 0.5);
            return lo + hi * 256.0 - 32768.0;
        }

        float RoundToIntStable(float value)
        {
            return value < 0.0 ? ceil(value - 0.5) : floor(value + 0.5);
        }

        float AcCoeffSizeCategory(float coeff)
        {
            float magnitude = abs(RoundToIntStable(coeff));
            if (magnitude < 0.5) return 0.0;
            if (magnitude < 2.0) return 1.0;
            if (magnitude < 4.0) return 2.0;
            if (magnitude < 8.0) return 3.0;
            if (magnitude < 16.0) return 4.0;
            if (magnitude < 32.0) return 5.0;
            if (magnitude < 64.0) return 6.0;
            if (magnitude < 128.0) return 7.0;
            if (magnitude < 256.0) return 8.0;
            if (magnitude < 512.0) return 9.0;
            return 10.0;
        }

        float CountSymbolForBin(float4 symbol, float targetBin)
        {
            float flags = floor(saturate(symbol.a) * 255.0 + 0.5);
            bool valid = fmod(flags, 2.0) >= 1.0;
            bool dc = fmod(floor(flags / 2.0), 2.0) >= 1.0;
            bool eob = fmod(floor(flags / 4.0), 2.0) >= 1.0;
            if (!valid || dc)
            {
                return 0.0;
            }

            if (eob)
            {
                return abs(targetBin) < 0.5 ? 1.0 : 0.0;
            }

            float run = floor(saturate(symbol.b) * 255.0 + 0.5);
            float sizeCategory = AcCoeffSizeCategory(UnpackSigned16(symbol));
            if (sizeCategory < 0.5)
            {
                return 0.0;
            }

            float count = 0.0;
            float zrlCount = floor(run / 16.0);
            float residualRun = run - zrlCount * 16.0;
            if (abs(targetBin - 240.0) < 0.5)
            {
                count += zrlCount;
            }

            float bin = residualRun * 16.0 + sizeCategory;
            return abs(targetBin - bin) < 0.5 ? count + 1.0 : count;
        }

        float2 GetPixel(float4 screenPos, float2 size)
        {
            return min(floor(screenPos.xy), max(size - 1.0, 0.0));
        }
        ENDCG

        Pass
        {
            Name "RleAcFrequencyRows"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 frequencySize = max(_FrequencySize.xy, 1.0.xx);
                float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
                float2 pixel = GetPixel(i.pos, frequencySize);
                float targetBin = pixel.x;
                int blockY = (int)pixel.y;
                float2 frequencyUv = (pixel + 0.5) / frequencySize;
                float count = _HasPreviousFrequencyRows > 0.5
                    ? DecodeUInt24(tex2D(_PreviousFrequencyRowsTex, frequencyUv))
                    : 0.0;
                int blockColumnStart = max((int)floor(_BlockColumnStart + 0.5), 0);
                int blockColumnEnd = min(
                    blockColumnStart + max((int)floor(_BlockColumnCount + 0.5), 1),
                    (int)ceil(symbolSize.x / 8.0));
                int slotStart = clamp((int)floor(_AcSlotStart + 0.5), 1, 63);
                int slotEnd = min(slotStart + max((int)floor(_AcSlotCount + 0.5), 1), 64);

                // 1 pixel が 1 bin / 1 block row を担当し、横方向の block x 63 AC slots を数える
                [loop]
                for (int blockX = blockColumnStart; blockX < blockColumnEnd; blockX++)
                {
                    [loop]
                    for (int slotIndex = slotStart; slotIndex < slotEnd; slotIndex++)
                    {
                        // slotIndexは1～63の非負整数なので、正確な2進小数0.125の乗算で/8を置き換える
                        float slotRow = floor((float)slotIndex * 0.125);
                        float2 slot = float2(fmod((float)slotIndex, 8.0), slotRow);
                        float2 uv = (float2(blockX, blockY) * 8.0 + slot + 0.5) / symbolSize;
                        count += CountSymbolForBin(tex2D(_MainTex, uv), targetBin);
                    }
                }

                return EncodeUInt24(count);
            }
            ENDCG
        }

        Pass
        {
            Name "RleAcFrequencyTotal"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 frequencySize = max(_FrequencySize.xy, 1.0.xx);
                float2 pixel = GetPixel(i.pos, float2(frequencySize.x, 1.0));
                int targetBin = (int)pixel.x;
                float count = 0.0;

                // row pass の各 block row を合算して、256-bin の最終 histogram にする
                [loop]
                for (int row = 0; row < (int)ceil(frequencySize.y); row++)
                {
                    float2 uv = (float2(targetBin, row) + 0.5) / frequencySize;
                    count += DecodeUInt24(tex2D(_MainTex, uv));
                }

                return EncodeUInt24(count);
            }
            ENDCG
        }
    }
}
