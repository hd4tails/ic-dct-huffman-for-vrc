// Huffmanテーブル、チャンク長、overflow値などの付随データをreadback用Textureへ格納するシェーダー
// 構成: 1つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
Shader "HDAssets/IC/DCTH/DCTHHuffmanMetadataPackBlit"
{
    // metadata readback stage: table byteやchunk長などの付随dataをRGBA readback Textureへpackする
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Source", 2D) = "black" {}
        // 容量を超過したブロックを示すmask Texture
        _OverflowMaskTex ("Overflow Mask", 2D) = "black" {}
        // チャンク有効バイト数を保持する入力Texture
        _ChunkValidTex ("Chunk Valid Bytes", 2D) = "black" {}
        // DC Huffman符号を保持する入力Texture
        _DcCodesTex ("DC Huffman Codes", 2D) = "black" {}
        // AC Huffman符号を保持する入力Texture
        _AcCodesTex ("AC Huffman Codes", 2D) = "black" {}
        // xyにmetadata Textureの幅・高さを保持する入力値
        _MetadataSize ("Metadata Size", Vector) = (256, 3, 0, 0)
        // xyに横・縦方向のチャンク数を保持する入力値
        _ChunkCount ("Chunk Count", Vector) = (16, 16, 0, 0)
        // xyにDC Huffman tableの幅・高さを保持する入力値
        _DcTableSize ("DC Table Size", Vector) = (16, 1, 0, 0)
        // xyにAC Huffman tableの幅・高さを保持する入力値
        _AcTableSize ("AC Table Size", Vector) = (256, 1, 0, 0)
        // xyにoverflow縮約atlasの幅・高さを保持する入力値
        _OverflowAtlasSize ("Overflow Atlas Size", Vector) = (127, 64, 0, 0)
        // overflow縮約atlas内のroot位置
        _OverflowRootOffset ("Overflow Root Offset", Vector) = (126, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "HuffmanMetadataPack"

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            // 容量を超過したブロックを示すmask Texture
            sampler2D _OverflowMaskTex;
            // チャンク有効バイト数を保持する入力Texture
            sampler2D _ChunkValidTex;
            // DC Huffman符号を保持する入力Texture
            sampler2D _DcCodesTex;
            // AC Huffman符号を保持する入力Texture
            sampler2D _AcCodesTex;
            // xyにmetadata Textureの幅・高さを保持する入力値
            float4 _MetadataSize;
            // xyに横・縦方向のチャンク数を保持する入力値
            float4 _ChunkCount;
            // xyにDC Huffman tableの幅・高さを保持する入力値
            float4 _DcTableSize;
            // xyにAC Huffman tableの幅・高さを保持する入力値
            float4 _AcTableSize;
            // xyにoverflow縮約atlasの幅・高さを保持する入力値
            float4 _OverflowAtlasSize;
            // overflow縮約atlas内のroot位置
            float4 _OverflowRootOffset;

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

            float4 ReadPixel(sampler2D tex, float2 size, float2 pixel)
            {
                float2 uv = (pixel + 0.5) / max(size, 1.0.xx);
                return tex2D(tex, uv);
            }

            float DecodeByte(float value)
            {
                return floor(saturate(value) * 255.0 + 0.5);
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

            float4 frag(Varyings i) : SV_Target
            {
                float2 pixel = floor(i.pos.xy);
                float metadataWidth = max(_MetadataSize.x, 1.0);

                if (pixel.y < 0.5)
                {
                    if (pixel.x < 0.5)
                    {
                        // overflow atlasのroot pixelには全block中の最大使用bytesが入る
                        // 容量を超過したブロックを示すmask Texture
                        float maximumBlockBytes = DecodeUInt24(ReadPixel(_OverflowMaskTex, _OverflowAtlasSize.xy, _OverflowRootOffset.xy));
                        return EncodeUInt24(maximumBlockBytes);
                    }

                    float dcIndex = pixel.x - 1.0;
                    if (dcIndex >= 0.0 && dcIndex < _DcTableSize.x)
                    {
                        return ReadPixel(_DcCodesTex, _DcTableSize.xy, float2(dcIndex, 0.0));
                    }

                    return float4(0.0, 0.0, 0.0, 0.0);
                }

                if (pixel.y < 1.5)
                {
                    if (pixel.x < _AcTableSize.x)
                    {
                        return ReadPixel(_AcCodesTex, _AcTableSize.xy, float2(pixel.x, 0.0));
                    }

                    return float4(0.0, 0.0, 0.0, 0.0);
                }

                float chunkIndex = pixel.x + (pixel.y - 2.0) * metadataWidth;
                // xyに横・縦方向のチャンク数を保持する入力値
                float chunkPixels = max(_ChunkCount.x * _ChunkCount.y, 1.0);
                if (chunkIndex < 0.0 || chunkIndex >= chunkPixels)
                {
                    return float4(0.0, 0.0, 0.0, 0.0);
                }

                float chunkWidth = max(_ChunkCount.x, 1.0);
                float chunkX = fmod(chunkIndex, chunkWidth);
                float chunkY = floor(chunkIndex / chunkWidth);
                return ReadPixel(_ChunkValidTex, _ChunkCount.xy, float2(chunkX, chunkY));
            }
            ENDCG
        }
    }
}
