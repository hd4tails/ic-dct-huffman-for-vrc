#pragma target 3.0
#include "UnityCG.cginc"
#include "DcthHalfRound.cginc"

// 現在のBlitで基準入力として読むTexture
sampler2D _MainTex;
// DCT係数へ適用する量子化table Texture
sampler2D _QuantTex;
// 量子化値の逆数を保持するlookup Texture
sampler2D _QuantReciprocalTex;
// gamma変換結果を参照するlookup Texture
sampler2D _GammaEncodeTex;
// 容量予測事前処理ブロックmapを保持する入力Texture
sampler2D _SampleBlockMapTex;
// 容量予測事前処理前段DCを保持する入力Texture
sampler2D _SamplePreviousDcTex;
// 水平分割処理の前段結果を保持するTexture
sampler2D _HorizontalPreviousTex;
// xyにDCT係数Textureの幅・高さを保持する入力値
float4 _CoeffSize;
// xyに入力Textureの幅・高さを保持する入力値
float4 _SourceSize;
// 処理対象とするY・A・Cb・Crプレーンを選択するモード
float _PlaneMode;
// 入力Textureを色ではなく未変換の格納値として読むかを示すフラグ
float _InputIsRawStorage;
// 入力TextureがsRGB領域の値を保持しているかを示すフラグ
float _SourceTextureSrgb;
// 符号化するRGB値をsRGB領域として扱うかを示すフラグ
float _EncodeSrgb;
// 入力RGBAから対象プレーンを抽出する重み
float4 _PlaneWeights;
// 対象プレーンを選ぶための格納位置オフセット
float _PlaneOffset;
// プレーンごとに適用する量子化の重み
float4 _QuantWeights;
// gamma lookup Textureの要素数
float _GammaEncodeSize;
// gamma lookup Textureの幅
float _GammaEncodeWidth;
// gamma lookup Textureの高さ
float _GammaEncodeHeight;
// xyに容量予測用ブロックmapの幅・高さを保持する入力値
float4 _SampleMapSize;
// xyに容量予測で参照するブロック範囲、zに合計ブロック数を保持する入力値
float4 _SampleBlockCount;
// xyに入力画像の横・縦方向のブロック数を保持する入力値
float4 _SourceBlockCount;
// 水平処理サンプルオフセット
float _HorizontalSampleOffset;
// 水平処理前段が利用可能かを示すフラグ
float _HasHorizontalPrevious;

// fragmentではSV_POSITIONを出力先texel座標として使う
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

float SelectPlane(float4 color)
{
    float planeMode = floor(_PlaneMode + 0.5);
    float selectedPlane = 0.0;

    // 早期returnを避け、全plane分岐で初期化済みの戻り値へ代入する
    // 有効なPlaneModeに対する計算式は従来と同じで、Shaderコンパイラの未初期化警告だけを防ぐ
    if (planeMode < 0.5)
    {
        selectedPlane = ICDctRound(ICDctAdd(ICDctAdd(ICDctMul(color.r, 0.299), ICDctMul(color.g, 0.587)), ICDctMul(color.b, 0.114)));
    }
    else if (planeMode < 1.5)
    {
        selectedPlane = ICDctRound(color.a);
    }
    else if (planeMode < 2.5)
    {
        selectedPlane = ICDctRound(ICDctAdd(ICDctAdd(ICDctAdd(ICDctMul(color.r, -0.168736), ICDctMul(color.g, -0.331264)), ICDctMul(color.b, 0.5)), 0.5));
    }
    else
    {
        selectedPlane = ICDctRound(ICDctAdd(ICDctAdd(ICDctAdd(ICDctMul(color.r, 0.5), ICDctMul(color.g, -0.418688)), ICDctMul(color.b, -0.081312)), 0.5));
    }

    return selectedPlane;
}

float SelectDctBasisSampleConst(float sample, float v0, float v1, float v2, float v3, float v4, float v5, float v6, float v7)
{
    float s = floor(sample + 0.5);
    if (s < 0.5) return v0;
    if (s < 1.5) return v1;
    if (s < 2.5) return v2;
    if (s < 3.5) return v3;
    if (s < 4.5) return v4;
    if (s < 5.5) return v5;
    if (s < 6.5) return v6;
    return v7;
}

