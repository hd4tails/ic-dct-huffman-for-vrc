using UnityEngine;

namespace HDAssets.ImageCompress.Dcth
{
    // DCTH圧縮で使用する量子化tableと符号化tableのpresetを定義する
    // 入力planeと量子化presetの選択値、および64要素の各tableをまとめて提供する
    public enum DcthEncodingTableMode
    {
        // 固定tableを使用するmode
        Fixed = 0,
        // 実行時に生成したtableを使用するmode
        Generated = 1,
        // 外部から同期されたtableを使用するmode
        CustomSynced = 2
    }

    public enum DcthColorPlane
    {
        // 輝度plane
        Y = 0,
        // alpha plane
        A = 1,
        // 色差plane
        CbCr = 2
    }

    public enum DcthInputPlane
    {
        // 輝度plane
        Y = 0,
        // alpha plane
        A = 1,
        // 青色差plane
        Cb = 2,
        // 赤色差plane
        Cr = 3
    }

    public enum DcthQuantPreset
    {
        // 標準の量子化preset
        Normal = 0,
        // 写真向けの量子化preset
        Photo = 1,
        // イラスト向けの量子化preset
        Illustration = 2,
        // 滑らかな画像向けの量子化preset
        Smooth = 3,
        // 外部Q98と同じ量子化preset
        ExternalQ98 = 4,
        // 輝度の滑らかさを重視する量子化preset
        SmoothYPlus = 5
    }

    public static class DcthEncodingTableProfile
    {
        // table 長さを示す定数
        public const int TableLength = 64;
        // format versionを示す定数
        public const int FormatVersion = 1;

        // JPEG の luminance table を基準にした写真向けの量子化 table
        public static readonly int[] BaseYQuant =
        {
            16, 11, 10, 16, 24, 40, 51, 61,
            12, 12, 14, 19, 26, 58, 60, 55,
            14, 13, 16, 24, 40, 57, 69, 56,
            14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68,109,103, 77,
            24, 35, 55, 64, 81,104,113, 92,
            49, 64, 78, 87,103,121,120,101,
            72, 92, 95, 98,112,100,103, 99
        };

        // ベタ塗りや線画の輪郭を残しやすくするイラスト向けの Y table
        public static readonly int[] IllustrationYQuant =
        {
            10,  8,  8, 10, 14, 18, 24, 32,
             8,  8,  9, 11, 14, 22, 28, 34,
             8,  9, 10, 13, 18, 26, 34, 40,
            10, 11, 13, 18, 24, 34, 44, 52,
            14, 14, 18, 24, 34, 46, 58, 68,
            18, 22, 26, 34, 46, 62, 76, 86,
            24, 28, 34, 44, 58, 76, 92,104,
            32, 34, 40, 52, 68, 86,104,112
        };

        // Photo Y 量子化を外部へ公開する値
        public static readonly int[] PhotoYQuant =
        {
            14, 10, 10, 14, 22, 42, 56, 70,
            10, 11, 13, 18, 28, 62, 68, 66,
            12, 12, 15, 24, 44, 66, 78, 72,
            14, 16, 22, 32, 58, 94, 94, 78,
            18, 24, 40, 62, 78,122,122, 96,
            26, 38, 62, 76, 98,126,138,116,
            56, 76, 96,108,126,150,148,126,
            84,112,118,122,140,126,130,126
        };

        // Smooth Y 量子化を外部へ公開する値
        public static readonly int[] SmoothYQuant =
        {
             9,  7,  7,  9, 12, 16, 21, 28,
             7,  7,  8, 10, 12, 19, 25, 30,
             7,  8,  9, 11, 16, 23, 30, 36,
             9, 10, 11, 16, 21, 30, 39, 46,
            12, 12, 16, 21, 30, 40, 51, 60,
            16, 19, 23, 30, 40, 55, 67, 76,
            21, 25, 30, 39, 51, 67, 81, 92,
            28, 30, 36, 46, 60, 76, 92,100
        };

