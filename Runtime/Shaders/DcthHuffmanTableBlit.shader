// DC・ACの出現頻度から、符号化と復号で共有する正準Huffmanテーブルを構築するシェーダー
// 構成: 6つのPassで構成し、UnityCG.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 同じフラグメント関数をPassごとの設定で使い分ける
Shader "HDAssets/IC/DCTH/DCTHHuffmanTableBlit"
{
    // canonical Huffman table stage: DC/AC frequency Textureからencode/decode共用のcode length/code Textureを作る
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Frequency Or Length Texture", 2D) = "black" {}
        // 出現頻度を保持する入力Texture
        _FrequencyTex ("Frequency Texture", 2D) = "black" {}
        // 符号長制限前のhistogramを保持するTexture
        _RawLengthHistogramTex ("Raw Length Histogram", 2D) = "black" {}
        // 符号長制限後のhistogramを保持するTexture
        _LimitedLengthHistogramTex ("Limited Length Histogram", 2D) = "black" {}
        // 前段tableを保持する入力Texture
        _PreviousTableTex ("Previous Table Texture", 2D) = "black" {}
        // シンボル個数
        _SymbolCount ("Symbol Count", Float) = 256
        // xyにHuffman tableの幅・高さを保持する入力値
        _TableSize ("Table Size", Vector) = (256, 1, 0, 0)
        // xyに入力Huffman tableの幅・高さを保持する入力値
        _InputTableSize ("Input Table Size", Vector) = (256, 1, 0, 0)
        // 対象シンボル開始位置
        _TargetSymbolStart ("Target Symbol Start", Float) = -1
        // 対象シンボル個数
        _TargetSymbolCount ("Target Symbol Count", Float) = 256
        // 前段までのHuffman tableが利用可能かを示すフラグ
        _HasPreviousTable ("Has Previous Table", Float) = 0
        // 単一値Texture向けの処理経路を使用するかを示すフラグ
        _SingleValueMode ("Single Value Mode", Float) = 0
        // xyに単一値Textureの幅・高さを保持する入力値
        _SingleValueTextureSize ("Single Value Texture Size", Vector) = (4, 4, 0, 0)
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

        // Huffman tableが扱うシンボル数の上限
        #define MAX_SYMBOLS 256
        // Huffman tree構築で使用するnode数の上限
        #define MAX_NODES 512
        // Huffman符号長の上限bit数
        #define MAX_CODE_LENGTH 16
        // 制限前Huffman treeで扱う符号長の上限
        #define MAX_TREE_LENGTH 255
        // 符号長histogramが保持するbucket数
        #define MAX_LENGTH_BUCKETS 256

        // 現在のBlitで基準入力として読むTexture
        sampler2D _MainTex;
        // 出現頻度を保持する入力Texture
        sampler2D _FrequencyTex;
        // 符号長制限前のhistogramを保持するTexture
        sampler2D _RawLengthHistogramTex;
        // 符号長制限後のhistogramを保持するTexture
        sampler2D _LimitedLengthHistogramTex;
        // 前段tableを保持する入力Texture
        sampler2D _PreviousTableTex;
        // シンボル個数
        float _SymbolCount;
        // xyにHuffman tableの幅・高さを保持する入力値
        float4 _TableSize;
        // xyに入力Huffman tableの幅・高さを保持する入力値
        float4 _InputTableSize;
        // 対象シンボル開始位置
        float _TargetSymbolStart;
        // 対象シンボル個数
        float _TargetSymbolCount;
        // 前段までのHuffman tableが利用可能かを示すフラグ
        float _HasPreviousTable;
        // 単一値Texture向けの処理経路を使用するかを示すフラグ
        float _SingleValueMode;
        // xyに単一値Textureの幅・高さを保持する入力値
        float4 _SingleValueTextureSize;

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

        float DecodeUInt24(float4 packed)
        {
            float r = floor(saturate(packed.r) * 255.0 + 0.5);
            float g = floor(saturate(packed.g) * 255.0 + 0.5);
            float b = floor(saturate(packed.b) * 255.0 + 0.5);
            return r + g * 256.0 + b * 65536.0;
        }

        float4 EncodeLength(float value)
        {
            return float4(clamp(floor(value + 0.5), 0.0, 255.0) / 255.0, 0.0, 0.0, 1.0);
        }

        float4 EncodeCodeAndLength(float code, float length)
        {
            float clampedCode = min(floor(code + 0.5), 16777215.0);
            float lo = fmod(clampedCode, 256.0);
            float mid = fmod(floor(clampedCode / 256.0), 256.0);
            float hi = floor(clampedCode / 65536.0);
            return float4(lo / 255.0, mid / 255.0, hi / 255.0, clamp(floor(length + 0.5), 0.0, 255.0) / 255.0);
        }

        float4 EncodeCount(float value)
        {
            float clamped = clamp(floor(value + 0.5), 0.0, 65535.0);
            return float4(fmod(clamped, 256.0) / 255.0, floor(clamped / 256.0) / 255.0, 0.0, 1.0);
        }

        float ReadPackedCount(float4 packed)
        {
            return floor(saturate(packed.r) * 255.0 + 0.5)
                + floor(saturate(packed.g) * 255.0 + 0.5) * 256.0;
        }

        float ReadFrequency(int symbol)
        {
            float2 tableSize = max(_InputTableSize.xy, 1.0.xx);
            float2 uv = (float2(symbol, 0.0) + 0.5) / tableSize;
            // frequency/length tableはPoint filter・mipなしのdataなので、table構築loop内ではLOD 0を固定する
            return DecodeUInt24(tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)));
        }

        float ReadLimitedLengthFrequency(int symbol)
        {
            float2 tableSize = max(_TableSize.xy, 1.0.xx);
            float2 uv = (float2(symbol, 0.0) + 0.5) / tableSize;
            return DecodeUInt24(tex2Dlod(_FrequencyTex, float4(uv, 0.0, 0.0)));
        }

        float ReadLength(int symbol)
        {
            float2 tableSize = max(_InputTableSize.xy, 1.0.xx);
            float2 uv = (float2(symbol, 0.0) + 0.5) / tableSize;
            return floor(saturate(tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)).r) * 255.0 + 0.5);
        }

        float ReadRawLengthCount(int codeLength)
        {
            float2 uv = (float2(codeLength, 0.0) + 0.5) / float2(MAX_LENGTH_BUCKETS, 1.0);
            return ReadPackedCount(tex2Dlod(_RawLengthHistogramTex, float4(uv, 0.0, 0.0)));
        }

        float ReadLimitedLengthCount(int codeLength)
        {
            float2 uv = (float2(codeLength - 1, 0.0) + 0.5) / float2(MAX_CODE_LENGTH, 1.0);
            return ReadPackedCount(tex2Dlod(_LimitedLengthHistogramTex, float4(uv, 0.0, 0.0)));
        }

        float Pow2Small(int exponent)
        {
            return exp2((float)clamp(exponent, 0, MAX_CODE_LENGTH));
        }

        bool IsTargetSymbol(int symbol)
        {
            int start = (int)floor(_TargetSymbolStart + 0.5);
            if (start < 0)
            {
                return true;
            }

            int count = max((int)floor(_TargetSymbolCount + 0.5), 1);
            return symbol >= start && symbol < start + count;
        }

        float4 ReadPreviousTable(int symbol, float symbolCount)
        {
            if (_HasPreviousTable < 0.5)
            {
                return float4(0.0, 0.0, 0.0, 0.0);
            }

            float2 uv = (float2(symbol, 0.0) + 0.5) / float2(max(symbolCount, 1.0), 1.0);
            return tex2Dlod(_PreviousTableTex, float4(uv, 0.0, 0.0));
        }
        ENDCG

        Pass
        {
            Name "BuildRawCodeLengths"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float symbolCount = clamp(floor(_SymbolCount + 0.5), 1.0, MAX_SYMBOLS);
                float2 pixel = GetPixel(i.pos, float2(symbolCount, 1.0));
                float2 singlePixel = GetPixel(i.pos, max(_SingleValueTextureSize.xy, 1.0.xx));
                if (_SingleValueMode > 0.5 && (singlePixel.x > 0.5 || singlePixel.y > 0.5))
                {
                    return EncodeLength(0.0);
                }
                int targetSymbol = _SingleValueMode > 0.5
                    ? clamp((int)floor(_TargetSymbolStart + 0.5), 0, (int)symbolCount - 1)
                    : (int)pixel.x;
                if (!IsTargetSymbol(targetSymbol))
                {
                    return ReadPreviousTable(targetSymbol, symbolCount);
                }
                float targetFrequency = ReadFrequency(targetSymbol);
                if (targetFrequency < 0.5)
                {
                    return EncodeLength(0.0);
                }

                float weights[MAX_NODES];
                int leafCount = 0;
                int targetNode = -1;

                // 各 fragment が同じ length-limited tree を生成し、自分の symbol の code length だけを返す
                // Android の fragment shader で大きな一時配列が壊れないよう、leafSymbols/rawLengths/active は持たない
                [loop]
                for (int symbol = 0; symbol < MAX_SYMBOLS; symbol++)
                {
                    if (symbol >= (int)symbolCount)
                    {
                        break;
                    }

                    float frequency = ReadFrequency(symbol);
                    if (frequency < 0.5)
                    {
                        continue;
                    }

                    weights[leafCount] = frequency;
                    if (symbol == targetSymbol)
                    {
                        targetNode = leafCount;
                    }

                    leafCount++;
                }

                if (leafCount <= 1)
                {
                    return EncodeLength(1.0);
                }

                int nodeCount = leafCount;
                int openCount = leafCount;
                int targetRawLengthInt = 0;

                [loop]
                for (int mergeIndex = 0; mergeIndex < MAX_SYMBOLS - 1; mergeIndex++)
                {
                    if (openCount <= 1)
                    {
                        break;
                    }

                    int first = -1;
                    int second = -1;
                    [loop]
                    for (int node = 0; node < MAX_NODES; node++)
                    {
                        if (node >= nodeCount)
                        {
                            break;
                        }

                        if (weights[node] < 0.0)
                        {
                            continue;
                        }

                        if (first < 0 || weights[node] < weights[first] || (abs(weights[node] - weights[first]) < 0.5 && node < first))
                        {
                            second = first;
                            first = node;
                        }
                        else if (second < 0 || weights[node] < weights[second] || (abs(weights[node] - weights[second]) < 0.5 && node < second))
                        {
                            second = node;
                        }
                    }

                    float mergedWeight = weights[first] + weights[second];
                    if (targetNode == first || targetNode == second)
                    {
                        targetRawLengthInt++;
                        targetNode = nodeCount;
                    }

                    weights[first] = -1.0;
                    weights[second] = -1.0;
                    weights[nodeCount] = mergedWeight;
                    nodeCount++;
                    openCount--;
                }

                return EncodeLength(clamp(targetRawLengthInt, 1, MAX_TREE_LENGTH));
            }
            ENDCG
        }

        Pass
        {
            Name "LimitCodeLengths"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float symbolCount = clamp(floor(_SymbolCount + 0.5), 1.0, MAX_SYMBOLS);
                float2 pixel = GetPixel(i.pos, float2(symbolCount, 1.0));
                int targetSymbol = (int)pixel.x;
                if (!IsTargetSymbol(targetSymbol))
                {
                    return ReadPreviousTable(targetSymbol, symbolCount);
                }
                float targetFrequency = ReadLimitedLengthFrequency(targetSymbol);
                if (targetFrequency < 0.5)
                {
                    return EncodeLength(0.0);
                }

                float targetRawLength = ReadLength(targetSymbol);
                float rank = 0.0;
                [loop]
                for (int rankSymbol = 0; rankSymbol < MAX_SYMBOLS; rankSymbol++)
                {
                    if (rankSymbol >= (int)symbolCount)
                    {
                        break;
                    }

                    float candidateFrequency = ReadLimitedLengthFrequency(rankSymbol);
                    if (candidateFrequency < 0.5)
                    {
                        continue;
                    }

                    float candidateRawLength = ReadLength(rankSymbol);
                    bool sameRawLength = abs(candidateRawLength - targetRawLength) < 0.5;
                    if (candidateRawLength < targetRawLength
                        || (sameRawLength && candidateFrequency > targetFrequency)
                        || (sameRawLength && abs(candidateFrequency - targetFrequency) < 0.5 && rankSymbol < targetSymbol))
                    {
                        rank += 1.0;
                    }
                }

                float cumulative = 0.0;
                [loop]
                for (int limitedLength = 1; limitedLength <= MAX_CODE_LENGTH; limitedLength++)
                {
                    float next = cumulative + ReadLimitedLengthCount(limitedLength);
                    if (rank < next)
                    {
                        return EncodeLength(limitedLength);
                    }

                    cumulative = next;
                }

                return EncodeLength(MAX_CODE_LENGTH);
            }
            ENDCG
        }

        Pass
        {
            Name "BuildCanonicalCodes"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float symbolCount = clamp(floor(_SymbolCount + 0.5), 1.0, MAX_SYMBOLS);
                float2 pixel = GetPixel(i.pos, float2(symbolCount, 1.0));
                int targetSymbol = (int)pixel.x;
                if (!IsTargetSymbol(targetSymbol))
                {
                    return ReadPreviousTable(targetSymbol, symbolCount);
                }
                float targetLength = ReadLength(targetSymbol);
                if (targetLength < 0.5)
                {
                    return EncodeCodeAndLength(0.0, 0.0);
                }

                float code = 0.0;
                [loop]
                for (int symbol = 0; symbol < MAX_SYMBOLS; symbol++)
                {
                    if (symbol >= (int)symbolCount)
                    {
                        break;
                    }

                    float candidateLength = ReadLength(symbol);
                    if (candidateLength > 0.5 && candidateLength < targetLength - 0.5)
                    {
                        code += Pow2Small((int)targetLength - (int)candidateLength);
                    }
                    else if (symbol < targetSymbol && abs(candidateLength - targetLength) < 0.5)
                    {
                        code += 1.0;
                    }
                }

                return EncodeCodeAndLength(code, targetLength);
            }
            ENDCG
        }

        Pass
        {
            Name "BuildRawLengthHistogram"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                int targetLength = clamp((int)floor(i.pos.x), 0, MAX_TREE_LENGTH);
                if (!IsTargetSymbol(targetLength))
                {
                    float2 previousUv = (float2(targetLength, 0.0) + 0.5) / float2(MAX_LENGTH_BUCKETS, 1.0);
                    return _HasPreviousTable > 0.5
                        ? tex2Dlod(_PreviousTableTex, float4(previousUv, 0.0, 0.0))
                        : EncodeCount(0.0);
                }
                int symbolCount = clamp((int)floor(_SymbolCount + 0.5), 1, MAX_SYMBOLS);
                float count = 0.0;
                [loop]
                for (int symbol = 0; symbol < MAX_SYMBOLS; symbol++)
                {
                    if (symbol >= symbolCount)
                    {
                        break;
                    }

                    if (ReadLimitedLengthFrequency(symbol) < 0.5)
                    {
                        continue;
                    }

                    int rawLength = clamp((int)ReadLength(symbol), 1, MAX_TREE_LENGTH);
                    if (rawLength == targetLength)
                    {
                        count += 1.0;
                    }
                }

                return EncodeCount(count);
            }
            ENDCG
        }

        Pass
        {
            Name "LimitLengthHistogram"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 singlePixel = GetPixel(i.pos, max(_SingleValueTextureSize.xy, 1.0.xx));
                if (_SingleValueMode > 0.5 && (singlePixel.x > 0.5 || singlePixel.y > 0.5))
                {
                    return EncodeCount(0.0);
                }
                int targetLength = _SingleValueMode > 0.5
                    ? clamp((int)floor(_TargetSymbolStart + 0.5) + 1, 1, MAX_CODE_LENGTH)
                    : clamp((int)floor(i.pos.x) + 1, 1, MAX_CODE_LENGTH);
                float lengthCounts[MAX_LENGTH_BUCKETS];
                [loop]
                for (int lengthIndex = 0; lengthIndex < MAX_LENGTH_BUCKETS; lengthIndex++)
                {
                    lengthCounts[lengthIndex] = ReadRawLengthCount(lengthIndex);
                }

                [loop]
                for (int length = MAX_TREE_LENGTH; length > MAX_CODE_LENGTH; length--)
                {
                    [loop]
                    for (int overflow = 0; overflow < MAX_SYMBOLS; overflow++)
                    {
                        if (lengthCounts[length] < 0.5)
                        {
                            break;
                        }

                        int donor = length - 2;
                        [loop]
                        for (int donorSearch = 0; donorSearch < MAX_TREE_LENGTH; donorSearch++)
                        {
                            if (donor <= 0 || lengthCounts[donor] > 0.5)
                            {
                                break;
                            }

                            donor--;
                        }

                        if (donor <= 0)
                        {
                            break;
                        }

                        lengthCounts[length] -= 2.0;
                        lengthCounts[length - 1] += 1.0;
                        lengthCounts[donor + 1] += 2.0;
                        lengthCounts[donor] -= 1.0;
                    }
                }

                return EncodeCount(lengthCounts[targetLength]);
            }
            ENDCG
        }

        Pass
        {
            Name "MergeSingleValue"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float symbolCount = clamp(floor(_SymbolCount + 0.5), 1.0, MAX_SYMBOLS);
                int destinationSymbol = (int)GetPixel(i.pos, float2(symbolCount, 1.0)).x;
                int targetSymbol = clamp((int)floor(_TargetSymbolStart + 0.5), 0, (int)symbolCount - 1);
                // 小さい単一値RTの結果を全table幅へ直接Blitすると座標対応が崩れる
                // 対象symbolだけを書き換え、他symbolは前段tableを保持してcanonical codeを崩さない
                if (destinationSymbol == targetSymbol)
                {
                    float2 singleUv = 0.5 / max(_SingleValueTextureSize.xy, 1.0.xx);
                    return tex2Dlod(_MainTex, float4(singleUv, 0.0, 0.0));
                }

                return ReadPreviousTable(destinationSymbol, symbolCount);
            }
            ENDCG
        }
    }
}
