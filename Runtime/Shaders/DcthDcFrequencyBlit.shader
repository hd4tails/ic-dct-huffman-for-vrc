// DC差分をカテゴリ別に数え、DC用Huffmanテーブルの出現頻度を作るシェーダー
// 構成: 2つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHDcFrequencyBlit"
{
    // Huffman table準備: DC deltaからDC category別frequencyを作る
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("DC Delta Texture", 2D) = "black" {}
        // 前段出現頻度を保持する入力Texture
        _PreviousFrequencyTex ("Previous Frequency Texture", 2D) = "black" {}
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // xyにfrequency Textureの幅・高さを保持する入力値
        _FrequencySize ("Frequency Size", Vector) = (16, 64, 0, 0)
        // 今回集計するfrequencyグループの開始位置
        _FrequencyGroupStart ("Frequency Group Start", Float) = 0
        // 今回集計するfrequencyグループ数
        _FrequencyGroupCount ("Frequency Group Count", Float) = 64
        // 前段までのfrequencyが利用可能かを示すフラグ
        _HasPreviousFrequency ("Has Previous Frequency", Float) = 0
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
        // 前段出現頻度を保持する入力Texture
        sampler2D _PreviousFrequencyTex;
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;
        // xyにfrequency Textureの幅・高さを保持する入力値
        float4 _FrequencySize;
        // 今回集計するfrequencyグループの開始位置
        float _FrequencyGroupStart;
        // 今回集計するfrequencyグループ数
        float _FrequencyGroupCount;
        // 前段までのfrequencyが利用可能かを示すフラグ
        float _HasPreviousFrequency;

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

        float DcCoeffSizeCategory(float coeff)
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
            if (magnitude < 1024.0) return 10.0;
            return 11.0;
        }

        float ReadDcDelta(float2 block)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            float2 uv = (block + 0.5) / blockCount;
            return UnpackSigned16(tex2D(_MainTex, uv));
        }
        ENDCG

        Pass
        {
            Name "DcFrequencyRows"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 frequencySize = max(_FrequencySize.xy, 1.0.xx);
                float2 pixel = GetPixel(i.pos, frequencySize);
                float targetSize = pixel.x;
                int blockY = (int)pixel.y;
                float2 frequencyUv = (pixel + 0.5) / frequencySize;
                float count = _HasPreviousFrequency > 0.5
                    ? DecodeUInt24(tex2D(_PreviousFrequencyTex, frequencyUv))
                    : 0.0;
                int groupStart = max((int)floor(_FrequencyGroupStart + 0.5), 0);
                int groupEnd = min(
                    groupStart + max((int)floor(_FrequencyGroupCount + 0.5), 1),
                    (int)ceil(_BlockCount.x));

                // 1 pixel が 1 size category / 1 block row を担当し、横方向の block 分の DC delta を数える
                [loop]
                for (int blockX = groupStart; blockX < groupEnd; blockX++)
                {
                    float category = DcCoeffSizeCategory(ReadDcDelta(float2(blockX, blockY)));
                    if (abs(category - targetSize) < 0.5)
                    {
                        count += 1.0;
                    }
                }

                return EncodeUInt24(count);
            }
            ENDCG
        }

        Pass
        {
            Name "DcFrequencyTotal"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 frequencySize = max(_FrequencySize.xy, 1.0.xx);
                float2 pixel = GetPixel(i.pos, float2(frequencySize.x, 1.0));
                int targetSize = (int)pixel.x;
                float2 outputUv = (pixel + 0.5) / float2(frequencySize.x, 1.0);
                float count = _HasPreviousFrequency > 0.5
                    ? DecodeUInt24(tex2D(_PreviousFrequencyTex, outputUv))
                    : 0.0;
                int groupStart = max((int)floor(_FrequencyGroupStart + 0.5), 0);
                int groupEnd = min(
                    groupStart + max((int)floor(_FrequencyGroupCount + 0.5), 1),
                    (int)ceil(frequencySize.y));

                // row pass の各 block row を合算して、DC Huffman 用の 16-bin histogram にする
                [loop]
                for (int row = groupStart; row < groupEnd; row++)
                {
                    float2 uv = (float2(targetSize, row) + 0.5) / frequencySize;
                    count += DecodeUInt24(tex2D(_MainTex, uv));
                }

                return EncodeUInt24(count);
            }
            ENDCG
        }
    }
}
