// 復元したY・A・Cb・Crプレーンを格納し、最終的なRGBA画像へ合成するDCTH展開用シェーダー
// 構成: 5つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
// 4つのPackPlane Passで各プレーンを格納し、ComposePacked PassでRGBAへ合成する
Shader "HDAssets/IC/DCTH/DCTHComposeRgbaBlit"
{
    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Y or packed texture", 2D) = "black" {}
        // alphaプレーンの処理方法を選択するモード
        _AlphaMode ("Alpha Mode", Float) = 1
        // xyに出力Textureの幅・高さを保持する入力値
        _OutputSize ("Output Size", Vector) = (512, 512, 0, 0)
        // xyに格納対象プレーンの入力幅・高さを保持する入力値
        _PackedPlaneSourceSize ("Packed plane source size", Vector) = (512, 512, 0, 0)
        // xyに格納先プレーンTextureの幅・高さを保持する入力値
        _PackedPlaneTexSize ("Packed plane texture size", Vector) = (512, 512, 0, 0)
        // 格納対象プレーンが半解像度かを示すフラグ
        _PackedPlaneHalfSize ("Packed plane half size", Float) = 0
        // 符号化するRGB値をsRGB領域として扱うかを示すフラグ
        _EncodeSrgb ("Encoded sRGB", Float) = 1
        // 出力RGB値をsRGB領域へ変換するかを示すフラグ
        _OutputSrgb ("Output sRGB", Float) = 1
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
        // alphaプレーンの処理方法を選択するモード
        float _AlphaMode;
        // xyに出力Textureの幅・高さを保持する入力値
        float4 _OutputSize;
        // xyに格納対象プレーンの入力幅・高さを保持する入力値
        float4 _PackedPlaneSourceSize;
        // xyに格納先プレーンTextureの幅・高さを保持する入力値
        float4 _PackedPlaneTexSize;
        // 格納対象プレーンが半解像度かを示すフラグ
        float _PackedPlaneHalfSize;
        // 符号化するRGB値をsRGB領域として扱うかを示すフラグ
        float _EncodeSrgb;
        // 出力RGB値をsRGB領域へ変換するかを示すフラグ
        float _OutputSrgb;

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

        float SamplePlanePoint(sampler2D planeTex, float2 sourcePixel, float2 sourceSize, float2 texSize)
        {
            float2 uv = (clamp(sourcePixel, float2(0.0, 0.0), sourceSize - 1.0) + 0.5) / texSize;
            return ICDctRound(tex2D(planeTex, uv).r);
        }

        float SamplePlaneBilinear(sampler2D planeTex, float2 sourcePixel, float2 sourceSize, float2 texSize)
        {
            float2 p0 = floor(sourcePixel);
            float2 f = saturate(sourcePixel - p0);
            float v00 = SamplePlanePoint(planeTex, p0, sourceSize, texSize);
            float v10 = SamplePlanePoint(planeTex, p0 + float2(1.0, 0.0), sourceSize, texSize);
            float v01 = SamplePlanePoint(planeTex, p0 + float2(0.0, 1.0), sourceSize, texSize);
            float v11 = SamplePlanePoint(planeTex, p0 + float2(1.0, 1.0), sourceSize, texSize);
            float vx0 = ICDctAdd(v00, ICDctMul(ICDctSub(v10, v00), f.x));
            float vx1 = ICDctAdd(v01, ICDctMul(ICDctSub(v11, v01), f.x));
            return ICDctAdd(vx0, ICDctMul(ICDctSub(vx1, vx0), f.y));
        }

        float3 UnityOutputRgb(float3 encodedRgb)
        {
            float3 rounded = saturate(ICDctRound3(encodedRgb));
            // 符号化するRGB値をsRGB領域として扱うかを示すフラグ
            bool encodedIsSrgb = _EncodeSrgb > 0.5;
            #if defined(UNITY_COLORSPACE_GAMMA)
            // 出力RGB値をsRGB領域へ変換するかを示すフラグ
            bool targetIsSrgb = _OutputSrgb > 0.5;
            #else
            bool targetIsSrgb = false;
            #endif
            if (encodedIsSrgb == targetIsSrgb)
            {
                return rounded;
            }
            return targetIsSrgb ? LinearToGammaSpace(rounded) : GammaToLinearSpace(rounded);
        }

        float4 PackPlane(Varyings i) : SV_Target
        {
            float2 outputSize = max(_OutputSize.xy, 1.0.xx);
            float2 outputPixel = min(floor(i.pos.xy), max(outputSize - 1.0, 0.0));
            float2 sourceSize = max(_PackedPlaneSourceSize.xy, 1.0.xx);
            float2 texSize = max(_PackedPlaneTexSize.xy, 1.0.xx);
            float2 sourcePixel = (outputPixel + 0.5) * sourceSize / outputSize - 0.5;
            float value = _PackedPlaneHalfSize > 0.5
                ? SamplePlaneBilinear(_MainTex, sourcePixel, sourceSize, texSize)
                : SamplePlanePoint(_MainTex, sourcePixel, sourceSize, texSize);
            return value.xxxx;
        }

        float4 ComposePacked(Varyings i) : SV_Target
        {
            float2 outputSize = max(_OutputSize.xy, 1.0.xx);
            float2 pixel = min(floor(i.pos.xy), max(outputSize - 1.0, 0.0));
            float4 packed = tex2D(_MainTex, (pixel + 0.5) / outputSize);
            float y = ICDctRound(packed.r);
            // alphaプレーンの処理方法を選択するモード
            float a = _AlphaMode < 0.5 ? 1.0 : ICDctRound(packed.g);
            float cb = ICDctSub(ICDctRound(packed.b), 0.5);
            float cr = ICDctSub(ICDctRound(packed.a), 0.5);
            float3 rgb;
            rgb.r = ICDctMadd(1.402, cr, y);
            rgb.g = ICDctSub(ICDctSub(y, ICDctMul(0.344136, cb)), ICDctMul(0.714136, cr));
            rgb.b = ICDctMadd(1.772, cb, y);
            return float4(UnityOutputRgb(rgb), saturate(a));
        }

        ENDCG

        Pass
        {
            Name "PackY"
            ColorMask R
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment PackPlane
            ENDCG
        }

        Pass
        {
            Name "PackA"
            ColorMask G
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment PackPlane
            ENDCG
        }

        Pass
        {
            Name "PackCb"
            ColorMask B
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment PackPlane
            ENDCG
        }

        Pass
        {
            Name "PackCr"
            ColorMask A
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment PackPlane
            ENDCG
        }

        Pass
        {
            Name "ComposePacked"
            ColorMask RGBA
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment ComposePacked
            ENDCG
        }
    }
}