// DCT basis定数tableは cos(((2 * sample + 1) * coeff * pi) / 16) から生成した
// 定数自体は個別に丸めず、積和helperが各積とsumを2進仮数gridへ揃える
// sampleごとの余分なICBinaryMantissaScaleを避けつつ、累積DCT経路をplatform間で安定させる
float ReadDctBasisConst(float sample, float coeff)
{
    float c = floor(coeff + 0.5);
    if (c < 0.5)
    {
        return 1.0;
    }
    if (c < 1.5)
    {
        return SelectDctBasisSampleConst(sample, 0.9807852804, 0.8314696123, 0.5555702330, 0.1950903220, -0.1950903220, -0.5555702330, -0.8314696123, -0.9807852804);
    }
    if (c < 2.5)
    {
        return SelectDctBasisSampleConst(sample, 0.9238795325, 0.3826834324, -0.3826834324, -0.9238795325, -0.9238795325, -0.3826834324, 0.3826834324, 0.9238795325);
    }
    if (c < 3.5)
    {
        return SelectDctBasisSampleConst(sample, 0.8314696123, -0.1950903220, -0.9807852804, -0.5555702330, 0.5555702330, 0.9807852804, 0.1950903220, -0.8314696123);
    }
    if (c < 4.5)
    {
        return SelectDctBasisSampleConst(sample, 0.7071067812, -0.7071067812, -0.7071067812, 0.7071067812, 0.7071067812, -0.7071067812, -0.7071067812, 0.7071067812);
    }
    if (c < 5.5)
    {
        return SelectDctBasisSampleConst(sample, 0.5555702330, -0.9807852804, 0.1950903220, 0.8314696123, -0.8314696123, -0.1950903220, 0.9807852804, -0.5555702330);
    }
    if (c < 6.5)
    {
        return SelectDctBasisSampleConst(sample, 0.3826834324, -0.9238795325, 0.9238795325, -0.3826834324, -0.3826834324, 0.9238795325, -0.9238795325, 0.3826834324);
    }

    return SelectDctBasisSampleConst(sample, 0.1950903220, -0.5555702330, 0.8314696123, -0.9807852804, 0.9807852804, -0.8314696123, 0.5555702330, -0.1950903220);
}

float SelectQuant(float4 quant)
{
    float planeMode = floor(_PlaneMode + 0.5);
    if (planeMode < 0.5) return quant.r;
    if (planeMode < 1.5) return quant.g;
    return quant.b;
}

float ReadGammaLut(float linearValue)
{
    // LUTは最大Texture幅を超えない2次元配置。C#側のindex順と一致させないと全画素の色が変わる
    float size = max(_GammaEncodeSize, 1.0);
    float index = floor(saturate(linearValue) * (size - 1.0) + 0.5);
    float width = max(_GammaEncodeWidth, 1.0);
    float height = max(_GammaEncodeHeight, 1.0);
    float x = index - floor(index / width) * width;
    float y = floor(index / width);
    return tex2D(_GammaEncodeTex, float2((x + 0.5) / width, (y + 0.5) / height)).r;
}

float3 ReadGammaLut3(float3 linearColor)
{
    return float3(
        ReadGammaLut(linearColor.r),
        ReadGammaLut(linearColor.g),
        ReadGammaLut(linearColor.b));
}

float3 QuantizeByte3(float3 color)
{
    return float3(
        ICClampRoundByte(ICDctMul(saturate(color.r), 255.0)) / 255.0,
        ICClampRoundByte(ICDctMul(saturate(color.g), 255.0)) / 255.0,
        ICClampRoundByte(ICDctMul(saturate(color.b), 255.0)) / 255.0);
}

bool SampledSourceIsSrgb()
{
    if (_InputIsRawStorage > 0.5)
    {
        return _SourceTextureSrgb > 0.5;
    }

    #if defined(UNITY_COLORSPACE_GAMMA)
    return _SourceTextureSrgb > 0.5;
    #else
    return false;
    #endif
}

float3 SourceRgbForDct(float3 color)
{
    bool sourceIsSrgb = SampledSourceIsSrgb();
    // 符号化するRGB値をsRGB領域として扱うかを示すフラグ
    bool encodeIsSrgb = _EncodeSrgb > 0.5;
    float3 sourceRgb = QuantizeByte3(color);

    // 色空間が一致する場合のbyte量子化を既定値にし、変換が必要な場合だけ上書きする
    // これにより従来の色変換結果を保ったまま、関数戻り値の未初期化警告を防ぐ
    if (sourceIsSrgb != encodeIsSrgb)
    {
        if (encodeIsSrgb)
        {
            sourceRgb = ReadGammaLut3(color);
        }
        else
        {
            sourceRgb = QuantizeByte3(GammaToLinearSpace(saturate(color)));
        }
    }

    return sourceRgb;
}

// 選択した入力planeをDCT用の中心化8bit sampleへ変換する
float ReadY255(float2 pixel)
{
    float2 sourceSize = max(_SourceSize.xy, 1.0.xx);
    float2 sourcePixel = clamp(pixel, float2(0.0, 0.0), sourceSize - 1.0);
    float2 uv = (sourcePixel + 0.5) / sourceSize;
    float4 color = ICDctRound4(tex2D(_MainTex, uv));
    // 選択したDCT色領域へ変換する。shader pow()差でHuffman容量が変わらないよう、
    // sRGB経路はpoint-sampled LUTを使う
    color.rgb = SourceRgbForDct(color.rgb);
    float plane = SelectPlane(color);
    float planeByte = ICClampRoundByte(ICDctMul(saturate(plane), 255.0));
    return ICDctSub(planeByte, 128.0);
}

