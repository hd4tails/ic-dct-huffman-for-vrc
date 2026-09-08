// 固定スロットへ展開したDCTシンボルを逆量子化し、IDCTで1プレーンの画素値へ戻すシェーダー
// 構成: 2つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHDecodeSymbolsBlit"
{
    // Decode補助: 固定symbolを係数Textureへ戻し、逆量子化とIDCTで1 planeを復元する
    // Texture寸法とchannel packingはC#側のdecode用Texture配置と一致させる

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Source Texture", 2D) = "black" {}
        // 前段IDCTを保持する入力Texture
        _PreviousIdctTex ("Previous IDCT Texture", 2D) = "black" {}
        // DCT係数へ適用する量子化table Texture
        _QuantTex ("Quant Table", 2D) = "white" {}
        // ブロックごとのHuffman bit開始位置を保持するTexture
        _BlockOffsetTex ("Block Offset Texture", 2D) = "black" {}
        // xyに出力Textureの幅・高さを保持する入力値
        _OutputSize ("Output Size", Vector) = (512, 512, 0, 0)
        // xyにシンボルTextureの幅・高さを保持する入力値
        _SymbolSize ("Symbol Size", Vector) = (512, 512, 0, 0)
        // 処理対象とするY・A・Cb・Crプレーンを選択するモード
        _PlaneMode ("Plane Mode", Float) = 0
        // 今回処理する局所ブロックグループ番号
        _DecodeLocalGroup ("Decode Local Group", Float) = -1
        // 復号局所1グループあたりの行数
        _DecodeLocalRowsPerGroup ("Decode Local Rows Per Group", Float) = 1
        // 復号ブロック行グループ番号
        _DecodeBlockRowGroup ("Decode Block Row Group", Float) = -1
        // 復号ブロック1グループあたりの行数
        _DecodeBlockRowsPerGroup ("Decode Block Rows Per Group", Float) = 1
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
        // 前段IDCTを保持する入力Texture
        sampler2D _PreviousIdctTex;
        // DCT係数へ適用する量子化table Texture
        sampler2D _QuantTex;
        // ブロックごとのHuffman bit開始位置を保持するTexture
        sampler2D _BlockOffsetTex;
        // xyに出力Textureの幅・高さを保持する入力値
        float4 _OutputSize;
        // xyにシンボルTextureの幅・高さを保持する入力値
        float4 _SymbolSize;
        // 処理対象とするY・A・Cb・Crプレーンを選択するモード
        float _PlaneMode;
        // 今回処理する局所ブロックグループ番号
        float _DecodeLocalGroup;
        // 復号局所1グループあたりの行数
        float _DecodeLocalRowsPerGroup;
        // 復号ブロック行グループ番号
        float _DecodeBlockRowGroup;
        // 復号ブロック1グループあたりの行数
        float _DecodeBlockRowsPerGroup;
        // プレーンごとに適用する量子化の重み
        float4 _QuantWeights;

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

        float SelectDctBasisSampleConst(float sample, float v0, float v1, float v2, float v3, float v4, float v5, float v6, float v7)
        {
            float s = floor(sample + 0.5);
            if (s < 0.5) return v0;
            if (s < 1.5) return v1;
            if (s < 2.5) return v2;
            if (s < 3.5) return v3;
            if (s < 4.5) return v4;
            if (s < 5.5) return v5;
            if (s < 6.5) return v6;
            return v7;
        }

        // IDCT basis定数tableは
        // (coeff == 0 ? 0.70710678118 : 1) * cos(((2 * sample + 1) * coeff * pi) / 16) から生成した
        // RGBAFloat basis Texture samplingのAndroid差を避けるため、Textureでなく定数を使う
        float ReadNormalizedDctBasis(float sample, float coeff)
        {
            float c = floor(coeff + 0.5);
            if (c < 0.5)
            {
                return ICDctRound(0.70710678118);
            }
            if (c < 1.5)
            {
                return ICDctRound(SelectDctBasisSampleConst(sample, 0.9807852804, 0.8314696123, 0.5555702330, 0.1950903220, -0.1950903220, -0.5555702330, -0.8314696123, -0.9807852804));
            }
            if (c < 2.5)
            {
                return ICDctRound(SelectDctBasisSampleConst(sample, 0.9238795325, 0.3826834324, -0.3826834324, -0.9238795325, -0.9238795325, -0.3826834324, 0.3826834324, 0.9238795325));
            }
            if (c < 3.5)
            {
                return ICDctRound(SelectDctBasisSampleConst(sample, 0.8314696123, -0.1950903220, -0.9807852804, -0.5555702330, 0.5555702330, 0.9807852804, 0.1950903220, -0.8314696123));
            }
            if (c < 4.5)
            {
                return ICDctRound(SelectDctBasisSampleConst(sample, 0.7071067812, -0.7071067812, -0.7071067812, 0.7071067812, 0.7071067812, -0.7071067812, -0.7071067812, 0.7071067812));
            }
            if (c < 5.5)
            {
                return ICDctRound(SelectDctBasisSampleConst(sample, 0.5555702330, -0.9807852804, 0.1950903220, 0.8314696123, -0.8314696123, -0.1950903220, 0.9807852804, -0.5555702330));
            }
            if (c < 6.5)
            {
                return ICDctRound(SelectDctBasisSampleConst(sample, 0.3826834324, -0.9238795325, 0.9238795325, -0.3826834324, -0.3826834324, 0.9238795325, -0.9238795325, 0.3826834324));
            }

            return ICDctRound(SelectDctBasisSampleConst(sample, 0.1950903220, -0.5555702330, 0.8314696123, -0.9807852804, 0.9807852804, -0.8314696123, 0.5555702330, -0.1950903220));
        }

        float SelectQuant(float4 quant)
        {
            float planeMode = floor(_PlaneMode + 0.5);
            if (planeMode < 0.5) return quant.r;
            if (planeMode < 1.5) return quant.g;
            return quant.b;
        }

        float ReadYQuant(float2 coeff)
        {
            float2 uv = (coeff + 0.5) / 8.0;
            return max(ICDctRoundToIntStable(ICDctMul(SelectQuant(ICDctRound4(tex2D(_QuantTex, uv))), 255.0)), 1.0);
        }

        int CoordToZigzag(float2 coord)
        {
            int flat = (int)(coord.y * 8.0 + coord.x);
            if (flat == 0) return 0;
            if (flat == 1) return 1;
            if (flat == 2) return 5;
            if (flat == 3) return 6;
            if (flat == 4) return 14;
            if (flat == 5) return 15;
            if (flat == 6) return 27;
            if (flat == 7) return 28;
            if (flat == 8) return 2;
            if (flat == 9) return 4;
            if (flat == 10) return 7;
            if (flat == 11) return 13;
            if (flat == 12) return 16;
            if (flat == 13) return 26;
            if (flat == 14) return 29;
            if (flat == 15) return 42;
            if (flat == 16) return 3;
            if (flat == 17) return 8;
            if (flat == 18) return 12;
            if (flat == 19) return 17;
            if (flat == 20) return 25;
            if (flat == 21) return 30;
            if (flat == 22) return 41;
            if (flat == 23) return 43;
            if (flat == 24) return 9;
            if (flat == 25) return 11;
            if (flat == 26) return 18;
            if (flat == 27) return 24;
            if (flat == 28) return 31;
            if (flat == 29) return 40;
            if (flat == 30) return 44;
            if (flat == 31) return 53;
            if (flat == 32) return 10;
            if (flat == 33) return 19;
            if (flat == 34) return 23;
            if (flat == 35) return 32;
            if (flat == 36) return 39;
            if (flat == 37) return 45;
            if (flat == 38) return 52;
            if (flat == 39) return 54;
            if (flat == 40) return 20;
            if (flat == 41) return 22;
            if (flat == 42) return 33;
            if (flat == 43) return 38;
            if (flat == 44) return 46;
            if (flat == 45) return 51;
            if (flat == 46) return 55;
            if (flat == 47) return 60;
            if (flat == 48) return 21;
            if (flat == 49) return 34;
            if (flat == 50) return 37;
            if (flat == 51) return 47;
            if (flat == 52) return 50;
            if (flat == 53) return 56;
            if (flat == 54) return 59;
            if (flat == 55) return 61;
            if (flat == 56) return 35;
            if (flat == 57) return 36;
            if (flat == 58) return 48;
            if (flat == 59) return 49;
            if (flat == 60) return 57;
            if (flat == 61) return 58;
            if (flat == 62) return 62;
            return 63;
        }

        float UnpackSigned16(float4 packed)
        {
            float lo = floor(saturate(packed.r) * 255.0 + 0.5);
            float hi = floor(saturate(packed.g) * 255.0 + 0.5);
            return lo + hi * 256.0 - 32768.0;
        }

        float ReadCoeffFromSymbolFixed(float2 block, float2 coeff)
        {
            float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
            int zigzagIndex = CoordToZigzag(coeff);
            // indexは0～63の非負整数で、0.125は2進数で正確に表現できる
            // 整数除算を使わず、PC/Mobileとも元のindex / 8と同じrowを求める
            float slotRow = floor((float)zigzagIndex * 0.125);
            float2 slot = float2(fmod((float)zigzagIndex, 8.0), slotRow);
            float2 uv = (block * 8.0 + slot + 0.5) / symbolSize;
            float4 symbol = tex2D(_MainTex, uv);
            float flags = floor(saturate(symbol.a) * 255.0 + 0.5);

            // compact streamは固定slotへ展開済み。係数は整数payload dataなので、
            // byte[] pack前の実数DCT/量子化判定用丸めを再適用しない
            return fmod(flags, 2.0) >= 1.0 ? UnpackSigned16(symbol) : 0.0;
        }

        float ReadVerticalPartial(float2 block, float u, float sampleY)
        {
            float2 outputSize = max(_OutputSize.xy, 1.0.xx);
            float2 uv = (block * 8.0 + float2(u, sampleY) + 0.5) / outputSize;
            return ICDctRound(tex2D(_MainTex, uv).r);
        }

        float2 GetPixel(float4 screenPos, float2 size)
        {
            return min(floor(screenPos.xy), max(size - 1.0, 0.0));
        }

        bool IsActiveIdctGroup(float blockRow, float localValue)
        {
            float group = floor(_DecodeLocalGroup + 0.5);
            // Androidの1 frameを短く保つため、C#がIDCTをlocal row/column groupでscheduleする
            // groupを広げると1 frameのshader負荷が増えるため、製品既定値に従う
            float rowsPerGroup = max(floor(_DecodeLocalRowsPerGroup + 0.5), 1.0);
            float localGroup = floor(floor(localValue + 0.5) / rowsPerGroup);
            if (group >= 0.0 && abs(localGroup - group) > 0.5)
            {
                return false;
            }

            // local分割だけでは各Blitがplane全体を触る。DCT block rowでも分割し、
            // IDCT式と最終pixelを変えずAndroid展開中の応答を保つ
            float blockGroup = floor(_DecodeBlockRowGroup + 0.5);
            float blockRowsPerGroup = max(floor(_DecodeBlockRowsPerGroup + 0.5), 1.0);
            float currentBlockGroup = floor(floor(blockRow + 0.5) / blockRowsPerGroup);
            return blockGroup < 0.0 || abs(currentBlockGroup - blockGroup) <= 0.5;
        }
        ENDCG

        Pass
        {
            Name "DecodeSymbolsVertical"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
                float2 workPixel = GetPixel(i.pos, symbolSize);
                float2 block = floor(workPixel / 8.0);
                float2 local = workPixel - block * 8.0;
                float u = local.x;
                float sampleY = local.y;
                if (!IsActiveIdctGroup(block.y, sampleY))
                {
                    // Android decodeは1 frameにつき小さいlocal-row groupを処理する
                    // 対象外rowは直前のping-pong値をcopyし、長い全画面IDCTを使わず最終的に全pixelを埋める
                    return tex2D(_PreviousIdctTex, (workPixel + 0.5) / symbolSize);
                }

                float sum = 0.0;
                // IDCTの逆量子化と積和は高精度floatで計算する
                [unroll]
                for (int v = 0; v < 8; v++)
                {
                    float2 coeff = float2(u, v);
                    float dequantized = ICDctMul(ReadCoeffFromSymbolFixed(block, coeff), ReadYQuant(coeff));
                    sum = ICDctMadd(dequantized, ReadNormalizedDctBasis(sampleY, v), sum);
                }

                return float4(sum, 0.0, 0.0, 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "DecodeSymbolsHorizontal"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 outputSize = max(_OutputSize.xy, 1.0.xx);
                float2 pixel = GetPixel(i.pos, outputSize);
                float2 block = floor(pixel / 8.0);
                float2 localPixel = pixel - block * 8.0;
                if (!IsActiveIdctGroup(block.y, localPixel.x))
                {
                    // horizontal IDCTもvertical passと同じMobile frame予算のためlocal-column groupで分割する
                    return tex2D(_PreviousIdctTex, (pixel + 0.5) / outputSize);
                }

                float sum = 0.0;
                // horizontal IDCT積和は高精度floatで計算する
                [unroll]
                for (int u = 0; u < 8; u++)
                {
                    sum = ICDctMadd(ReadVerticalPartial(block, u, localPixel.y), ReadNormalizedDctBasis(localPixel.x, u), sum);
                }

                float y = saturate(ICDctDiv(ICDctAdd(ICDctMul(0.25, sum), 128.0), 255.0));
                return float4(y, y, y, 1.0);
            }
            ENDCG
        }
    }
}
