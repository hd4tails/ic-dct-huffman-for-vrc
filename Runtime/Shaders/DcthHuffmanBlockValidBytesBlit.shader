// ブロックごとのビット数を、固定64バイトのブロックページ内で実際に使うバイト数へ変換するシェーダー
// 構成: 2つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHHuffmanBlockValidBytesBlit"
{
    // payload容量計算: block別bit数を、各64-byte block pageの有効byte数へ変換する
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Huffman Block Bits", 2D) = "black" {}
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
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
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;

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

        float ReadValidBytes(float2 pixel)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            float2 uv = (min(pixel, blockCount - 1.0) + 0.5) / blockCount;
            // bit countはRGBへpackした整数data。微小な正のRT/sample誤差で1 byte増えないよう、
            // byte切り上げ前に整数へ戻す
            return ICBitCountToByteCount(DecodeUInt24(tex2D(_MainTex, uv)));
        }
        ENDCG

        Pass
        {
            Name "BlockValidBytesRgba"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 block = floor(i.pos.xy);
                return EncodeUInt24(ReadValidBytes(block));
            }
            ENDCG
        }

        Pass
        {
            Name "BlockValidBytesFloat"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 block = floor(i.pos.xy);
                float bytes = ReadValidBytes(block);
                return float4(bytes, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
