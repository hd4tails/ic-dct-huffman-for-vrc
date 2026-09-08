// 可変長RLEストリームを、各ブロック64個の固定係数スロットへ展開するシェーダー
// 構成: 3つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHRleToSymbolFixedBlit"
{
    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("RLE Symbol Or Prefix Texture", 2D) = "black" {}
        // 非ゼロ係数prefixを保持する入力Texture
        _PrefixTex ("Prefix Texture", 2D) = "black" {}
        // 差分化前DC値を保持する入力Texture
        _DcValuesTex ("Raw DC Values Texture", 2D) = "black" {}
        // xyにシンボルTextureの幅・高さを保持する入力値
        _SymbolSize ("Symbol Size", Vector) = (512, 512, 0, 0)
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // prefix scanで加算する要素間隔
        _PrefixStep ("Prefix Step", Float) = 1
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
        // 非ゼロ係数prefixを保持する入力Texture
        sampler2D _PrefixTex;
        // 差分化前DC値を保持する入力Texture
        sampler2D _DcValuesTex;
        // xyにシンボルTextureの幅・高さを保持する入力値
        float4 _SymbolSize;
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;
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

        float2 GetPixel(float4 screenPos, float2 size)
        {
            return min(floor(screenPos.xy), max(size - 1.0, 0.0));
        }

        int GetZigzagIndex(float2 pixel, float2 block)
        {
            float2 slot = pixel - block * 8.0;
            return (int)(slot.y * 8.0 + slot.x);
        }

        float2 GetSlotUv(float2 block, int slotIndex)
        {
            float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
            float slotRow = floor((float)slotIndex * 0.125);
            float2 slot = float2(fmod((float)slotIndex, 8.0), slotRow);
            return (block * 8.0 + slot + 0.5) / symbolSize;
        }

        float4 ReadRleSymbol(float2 block, int slotIndex)
        {
            return tex2Dlod(_MainTex, float4(GetSlotUv(block, slotIndex), 0.0, 0.0));
        }

        bool HasFlag(float4 symbol, float bitValue)
        {
            float flags = floor(saturate(symbol.a) * 255.0 + 0.5);
            return fmod(floor(flags / bitValue), 2.0) >= 1.0;
        }

        float UnpackSigned16(float4 packed)
        {
            float lo = floor(saturate(packed.r) * 255.0 + 0.5);
            float hi = floor(saturate(packed.g) * 255.0 + 0.5);
            return lo + hi * 256.0 - 32768.0;
        }

        float RoundToIntStable(float value)
        {
            return value < 0.0 ? ceil(value - 0.5) : floor(value + 0.5);
        }

        float4 PackSymbol(float coeff, int zigzagIndex, bool valid)
        {
            float clamped = clamp(RoundToIntStable(coeff), -32768.0, 32767.0);
            float encoded = clamped + 32768.0;
            float lo = fmod(encoded, 256.0);
            float hi = floor(encoded / 256.0);
            float flags = valid ? 1.0 : 0.0;
            if (zigzagIndex == 0 && valid)
            {
                flags += 2.0;
            }

            return float4(lo / 255.0, hi / 255.0, zigzagIndex / 255.0, flags / 255.0);
        }

        float ReadRawDc(float2 block)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            return UnpackSigned16(tex2D(_DcValuesTex, (block + 0.5) / blockCount));
        }

        float4 EncodePrefix(float value)
        {
            return float4(clamp(floor(value + 0.5), 0.0, 255.0) / 255.0, 0.0, 0.0, 1.0);
        }

        float ReadPrefix(float2 block, int slotIndex)
        {
            return floor(saturate(tex2Dlod(_PrefixTex, float4(GetSlotUv(block, slotIndex), 0.0, 0.0)).r) * 255.0 + 0.5);
        }
        ENDCG

        Pass
        {
            Name "BuildPrefixBase"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
                float2 pixel = GetPixel(i.pos, symbolSize);
                float2 block = floor(pixel / 8.0);
                int slotIndex = GetZigzagIndex(pixel, block);
                if (slotIndex == 0)
                {
                    // AC係数はzigzag index 1から始まる
                    return EncodePrefix(1.0);
                }

                float4 symbol = ReadRleSymbol(block, slotIndex);
                if (!HasFlag(symbol, 1.0))
                {
                    return EncodePrefix(0.0);
                }

                // EOBは残りの全係数を覆う。64-stepのsentinelでprefixを単調増加に保ち、
                // 以降のtargetを同じslotへ対応させる
                if (HasFlag(symbol, 4.0))
                {
                    return EncodePrefix(64.0);
                }

                float run = floor(saturate(symbol.b) * 255.0 + 0.5);
                return EncodePrefix(run + 1.0);
            }
            ENDCG
        }

        Pass
        {
            Name "ScanPrefix"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
                float2 pixel = GetPixel(i.pos, symbolSize);
                float2 block = floor(pixel / 8.0);
                int slotIndex = GetZigzagIndex(pixel, block);
                float value = ReadPrefix(block, slotIndex);
                int prefixStep = max((int)floor(_PrefixStep + 0.5), 1);
                if (slotIndex >= prefixStep)
                {
                    value += ReadPrefix(block, slotIndex - prefixStep);
                }

                return EncodePrefix(value);
            }
            ENDCG
        }

        Pass
        {
            Name "DecodeFixedSymbols"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
                float2 pixel = GetPixel(i.pos, symbolSize);
                float2 block = floor(pixel / 8.0);
                int targetZigzag = GetZigzagIndex(pixel, block);
                if (targetZigzag == 0)
                {
                    return PackSymbol(ReadRawDc(block), 0, true);
                }

                int low = 1;
                int high = 63;
                [unroll]
                for (int searchStep = 0; searchStep < 6; searchStep++)
                {
                    int middle = (low + high) >> 1;
                    if (ReadPrefix(block, middle) > (float)targetZigzag)
                    {
                        high = middle;
                    }
                    else
                    {
                        low = middle + 1;
                    }
                }

                int symbolSlot = min(low, 63);
                float prefixEnd = ReadPrefix(block, symbolSlot);
                if (prefixEnd <= (float)targetZigzag)
                {
                    return PackSymbol(0.0, targetZigzag, false);
                }

                float4 symbol = ReadRleSymbol(block, symbolSlot);
                if (!HasFlag(symbol, 1.0) || HasFlag(symbol, 4.0))
                {
                    return PackSymbol(0.0, targetZigzag, false);
                }

                int coefficientZigzag = (int)prefixEnd - 1;
                return coefficientZigzag == targetZigzag
                    ? PackSymbol(UnpackSigned16(symbol), targetZigzag, true)
                    : PackSymbol(0.0, targetZigzag, false);
            }
            ENDCG
        }
    }
}
