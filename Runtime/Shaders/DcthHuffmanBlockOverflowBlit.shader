// 各ブロックのHuffman使用バイト数を求め、容量判定に使う最大値へ縮約するシェーダー
// 構成: 2つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHHuffmanBlockOverflowBlit"
{
    // 容量検証stage: 各blockのHuffman使用bytesを出力し、最終的に最大値へ縮約する
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Huffman Block Bits", 2D) = "black" {}
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // xyにoverflow mip atlasの幅・高さを保持する入力値
        _OverflowMipAtlasSize ("Overflow Mip Atlas Size", Vector) = (127, 64, 0, 0)
        // mip atlas内の読み取り元level開始位置
        _SourceLevelOffset ("Source Level Offset", Vector) = (0, 0, 0, 0)
        // 出力先Levelオフセット
        _DestLevelOffset ("Dest Level Offset", Vector) = (64, 0, 0, 0)
        // xyに読み取り元mip levelの幅・高さを保持する入力値
        _SourceLevelSize ("Source Level Size", Vector) = (64, 64, 0, 0)
        // xyに書き込み先mip levelの幅・高さを保持する入力値
        _DestLevelSize ("Dest Level Size", Vector) = (32, 32, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "HuffmanBlockOverflow"

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "DcthHalfRound.cginc"

            // 現在のBlitで基準入力として読むTexture
            sampler2D _MainTex;
            // xyに横・縦方向のブロック数を保持する入力値
            float4 _BlockCount;
            // xyにoverflow mip atlasの幅・高さを保持する入力値
            float4 _OverflowMipAtlasSize;
            // mip atlas内の読み取り元level開始位置
            float4 _SourceLevelOffset;
            // 出力先Levelオフセット
            float4 _DestLevelOffset;
            // xyに読み取り元mip levelの幅・高さを保持する入力値
            float4 _SourceLevelSize;
            // xyに書き込み先mip levelの幅・高さを保持する入力値
            float4 _DestLevelSize;

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

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 atlasSize = max(_OverflowMipAtlasSize.xy, 1.0.xx);
                float2 block = floor(i.uv * atlasSize);
                if (block.x < 0.0 || block.y < 0.0 || block.x >= blockCount.x || block.y >= blockCount.y)
                {
                    return EncodeUInt24(0.0);
                }

                float2 uv = (block + 0.5) / blockCount;
                // bit countをbyte数へ切り上げ、この後のmanual mipで最大値を求める
                float byteCount = ICBitCountToByteCount(DecodeUInt24(tex2D(_MainTex, uv)));
                return EncodeUInt24(byteCount);
            }
            ENDCG
        }

        Pass
        {
            Name "HuffmanBlockOverflowManualMip"
            // GenerateMipsではなくUInt24 maxを明示計算する。自動mipはfloat平均でMobile GPUのstall要因になり、
            // retryに必要な「最大block使用bytes」を整数dataとして保持できない

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "DcthHalfRound.cginc"

            // 現在のBlitで基準入力として読むTexture
            sampler2D _MainTex;
            // xyにoverflow mip atlasの幅・高さを保持する入力値
            float4 _OverflowMipAtlasSize;
            // mip atlas内の読み取り元level開始位置
            float4 _SourceLevelOffset;
            // 出力先Levelオフセット
            float4 _DestLevelOffset;
            // xyに読み取り元mip levelの幅・高さを保持する入力値
            float4 _SourceLevelSize;
            // xyに書き込み先mip levelの幅・高さを保持する入力値
            float4 _DestLevelSize;

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

            float ReadAtlasValue(float2 pixel)
            {
                float2 atlasSize = max(_OverflowMipAtlasSize.xy, 1.0.xx);
                return DecodeUInt24(tex2D(_MainTex, (pixel + 0.5) / atlasSize));
            }

            float MaxChildren(float2 dstPixel)
            {
                float2 srcBase = dstPixel * 2.0;
                float maximumValue = 0.0;
                [unroll]
                for (int y = 0; y < 2; y++)
                {
                    [unroll]
                    for (int x = 0; x < 2; x++)
                    {
                        float2 localPixel = srcBase + float2(x, y);
                        if (localPixel.x >= 0.0
                            && localPixel.y >= 0.0
                            && localPixel.x < _SourceLevelSize.x
                            && localPixel.y < _SourceLevelSize.y)
                        {
                            maximumValue = max(maximumValue, ReadAtlasValue(_SourceLevelOffset.xy + localPixel));
                        }
                    }
                }

                return floor(maximumValue + 0.5);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 atlasSize = max(_OverflowMipAtlasSize.xy, 1.0.xx);
                float2 pixel = floor(i.uv * atlasSize);
                // 出力先Levelオフセット
                float2 dstPixel = pixel - _DestLevelOffset.xy;
                if (dstPixel.x >= 0.0
                    && dstPixel.y >= 0.0
                    && dstPixel.x < _DestLevelSize.x
                    && dstPixel.y < _DestLevelSize.y)
                {
                    return EncodeUInt24(MaxChildren(dstPixel));
                }

                return tex2D(_MainTex, (pixel + 0.5) / atlasSize);
            }
            ENDCG
        }
    }
}
