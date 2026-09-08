// 量子化済みDCT係数をzigzag順のRLEシンボルへ変換し、Huffman符号化の入力を作るシェーダー
// 構成: 3つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
// フラグメント関数はPrefixBaseFrag、PrefixScanFrag、BuildRleFragに分かれている
Shader "HDAssets/IC/DCTH/DCTHCoeffToRleSymbolsBlit"
{
    // entropy encode準備: 量子化係数をzigzag/RLE化し、DCをAC runとは別に保持する
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Coefficient Texture", 2D) = "black" {}
        // 非ゼロ係数prefixを保持する入力Texture
        _PrefixTex ("Nonzero Prefix Texture", 2D) = "black" {}
        // xyにDCT係数Textureの幅・高さを保持する入力値
        _CoeffSize ("Coefficient Size", Vector) = (512, 512, 0, 0)
        // prefix scanで加算する要素間隔
        _PrefixStep ("Prefix Scan Step", Float) = 1
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
        // 非ゼロ係数prefixを保持する入力Texture
        sampler2D _PrefixTex;
        // xyにDCT係数Textureの幅・高さを保持する入力値
        float4 _CoeffSize;
        // prefix scanで加算する要素間隔
        float _PrefixStep;

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

        float2 ZigzagToCoord(int index)
        {
            if (index == 0) return float2(0, 0);
            if (index == 1) return float2(1, 0);
            if (index == 2) return float2(0, 1);
            if (index == 3) return float2(0, 2);
            if (index == 4) return float2(1, 1);
            if (index == 5) return float2(2, 0);
            if (index == 6) return float2(3, 0);
            if (index == 7) return float2(2, 1);
            if (index == 8) return float2(1, 2);
            if (index == 9) return float2(0, 3);
            if (index == 10) return float2(0, 4);
            if (index == 11) return float2(1, 3);
            if (index == 12) return float2(2, 2);
            if (index == 13) return float2(3, 1);
            if (index == 14) return float2(4, 0);
            if (index == 15) return float2(5, 0);
            if (index == 16) return float2(4, 1);
            if (index == 17) return float2(3, 2);
            if (index == 18) return float2(2, 3);
            if (index == 19) return float2(1, 4);
            if (index == 20) return float2(0, 5);
            if (index == 21) return float2(0, 6);
            if (index == 22) return float2(1, 5);
            if (index == 23) return float2(2, 4);
            if (index == 24) return float2(3, 3);
            if (index == 25) return float2(4, 2);
            if (index == 26) return float2(5, 1);
            if (index == 27) return float2(6, 0);
            if (index == 28) return float2(7, 0);
            if (index == 29) return float2(6, 1);
            if (index == 30) return float2(5, 2);
            if (index == 31) return float2(4, 3);
            if (index == 32) return float2(3, 4);
            if (index == 33) return float2(2, 5);
            if (index == 34) return float2(1, 6);
            if (index == 35) return float2(0, 7);
            if (index == 36) return float2(1, 7);
            if (index == 37) return float2(2, 6);
            if (index == 38) return float2(3, 5);
            if (index == 39) return float2(4, 4);
            if (index == 40) return float2(5, 3);
            if (index == 41) return float2(6, 2);
            if (index == 42) return float2(7, 1);
            if (index == 43) return float2(7, 2);
            if (index == 44) return float2(6, 3);
            if (index == 45) return float2(5, 4);
            if (index == 46) return float2(4, 5);
            if (index == 47) return float2(3, 6);
            if (index == 48) return float2(2, 7);
            if (index == 49) return float2(3, 7);
            if (index == 50) return float2(4, 6);
            if (index == 51) return float2(5, 5);
            if (index == 52) return float2(6, 4);
            if (index == 53) return float2(7, 3);
            if (index == 54) return float2(7, 4);
            if (index == 55) return float2(6, 5);
            if (index == 56) return float2(5, 6);
            if (index == 57) return float2(4, 7);
            if (index == 58) return float2(5, 7);
            if (index == 59) return float2(6, 6);
            if (index == 60) return float2(7, 5);
            if (index == 61) return float2(7, 6);
            if (index == 62) return float2(6, 7);
            return float2(7, 7);
        }

        float ReadCoeff(float2 block, int zigzagIndex, float2 coeffSize)
        {
            float2 coeffCoord = ZigzagToCoord(zigzagIndex);
            float2 coeffUv = (block * 8.0 + coeffCoord + 0.5) / coeffSize;
            // dct/quant境界処理は量子化時に適用済み。ここから係数は整数dataなので、
            // 通常の安定丸めで確定済み判定を変えない
            // DCT係数はPoint filter・mipなしの整数dataなので、可変loop内でも暗黙gradientを使わずLOD 0を読む
            // 座標と丸め処理は変えず、GPUごとの未定義なmip選択だけを排除する
            return ICQuantizedDctRoundToInt(tex2Dlod(_MainTex, float4(coeffUv, 0.0, 0.0)).r);
        }

        float4 PackRleSymbol(float coeff, int run, float flags)
        {
            // packするsymbolをReadCoeffと同じ整数gridへ保つ
            float clamped = ICQuantizedDctClampSigned16(coeff);
            float encoded = clamped + 32768.0;
            float lo = fmod(encoded, 256.0);
            float hi = floor(encoded / 256.0);
            return float4(lo / 255.0, hi / 255.0, run / 255.0, flags / 255.0);
        }

        float ReadPrefix(float2 block, int slotIndex, float2 coeffSize)
        {
            float slotRow = floor((float)slotIndex * 0.125);
            float2 slot = float2(fmod((float)slotIndex, 8.0), slotRow);
            float2 uv = (block * 8.0 + slot + 0.5) / coeffSize;
            return floor(saturate(tex2Dlod(_PrefixTex, float4(uv, 0.0, 0.0)).r) * 255.0 + 0.5);
        }

        float4 PackPrefix(float value)
        {
            return float4(clamp(floor(value + 0.5), 0.0, 255.0) / 255.0, 0.0, 0.0, 1.0);
        }

        int FindFirstPrefixAtLeast(float2 block, int targetRank, float2 coeffSize)
        {
            int low = 1;
            int high = 63;
            [unroll]
            for (int searchStep = 0; searchStep < 6; searchStep++)
            {
                int middle = (low + high) >> 1;
                if (ReadPrefix(block, middle, coeffSize) >= targetRank)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return low;
        }

        float4 PrefixBaseFrag(Varyings i) : SV_Target
        {
            float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
            float2 pixel = min(floor(i.pos.xy), coeffSize - 1.0);
            float2 block = floor(pixel / 8.0);
            float2 slot = pixel - block * 8.0;
            int slotIndex = (int)(slot.y * 8.0 + slot.x);
            float nonzero = slotIndex > 0 && abs(ReadCoeff(block, slotIndex, coeffSize)) > 0.5 ? 1.0 : 0.0;
            return PackPrefix(nonzero);
        }

        float4 PrefixScanFrag(Varyings i) : SV_Target
        {
            float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
            float2 pixel = min(floor(i.pos.xy), coeffSize - 1.0);
            float2 block = floor(pixel / 8.0);
            float2 slot = pixel - block * 8.0;
            int slotIndex = (int)(slot.y * 8.0 + slot.x);
            int prefixStep = max((int)floor(_PrefixStep + 0.5), 1);
            float prefix = ReadPrefix(block, slotIndex, coeffSize);
            if (slotIndex >= prefixStep)
            {
                prefix += ReadPrefix(block, slotIndex - prefixStep, coeffSize);
            }

            return PackPrefix(prefix);
        }

        float4 BuildRleFrag(Varyings i) : SV_Target
        {
            float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
            float2 pixel = min(floor(i.pos.xy), coeffSize - 1.0);
            float2 block = floor(pixel / 8.0);
            float2 slot = pixel - block * 8.0;
            int slotIndex = (int)(slot.y * 8.0 + slot.x);

            // slot 0 は DC。AC 側とは分けて、後段の DC delta 化に備える
            if (slotIndex == 0)
            {
                return PackRleSymbol(ReadCoeff(block, 0, coeffSize), 0, 3.0);
            }

            // slot 1 以降は「直前の非ゼロ AC からの zero run + 非ゼロ係数」を並べる
            // 最後に trailing zero が残る場合だけ EOB を追加するため、全 AC が非ゼロでも 64 slot に収まる
            int targetAcSymbol = slotIndex - 1;
            int nonzeroCount = (int)ReadPrefix(block, 63, coeffSize);
            if (targetAcSymbol < nonzeroCount)
            {
                int rank = targetAcSymbol + 1;
                int zigzagIndex = FindFirstPrefixAtLeast(block, rank, coeffSize);
                int previousZigzagIndex = targetAcSymbol > 0
                    ? FindFirstPrefixAtLeast(block, targetAcSymbol, coeffSize)
                    : 0;
                int run = zigzagIndex - previousZigzagIndex - 1;
                return PackRleSymbol(ReadCoeff(block, zigzagIndex, coeffSize), run, 1.0);
            }

            // EOB は Huffman 化で短い code にできる想定の marker。固定 3 byte の段階では圧縮効果は出ない
            if (targetAcSymbol == nonzeroCount)
            {
                int lastZigzagIndex = nonzeroCount > 0
                    ? FindFirstPrefixAtLeast(block, nonzeroCount, coeffSize)
                    : 0;
                int trailingRun = 63 - lastZigzagIndex;
                if (trailingRun > 0)
                {
                    return PackRleSymbol(0.0, trailingRun, 5.0);
                }
            }

            return PackRleSymbol(0.0, 0, 0.0);
        }
        ENDCG

        Pass
        {
            Name "BuildNonzeroPrefixBase"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment PrefixBaseFrag
            ENDCG
        }

        Pass
        {
            Name "ScanNonzeroPrefix"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment PrefixScanFrag
            ENDCG
        }

        Pass
        {
            Name "BuildRleSymbols"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment BuildRleFrag
            ENDCG
        }
    }
}
