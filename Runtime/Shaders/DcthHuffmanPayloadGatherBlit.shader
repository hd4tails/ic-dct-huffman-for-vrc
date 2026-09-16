// 固定ブロックページから有効バイトだけを集め、連続したHuffman payload Textureを作るシェーダー
// 構成: 1つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
Shader "HDAssets/IC/DCTH/DCTHHuffmanPayloadGatherBlit"
{
    // payload gather stage: block pageから有効byteだけをcompactなred-channel payload Textureへ集める
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Huffman Block Page", 2D) = "black" {}
        // 前段までに収集したpayloadを保持するTexture
        _PreviousPayloadTex ("Previous Payload Texture", 2D) = "black" {}
        // ブロックビット数を保持する入力Texture
        _BlockBitCountTex ("Block Bit Count", 2D) = "black" {}
        // チャンク有効バイト数を保持する入力Texture
        _ChunkValidBytesTex ("Chunk Valid Bytes", 2D) = "black" {}
        // チャンク有効バイト数mipを保持する入力Texture
        _ChunkValidBytesMipTex ("Chunk Valid Bytes Mip", 2D) = "black" {}
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // xyに横・縦方向のチャンク数を保持する入力値
        _ChunkCount ("Chunk Count", Vector) = (16, 16, 0, 0)
        // チャンクサイズ
        _ChunkSize ("Chunk Size", Float) = 4
        // xyにpage Textureの幅・高さ、zに1 pageのバイト数を保持する入力値
        _PageSize ("Page Size", Vector) = (4096, 64, 64, 0)
        // xyにstream Textureの幅・高さを保持する入力値
        _StreamSize ("Stream Size", Vector) = (512, 512, 0, 0)
        // mip個数
        _MipCount ("Mip Count", Float) = 5
        // 合計有効バイト数
        _TotalValidBytes ("Total Valid Bytes", Float) = 0
        // xyにchunk sum mip atlasの幅・高さを保持する入力値
        _ChunkMipAtlasSize ("Chunk Mip Atlas Size", Vector) = (31, 16, 0, 0)
        // 今回収集するpayload行グループ番号
        _EncodePayloadRowGroup ("Encode Payload Row Group", Float) = -1
        // 1グループで収集するpayload行数
        _EncodePayloadRowsPerGroup ("Encode Payload Rows Per Group", Float) = 8
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "HuffmanPayloadGather"

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "DcthHalfRound.cginc"

            // 現在のBlitで基準入力として読むTexture
            sampler2D _MainTex;
            // 前段までに収集したpayloadを保持するTexture
            sampler2D _PreviousPayloadTex;
            // ブロックビット数を保持する入力Texture
            sampler2D _BlockBitCountTex;
            // チャンク有効バイト数を保持する入力Texture
            sampler2D _ChunkValidBytesTex;
            // チャンク有効バイト数mipを保持する入力Texture
            sampler2D _ChunkValidBytesMipTex;
            // xyに横・縦方向のブロック数を保持する入力値
            float4 _BlockCount;
            // xyに横・縦方向のチャンク数を保持する入力値
            float4 _ChunkCount;
            // チャンクサイズ
            float _ChunkSize;
            // xyにpage Textureの幅・高さ、zに1 pageのバイト数を保持する入力値
            float4 _PageSize;
            // xyにstream Textureの幅・高さを保持する入力値
            float4 _StreamSize;
            // mip個数
            float _MipCount;
            // 合計有効バイト数
            float _TotalValidBytes;
            // xyにchunk sum mip atlasの幅・高さを保持する入力値
            float4 _ChunkMipAtlasSize;
            // 今回収集するpayload行グループ番号
            float _EncodePayloadRowGroup;
            // 1グループで収集するpayload行数
            float _EncodePayloadRowsPerGroup;

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
                return ICUNormToByte(value);
            }

            float DecodeUInt24(float4 packed)
            {
                return DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
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

            float GetMipLevelOffsetX(int mipLevel)
            {
                float offset = 0.0;
                [loop]
                for (int level = 0; level < 16; level++)
                {
                    if (level >= mipLevel)
                    {
                        break;
                    }

                    offset += max(ceil(_ChunkCount.x / Pow2Small(level)), 1.0);
                }

                return offset;
            }

            float ReadMipRegionByteSum(float2 mipPixel, int mipLevel)
            {
                // 長方形やNPOTのchunk配置では、範囲外の子を0byteとして探索から除外する
                float2 levelSize = max(ceil(_ChunkCount.xy / Pow2Small(mipLevel)), 1.0.xx);
                if (any(mipPixel < 0.0) || any(mipPixel >= levelSize))
                {
                    return 0.0;
                }

                float byteSum = 0.0;

                // level 0とatlas側の読み取り結果を、初期化済みの同じ戻り値へ代入する
                // mipLevelごとの参照先は従来どおりで、未初期化警告だけを防ぐ
                if (mipLevel <= 0)
                {
                    float2 chunkSize = max(_ChunkCount.xy, 1.0.xx);
                    float2 chunkUv = (mipPixel + 0.5) / chunkSize;
                    byteSum = DecodeUInt24(tex2Dlod(_ChunkValidBytesTex, float4(chunkUv, 0.0, 0.0)));
                }
                else
                {
                    float2 atlasSize = max(_ChunkMipAtlasSize.xy, 1.0.xx);
                    float2 atlasPixel = float2(GetMipLevelOffsetX(mipLevel), 0.0) + mipPixel;
                    float2 uv = (atlasPixel + 0.5) / atlasSize;
                    byteSum = DecodeUInt24(tex2Dlod(_ChunkValidBytesMipTex, float4(uv, 0.0, 0.0)));
                }

                return byteSum;
            }

            float ReadBlockBits(float2 block)
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 uv = (min(block, blockCount - 1.0) + 0.5) / blockCount;
                // block/page TextureはPoint filter・mipなしのdataなので、payload探索loop内ではLOD 0を明示する
                return DecodeUInt24(tex2Dlod(_BlockBitCountTex, float4(uv, 0.0, 0.0)));
            }

            float ReadPageByte(float2 block, float byteInBlock)
            {
                float pageBytes = max(_PageSize.z, 1.0);
                if (byteInBlock < 0.0 || byteInBlock >= pageBytes)
                {
                    return 0.0;
                }

                float2 pageSize = max(_PageSize.xy, 1.0.xx);
                float2 pagePixel = float2(block.x * pageBytes + byteInBlock, block.y);
                float2 uv = (pagePixel + 0.5) / pageSize;
                return ICUNormToByte(tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)).r);
            }

            float ReadPageBit(float2 block, float bitInBlock)
            {
                float roundedBit = floor(bitInBlock + 0.5);
                float byteInBlock = floor(roundedBit / 8.0);
                float bitInByte = roundedBit - byteInBlock * 8.0;
                float byteValue = ReadPageByte(block, byteInBlock);
                float shifted = floor(byteValue / Pow2Small((int)(7.0 - bitInByte)));
                return shifted - floor(shifted / 2.0) * 2.0;
            }

            void SelectChild(float2 childBase, int mipLevel, inout float localIndex, inout float2 node)
            {
                float2 child0 = childBase + float2(0.0, 0.0);
                float2 child1 = childBase + float2(1.0, 0.0);
                float2 child2 = childBase + float2(0.0, 1.0);
                float2 child3 = childBase + float2(1.0, 1.0);

                float sum0 = ReadMipRegionByteSum(child0, mipLevel);
                if (localIndex < sum0)
                {
                    node = child0;
                    return;
                }
                localIndex -= sum0;

                float sum1 = ReadMipRegionByteSum(child1, mipLevel);
                if (localIndex < sum1)
                {
                    node = child1;
                    return;
                }
                localIndex -= sum1;

                float sum2 = ReadMipRegionByteSum(child2, mipLevel);
                if (localIndex < sum2)
                {
                    node = child2;
                    return;
                }
                localIndex -= sum2;

                node = child3;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 streamSize = max(_StreamSize.xy, 1.0.xx);
                float2 pixel = floor(i.pos.xy);
                float byteIndex = pixel.y * streamSize.x + pixel.x;
                if (byteIndex < 0.0 || byteIndex >= _TotalValidBytes)
                {
                    return 0.0.xxxx;
                }

                float rowGroup = floor(_EncodePayloadRowGroup + 0.5);
                if (rowGroup >= 0.0)
                {
                    // Android/Questでは重いbyte gatherをrow groupで分割する
                    // 対象外rowは直前ping-pong payloadをcopyし、最終byte[]順と容量をsingle-pass時と一致させる
                    float rowsPerGroup = max(floor(_EncodePayloadRowsPerGroup + 0.5), 1.0);
                    float pixelRowGroup = floor(pixel.y / rowsPerGroup);
                    if (abs(pixelRowGroup - rowGroup) > 0.5)
                    {
                        return tex2D(_PreviousPayloadTex, (pixel + 0.5) / streamSize);
                    }
                }

                float localByteIndex = byteIndex;
                float2 chunk = 0.0.xx;

                // chunk mip treeを降り、このcompact payload byteを持つchunkを探す
                // payload byte順はbyte[]形式の一部なので、探索順は決定的でなければならない
                int startMipLevel = max((int)floor(_MipCount + 0.5) - 2, 0);
                [loop]
                for (int mipLevel = startMipLevel; mipLevel >= 0; mipLevel--)
                {
                    SelectChild(chunk * 2.0, mipLevel, localByteIndex, chunk);
                }

                float chunkSize = max(_ChunkSize, 1.0);
                float2 chunkBaseBlock = chunk * chunkSize;
                float chunkByteCount = ReadMipRegionByteSum(chunk, 0);
                float chunkTargetBit = localByteIndex * 8.0;
                float chunkValidBits = chunkByteCount * 8.0;
                float byteValue = 0.0;

                // 本体に16-block探索があるためMobileではrolledを維持する
                // 8出力bitをunrollするとhelper inline後にAndroid fragment shader code-size上限を超え得る
                [loop]
                for (int bitInOutputByte = 0; bitInOutputByte < 8; bitInOutputByte++)
                {
                    float localBitIndex = chunkTargetBit + (float)bitInOutputByte;
                    if (localBitIndex >= chunkValidBits)
                    {
                        continue;
                    }

                    float remainingBitIndex = localBitIndex;
                    float bitValue = 0.0;

                    [loop]
                    for (int localBlockIndex = 0; localBlockIndex < 16; localBlockIndex++)
                    {
                        // localBlockIndexは0～15。0.25の乗算はmediump相当でも正確で、整数/4と同じ座標になる
                        float localY = floor((float)localBlockIndex * 0.25);
                        float localX = (float)localBlockIndex - localY * 4.0;
                        float2 block = chunkBaseBlock + float2(localX, localY);
                        float blockBits = ReadBlockBits(block);
                        if (remainingBitIndex < blockBits)
                        {
                            bitValue = ReadPageBit(block, remainingBitIndex);
                            break;
                        }

                        remainingBitIndex -= blockBits;
                    }

                    byteValue += bitValue * Pow2Small(7 - bitInOutputByte);
                }

                // payload byteはR8 Texture経由でcopyする。復元byteを明示的に丸めてからRTへ渡す
                return float4(ICByteToUNorm(byteValue), 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
