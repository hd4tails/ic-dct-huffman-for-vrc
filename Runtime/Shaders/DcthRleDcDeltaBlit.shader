// DCTH展開時に、復元済みRLEストリームから各ブロックのDC差分を取り出すシェーダー
// 構成: 1つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
Shader "HDAssets/IC/DCTH/DCTHRleDcDeltaBlit"
{
    // decode補助: RLE streamからDC delta symbolを取り出す
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("RLE Symbol Texture", 2D) = "black" {}
        // xyにシンボルTextureの幅・高さを保持する入力値
        _SymbolSize ("Symbol Size", Vector) = (512, 512, 0, 0)
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "RleDcDelta"

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            // 現在のBlitで基準入力として読むTexture
            sampler2D _MainTex;
            // xyにシンボルTextureの幅・高さを保持する入力値
            float4 _SymbolSize;
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

            float2 GetPixel(float4 screenPos, float2 size)
            {
                return min(floor(screenPos.xy), max(size - 1.0, 0.0));
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 blockCount = max(_BlockCount.xy, 1.0.xx);
                float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
                float2 block = GetPixel(i.pos, blockCount);
                float2 uv = (block * 8.0 + 0.5) / symbolSize;
                float4 dcDelta = tex2D(_MainTex, uv);
                return float4(dcDelta.r, dcDelta.g, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