        // Smooth Y Plus Y 量子化を外部へ公開する値
        public static readonly int[] SmoothYPlusYQuant =
        {
             6,  5,  5,  6,  8, 10, 14, 18,
             5,  5,  5,  7,  8, 12, 16, 20,
             5,  5,  6,  7, 10, 15, 20, 23,
             6,  7,  7, 10, 14, 20, 25, 30,
             8,  8, 10, 14, 20, 26, 33, 39,
            10, 12, 15, 20, 26, 36, 44, 49,
            14, 16, 20, 25, 33, 44, 53, 60,
            18, 20, 23, 30, 39, 49, 60, 65
        };

        // sky_gradient_512aa.jpg の JPEG DQT を Q98 で再現するための table
        // 既存の quality scale を通すため、JPEG 内の実 quant 値を Q98 scale から逆算している
        public static readonly int[] ExternalQ98YQuant =
        {
             50,  25,  25,  50,  75, 125, 150, 175,
             25,  25,  50,  50,  75, 175, 175, 175,
             50,  50,  50,  75, 125, 175, 200, 175,
             50,  50,  75,  75, 150, 250, 250, 175,
             50,  75, 100, 175, 200, 325, 300, 225,
             75, 100, 175, 200, 250, 300, 350, 275,
            150, 200, 225, 250, 300, 375, 350, 300,
            225, 275, 275, 300, 325, 300, 300, 300
        };

        // JPEG の chrominance table を基準にした写真向けの CbCr 量子化 table
        public static readonly int[] BaseCbCrQuant =
        {
            17, 18, 24, 47, 99, 99, 99, 99,
            18, 21, 26, 66, 99, 99, 99, 99,
            24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99
        };

        // イラストでは色境界も残したいため、chroma の高周波を写真向けより残す
        public static readonly int[] IllustrationCbCrQuant =
        {
            12, 12, 14, 18, 26, 34, 44, 54,
            12, 13, 15, 22, 30, 40, 52, 62,
            14, 15, 20, 30, 42, 54, 66, 78,
            18, 22, 30, 42, 56, 70, 84, 96,
            26, 30, 42, 56, 72, 88,104,116,
            34, 40, 54, 70, 88,108,124,136,
            44, 52, 66, 84,104,124,144,156,
            54, 62, 78, 96,116,136,156,168
        };

        // Photo Cb Cr 量子化を外部へ公開する値
        public static readonly int[] PhotoCbCrQuant =
        {
            17, 18, 26, 54,112,128,140,148,
            18, 22, 30, 76,128,140,148,156,
            26, 30, 66,128,140,148,156,164,
            54, 76,128,140,148,156,164,172,
            112,128,140,148,156,164,172,180,
            128,140,148,156,164,172,180,188,
            140,148,156,164,172,180,188,196,
            148,156,164,172,180,188,196,204
        };

        // Smooth Cb Cr 量子化を外部へ公開する値
        public static readonly int[] SmoothCbCrQuant =
        {
            14, 14, 17, 28, 52, 66, 78, 88,
            14, 16, 19, 36, 66, 78, 88, 96,
            17, 19, 30, 58, 78, 88, 96,104,
            28, 36, 58, 78, 88, 96,104,112,
            52, 66, 78, 88, 98,108,118,128,
            66, 78, 88, 96,108,120,132,144,
            78, 88, 96,104,118,132,148,160,
            88, 96,104,112,128,144,160,172
        };

        // Smooth Y Plus Cb Cr 量子化を外部へ公開する値
        public static readonly int[] SmoothYPlusCbCrQuant =
        {
            11, 11, 14, 22, 42, 53, 62, 70,
            11, 13, 15, 29, 53, 62, 70, 77,
            14, 15, 24, 46, 62, 70, 77, 83,
            22, 29, 46, 62, 70, 77, 83, 90,
            42, 53, 62, 70, 78, 86, 94,102,
            53, 62, 70, 77, 86, 96,106,115,
            62, 70, 77, 83, 94,106,118,128,
            70, 77, 83, 90,102,115,128,138
        };

