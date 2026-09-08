// ブロックごとの有効バイト数をチャンク単位へ集約し、連続配置したpayloadの位置計算に使うシェーダー
// 構成: 4つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHHuffmanChunkValidBytesBlit"
{
    // chunk compaction準備: block別有効byte数をchunk別有効byte数へ集約する
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Huffman Block Bits", 2D) = "black" {}
        // チャンク有効バイト数を保持する入力Texture
        _ChunkValidTex ("Chunk Valid Bytes", 2D) = "black" {}
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // xyに横・縦方向のチャンク数を保持する入力値
        _ChunkCount ("Chunk Count", Vector) = (16, 16, 0, 0)
        // チャンクサイズ
        _ChunkSize ("Chunk Size", Float) = 4
        // xyにchunk sum mip atlasの幅・高さを保持する入力値
        _ChunkMipAtlasSize ("Chunk Mip Atlas Size", Vector) = (31, 16, 0, 0)
        // mip atlas内の読み取り元level開始位置
        _SourceLevelOffset ("Source Level Offset", Vector) = (0, 0, 0, 0)
        // 出力先Levelオフセット
        _DestLevelOffset ("Dest Level Offset", Vector) = (16, 0, 0, 0)
        // xyに読み取り元mip levelの幅・高さを保持する入力値
        _SourceLevelSize ("Source Level Size", Vector) = (16, 16, 0, 0)
        // xyに書き込み先mip levelの幅・高さを保持する入力値
        _DestLevelSize ("Dest Level Size", Vector) = (8, 8, 0, 0)
        // mip atlasの読み取り元levelを選択するモード
        _SourceLevelMode ("Source Level Mode", Float) = 0
        // 容量予測事前処理前段統計を保持する入力Texture
        _PreviousStatsTex ("Capacity Prepass Previous Stats", 2D) = "black" {}
        // xyに容量予測統計Textureの幅・高さを保持する入力値
        _PrepassStatsSize ("Capacity Prepass Stats Size", Vector) = (4, 1, 0, 0)
        // 容量予測の対象とするプレーン番号
        _PrepassTargetPlane ("Capacity Prepass Target Plane", Float) = 0
        // 容量予測事前処理サンプルブロック個数
        _PrepassSampleBlockCount ("Capacity Prepass Sample Block Count", Float) = 128
        // 容量予測事前処理サンプルブロック開始位置
        _PrepassSampleBlockStart ("Capacity Prepass Sample Block Start", Float) = 0
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
        // チャンク有効バイト数を保持する入力Texture
        sampler2D _ChunkValidTex;
        // 容量予測事前処理前段統計を保持する入力Texture
        sampler2D _PreviousStatsTex;
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;
        // xyに横・縦方向のチャンク数を保持する入力値
        float4 _ChunkCount;
        // チャンクサイズ
        float _ChunkSize;
        // xyにchunk sum mip atlasの幅・高さを保持する入力値
        float4 _ChunkMipAtlasSize;
        // mip atlas内の読み取り元level開始位置
        float4 _SourceLevelOffset;
        // 出力先Levelオフセット
        float4 _DestLevelOffset;
        // xyに読み取り元mip levelの幅・高さを保持する入力値
        float4 _SourceLevelSize;
        // xyに書き込み先mip levelの幅・高さを保持する入力値
        float4 _DestLevelSize;
        // mip atlasの読み取り元levelを選択するモード
        float _SourceLevelMode;
        // xyに容量予測統計Textureの幅・高さを保持する入力値
        float4 _PrepassStatsSize;
        // 容量予測の対象とするプレーン番号
        float _PrepassTargetPlane;
        // 容量予測事前処理サンプルブロック個数
        float _PrepassSampleBlockCount;
        // 容量予測事前処理サンプルブロック開始位置
        float _PrepassSampleBlockStart;

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

        float4 EncodeUInt24(float value)
        {
            float clamped = min(floor(value + 0.5), 16777215.0);
            float r = fmod(clamped, 256.0);
            float g = fmod(floor(clamped / 256.0), 256.0);
            float b = floor(clamped / 65536.0);
            return float4(r / 255.0, g / 255.0, b / 255.0, 1.0);
        }

        float ReadManualMipAtlasValue(float2 pixel)
        {
            float2 atlasSize = max(_ChunkMipAtlasSize.xy, 1.0.xx);
            float2 uv = (pixel + 0.5) / atlasSize;
            return DecodeUInt24(tex2D(_MainTex, uv));
        }

        float ReadManualMipChild(float2 localPixel)
        {
            if (localPixel.x < 0.0
                || localPixel.y < 0.0
                || localPixel.x >= _SourceLevelSize.x
                || localPixel.y >= _SourceLevelSize.y)
            {
                return 0.0;
            }

            if (_SourceLevelMode > 0.5)
            {
                float2 uv = (localPixel + 0.5) / max(_SourceLevelSize.xy, 1.0.xx);
                return DecodeUInt24(tex2D(_ChunkValidTex, uv));
            }

            return ReadManualMipAtlasValue(_SourceLevelOffset.xy + localPixel);
        }

        float SumManualMipChildren(float2 dstPixel)
        {
            float2 srcBase = dstPixel * 2.0;
            float total = 0.0;
            total += ReadManualMipChild(srcBase + float2(0.0, 0.0));
            total += ReadManualMipChild(srcBase + float2(1.0, 0.0));
            total += ReadManualMipChild(srcBase + float2(0.0, 1.0));
            total += ReadManualMipChild(srcBase + float2(1.0, 1.0));
            return floor(total + 0.5);
        }

        float ReadBlockBits(float2 block)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            float2 uv = (min(block, blockCount - 1.0) + 0.5) / blockCount;
            // block bit数TextureはPoint filter・mipなしの整数dataなので、集計loop内でもLOD 0を固定する
            return DecodeUInt24(tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)));
        }

        float ReadChunkValidBytes(float2 chunk)
        {
            float chunkSize = max(_ChunkSize, 1.0);
            float totalBits = 0.0;

            [unroll]
            for (int localY = 0; localY < 4; localY++)
            {
                [unroll]
                for (int localX = 0; localX < 4; localX++)
                {
                    float2 block = chunk * chunkSize + float2(localX, localY);
                    totalBits += ReadBlockBits(block);
                }
            }

            // block bit countのsumは整数data。PC/Androidのceil境界差を避けるためbyte数変換前に整数へ戻す
            return ICBitCountToByteCount(totalBits);
        }

        ENDCG

        Pass
        {
            Name "ChunkValidBytesRgba"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 chunk = floor(i.uv * max(_ChunkCount.xy, 1.0.xx));
                return EncodeUInt24(ReadChunkValidBytes(chunk));
            }
            ENDCG
        }

        Pass
        {
            Name "ManualMipBase"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 pixel = floor(i.uv * max(_ChunkMipAtlasSize.xy, 1.0.xx));
                if (pixel.x < _ChunkCount.x && pixel.y < _ChunkCount.y)
                {
                    return EncodeUInt24(ReadChunkValidBytes(pixel));
                }

                return float4(0.0, 0.0, 0.0, 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "ManualMipReduce"
            // payload indexにはGenerateMipsを使わない。Android/Questのmip平均は厳密なbyte sumへ戻らず、
            // Huffman payload gatherを壊し得る。このpassは代わりに厳密なUInt24 sum atlasを作り、
            // compressed byte容量は変えない
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 pixel = floor(i.uv * max(_ChunkMipAtlasSize.xy, 1.0.xx));
                // 出力先Levelオフセット
                float2 dstPixel = pixel - _DestLevelOffset.xy;
                if (dstPixel.x >= 0.0
                    && dstPixel.y >= 0.0
                    && dstPixel.x < _DestLevelSize.x
                    && dstPixel.y < _DestLevelSize.y)
                {
                    return EncodeUInt24(SumManualMipChildren(dstPixel));
                }

                return tex2D(_MainTex, (pixel + 0.5) / max(_ChunkMipAtlasSize.xy, 1.0.xx));
            }
            ENDCG
        }

        Pass
        {
            Name "CapacityPrepassStats"

            // 128 block以下のbit数をGPU側で集約し、Y/A/Cb/Crごとに4 byteだけをreadbackする
            // 大きな係数TextureをUdon CPUへ戻さないため、出力はtotal bits 24bitと最大block bytes 8bitだけに限定する
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 statsSize = max(_PrepassStatsSize.xy, 1.0.xx);
                float2 pixel = floor(i.uv * statsSize);
                float2 previousUv = (pixel + 0.5) / statsSize;
                if (abs(pixel.x - floor(_PrepassTargetPlane + 0.5)) > 0.25)
                {
                    return tex2D(_PreviousStatsTex, previousUv);
                }

                float4 previousPacked = tex2D(_PreviousStatsTex, previousUv);
                float totalBits = DecodeUInt24(previousPacked);
                float maxBlockBytes = ICUNormToByte(previousPacked.a);
                float sampleCount = clamp(floor(_PrepassSampleBlockCount + 0.5), 0.0, 128.0);
                float sampleStart = clamp(floor(_PrepassSampleBlockStart + 0.5), 0.0, sampleCount);
                [loop]
                for (int localSampleIndex = 0; localSampleIndex < 32; localSampleIndex++)
                {
                    float sampleIndex = sampleStart + localSampleIndex;
                    if (sampleIndex >= sampleCount)
                    {
                        break;
                    }

                    float2 block = float2(
                        fmod(sampleIndex, max(_BlockCount.x, 1.0)),
                        floor(sampleIndex / max(_BlockCount.x, 1.0)));
                    float blockBits = ReadBlockBits(block);
                    totalBits += blockBits;
                    maxBlockBytes = max(maxBlockBytes, ICBitCountToByteCount(blockBits));
                }

                float4 packed = EncodeUInt24(totalBits);
                packed.a = min(floor(maxBlockBytes + 0.5), 255.0) / 255.0;
                return packed;
            }
            ENDCG
        }
    }
}