// 現在選択中の8x8量子化tableを読む
float ReadYQuant(float2 coeff)
{
    float2 uv = (coeff + 0.5) / 8.0;
    return max(ICUNormToByte(SelectQuant(tex2D(_QuantTex, uv))), 1.0);
}

float ReadQuantReciprocal(float quant)
{
    float q = clamp(floor(quant + 0.5), 1.0, 255.0);
    float4 packed = tex2D(_QuantReciprocalTex, float2((q + 0.5) / 256.0, 0.5));
    float r = ICUNormToByte(packed.r);
    float g = ICUNormToByte(packed.g);
    float b = ICUNormToByte(packed.b);
    float fixedValue = r + g * 256.0 + b * 65536.0;
    return fixedValue * 0.00000095367431640625;
}

// 同一block内の(u, sampleY)からhorizontal DCT中間値を読む
float ReadHorizontalPartial(float2 block, float u, float sampleY)
{
    float2 coeffSize = max(_CoeffSize.xy, 1.0.xx);
    float2 uv = (block * 8.0 + float2(u, sampleY) + 0.5) / coeffSize;
    return ICEncodeDctAccumRound(tex2D(_MainTex, uv).r);
}

// Blit出力位置を範囲内のpixel座標へ変換する
float2 GetPixel(float4 screenPos, float2 size)
{
    return min(floor(screenPos.xy), max(size - 1.0, 0.0));
}

float ComputeQuantizedDctQuotient(float2 coeffPixel)
{
    float2 block = floor(coeffPixel / 8.0);
    float2 coeff = coeffPixel - block * 8.0;

    float sum = 0.0;
    [unroll(8)]
    for (int sy = 0; sy < 8; sy++)
    {
        sum = ICEncodeDctAccumMadd(ReadHorizontalPartial(block, coeff.x, sy), ReadDctBasisConst(sy, coeff.y), sum);
    }

    float normX = coeff.x < 0.5 ? 0.1767766953 : 0.25;
    float normY = coeff.y < 0.5 ? 0.70710678118 : 1.0;
    float norm = ICEncodeDctAccumMul(normX, normY);
    float dct = ICEncodeDctAccumMul(norm, sum);
    float quant = ReadYQuant(coeff);
    return ICEncodeDctAccumMul(dct, ReadQuantReciprocal(quant));
}

float DecodeByte(float value)
{
    return floor(saturate(value) * 255.0 + 0.5);
}

// 128 blockの選択座標はCPUで決定し、x/yを各16bitでRGBA32へ格納する
// shader内で乱数を生成しないため、PC/Mobileで必ず同じsource blockを参照する
float2 ReadCapacityPrepassSourceBlock(float2 sampleBlock)
{
    // xyに容量予測で参照するブロック範囲、zに合計ブロック数を保持する入力値
    float sampleIndex = sampleBlock.y * _SampleBlockCount.x + sampleBlock.x;
    float2 mapUv = (float2(sampleIndex, 0.0) + 0.5) / max(_SampleMapSize.xy, 1.0.xx);
    float4 packed = tex2D(_SampleBlockMapTex, mapUv);
    float x = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0;
    float y = DecodeByte(packed.b) + DecodeByte(packed.a) * 256.0;
    return min(float2(x, y), max(_SourceBlockCount.xy - 1.0, 0.0));
}

float ComputeCapacityPrepassUnquantizedDct(float2 coeffPixel)
{
    float2 block = floor(coeffPixel / 8.0);
    float2 coeff = coeffPixel - block * 8.0;
    float sum = 0.0;
    [unroll(8)]
    for (int sy = 0; sy < 8; sy++)
    {
        sum = ICEncodeDctAccumMadd(ReadHorizontalPartial(block, coeff.x, sy), ReadDctBasisConst(sy, coeff.y), sum);
    }

    float normX = coeff.x < 0.5 ? 0.1767766953 : 0.25;
    float normY = coeff.y < 0.5 ? 0.70710678118 : 1.0;
    float norm = ICEncodeDctAccumMul(normX, normY);
    return ICEncodeDctAccumMul(norm, sum);
}

float QuantizeCapacityPrepassDct(float dct, float2 coeff)
{
    float quant = ReadYQuant(coeff);
    float quotient = ICEncodeDctAccumMul(dct, ReadQuantReciprocal(quant));
    return ICEncodeDctRoundToIntStable(quotient);
}

float4 PackSigned16(float value)
{
    float clamped = ICQuantizedDctClampSigned16(value);
    float encoded = clamped + 32768.0;
    float lo = fmod(encoded, 256.0);
    float hi = floor(encoded / 256.0);
    return float4(lo / 255.0, hi / 255.0, 0.0, 1.0);
}
