// DC差分とRLEシンボルのHuffman符号長を合算し、ブロックごとの必要ビット数を求めるシェーダー
// 構成: 2つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
// フラグメント関数はInitializeFrag、AccumulateFragに分かれている
Shader "HDAssets/IC/DCTH/DCTHHuffmanBitCountBlit"
{
    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("RLE Symbol Texture", 2D) = "black" {}
        // 前段ビット数を保持する入力Texture
        _PreviousBitCountTex ("Previous Bit Count Texture", 2D) = "black" {}
        // DC差分を保持する入力Texture
        _DcDeltaTex ("DC Delta Texture", 2D) = "black" {}
        // DC Huffman符号を保持する入力Texture
        _DcHuffmanCodesTex ("DC Huffman Codes", 2D) = "black" {}
        // AC Huffman符号を保持する入力Texture
        _AcHuffmanCodesTex ("AC Huffman Codes", 2D) = "black" {}
        // xyにシンボルTextureの幅・高さを保持する入力値
        _SymbolSize ("Symbol Size", Vector) = (512, 512, 0, 0)
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // xyにDC Huffman tableの幅・高さを保持する入力値
        _DcTableSize ("DC Table Size", Vector) = (16, 1, 0, 0)
        // xyにAC Huffman tableの幅・高さを保持する入力値
        _AcTableSize ("AC Table Size", Vector) = (256, 1, 0, 0)
        // Huffman符号長がR channelに格納されているかを示すフラグ
        _CodeLengthsStoredInRed ("Code Lengths Stored In Red", Float) = 0
        // bit数を加算するスロットグループ番号
        _BitCountSlotGroup ("Bit Count Slot Group", Float) = 0
        // 1グループでbit数を加算するスロット数
        _BitCountSlotsPerGroup ("Bit Count Slots Per Group", Float) = 8
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
        // 前段ビット数を保持する入力Texture
        sampler2D _PreviousBitCountTex;
        // DC差分を保持する入力Texture
        sampler2D _DcDeltaTex;
        // DC Huffman符号を保持する入力Texture
        sampler2D _DcHuffmanCodesTex;
        // AC Huffman符号を保持する入力Texture
        sampler2D _AcHuffmanCodesTex;
        // xyにシンボルTextureの幅・高さを保持する入力値
        float4 _SymbolSize;
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;
        // xyにDC Huffman tableの幅・高さを保持する入力値
        float4 _DcTableSize;
        // xyにAC Huffman tableの幅・高さを保持する入力値
        float4 _AcTableSize;
        // Huffman符号長がR channelに格納されているかを示すフラグ
        float _CodeLengthsStoredInRed;
        // bit数を加算するスロットグループ番号
        float _BitCountSlotGroup;
        // 1グループでbit数を加算するスロット数
        float _BitCountSlotsPerGroup;

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

        float DecodeByte(float value)
        {
            return floor(saturate(value) * 255.0 + 0.5);
        }

        float DecodeUInt24(float4 packed)
        {
            return DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
        }

        float4 EncodeUInt24WithAlphaByte(float value, float alphaByte)
        {
            float clamped = min(floor(value + 0.5), 16777215.0);
            float r = fmod(clamped, 256.0);
            float g = fmod(floor(clamped / 256.0), 256.0);
            float b = floor(clamped / 65536.0);
            return float4(r / 255.0, g / 255.0, b / 255.0, clamp(floor(alphaByte + 0.5), 0.0, 255.0) / 255.0);
        }

        float UnpackSigned16(float4 packed)
        {
            return DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 - 32768.0;
        }

        float CoeffSizeCategory(float coeff, float maxCategory)
        {
            float magnitude = floor(abs(coeff) + 0.5);
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
            return maxCategory;
        }

        float ReadDcDelta(float2 block)
        {
            float2 uv = (block + 0.5) / max(_BlockCount.xy, 1.0.xx);
            return UnpackSigned16(tex2Dlod(_DcDeltaTex, float4(uv, 0.0, 0.0)));
        }

        float4 ReadRleSymbol(float2 block, int slotIndex)
        {
            float slotRow = floor((float)slotIndex * 0.125);
            float2 slot = float2(fmod((float)slotIndex, 8.0), slotRow);
            float2 uv = (block * 8.0 + slot + 0.5) / max(_SymbolSize.xy, 1.0.xx);
            return tex2Dlod(_MainTex, float4(uv, 0.0, 0.0));
        }

        float ReadDcCodeLength(float category)
        {
            float2 uv = (float2(category, 0.0) + 0.5) / max(_DcTableSize.xy, 1.0.xx);
            float4 packed = tex2Dlod(_DcHuffmanCodesTex, float4(uv, 0.0, 0.0));
            return DecodeByte(_CodeLengthsStoredInRed > 0.5 ? packed.r : packed.a);
        }

        float ReadAcCodeLength(float bin)
        {
            float2 uv = (float2(bin, 0.0) + 0.5) / max(_AcTableSize.xy, 1.0.xx);
            float4 packed = tex2Dlod(_AcHuffmanCodesTex, float4(uv, 0.0, 0.0));
            return DecodeByte(_CodeLengthsStoredInRed > 0.5 ? packed.r : packed.a);
        }

        float GetAcSymbolBitCount(float4 symbol)
        {
            float flags = DecodeByte(symbol.a);
            bool valid = fmod(flags, 2.0) >= 1.0;
            bool dc = fmod(floor(flags / 2.0), 2.0) >= 1.0;
            bool eob = fmod(floor(flags / 4.0), 2.0) >= 1.0;
            if (!valid || dc) return 0.0;
            if (eob) return ReadAcCodeLength(0.0);

            float run = DecodeByte(symbol.b);
            float category = CoeffSizeCategory(UnpackSigned16(symbol), 10.0);
            if (category < 0.5) return 0.0;
            float zrlCount = floor(run / 16.0);
            float residualRun = run - zrlCount * 16.0;
            float bin = residualRun * 16.0 + category;
            return zrlCount * ReadAcCodeLength(240.0) + ReadAcCodeLength(bin) + category;
        }
        ENDCG

        Pass
        {
            Name "InitializeHuffmanBlockBits"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment InitializeFrag

            float4 InitializeFrag(Varyings i) : SV_Target
            {
                float2 block = min(floor(i.pos.xy), max(_BlockCount.xy - 1.0, 0.0));
                float dcCategory = CoeffSizeCategory(ReadDcDelta(block), 11.0);
                float dcBits = ReadDcCodeLength(dcCategory) + dcCategory;
                return EncodeUInt24WithAlphaByte(dcBits, dcBits);
            }
            ENDCG
        }

        Pass
        {
            Name "AccumulateHuffmanBlockBits"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment AccumulateFrag

            float4 AccumulateFrag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 block = min(floor(i.pos.xy), blockCount - 1.0);
                float4 previous = tex2Dlod(_PreviousBitCountTex, float4((block + 0.5) / blockCount, 0.0, 0.0));
                float bitCount = DecodeUInt24(previous);
                int slotsPerGroup = clamp((int)floor(_BitCountSlotsPerGroup + 0.5), 1, 8);
                int slotStart = 1 + max((int)floor(_BitCountSlotGroup + 0.5), 0) * slotsPerGroup;
                int slotEnd = min(slotStart + slotsPerGroup, 64);
                [loop]
                for (int localSlot = 0; localSlot < 8; localSlot++)
                {
                    int slotIndex = slotStart + localSlot;
                    if (slotIndex >= slotEnd) break;
                    bitCount += GetAcSymbolBitCount(ReadRleSymbol(block, slotIndex));
                }

                return EncodeUInt24WithAlphaByte(bitCount, DecodeByte(previous.a));
            }
            ENDCG
        }
    }
}
