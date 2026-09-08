// Huffman符号化したブロックストリームを、固定64バイトのブロックページへビット単位で格納するシェーダー
// 構成: 3つのPassで構成し、UnityCG.cginc、DcthHalfRound.cgincを読み込んで共通の頂点処理・補助関数を利用する
// フラグメント関数はInitializeStateFrag、OutputGroupFrag、AdvanceStateFragに分かれている
Shader "HDAssets/IC/DCTH/DCTHHuffmanBlockPageEncodeBlit"
{
    // bit packing stage: Huffman化したblock streamを固定64-byte block pageへ書く
    // Texture寸法とchannel packingはDCTH形式の一部なので、shader変更時はC#側も同時に更新する

    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("RLE Symbol Texture", 2D) = "black" {}
        // 前段ブロックpageを保持する入力Texture
        _PreviousBlockPageTex ("Previous Block Page Texture", 2D) = "black" {}
        // ブロックページ符号化の継続状態を保持するTexture
        _BlockPageStateTex ("Block Page State Texture", 2D) = "black" {}
        // DC差分を保持する入力Texture
        _DcDeltaTex ("DC Delta Texture", 2D) = "black" {}
        // DC Huffman符号を保持する入力Texture
        _DcHuffmanCodesTex ("DC Huffman Codes", 2D) = "black" {}
        // AC Huffman符号を保持する入力Texture
        _AcHuffmanCodesTex ("AC Huffman Codes", 2D) = "black" {}
        // xyにシンボルTextureの幅・高さを保持する入力値
        _SymbolSize ("Symbol Size", Vector) = (512, 512, 0, 0)
        // xyに横・縦方向のブロック数を保持する入力値
        _BlockCount ("Block Count", Vector) = (64, 64, 0, 0)
        // xyにpage Textureの幅・高さ、zに1 pageのバイト数を保持する入力値
        _PageSize ("Page Size", Vector) = (4096, 64, 64, 0)
        // xyにDC Huffman tableの幅・高さを保持する入力値
        _DcTableSize ("DC Table Size", Vector) = (16, 1, 0, 0)
        // xyにAC Huffman tableの幅・高さを保持する入力値
        _AcTableSize ("AC Table Size", Vector) = (256, 1, 0, 0)
        // 符号化ブロックpage行グループ番号
        _EncodeBlockPageRowGroup ("Encode Block Page Row Group", Float) = -1
        // 符号化ブロックpage1グループあたりの行数
        _EncodeBlockPageRowsPerGroup ("Encode Block Page Rows Per Group", Float) = 1
        // 今回符号化する係数スロットのグループ番号
        _EncodeSlotGroup ("Encode Slot Group", Float) = 0
        // 符号化1グループあたりのスロット数
        _EncodeSlotsPerGroup ("Encode Slots Per Group", Float) = 8
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
        // 前段ブロックpageを保持する入力Texture
        sampler2D _PreviousBlockPageTex;
        // ブロックページ符号化の継続状態を保持するTexture
        sampler2D _BlockPageStateTex;
        // DC差分を保持する入力Texture
        sampler2D _DcDeltaTex;
        // DC Huffman符号を保持する入力Texture
        sampler2D _DcHuffmanCodesTex;
        // AC Huffman符号を保持する入力Texture
        sampler2D _AcHuffmanCodesTex;
        // xyにシンボルTextureの幅・高さを保持する入力値
        float4 _SymbolSize;
        // xyに横・縦方向のブロック数を保持する入力値
        float4 _BlockCount;
        // xyにpage Textureの幅・高さ、zに1 pageのバイト数を保持する入力値
        float4 _PageSize;
        // xyにDC Huffman tableの幅・高さを保持する入力値
        float4 _DcTableSize;
        // xyにAC Huffman tableの幅・高さを保持する入力値
        float4 _AcTableSize;
        // 符号化ブロックpage行グループ番号
        float _EncodeBlockPageRowGroup;
        // 符号化ブロックpage1グループあたりの行数
        float _EncodeBlockPageRowsPerGroup;
        // 今回符号化する係数スロットのグループ番号
        float _EncodeSlotGroup;
        // 符号化1グループあたりのスロット数
        float _EncodeSlotsPerGroup;

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

        float Pow2Small(int exponentValue)
        {
            if (exponentValue <= 0) return 1.0;
            if (exponentValue == 1) return 2.0;
            if (exponentValue == 2) return 4.0;
            if (exponentValue == 3) return 8.0;
            if (exponentValue == 4) return 16.0;
            if (exponentValue == 5) return 32.0;
            if (exponentValue == 6) return 64.0;
            if (exponentValue == 7) return 128.0;
            if (exponentValue == 8) return 256.0;
            if (exponentValue == 9) return 512.0;
            if (exponentValue == 10) return 1024.0;
            if (exponentValue == 11) return 2048.0;
            if (exponentValue == 12) return 4096.0;
            if (exponentValue == 13) return 8192.0;
            if (exponentValue == 14) return 16384.0;
            return 32768.0;
        }

        float UnpackSigned16(float4 packed)
        {
            float lo = DecodeByte(packed.r);
            float hi = DecodeByte(packed.g);
            return lo + hi * 256.0 - 32768.0;
        }

        float CoeffSizeCategory(float coeff, float maxCategory)
        {
            float roundedCoeff = coeff < 0.0 ? ceil(coeff - 0.5) : floor(coeff + 0.5);
            float magnitude = abs(roundedCoeff);
            if (magnitude < 0.5) return 0.0;
            if (magnitude < 2.0) return 1.0;
            if (magnitude < 4.0) return 2.0;
            if (magnitude < 8.0) return 3.0;
            if (magnitude < 16.0) return 4.0;
            if (magnitude < 32.0) return 5.0;
            if (magnitude < 64.0) return 6.0;
            if (magnitude < 128.0) return 7.0;
            if (magnitude < 256.0) return 8.0;
            if (magnitude < 512.0) return 9.0;
            if (magnitude < 1024.0) return 10.0;
            return maxCategory;
        }

        float4 ReadRleSymbol(float2 block, int slotIndex)
        {
            float2 symbolSize = max(_SymbolSize.xy, 1.0.xx);
            // slotIndexは0～63の非負整数なので、正確な2進小数0.125の乗算で/8を置き換える
            float slotRow = floor((float)slotIndex * 0.125);
            float2 slot = float2(fmod((float)slotIndex, 8.0), slotRow);
            float2 uv = (block * 8.0 + slot + 0.5) / symbolSize;
            // page生成で読むsymbol・DC・Huffman tableはPoint filter・mipなしのdata Textureである
            // 可変loop内の暗黙gradientを避け、同じtexelをLOD 0から決定的に読む
            return tex2Dlod(_MainTex, float4(uv, 0.0, 0.0));
        }

        float ReadDcDelta(float2 block)
        {
            float2 blockCount = max(_BlockCount.xy, 1.0.xx);
            float2 uv = (block + 0.5) / blockCount;
            return UnpackSigned16(tex2Dlod(_DcDeltaTex, float4(uv, 0.0, 0.0)));
        }

        float2 ReadDcCode(float category)
        {
            float2 uv = (float2(category, 0.0) + 0.5) / max(_DcTableSize.xy, 1.0.xx);
            float4 packed = tex2Dlod(_DcHuffmanCodesTex, float4(uv, 0.0, 0.0));
            float code = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
            float length = DecodeByte(packed.a);
            return float2(code, length);
        }

        float2 ReadAcCode(float bin)
        {
            float2 uv = (float2(bin, 0.0) + 0.5) / max(_AcTableSize.xy, 1.0.xx);
            float4 packed = tex2Dlod(_AcHuffmanCodesTex, float4(uv, 0.0, 0.0));
            float code = DecodeByte(packed.r) + DecodeByte(packed.g) * 256.0 + DecodeByte(packed.b) * 65536.0;
            float length = DecodeByte(packed.a);
            return float2(code, length);
        }

        bool HasFlag(float4 symbol, float bitValue)
        {
            float flags = DecodeByte(symbol.a);
            return fmod(floor(flags / bitValue), 2.0) >= 1.0;
        }

        float ReadMsbBit(float value, float length, float bitIndex)
        {
            float roundedBitIndex = floor(bitIndex + 0.5);
            if (roundedBitIndex < -0.5 || roundedBitIndex >= length)
            {
                return 0.0;
            }

            float shift = length - 1.0 - roundedBitIndex;
            float shifted = floor(value / Pow2Small((int)floor(shift + 0.5)));
            return shifted - floor(shifted / 2.0) * 2.0;
        }

        float EncodeAmplitude(float coeff, float category)
        {
            int categoryInt = (int)floor(category + 0.5);
            float maxSigned = Pow2Small(categoryInt) - 1.0;
            float stableCoeff = coeff < 0.0 ? ceil(coeff - 0.5) : floor(coeff + 0.5);
            float roundedCoeff = clamp(stableCoeff, -maxSigned, maxSigned);
            float maxValue = Pow2Small(categoryInt) - 1.0;
            return roundedCoeff >= 0.0 ? roundedCoeff : maxValue + roundedCoeff;
        }

        float SymbolBitLength(float4 symbol, float2 block)
        {
            if (!HasFlag(symbol, 1.0))
            {
                return 0.0;
            }

            if (HasFlag(symbol, 2.0))
            {
                float dcCategory = CoeffSizeCategory(ReadDcDelta(block), 11.0);
                float2 dcCode = ReadDcCode(dcCategory);
                return dcCode.y + dcCategory;
            }

            if (HasFlag(symbol, 4.0))
            {
                return ReadAcCode(0.0).y;
            }

            float run = DecodeByte(symbol.b);
            float category = CoeffSizeCategory(UnpackSigned16(symbol), 10.0);
            if (category < 0.5)
            {
                return 0.0;
            }

            float zrlCount = floor(run / 16.0);
            float residualRun = run - zrlCount * 16.0;
            float2 zrlCode = ReadAcCode(240.0);
            float2 acCode = ReadAcCode(residualRun * 16.0 + category);
            return zrlCount * zrlCode.y + acCode.y + category;
        }

        float SymbolSequenceBit(float4 symbol, float2 block, float bitIndex)
        {
            float sequenceBit = 0.0;

            // symbol種別ごとのbit計算式は変えず、初期化済みの戻り値へ集約する
            // 複数の早期returnを未初期化経路と判断するShaderコンパイラ警告を防ぐための構造である
            if (HasFlag(symbol, 2.0))
            {
                float dcCategory = CoeffSizeCategory(ReadDcDelta(block), 11.0);
                float2 dcCode = ReadDcCode(dcCategory);
                if (bitIndex < dcCode.y)
                {
                    sequenceBit = ReadMsbBit(dcCode.x, dcCode.y, bitIndex);
                }
                else
                {
                    float amplitude = EncodeAmplitude(ReadDcDelta(block), dcCategory);
                    sequenceBit = ReadMsbBit(amplitude, dcCategory, bitIndex - dcCode.y);
                }
            }
            else if (HasFlag(symbol, 4.0))
            {
                float2 eobCode = ReadAcCode(0.0);
                sequenceBit = ReadMsbBit(eobCode.x, eobCode.y, bitIndex);
            }
            else
            {
                float coeff = UnpackSigned16(symbol);
                float run = DecodeByte(symbol.b);
                float category = CoeffSizeCategory(coeff, 10.0);
                float zrlCount = floor(run / 16.0);
                float residualRun = run - zrlCount * 16.0;
                float2 zrlCode = ReadAcCode(240.0);
                float zrlBits = zrlCount * zrlCode.y;
                if (bitIndex < zrlBits && zrlCode.y > 0.5)
                {
                    sequenceBit = ReadMsbBit(zrlCode.x, zrlCode.y, fmod(bitIndex, zrlCode.y));
                }
                else
                {
                    float2 acCode = ReadAcCode(residualRun * 16.0 + category);
                    float localBit = bitIndex - zrlBits;
                    if (localBit < acCode.y)
                    {
                        sequenceBit = ReadMsbBit(acCode.x, acCode.y, localBit);
                    }
                    else
                    {
                        float amplitude = EncodeAmplitude(coeff, category);
                        sequenceBit = ReadMsbBit(amplitude, category, localBit - acCode.y);
                    }
                }
            }

            return sequenceBit;
        }

        bool IsTargetBlockRow(float blockY)
        {
            float rowGroup = floor(_EncodeBlockPageRowGroup + 0.5);
            if (rowGroup < 0.0)
            {
                return true;
            }

            float rowsPerGroup = max(floor(_EncodeBlockPageRowsPerGroup + 0.5), 1.0);
            return abs(floor(blockY / rowsPerGroup) - rowGroup) <= 0.5;
        }

        float4 OutputGroupFrag(Varyings i) : SV_Target
        {
            float pageBytes = max(_PageSize.z, 1.0);
            float2 pixel = floor(i.pos.xy);
            float2 block = float2(floor(pixel.x / pageBytes), pixel.y);
            float byteInPage = pixel.x - block.x * pageBytes;

            if (block.x < 0.0 || block.y < 0.0 || block.x >= _BlockCount.x || block.y >= _BlockCount.y)
            {
                return 0.0.xxxx;
            }

            float rowGroup = floor(_EncodeBlockPageRowGroup + 0.5);
            if (rowGroup >= 0.0)
            {
                // 64-symbol bit packingは重いためAndroid/Questではblock row分割する
                // 対象外rowは直前ping-pong pageをcopyし、byte配置と容量を変えない
                float rowsPerGroup = max(floor(_EncodeBlockPageRowsPerGroup + 0.5), 1.0);
                float blockRowGroup = floor(block.y / rowsPerGroup);
                if (abs(blockRowGroup - rowGroup) > 0.5)
                {
                    return tex2D(_PreviousBlockPageTex, (pixel + 0.5) / max(_PageSize.xy, 1.0.xx));
                }
            }

            float bitStart = byteInPage * 8.0;
            int slotGroup = max((int)floor(_EncodeSlotGroup + 0.5), 0);
            int slotsPerGroup = clamp((int)floor(_EncodeSlotsPerGroup + 0.5), 1, 8);
            int slotStart = slotGroup * slotsPerGroup;
            int slotEnd = min(slotStart + slotsPerGroup, 64);
            float2 stateUv = (block + 0.5) / max(_BlockCount.xy, 1.0.xx);
            float localBitOffset = DecodeUInt24(tex2D(_BlockPageStateTex, stateUv));
            float byteValue = slotGroup <= 0
                ? 0.0
                : DecodeByte(tex2D(_PreviousBlockPageTex, (pixel + 0.5) / max(_PageSize.xy, 1.0.xx)).r);

            [loop]
            for (int localSlot = 0; localSlot < 8; localSlot++)
            {
                int slotIndex = slotStart + localSlot;
                if (slotIndex >= slotEnd)
                {
                    break;
                }

                float4 symbol = ReadRleSymbol(block, slotIndex);
                float symbolBits = SymbolBitLength(symbol, block);

                // このpassは出力byteごとに最大64 symbolをloopする。内側8bit loopのunrollで
                // Android fragment shader codeが肥大化しないよう、Mobileではrolledのままにする
                [loop]
                for (int bitInByte = 0; bitInByte < 8; bitInByte++)
                {
                    float symbolBitIndex = bitStart + bitInByte - localBitOffset;
                    if (symbolBitIndex >= 0.0 && symbolBitIndex < symbolBits)
                    {
                        byteValue += SymbolSequenceBit(symbol, block, symbolBitIndex) * Pow2Small(7 - bitInByte);
                    }
                }

                localBitOffset += symbolBits;
            }

            if (bitStart >= localBitOffset)
            {
                return float4(ICByteToUNorm(byteValue), 0.0, 0.0, 1.0);
            }

            // byteValueはbitから構築した整数byte。RTへ微小な小数を残さないよう書込前に丸める
            return float4(ICByteToUNorm(byteValue), 0.0, 0.0, 1.0);
        }

        float4 InitializeStateFrag(Varyings i) : SV_Target
        {
            float2 block = floor(i.pos.xy);
            if (block.x < 0.0 || block.y < 0.0 || block.x >= _BlockCount.x || block.y >= _BlockCount.y)
            {
                return 0.0.xxxx;
            }

            float2 stateUv = (block + 0.5) / max(_BlockCount.xy, 1.0.xx);
            if (!IsTargetBlockRow(block.y))
            {
                return tex2D(_BlockPageStateTex, stateUv);
            }

            return EncodeUInt24(0.0);
        }

        float4 AdvanceStateFrag(Varyings i) : SV_Target
        {
            float2 block = floor(i.pos.xy);
            if (block.x < 0.0 || block.y < 0.0 || block.x >= _BlockCount.x || block.y >= _BlockCount.y)
            {
                return 0.0.xxxx;
            }

            float2 stateUv = (block + 0.5) / max(_BlockCount.xy, 1.0.xx);
            if (!IsTargetBlockRow(block.y))
            {
                return tex2D(_BlockPageStateTex, stateUv);
            }

            int slotGroup = max((int)floor(_EncodeSlotGroup + 0.5), 0);
            int slotsPerGroup = clamp((int)floor(_EncodeSlotsPerGroup + 0.5), 1, 8);
            int slotStart = slotGroup * slotsPerGroup;
            int slotEnd = min(slotStart + slotsPerGroup, 64);
            float bitOffset = DecodeUInt24(tex2D(_BlockPageStateTex, stateUv));
            [loop]
            for (int localSlot = 0; localSlot < 8; localSlot++)
            {
                int slotIndex = slotStart + localSlot;
                if (slotIndex >= slotEnd)
                {
                    break;
                }

                bitOffset += SymbolBitLength(ReadRleSymbol(block, slotIndex), block);
            }

            return EncodeUInt24(bitOffset);
        }
        ENDCG

        Pass
        {
            Name "InitializeBlockPageState"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment InitializeStateFrag
            ENDCG
        }

        Pass
        {
            Name "EncodeBlockPageGroup"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment OutputGroupFrag
            ENDCG
        }

        Pass
        {
            Name "AdvanceBlockPageState"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment AdvanceStateFrag
            ENDCG
        }
    }
}
