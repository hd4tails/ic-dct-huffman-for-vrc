// ImageCompress shaderで共用するDCT数値安定化helper
// runtime encodeではcolor samplingと8bit sample確定まで既存の10進/byte丸めを使う
// sample確定後のDCT basis、積和、正規化、量子化は2進仮数gridへ揃え、
// PC/Android/iOSのfloat差で係数判定が変わらないようにする
#ifndef IMAGE_COMPRESS_HALF_ROUND_INCLUDED
#define IMAGE_COMPRESS_HALF_ROUND_INCLUDED

// floatで安全に保持できる固定小数点整数の上限値
#define IC_FIXED_MAX_INT 2147483520.0
// 製品設定は2進仮数16bit。half相当の10/12bitよりDCT精度を残しつつ、shader compiler間で変わる下位bitを捨てる
// 下のbranch tableはこの値向けに展開しているため、変更時は両方を更新する
#define IC_BINARY_MANTISSA_BITS 16.0
// 実数dct/quant→整数係数の最終境界処理。2進仮数gridだけでplatform差を吸収できる間は0を維持する
// 値を増やすと意図的に精度を下げて境界安全幅を広げる
#define IC_DCT_COEFF_TIE_BAND 0.0

float ICRoundSigned(float value)
{
    float magnitude = floor(abs(value) + 0.5);
    return value < 0.0 ? -magnitude : magnitude;
}

float ICBinaryMantissaScale(float magnitude)
{
    // asuint/log2/exp2を使わず2進仮数16bitへ揃える。halfとfull floatの中間精度を保ち、
    // Quest級Mobile shaderで重い、または移植性の低い命令を避ける
    // scale = 2^(IC_BINARY_MANTISSA_BITS - floor(log2(abs(value))))
    if (magnitude >= 32768.0) return 2.0;
    if (magnitude >= 16384.0) return 4.0;
    if (magnitude >= 8192.0) return 8.0;
    if (magnitude >= 4096.0) return 16.0;
    if (magnitude >= 2048.0) return 32.0;
    if (magnitude >= 1024.0) return 64.0;
    if (magnitude >= 512.0) return 128.0;
    if (magnitude >= 256.0) return 256.0;
    if (magnitude >= 128.0) return 512.0;
    if (magnitude >= 64.0) return 1024.0;
    if (magnitude >= 32.0) return 2048.0;
    if (magnitude >= 16.0) return 4096.0;
    if (magnitude >= 8.0) return 8192.0;
    if (magnitude >= 4.0) return 16384.0;
    if (magnitude >= 2.0) return 32768.0;
    if (magnitude >= 1.0) return 65536.0;
    if (magnitude >= 0.5) return 131072.0;
    if (magnitude >= 0.25) return 262144.0;
    if (magnitude >= 0.125) return 524288.0;
    if (magnitude >= 0.0625) return 1048576.0;
    if (magnitude >= 0.03125) return 2097152.0;
    if (magnitude >= 0.015625) return 4194304.0;
    if (magnitude >= 0.0078125) return 8388608.0;
    return 16777216.0;
}

float ICSignificantScale(float magnitude)
{
    // log/powを使わず有効10進4桁へ揃え、Mobile向けの軽さを保つ
    if (magnitude >= 10000.0) return 0.1;
    if (magnitude >= 1000.0) return 1.0;
    if (magnitude >= 100.0) return 10.0;
    if (magnitude >= 10.0) return 100.0;
    if (magnitude >= 1.0) return 1000.0;
    if (magnitude >= 0.1) return 10000.0;
    if (magnitude >= 0.01) return 100000.0;
    if (magnitude >= 0.001) return 1000000.0;
    return 10000000.0;
}

float ICRoundBinaryMantissa(float value)
{
    float scale = ICBinaryMantissaScale(abs(value));
    float rounded = ICRoundSigned(value * scale);
    return clamp(rounded, -IC_FIXED_MAX_INT, IC_FIXED_MAX_INT) / scale;
}

float ICRoundSignificant(float value)
{
    // color/decode共用helperは従来の10進gridを維持する
    // encode専用DCT積和にはICRoundBinaryMantissaを使う
    float scale = ICSignificantScale(abs(value));
    float rounded = ICRoundSigned(value * scale);
    return clamp(rounded, -IC_FIXED_MAX_INT, IC_FIXED_MAX_INT) / scale;
}

float2 ICRoundSignificant2(float2 value)
{
    return float2(ICRoundSignificant(value.x), ICRoundSignificant(value.y));
}

float3 ICRoundSignificant3(float3 value)
{
    return float3(ICRoundSignificant(value.x), ICRoundSignificant(value.y), ICRoundSignificant(value.z));
}

float4 ICRoundSignificant4(float4 value)
{
    return float4(ICRoundSignificant(value.x), ICRoundSignificant(value.y), ICRoundSignificant(value.z), ICRoundSignificant(value.w));
}