        // External Q98 Cb Cr 量子化を外部へ公開する値
        public static readonly int[] ExternalQ98CbCrQuant =
        {
             50,  50,  75, 150, 300, 300, 300, 300,
             50,  75,  75, 200, 300, 300, 300, 300,
             75,  75, 175, 300, 300, 300, 300, 300,
            150, 200, 300, 300, 300, 300, 300, 300,
            300, 300, 300, 300, 300, 300, 300, 300,
            300, 300, 300, 300, 300, 300, 300, 300,
            300, 300, 300, 300, 300, 300, 300, 300,
            300, 300, 300, 300, 300, 300, 300, 300
        };

        // alpha は輪郭の破綻が目立ちやすいため、Y より少し強めに残す table にする
        public static readonly int[] BaseAQuant =
        {
            12, 10, 10, 12, 18, 28, 36, 44,
            10, 10, 12, 15, 20, 42, 44, 40,
            12, 11, 13, 18, 28, 40, 48, 40,
            12, 14, 17, 22, 36, 60, 56, 44,
            15, 17, 26, 40, 48, 70, 68, 52,
            18, 25, 38, 44, 56, 68, 74, 60,
            34, 44, 54, 60, 68, 80, 78, 66,
            48, 60, 62, 64, 74, 66, 68, 66
        };

        // Illustration A 量子化を外部へ公開する値
        public static readonly int[] IllustrationAQuant =
        {
             8,  7,  7,  8, 11, 14, 18, 24,
             7,  7,  8,  9, 11, 16, 22, 28,
             7,  8,  8, 10, 14, 20, 28, 34,
             8,  9, 10, 14, 18, 28, 38, 46,
            11, 11, 14, 18, 28, 40, 52, 62,
            14, 16, 20, 28, 40, 56, 70, 82,
            18, 22, 28, 38, 52, 70, 88,100,
            24, 28, 34, 46, 62, 82,100,112
        };

        // Photo A 量子化を外部へ公開する値
        public static readonly int[] PhotoAQuant =
        {
            12, 10, 10, 12, 18, 30, 40, 50,
            10, 10, 12, 16, 22, 48, 54, 52,
            12, 11, 14, 20, 32, 48, 58, 52,
            12, 15, 18, 24, 42, 70, 68, 56,
            16, 18, 30, 48, 58, 86, 84, 68,
            20, 28, 44, 56, 70, 88, 96, 80,
            40, 56, 70, 80, 88,104,102, 88,
            60, 80, 84, 88,100, 90, 92, 90
        };

        // Smooth A 量子化を外部へ公開する値
        public static readonly int[] SmoothAQuant =
        {
             7,  6,  6,  7, 10, 13, 16, 22,
             6,  6,  7,  8, 10, 15, 20, 25,
             6,  7,  7,  9, 13, 18, 25, 30,
             7,  8,  9, 13, 16, 25, 34, 41,
            10, 10, 13, 16, 25, 36, 47, 56,
            13, 15, 18, 25, 36, 50, 63, 74,
            16, 20, 25, 34, 47, 63, 79, 90,
            22, 25, 30, 41, 56, 74, 90,100
        };

        // External Q98 A 量子化を外部へ公開する値
        public static readonly int[] ExternalQ98AQuant =
        {
             50,  25,  25,  50,  75, 125, 150, 175,
             25,  25,  50,  50,  75, 175, 175, 175,
             50,  50,  50,  75, 125, 175, 200, 175,
             50,  50,  75,  75, 150, 250, 250, 175,
             50,  75, 100, 175, 200, 325, 300, 225,
             75, 100, 175, 200, 250, 300, 350, 275,
            150, 200, 225, 250, 300, 375, 350, 300,
            225, 275, 275, 300, 325, 300, 300, 300
        };

