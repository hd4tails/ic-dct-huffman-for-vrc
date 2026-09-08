// DCTH展開前に、チャンクの有効バイト数とオフセットから各ブロックのビット位置を再構築するシェーダー
// 構成: 1つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
Shader "HDAssets/IC/DCTH/DCTHHuffmanChunkBlockValidBlit"
{
    // decode準備stage: chunk有効byte数とchunk offsetからblock別bit offsetを再構築する
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Huffman Payload", 2D) = "black" {}
        // 前段までのHuffman bit開始位置を保持するTexture
        _PreviousBitOffsetTex ("Previous Bit Offset", 2D) = "black" {}
        // チャンク有効バイト数を保持する入力Texture
        _ChunkValidBytesTex ("Chunk Valid Bytes", 2D) = "black" {}
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
        // 今回処理するチャンク行の開始位置
        _ChunkRowStart ("Chunk Row Start", Float) = 0
        // 今回処理するチャンク行数
        _ChunkRowCount ("Chunk Row Count", Float) = 65535
        // 今回処理するチャンク列の開始位置
        _ChunkColumnStart ("Chunk Column Start", Float) = 0
        // 今回処理するチャンク列数
        _ChunkColumnCount ("Chunk Column Count", Float) = 65535
        // 今回処理する局所ブロックの開始index
        _DecodeLocalIndex ("Decode Local Index", Float) = -1
        // 今回処理する局所ブロック数
        _DecodeLocalCount ("Decode Local Count", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "HuffmanChunkBlockValid"

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            // 現在のBlitで基準入力として読むTexture
            sampler2D _MainTex;
            // 前段までのHuffman bit開始位置を保持するTexture
            sampler2D _PreviousBitOffsetTex;
            // チャンク有効バイト数を保持する入力Texture
            sampler2D _ChunkValidBytesTex;
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
            // 今回処理するチャンク行の開始位置
            float _ChunkRowStart;
            // 今回処理するチャンク行数
            float _ChunkRowCount;
            // 今回処理するチャンク列の開始位置
            float _ChunkColumnStart;
            // 今回処理するチャンク列数
            float _ChunkColumnCount;
            // 今回処理する局所ブロックの開始index
            float _DecodeLocalIndex;
            // 今回処理する局所ブロック数
            float _DecodeLocalCount;

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
                return DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
            }

            float4 EncodeUInt24(float value)
            {
                float clamped = min(floor(value + 0.5), 16777215.0);
                float r = fmod(clamped, 256.0);
                float g = fmod(floor(clamped / 256.0), 256.0);
                float b = floor(clamped / 65536.0);
                return float4(r / 255.0, g / 255.0, b / 255.0, 1.0);
            }

            float ReadChunkValidBytes(float2 chunk)
            {
                float2 chunkCount = max(_ChunkCount.xy, 1.0.xx);
                float2 uv = (min(chunk, chunkCount - 1.0) + 0.5) / chunkCount;
                return DecodeUInt24(tex2D(_ChunkValidBytesTex, uv));
            }

            float2 ReadChunkStartPixel(float2 chunk)
            {
                float2 chunkCount = max(_ChunkCount.xy, 1.0.xx);
                float2 uv = (min(chunk, chunkCount - 1.0) + 0.5) / chunkCount;
                float4 packed = tex2D(_ChunkOffsetTex, uv);
                float x = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0;
                float y = DecodeByte(packed.b) + DecodeByte(packed.a) * 256.0;
                return float2(x, y);
            }

            float2 ReadDcCode(int category)
            {
                float2 uv = (float2(category, 0.0) + 0.5) / max(_DcTableSize.xy, 1.0.xx);
                // stream/tableはPoint filter・mipなしのdata Textureなので、探索loop内ではLOD 0を明示する
                float4 packed = tex2Dlod(_DcHuffmanCodesTex, float4(uv, 0.0, 0.0));
                float code = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
                float length = DecodeByte(packed.a);
                return float2(code, length);
            }

            float2 ReadAcCode(int bin)
            {
                float2 uv = (float2(bin, 0.0) + 0.5) / max(_AcTableSize.xy, 1.0.xx);
                float4 packed = tex2Dlod(_AcHuffmanCodesTex, float4(uv, 0.0, 0.0));
                float code = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
                float length = DecodeByte(packed.a);
                return float2(code, length);
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

                float2 uv = (float2(x, y) + 0.5) / streamSize;
                return DecodeByte(tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)).r);
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

            float DecodeBlockBitLength(float2 chunkStartPixel, float bitCursor)
            {
                float startBitCursor = bitCursor;

                // DCはHuffman categoryの後ろへamplitude bitを続けてencodeする
                int dcCategory = DecodeDcSymbol(chunkStartPixel, bitCursor);
                bitCursor += dcCategory;

                int zigzagCursor = 1;
                [loop]
                for (int decodeEvent = 0; decodeEvent < 96; decodeEvent++)
                {
                    if (zigzagCursor >= 64)
                    {
                        break;
                    }

                    int acSymbol = DecodeAcSymbol(chunkStartPixel, bitCursor);
                    if (acSymbol == 0)
                    {
                        break;
                    }

                    if (acSymbol == 240)
                    {
                        zigzagCursor += 16;
                        continue;
                    }

                    // acSymbolは0～255。0.0625は2進数で正確に表現でき、mediump相当でも/16の商が変わらない
                    int run = (int)floor((float)acSymbol * 0.0625);
                    int category = acSymbol - run * 16;
                    bitCursor += category;
                    zigzagCursor += run + 1;
                }

                return max(bitCursor - startBitCursor, 0.0);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 block = floor(i.pos.xy);
                if (block.x < 0.0 || block.y < 0.0 || block.x >= blockCount.x || block.y >= blockCount.y)
                {
                    return 0.0.xxxx;
                }

                float chunkSize = max(_ChunkSize, 1.0);
                float2 chunk = floor(block / chunkSize);
                if (chunk.x < _ChunkColumnStart
                    || chunk.x >= _ChunkColumnStart + max(_ChunkColumnCount, 1.0)
                    || chunk.y < _ChunkRowStart
                    || chunk.y >= _ChunkRowStart + max(_ChunkRowCount, 1.0))
                {
                    float2 uv = (block + 0.5) / blockCount;
                    return tex2D(_PreviousBitOffsetTex, uv);
                }

                float2 localBlock = block - chunk * chunkSize;
                int targetLocalIndex = (int)(localBlock.y * chunkSize + localBlock.x);
                float2 chunkStartPixel = ReadChunkStartPixel(chunk);
                float bitCursor = 0.0;
                float chunkEndBit = ReadChunkValidBytes(chunk) * 8.0;

                int decodeLocalIndex = (int)floor(_DecodeLocalIndex + 0.5);
                if (decodeLocalIndex >= 0)
                {
                    int decodeLocalCount = max((int)floor(_DecodeLocalCount + 0.5), 1);
                    int decodeLocalEnd = min(decodeLocalIndex + decodeLocalCount, 16);
                    if (targetLocalIndex < decodeLocalIndex || targetLocalIndex >= decodeLocalEnd)
                    {
                        float2 uv = (block + 0.5) / blockCount;
                        return tex2D(_PreviousBitOffsetTex, uv);
                    }

                    if (decodeLocalIndex == 0)
                    {
                        if (targetLocalIndex == 0)
                        {
                            return EncodeUInt24(bitCursor);
                        }
                    }
                    else
                    {
                        int previousLocalIndex = decodeLocalIndex - 1;
                        float previousLocalX = fmod(previousLocalIndex, chunkSize);
                        float previousLocalY = floor(previousLocalIndex / chunkSize);
                        float2 previousBlock = chunk * chunkSize + float2(previousLocalX, previousLocalY);
                        float2 previousUv = (previousBlock + 0.5) / blockCount;
                        bitCursor = DecodeUInt24(tex2D(_PreviousBitOffsetTex, previousUv));
                        if (bitCursor < chunkEndBit)
                        {
                            bitCursor += DecodeBlockBitLength(chunkStartPixel, bitCursor);
                        }
                    }

                    [loop]
                    for (int localIndex = 0; localIndex < 16; localIndex++)
                    {
                        int activeLocalIndex = decodeLocalIndex + localIndex;
                        if (activeLocalIndex >= targetLocalIndex || activeLocalIndex >= decodeLocalEnd)
                        {
                            break;
                        }

                        if (bitCursor < chunkEndBit)
                        {
                            bitCursor += DecodeBlockBitLength(chunkStartPixel, bitCursor);
                        }
                    }

                    return EncodeUInt24(bitCursor);
                }

                // 製品decodeは_DecodeLocalIndex/_DecodeLocalCountを渡し、1 passで小さいlocal-block groupを処理する
                // 大きなHuffman helper本体がAndroid compileで肥大化しないよう、fallbackもrolledを維持する
                [loop]
                for (int localIndex = 0; localIndex < 16; localIndex++)
                {
                    if (localIndex < targetLocalIndex && bitCursor < chunkEndBit)
                    {
                        bitCursor += DecodeBlockBitLength(chunkStartPixel, bitCursor);
                    }
                }

                return EncodeUInt24(bitCursor);
            }
            ENDCG
        }
    }
}