float ICStableFixedRound(float value)
{
    return ICRoundSignificant(value);
}

float ICRoundToIntStable(float value)
{
    float roundedValue = ICStableFixedRound(value);
    return roundedValue < 0.0 ? ceil(roundedValue - 0.5) : floor(roundedValue + 0.5);
}

float ICClampRoundByte(float value)
{
    return clamp(ICRoundToIntStable(value), 0.0, 255.0);
}

float ICByteRound(float value)
{
    return clamp(floor(value + 0.5), 0.0, 255.0);
}

float ICByteToUNorm(float value)
{
    return ICByteRound(value) / 255.0;
}

float ICUNormToByte(float value)
{
    return ICByteRound(saturate(value) * 255.0);
}

float ICBitCountToByteCount(float bitCount)
{
    float roundedBitCount = floor(bitCount + 0.5);
    return floor((roundedBitCount + 7.0) / 8.0);
}

float ICToGammaStable(float value)
{
    float sourceValue = saturate(value);
    if (sourceValue <= 0.0031308)
    {
        return ICRoundSignificant(sourceValue * 12.92);
    }

    return ICRoundSignificant(1.055 * pow(sourceValue, 0.4166666667) - 0.055);
}

float3 ICToGammaStable3(float3 value)
{
    return float3(
        ICToGammaStable(value.r),
        ICToGammaStable(value.g),
        ICToGammaStable(value.b));
}

float ICDctRound(float value)
{
    return ICRoundSignificant(value);
}

float3 ICDctRound3(float3 value)
{
    return ICRoundSignificant3(value);
}

float4 ICDctRound4(float4 value)
{
    return ICRoundSignificant4(value);
}

float ICDctAdd(float a, float b)
{
    return ICRoundSignificant(a + b);
}

float ICDctSub(float a, float b)
{
    return ICRoundSignificant(a - b);
}

float ICDctMul(float a, float b)
{
    return ICRoundSignificant(a * b);
}

float ICDctDiv(float a, float b)
{
    if (abs(b) < 0.00005)
    {
        return 0.0;
    }

    return ICRoundSignificant(a / b);
}

float ICDctMadd(float a, float b, float c)
{
    // multiplyとaddを別々に丸め、特定platformだけFMAへ融合されてDCT積和が変わるのを防ぐ
    return ICDctAdd(ICDctMul(a, b), c);
}

// 実数encode用helper。DCT計算結果または量子化前係数の間だけ使う
// 2進仮数gridへ揃え、platform固有float差がpass間で累積しないようにする
float ICEncodeRealRound(float value)
{
    return ICRoundBinaryMantissa(value);
}

float ICEncodeDctAccumRound(float value)
{
    // encode側DCT sumも他の実数helperと同じ2進仮数gridを使う
    // 加算、乗算、正規化、除算を1つのplatform非依存spacingへ統一し、
    // 10進固定gridとfloat相対誤差を混在させない
    return ICEncodeRealRound(value);
}

float ICEncodeDctAccumMul(float a, float b)
{
    return ICEncodeDctAccumRound(a * b);
}

float ICEncodeDctAccumMadd(float a, float b, float c)
{
    float product = ICEncodeDctAccumMul(a, b);
    return ICEncodeDctAccumRound(c + product);
}

float ICEncodeDctRoundToIntStable(float value)
{
    float roundedValue = ICEncodeDctAccumRound(value);
    // n+0.5近傍はPC/Androidで係数が分かれやすい境界
    // IC_DCT_COEFF_TIE_BANDが0以外なら狭い帯域を0方向へ倒す。現在値0では2進仮数grid後の通常四捨五入
    // 実数DCT量子化商だけに使い、後段の量子化済み係数は通常の整数丸めで読む
    float magnitude = abs(roundedValue);
    float baseValue = floor(magnitude);
    float fraction = magnitude - baseValue;
    if (abs(fraction - 0.5) < IC_DCT_COEFF_TIE_BAND)
    {
        return roundedValue < 0.0 ? -baseValue : baseValue;
    }

    float roundedMagnitude = floor(magnitude + 0.5);
    return roundedValue < 0.0 ? -roundedMagnitude : roundedMagnitude;
}

// 整数data用helper。encode passがDCT係数を量子化した後だけ使う
// n+0.5境界処理を再適用すると確定済み整数が変わるため、ここでは意図的に使わない
float ICQuantizedDctRoundToInt(float value)
{
    // DCT量子化で整数係数が確定した後だけ使う。この段階の値は0,1,2...近傍なので、
    // 通常の安定丸めでencode判定を保ち、広いn+0.5境界処理を二重適用しない
    return ICRoundToIntStable(value);
}

float ICQuantizedDctClampSigned16(float value)
{
    return clamp(ICQuantizedDctRoundToInt(value), -32768.0, 32767.0);
}

float ICDctRoundToIntStable(float value)
{
    return ICRoundToIntStable(value);
}

#endif