        // 使用できる量子化プリセット数を返す
        public static int PresetCount
        {
            get { return 6; }
        }

        // 量子化プリセットを対応範囲へ丸める
        public static DcthQuantPreset ClampPreset(DcthQuantPreset preset)
        {
            if (preset == DcthQuantPreset.Photo)
            {
                return DcthQuantPreset.Photo;
            }

            if (preset == DcthQuantPreset.Illustration)
            {
                return DcthQuantPreset.Illustration;
            }

            if (preset == DcthQuantPreset.Smooth)
            {
                return DcthQuantPreset.Smooth;
            }

            if (preset == DcthQuantPreset.ExternalQ98)
            {
                return DcthQuantPreset.ExternalQ98;
            }

            if (preset == DcthQuantPreset.SmoothYPlus)
            {
                return DcthQuantPreset.SmoothYPlus;
            }

            return DcthQuantPreset.Normal;
        }

        // 量子化プリセットの表示名を返す
        public static string PresetLabel(DcthQuantPreset preset)
        {
            DcthQuantPreset clamped = ClampPreset(preset);
            if (clamped == DcthQuantPreset.Photo)
            {
                return "Photo";
            }

            if (clamped == DcthQuantPreset.Illustration)
            {
                return "Illustration";
            }

            if (clamped == DcthQuantPreset.Smooth)
            {
                return "Smooth";
            }

            if (clamped == DcthQuantPreset.SmoothYPlus)
            {
                return "Smooth Y+";
            }

            return clamped == DcthQuantPreset.ExternalQ98 ? "External Q98" : "Normal";
        }

        // 量子化プリセットを保存・比較用の識別値へ変換する
        public static int PresetId(DcthQuantPreset preset)
        {
            return (int)ClampPreset(preset);
        }

        // 入力planeを対応範囲へ丸める
        public static DcthInputPlane ClampInputPlane(DcthInputPlane plane)
        {
            if (plane == DcthInputPlane.A)
            {
                return DcthInputPlane.A;
            }

            if (plane == DcthInputPlane.Cb)
            {
                return DcthInputPlane.Cb;
            }

            if (plane == DcthInputPlane.Cr)
            {
                return DcthInputPlane.Cr;
            }

            return DcthInputPlane.Y;
        }

        // 入力planeの表示名を返す
        public static string InputPlaneLabel(DcthInputPlane plane)
        {
            DcthInputPlane clamped = ClampInputPlane(plane);
            if (clamped == DcthInputPlane.A)
            {
                return "A";
            }

            if (clamped == DcthInputPlane.Cb)
            {
                return "Cb";
            }

            return clamped == DcthInputPlane.Cr ? "Cr" : "Y";
        }

        // 入力planeを保存・比較用の識別値へ変換する
        public static int InputPlaneId(DcthInputPlane plane)
        {
            return (int)ClampInputPlane(plane);
        }

        // 入力planeに対応する量子化tableのplaneを返す
        public static DcthColorPlane QuantPlaneForInput(DcthInputPlane plane)
        {
            DcthInputPlane clamped = ClampInputPlane(plane);
            if (clamped == DcthInputPlane.A)
            {
                return DcthColorPlane.A;
            }

            if (clamped == DcthInputPlane.Cb || clamped == DcthInputPlane.Cr)
            {
                return DcthColorPlane.CbCr;
            }

            return DcthColorPlane.Y;
        }

