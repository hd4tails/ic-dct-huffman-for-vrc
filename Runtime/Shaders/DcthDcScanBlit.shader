// DCTH展開時に、DC差分のプレフィックス和から各ブロックの絶対DC値を復元するシェーダー
// 構成: 2つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHDcScanBlit"
{
    // DC prefix-scan stage: decode側のsymbol展開中にdeltaから絶対DC値を復元する
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("DC Delta Texture", 2D) = "black" {}
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // prefix scanで加算するブロック間隔
        _ScanStep ("Scan Step", Float) = 1
        // 今回prefix scanするブロック行グループ番号
        _ScanBlockRowGroup ("Scan Block Row Group", Float) = -1
        // 1グループでprefix scanするブロック行数
        _ScanBlockRowsPerGroup ("Scan Block Rows Per Group", Float) = 32
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
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;
        // prefix scanで加算するブロック間隔
        float _ScanStep;
        // 今回prefix scanするブロック行グループ番号
        float _ScanBlockRowGroup;
        // 1グループでprefix scanするブロック行数
        float _ScanBlockRowsPerGroup;

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

        float4 PackSigned16(float value)
        {
            float rounded = value < 0.0 ? ceil(value - 0.5) : floor(value + 0.5);
            float clamped = clamp(rounded, -32768.0, 32767.0);
            float encoded = clamped + 32768.0;
            float lo = fmod(encoded, 256.0);
            float hi = floor(encoded / 256.0);
            return float4(lo / 255.0, hi / 255.0, 0.0, 1.0);
        }

        float UnpackSigned16(float4 packed)
        {
            float lo = floor(saturate(packed.r) * 255.0 + 0.5);
            float hi = floor(saturate(packed.g) * 255.0 + 0.5);
            return lo + hi * 256.0 - 32768.0;
        }

        float ReadPackedDc(float2 block)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            float2 uv = (block + 0.5) / blockCount;
            return UnpackSigned16(tex2D(_MainTex, uv));
        }
        ENDCG

        Pass
        {
            Name "Copy"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 block = GetPixel(i.pos, blockCount);
                return PackSigned16(ReadPackedDc(block));
            }
            ENDCG
        }

        Pass
        {
            Name "HillisSteeleStep"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 block = GetPixel(i.pos, blockCount);
                float scanBlockRowGroup = floor(_ScanBlockRowGroup + 0.5);
                if (scanBlockRowGroup >= 0.0)
                {
                    float rowsPerGroup = max(floor(_ScanBlockRowsPerGroup + 0.5), 1.0);
                    float blockRowGroup = floor(block.y / rowsPerGroup);
                    if (abs(blockRowGroup - scanBlockRowGroup) > 0.5)
                    {
                        // 各row帯pass前に直前scan Textureで出力先を埋める
                        // ここでclipし、出力先RTを読まず書込済みrowを維持する
                        clip(-1.0);
                    }
                }

                float blockIndex = block.y * blockCount.x + block.x;
                float value = ReadPackedDc(block);
                float step = max(floor(_ScanStep + 0.5), 1.0);

                // Hillis-Steele scan: step距離前にpartial sumがあれば現在のDC値へ加算する
                if (blockIndex >= step)
                {
                    float previousIndex = blockIndex - step;
                    float2 previousBlock = float2(fmod(previousIndex, blockCount.x), floor(previousIndex / blockCount.x));
                    value += ReadPackedDc(previousBlock);
                }

                return PackSigned16(value);
            }
            ENDCG
        }
    }
}
