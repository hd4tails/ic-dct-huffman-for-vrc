// 連続配置したHuffman payloadを段階的に復号し、DC値と固定スロット用RLEシンボルを復元するシェーダー
// 構成: 5つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHHuffmanDecodeRleBlit"
{
    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Huffman Bitstream Texture", 2D) = "black" {}
        // 前段RLEを保持する入力Texture
        _PreviousRleTex ("Previous RLE Texture", 2D) = "black" {}
        // 復号状態を保持する入力Texture
        _DecodeStateTex ("Decode State Texture", 2D) = "black" {}
        // ブロックごとのHuffman bit開始位置を保持するTexture
        _HuffmanBitOffsetTex ("Huffman Bit Offset Texture", 2D) = "black" {}
        // チャンクオフセットを保持する入力Texture
        _ChunkOffsetTex ("Chunk Offset", 2D) = "black" {}
        // DC Huffman符号を保持する入力Texture
        _DcHuffmanCodesTex ("DC Huffman Codes", 2D) = "black" {}
        // AC Huffman符号を保持する入力Texture
        _AcHuffmanCodesTex ("AC Huffman Codes", 2D) = "black" {}
        // AC復号用符号長集計を保持する入力Texture
        _AcDecodeLengthSummaryTex ("AC Decode Length Summary", 2D) = "black" {}
        // AC復号用シンボルtableを保持する入力Texture
        _AcDecodeSymbolTex ("AC Decode Symbol Table", 2D) = "black" {}
        // 前段AC復号用シンボルtableを保持する入力Texture
        _PreviousAcDecodeSymbolTex ("Previous AC Decode Symbol Table", 2D) = "black" {}
        // xyにシンボルTextureの幅・高さを保持する入力値
        _SymbolSize ("Symbol Size", Vector) = (512, 512, 0, 0)
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // xyに横・縦方向のチャンク数を保持する入力値
        _ChunkCount ("Chunk Count", Vector) = (16, 16, 0, 0)
        // チャンクサイズ
        _ChunkSize ("Chunk Size", Float) = 4
        // xyにstream Textureの幅・高さを保持する入力値
        _StreamSize ("Stream Size", Vector) = (512, 512, 0, 0)
        // xyにDC Huffman tableの幅・高さを保持する入力値
        _DcTableSize ("DC Table Size", Vector) = (16, 1, 0, 0)
        // xyにAC Huffman tableの幅・高さを保持する入力値
        _AcTableSize ("AC Table Size", Vector) = (256, 1, 0, 0)
        // 今回復号する係数スロットのグループ番号
        _DecodeSlotGroup ("Decode Slot Group", Float) = -1
        // 復号1グループあたりのスロット数
        _DecodeSlotsPerGroup ("Decode Slots Per Group", Float) = 8
        // 復号チャンク行グループ番号
        _DecodeChunkRowGroup ("Decode Chunk Row Group", Float) = -1
        // 復号チャンク1グループあたりの行数
        _DecodeChunkRowsPerGroup ("Decode Chunk Rows Per Group", Float) = 4
        // ブロックページへ状態値を書き込むスロット番号
        _StateOutputSlot ("State Output Slot", Float) = 1
        // ブロックページへ状態値を書き終えるスロット位置
        _StateOutputEnd ("State Output End", Float) = 8
        // 今回構築する復号用Huffman符号長の開始値
        _DecodeLengthStart ("Decode Length Start", Float) = 1
        // 今回構築する復号用Huffman符号長の個数
        _DecodeLengthCount ("Decode Length Count", Float) = 4
        // 復号シンボル開始位置
        _DecodeSymbolStart ("Decode Symbol Start", Float) = 0
        // 復号シンボル個数
        _DecodeSymbolCount ("Decode Symbol Count", Float) = 16
        // 前段AC復号シンボルが利用可能かを示すフラグ
        _HasPreviousAcDecodeSymbol ("Has Previous AC Decode Symbol", Float) = 0
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
        // 前段RLEを保持する入力Texture
        sampler2D _PreviousRleTex;
        // 復号状態を保持する入力Texture
        sampler2D _DecodeStateTex;
        // ブロックごとのHuffman bit開始位置を保持するTexture
        sampler2D _HuffmanBitOffsetTex;
        // チャンクオフセットを保持する入力Texture
        sampler2D _ChunkOffsetTex;
        // DC Huffman符号を保持する入力Texture
        sampler2D _DcHuffmanCodesTex;
        // AC Huffman符号を保持する入力Texture
        sampler2D _AcHuffmanCodesTex;
        // AC復号用符号長集計を保持する入力Texture
        sampler2D _AcDecodeLengthSummaryTex;
        // AC復号用シンボルtableを保持する入力Texture
        sampler2D _AcDecodeSymbolTex;
        // 前段AC復号用シンボルtableを保持する入力Texture
        sampler2D _PreviousAcDecodeSymbolTex;
        // xyにシンボルTextureの幅・高さを保持する入力値
        float4 _SymbolSize;
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;
        // xyに横・縦方向のチャンク数を保持する入力値
        float4 _ChunkCount;
        // チャンクサイズ
        float _ChunkSize;
        // xyにstream Textureの幅・高さを保持する入力値
        float4 _StreamSize;
        // xyにDC Huffman tableの幅・高さを保持する入力値
        float4 _DcTableSize;
        // xyにAC Huffman tableの幅・高さを保持する入力値
        float4 _AcTableSize;
        // 今回復号する係数スロットのグループ番号
        float _DecodeSlotGroup;
        // 復号1グループあたりのスロット数
        float _DecodeSlotsPerGroup;
        // 復号チャンク行グループ番号
        float _DecodeChunkRowGroup;
        // 復号チャンク1グループあたりの行数
        float _DecodeChunkRowsPerGroup;
        // ブロックページへ状態値を書き込むスロット番号
        float _StateOutputSlot;
        // ブロックページへ状態値を書き終えるスロット位置
        float _StateOutputEnd;
        // 今回構築する復号用Huffman符号長の開始値
        float _DecodeLengthStart;
        // 今回構築する復号用Huffman符号長の個数
        float _DecodeLengthCount;
        // 復号シンボル開始位置
        float _DecodeSymbolStart;
        // 復号シンボル個数
        float _DecodeSymbolCount;
        // 前段AC復号シンボルが利用可能かを示すフラグ
        float _HasPreviousAcDecodeSymbol;

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

        float RoundToIntStable(float value)
        {
            return value < 0.0 ? ceil(value - 0.5) : floor(value + 0.5);
        }

        float Pow2Small(int exponentValue)
        {
            if (exponentValue <= 0) return 1.0;
            if (exponentValue == 1) return 2.0;
            if (exponentValue == 2) return 4.0;
            if (exponentValue == 3) return 8.0;
            if (exponentValue == 4) return 16.0;
            if (exponentValue == 5) return 32.0;
            if (exponentValue == 6) return 64.0;
            if (exponentValue == 7) return 128.0;
            if (exponentValue == 8) return 256.0;
            if (exponentValue == 9) return 512.0;
            if (exponentValue == 10) return 1024.0;
            if (exponentValue == 11) return 2048.0;
            if (exponentValue == 12) return 4096.0;
            if (exponentValue == 13) return 8192.0;
            if (exponentValue == 14) return 16384.0;
            return 32768.0;
        }

        float DecodeUInt24(float4 packed)
        {
            float r = DecodeByte(packed.r);
            float g = DecodeByte(packed.g);
            float b = DecodeByte(packed.b);
            return r + g * 256.0 + b * 65536.0;
        }

        float2 DecodeUInt16Pair(float4 packed)
        {
            float x = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0;
            float y = DecodeByte(packed.b) + DecodeByte(packed.a) * 256.0;
            return float2(x, y);
        }

        float4 PackRleSymbol(float coeff, int run, float flags)
        {
            float clamped = clamp(RoundToIntStable(coeff), -32768.0, 32767.0);
            float encoded = clamped + 32768.0;
            float lo = fmod(encoded, 256.0);
            float hi = floor(encoded / 256.0);
            return float4(lo / 255.0, hi / 255.0, clamp(run, 0, 255) / 255.0, flags / 255.0);
        }

        float4 PackDecodeState(float bitCursor, int zigzagCursor, int accumulatedRun, bool done)
        {
            float cursor = clamp(floor(bitCursor + 0.5), 0.0, 65535.0);
            float cursorLo = fmod(cursor, 256.0);
            float cursorHi = floor(cursor / 256.0);
            float zigzagAndDone = clamp(zigzagCursor, 0, 127) + (done ? 128.0 : 0.0);
            return float4(
                cursorLo / 255.0,
                cursorHi / 255.0,
                zigzagAndDone / 255.0,
                clamp(accumulatedRun, 0, 255) / 255.0);
        }

        void UnpackDecodeState(float4 packed, out float bitCursor, out int zigzagCursor, out int accumulatedRun, out bool done)
        {
            bitCursor = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0;
            float zigzagAndDone = DecodeByte(packed.b);
            done = zigzagAndDone >= 128.0;
            zigzagCursor = (int)fmod(zigzagAndDone, 128.0);
            accumulatedRun = (int)DecodeByte(packed.a);
        }

        float ReadBlockBitOffset(float2 block)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            return DecodeUInt24(tex2D(_HuffmanBitOffsetTex, (block + 0.5) / blockCount));
        }

        float4 ReadDecodeState(float2 block)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            return tex2D(_DecodeStateTex, (block + 0.5) / blockCount);
        }

        float2 ReadChunkStartPixel(float2 chunk)
        {
            float2 chunkCount = max(_ChunkCount.xy, 1.0.xx);
            float2 uv = (min(chunk, chunkCount - 1.0) + 0.5) / chunkCount;
            return DecodeUInt16Pair(tex2D(_ChunkOffsetTex, uv));
        }

        float2 ReadDcCode(int category)
        {
            float2 uv = (float2(category, 0.0) + 0.5) / max(_DcTableSize.xy, 1.0.xx);
            float4 packed = tex2Dlod(_DcHuffmanCodesTex, float4(uv, 0.0, 0.0));
            float code = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
            return float2(code, DecodeByte(packed.a));
        }

        float2 ReadAcCode(int bin)
        {
            float2 uv = (float2(bin, 0.0) + 0.5) / max(_AcTableSize.xy, 1.0.xx);
            float4 packed = tex2Dlod(_AcHuffmanCodesTex, float4(uv, 0.0, 0.0));
            float code = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
            return float2(code, DecodeByte(packed.a));
        }

        float2 ReadAcDecodeLengthSummary(int codeLength)
        {
            float2 uv = (float2(codeLength - 1, 0.0) + 0.5) / float2(16.0, 1.0);
            float4 packed = tex2Dlod(_AcDecodeLengthSummaryTex, float4(uv, 0.0, 0.0));
            return float2(
                DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0,
                DecodeByte(packed.b) + DecodeByte(packed.a) * 256.0);
        }

        int ReadAcDecodeSymbol(int codeLength, int codeRank)
        {
            float2 uv = (float2(codeRank, codeLength - 1) + 0.5) / float2(256.0, 16.0);
            return (int)DecodeByte(tex2Dlod(_AcDecodeSymbolTex, float4(uv, 0.0, 0.0)).r);
        }

        float ReadStreamByte(float2 chunkStartPixel, float byteIndex)
        {
            float2 streamSize = max(_StreamSize.xy, 1.0.xx);
            if (byteIndex < 0.0)
            {
                return 0.0;
            }

            float x = chunkStartPixel.x + byteIndex;
            float y = chunkStartPixel.y + floor(x / streamSize.x);
            x = fmod(x, streamSize.x);
            if (y < 0.0 || y >= streamSize.y)
            {
                return 0.0;
            }

            return DecodeByte(tex2Dlod(_MainTex, float4((float2(x, y) + 0.5) / streamSize, 0.0, 0.0)).r);
        }

        float ReadStreamBit(float2 chunkStartPixel, float bitIndex)
        {
            float roundedBitIndex = floor(bitIndex + 0.5);
            float byteIndex = floor(roundedBitIndex / 8.0);
            float bitInByte = roundedBitIndex - byteIndex * 8.0;
            float byteValue = ReadStreamByte(chunkStartPixel, byteIndex);
            return fmod(floor(byteValue / Pow2Small((int)(7.0 - bitInByte))), 2.0);
        }

        float ReadBits(float2 chunkStartPixel, float bitCursor, int bitCount)
        {
            float value = 0.0;
            [loop]
            for (int bit = 0; bit < 16; bit++)
            {
                if (bit >= bitCount)
                {
                    break;
                }

                value = value * 2.0 + ReadStreamBit(chunkStartPixel, bitCursor + bit);
            }

            return floor(value + 0.5);
        }

        float DecodeAmplitude(float bits, int category)
        {
            if (category <= 0)
            {
                return 0.0;
            }

            float threshold = Pow2Small(category - 1);
            float maxValue = Pow2Small(category) - 1.0;
            return bits >= threshold ? bits : bits - maxValue;
        }

        int DecodeDcSymbol(float2 chunkStartPixel, inout float bitCursor)
        {
            float lookahead = ReadBits(chunkStartPixel, bitCursor, 16);
            int bestLength = 17;
            int bestCategory = 0;
            [loop]
            for (int category = 0; category < 16; category++)
            {
                float2 entry = ReadDcCode(category);
                int length = (int)floor(entry.y + 0.5);
                if (length <= 0 || length > 16 || length >= bestLength)
                {
                    continue;
                }

                float prefix = floor(lookahead / Pow2Small(16 - length));
                if (abs(entry.x - prefix) < 0.5)
                {
                    bestLength = length;
                    bestCategory = category;
                }
            }

            if (bestLength <= 16)
            {
                bitCursor += bestLength;
                return bestCategory;
            }

            bitCursor += 16.0;
            return 0;
        }

        int DecodeAcSymbol(float2 chunkStartPixel, inout float bitCursor)
        {
            float lookahead = ReadBits(chunkStartPixel, bitCursor, 16);
            [loop]
            for (int length = 1; length <= 16; length++)
            {
                float2 summary = ReadAcDecodeLengthSummary(length);
                float firstCode = floor(summary.x + 0.5);
                int codeCount = (int)floor(summary.y + 0.5);
                if (codeCount <= 0)
                {
                    continue;
                }

                float prefix = floor(lookahead / Pow2Small(16 - length));
                float rank = prefix - firstCode;
                if (rank >= 0.0 && rank < codeCount)
                {
                    bitCursor += length;
                    return ReadAcDecodeSymbol(length, (int)floor(rank + 0.5));
                }
            }

            bitCursor += 16.0;
            return 0;
        }

        bool IsActiveChunkRow(float2 chunk)
        {
            float group = floor(_DecodeChunkRowGroup + 0.5);
            if (group < 0.0)
            {
                return true;
            }

            float rowsPerGroup = max(floor(_DecodeChunkRowsPerGroup + 0.5), 1.0);
            return abs(floor(chunk.y / rowsPerGroup) - group) <= 0.5;
        }

        void DecodeDcValue(float2 chunkStartPixel, float2 block, out float bitCursor, out float dcDelta)
        {
            bitCursor = ReadBlockBitOffset(block);
            int dcCategory = DecodeDcSymbol(chunkStartPixel, bitCursor);
            float dcBits = ReadBits(chunkStartPixel, bitCursor, dcCategory);
            bitCursor += dcCategory;
            dcDelta = DecodeAmplitude(dcBits, dcCategory);
        }
        ENDCG

        Pass
        {
            Name "InitializeDecodeState"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 block = min(floor(i.pos.xy), blockCount - 1.0);
                float chunkSize = max(_ChunkSize, 1.0);
                float2 chunk = floor(block / chunkSize);
                if (!IsActiveChunkRow(chunk))
                {
                    return ReadDecodeState(block);
                }

                float bitCursor;
                float dcDelta;
                DecodeDcValue(ReadChunkStartPixel(chunk), block, bitCursor, dcDelta);
                return PackDecodeState(bitCursor, 1, 0, false);
            }
            ENDCG
        }

        Pass
        {
            Name "DecodeRleGroup"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
                float2 pixel = min(floor(i.pos.xy), symbolSize - 1.0);
                float2 block = floor(pixel / 8.0);
                float chunkSize = max(_ChunkSize, 1.0);
                float2 chunk = floor(block / chunkSize);
                float2 slot = pixel - block * 8.0;
                int slotIndex = (int)(slot.y * 8.0 + slot.x);
                float slotsPerGroup = max(floor(_DecodeSlotsPerGroup + 0.5), 1.0);
                float slotGroup = floor(slotIndex / slotsPerGroup);
                float decodeSlotGroup = floor(_DecodeSlotGroup + 0.5);
                if ((decodeSlotGroup >= 0.0 && abs(slotGroup - decodeSlotGroup) > 0.5) || !IsActiveChunkRow(chunk))
                {
                    return tex2D(_PreviousRleTex, (pixel + 0.5) / symbolSize);
                }

                float2 chunkStartPixel = ReadChunkStartPixel(chunk);
                if (slotIndex == 0)
                {
                    float bitCursor;
                    float dcDelta;
                    DecodeDcValue(chunkStartPixel, block, bitCursor, dcDelta);
                    return PackRleSymbol(dcDelta, 0, 3.0);
                }

                float bitCursor;
                int zigzagCursor;
                int accumulatedRun;
                bool done;
                UnpackDecodeState(ReadDecodeState(block), bitCursor, zigzagCursor, accumulatedRun, done);
                if (done)
                {
                    return PackRleSymbol(0.0, 0, 0.0);
                }

                int outputSlot = max((int)floor(_StateOutputSlot + 0.5), 1);
                [loop]
                for (int decodeEvent = 0; decodeEvent < 96; decodeEvent++)
                {
                    int acSymbol = DecodeAcSymbol(chunkStartPixel, bitCursor);
                    if (acSymbol == 0)
                    {
                        return outputSlot == slotIndex
                            ? PackRleSymbol(0.0, 64 - zigzagCursor, 5.0)
                            : PackRleSymbol(0.0, 0, 0.0);
                    }

                    if (acSymbol == 240)
                    {
                        accumulatedRun += 16;
                        continue;
                    }

                    int symbolRun = (int)floor((float)acSymbol * 0.0625);
                    int run = accumulatedRun + symbolRun;
                    int category = acSymbol - symbolRun * 16;
                    float coeffBits = ReadBits(chunkStartPixel, bitCursor, category);
                    bitCursor += category;
                    float coeff = DecodeAmplitude(coeffBits, category);
                    if (outputSlot == slotIndex)
                    {
                        return PackRleSymbol(coeff, run, 1.0);
                    }

                    zigzagCursor += run + 1;
                    accumulatedRun = 0;
                    outputSlot++;
                }

                return PackRleSymbol(0.0, 0, 0.0);
            }
            ENDCG
        }

        Pass
        {
            Name "AdvanceDecodeState"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 block = min(floor(i.pos.xy), blockCount - 1.0);
                float chunkSize = max(_ChunkSize, 1.0);
                float2 chunk = floor(block / chunkSize);
                float4 packedState = ReadDecodeState(block);
                if (!IsActiveChunkRow(chunk))
                {
                    return packedState;
                }

                float bitCursor;
                int zigzagCursor;
                int accumulatedRun;
                bool done;
                UnpackDecodeState(packedState, bitCursor, zigzagCursor, accumulatedRun, done);
                if (done)
                {
                    return packedState;
                }

                int outputSlot = max((int)floor(_StateOutputSlot + 0.5), 1);
                int outputEnd = clamp((int)floor(_StateOutputEnd + 0.5), outputSlot, 64);
                float2 chunkStartPixel = ReadChunkStartPixel(chunk);
                [loop]
                for (int decodeEvent = 0; decodeEvent < 96; decodeEvent++)
                {
                    if (outputSlot >= outputEnd)
                    {
                        break;
                    }

                    int acSymbol = DecodeAcSymbol(chunkStartPixel, bitCursor);
                    if (acSymbol == 0)
                    {
                        done = true;
                        break;
                    }

                    if (acSymbol == 240)
                    {
                        accumulatedRun += 16;
                        continue;
                    }

                    int symbolRun = (int)floor((float)acSymbol * 0.0625);
                    int run = accumulatedRun + symbolRun;
                    int category = acSymbol - symbolRun * 16;
                    bitCursor += category;
                    zigzagCursor += run + 1;
                    accumulatedRun = 0;
                    outputSlot++;
                }

                if (outputSlot < outputEnd)
                {
                    done = true;
                }

                return PackDecodeState(bitCursor, zigzagCursor, accumulatedRun, done);
            }
            ENDCG
        }

        Pass
        {
            Name "BuildAcDecodeLengthSummary"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                int targetLength = clamp((int)floor(i.pos.x) + 1, 1, 16);
                float firstCode = 65536.0;
                float codeCount = 0.0;
                [loop]
                for (int symbol = 0; symbol < 256; symbol++)
                {
                    float2 entry = ReadAcCode(symbol);
                    int length = (int)floor(entry.y + 0.5);
                    if (length != targetLength)
                    {
                        continue;
                    }

                    firstCode = min(firstCode, entry.x);
                    codeCount += 1.0;
                }

                if (codeCount < 0.5)
                {
                    firstCode = 0.0;
                }

                float codeLo = fmod(firstCode, 256.0);
                float codeHi = floor(firstCode / 256.0);
                float countLo = fmod(codeCount, 256.0);
                float countHi = floor(codeCount / 256.0);
                return float4(codeLo, codeHi, countLo, countHi) / 255.0;
            }
            ENDCG
        }

        Pass
        {
            Name "BuildAcDecodeSymbolTable"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                int targetRank = clamp((int)floor(i.pos.x), 0, 255);
                int targetLength = clamp((int)floor(i.pos.y) + 1, 1, 16);
                float2 tableUv = (float2(targetRank, targetLength - 1) + 0.5) / float2(256.0, 16.0);
                float4 previous = _HasPreviousAcDecodeSymbol > 0.5
                    ? tex2Dlod(_PreviousAcDecodeSymbolTex, float4(tableUv, 0.0, 0.0))
                    : 0.0.xxxx;
                if (DecodeByte(previous.b) > 0.5)
                {
                    return previous;
                }

                int rank = (int)floor(DecodeByte(previous.g) + 0.5);
                int symbolStart = clamp((int)floor(_DecodeSymbolStart + 0.5), 0, 255);
                int symbolCount = clamp((int)floor(_DecodeSymbolCount + 0.5), 1, 256);
                [loop]
                for (int localSymbol = 0; localSymbol < 16; localSymbol++)
                {
                    if (localSymbol >= symbolCount)
                    {
                        break;
                    }

                    int symbol = symbolStart + localSymbol;
                    if (symbol >= 256)
                    {
                        break;
                    }

                    float2 entry = ReadAcCode(symbol);
                    int length = (int)floor(entry.y + 0.5);
                    if (length != targetLength)
                    {
                        continue;
                    }

                    if (rank == targetRank)
                    {
                        return float4(symbol / 255.0, rank / 255.0, 1.0, 1.0);
                    }

                    rank++;
                }

                return float4(0.0, min(rank, 255) / 255.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