        // 色planeとプリセットに対応する基準量子化tableを返す
        public static int[] GetBaseQuantTable(DcthColorPlane plane, DcthQuantPreset preset)
        {
            DcthQuantPreset clamped = ClampPreset(preset);
            if (plane == DcthColorPlane.A)
            {
                if (clamped == DcthQuantPreset.Photo)
                {
                    return PhotoAQuant;
                }

                if (clamped == DcthQuantPreset.Illustration)
                {
                    return IllustrationAQuant;
                }

                if (clamped == DcthQuantPreset.Smooth)
                {
                    return SmoothAQuant;
                }

                if (clamped == DcthQuantPreset.SmoothYPlus)
                {
                    return SmoothAQuant;
                }

                return clamped == DcthQuantPreset.ExternalQ98 ? ExternalQ98AQuant : BaseAQuant;
            }

            if (plane == DcthColorPlane.CbCr)
            {
                if (clamped == DcthQuantPreset.Photo)
                {
                    return PhotoCbCrQuant;
                }

                if (clamped == DcthQuantPreset.Illustration)
                {
                    return IllustrationCbCrQuant;
                }

                if (clamped == DcthQuantPreset.Smooth)
                {
                    return SmoothCbCrQuant;
                }

                if (clamped == DcthQuantPreset.SmoothYPlus)
                {
                    return SmoothYPlusCbCrQuant;
                }

                return clamped == DcthQuantPreset.ExternalQ98 ? ExternalQ98CbCrQuant : BaseCbCrQuant;
            }

            if (clamped == DcthQuantPreset.Photo)
            {
                return PhotoYQuant;
            }

            if (clamped == DcthQuantPreset.Illustration)
            {
                return IllustrationYQuant;
            }

            if (clamped == DcthQuantPreset.Smooth)
            {
                return SmoothYQuant;
            }

            if (clamped == DcthQuantPreset.SmoothYPlus)
            {
                return SmoothYPlusYQuant;
            }

            return clamped == DcthQuantPreset.ExternalQ98 ? ExternalQ98YQuant : BaseYQuant;
        }

        // 品質値に合わせて基準量子化値を1から255の範囲へ調整する
        public static int ScaleQuantValue(int baseValue, int quality)
        {
            // JPEG に近い quality scale で、基準 table を 1-255 に丸める
            int q = Mathf.Clamp(quality, 1, 100);
            int scale = q < 50 ? 5000 / q : 200 - q * 2;
            int value = (baseValue * scale + 50) / 100;
            return Mathf.Clamp(value, 1, 255);
        }

        // mode、preset、品質値、量子化tableから整合性確認用hashを計算する
        public static int ComputeTableHash(
            int quality,
            DcthEncodingTableMode mode,
            DcthQuantPreset preset,
            int[] y,
            int[] a,
            int[] cbcr)
        {
            // encoder/decoder 間で table 一致を検証するため、version/mode/preset/quality/table 内容を hash 化する
            uint hash = 2166136261u;
            hash = HashByte(hash, FormatVersion);
            hash = HashByte(hash, (int)mode);
            hash = HashByte(hash, PresetId(preset));
            hash = HashByte(hash, quality);
            hash = HashArray(hash, y);
            hash = HashArray(hash, a);
            hash = HashArray(hash, cbcr);
            return unchecked((int)hash);
        }

        // table hashを8桁の16進文字列へ変換する
        public static string HashToHex(int hash)
        {
            return unchecked((uint)hash).ToString("X8");
        }

        // 整数配列を順にhashへ取り込む
        private static uint HashArray(uint hash, int[] values)
        {
            if (values == null)
            {
                return HashByte(hash, 0);
            }

            hash = HashByte(hash, values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                hash = HashByte(hash, values[i]);
            }

            return hash;
        }

        // 整数値の4byteを順にhashへ取り込む
        private static uint HashByte(uint hash, int value)
        {
            hash ^= (byte)(value & 0xff);
            hash *= 16777619u;
            hash ^= (byte)((value >> 8) & 0xff);
            hash *= 16777619u;
            hash ^= (byte)((value >> 16) & 0xff);
            hash *= 16777619u;
            hash ^= (byte)((value >> 24) & 0xff);
            hash *= 16777619u;
            return hash;
        }
    }
}
