using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon;

namespace HDAssets.ImageCompress.Dcth
{
    // VRChat/UdonSharp上でDCT、量子化、RLE、Huffmanを使った画像圧縮と展開を行うライブラリ
    // 処理の流れ:
    // 1. 圧縮要求では入力と容量上限を検証し、GPUでDCT・量子化・RLE symbol生成を段階実行する
    // 2. 頻度tableからHuffman codeを構築し、headerとY・A・Cb・Crのsegmentをbyte[]へ格納する
    // 3. 展開要求ではheaderと各segmentを検証し、Huffman復号・逆量子化・IDCTで画像を復元する
    // 4. GPU処理とreadbackの完了後に結果を確定し、呼び出し元へ完了または失敗イベントを送る
    // 処理中のRTはGPU完了確認後に一括解放し、成功時は最終出力だけを保持する
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class DcthCodecLibrary : UdonSharpBehaviour
    {
        // ASTC/BC7と同列に扱う、この独自DCT/Huffman形式の公開名称
        // byte[]先頭にも同じASCII 4文字をlittle-endianで格納する
        public const string CodecName = "DCTH";
        // 無効な処理handleを示す値
        public const int InvalidHandleId = -1;
        // 対象handleの状態を判定できないことを示す値
        public const int TaskStateUnknown = 0;
        // 処理開始待ち状態を示す値
        public const int TaskStatePending = 1;
        // 処理実行中状態を示す値
        public const int TaskStateRunning = 2;
        // 処理成功状態を示す値
        public const int TaskStateSucceeded = 3;
        // 処理失敗状態を示す値
        public const int TaskStateFailed = 4;
        // 処理キャンセル状態を示す値
        public const int TaskStateCancelled = 5;
        // 進行段階を判定できないことを示す値
        public const int ProgressStageUnknown = 0;
        // 処理開始待ち段階を示す値
        public const int ProgressStagePending = 1;
        // 入力とGPUリソースの準備段階を示す値
        public const int ProgressStagePreparing = 2;
        // 圧縮または展開の実行段階を示す値
        public const int ProgressStageProcessing = 3;
        // GPU出力のreadback段階を示す値
        public const int ProgressStageReadback = 4;
        // 結果確定と後処理段階を示す値
        public const int ProgressStageFinalizing = 5;
        // 全処理完了段階を示す値
        public const int ProgressStageComplete = 6;
        // 圧縮成功時に要求元へ送るイベント名
        public const string CompressionSucceededEventName = "_HandleDcthCompressComplete";
        // 圧縮失敗時に要求元へ送るイベント名
        public const string CompressionFailedEventName = "_HandleDcthCompressFailed";
        // 画質を下げて圧縮を再試行するときに要求元へ送るイベント名
        public const string CompressionRetryingEventName = "_HandleDcthCompressRetrying";
        // 展開成功時に要求元へ送るイベント名
        public const string ExpansionSucceededEventName = "_HandleDcthExpandComplete";
        // 展開失敗時に要求元へ送るイベント名
        public const string ExpansionFailedEventName = "_HandleDcthExpandFailed";
        // 圧縮warmup成功時に要求元へ送るイベント名
        public const string CompressionWarmupSucceededEventName = "_HandleDcthCompressWarmupComplete";
        // 圧縮warmup失敗時に要求元へ送るイベント名
        public const string CompressionWarmupFailedEventName = "_HandleDcthCompressWarmupFailed";
        // 展開warmup成功時に要求元へ送るイベント名
        public const string ExpansionWarmupSucceededEventName = "_HandleDcthExpandWarmupComplete";
        // 展開warmup失敗時に要求元へ送るイベント名
        public const string ExpansionWarmupFailedEventName = "_HandleDcthExpandWarmupFailed";
        // 既定の入力Textureサイズ
        public const int DefaultTextureSize = 512;
        // DCTブロックの一辺のpixel数
        private const int DctBlockSize = 8;
        // 1 chunkに含めるDCTブロック数
        private const int ChunkSize = 4;
        // warmupに使用するTextureの一辺のpixel数
        private const int WarmupTextureSize = DctBlockSize * ChunkSize;
        // 1 DCT blockへ割り当てる固定payload幅。shaderのpage座標、overflow判定、readback容量と直結するため、
        // 値を変える場合は全shaderのaddressingとencode/decode双方の上限を同時に更新する
        private const int HuffmanBlockPageBytes = 64;
        // DC係数の頻度tableに使用するbin数
        private const int DcFrequencyBinCount = 16;
        // AC係数の頻度tableに使用するbin数
        private const int AcFrequencyBinCount = 256;
        // 輝度planeの識別値
        private const int PlaneY = 0;
        // alpha planeの識別値
        private const int PlaneA = 1;
        // Cb planeの識別値
        private const int PlaneCb = 2;
        // Cr planeの識別値
        private const int PlaneCr = 3;
        // metadata readbackの識別値
        private const int HuffmanReadbackKindMetadata = 0;
        // 作業data readbackの識別値
        private const int HuffmanReadbackKindWork = 1;
        // DCT係数 readbackの識別値
        private const int HuffmanReadbackKindCoefficients = 2;
        // RLE symbol readbackの識別値
        private const int HuffmanReadbackKindRleSymbols = 3;
        // AC頻度table readbackの識別値
        private const int HuffmanReadbackKindAcFrequency = 4;
        // DC差分 readbackの識別値
        private const int HuffmanReadbackKindDcDelta = 5;
        // DC scan結果 readbackの識別値
        private const int HuffmanReadbackKindDcScan = 6;
        // DC頻度table readbackの識別値
        private const int HuffmanReadbackKindDcFrequency = 7;
        // DC Huffman code readbackの識別値
        private const int HuffmanReadbackKindDcCodes = 8;
        // AC Huffman code readbackの識別値
        private const int HuffmanReadbackKindAcCodes = 9;
        // ブロックごとのbit数 readbackの識別値
        private const int HuffmanReadbackKindBlockBits = 10;
        // ブロック有効判定 readbackの識別値
        private const int HuffmanReadbackKindBlockValid = 11;
        // chunk有効判定 readbackの識別値
        private const int HuffmanReadbackKindChunkValid = 12;
        // 圧縮payload readbackの識別値
        private const int HuffmanReadbackKindPayload = 13;
        // byte[]へ出力するのはmetadataとpayloadだけ。削除済みの診断用readbackを
        // 製品処理で巡回しないよう、schedulerは1 planeあたり2 slotに限定する
        private const int HuffmanReadbackKindsPerPlane = 2;
        // Huffman readback step 数を示す定数
        private const int HuffmanReadbackStepCount = 8;
        // GPU 符号化 段階 Gap種別s Per planeを示す定数
        private const int GpuEncodeStageGapKindsPerPlane = 8;
        // GPU 符号化 段階 Gap step 数を示す定数
        private const int GpuEncodeStageGapStepCount = 32;
        // GPU 復号 段階 Gap種別s Per planeを示す定数
        private const int GpuDecodeStageGapKindsPerPlane = 6;
        // GPU 復号 表示 Gap step 数を示す定数
        private const int GpuDecodeDisplayGapStepCount = GpuDecodeStageGapKindsPerPlane * 4 + 2;
        // GPU 段階 Gap step 数を示す定数
        private const int GpuStageGapStepCount = GpuEncodeStageGapStepCount + GpuDecodeDisplayGapStepCount;
        // Huffman ブロック Overflow bit数を示す定数
        private const int HuffmanBlockOverflowBits = HuffmanBlockPageBytes * 8;
        // 復号 bit offset local ブロック 数を示す定数
        private const int DecodeBitOffsetLocalBlockCount = 16;
        // Huffman chunk内のbit offsetは、block Nがblock N-1の終端bitに依存する
        // Androidの1 frameへ依存scanを集中させないため、現在は1 passにつき1 local blockを処理し、
        // 総時間よりworst frameの安定を優先する
        private const int DecodeBitOffsetLocalBlocksPerGroup = 1;
        // bit offset local group 数を復号する
        private const int DecodeBitOffsetLocalGroupCount = (DecodeBitOffsetLocalBlockCount + DecodeBitOffsetLocalBlocksPerGroup - 1) / DecodeBitOffsetLocalBlocksPerGroup;
        // Android実測でchunk-block-valid passが50 FPSを下回ったため、1 eventを2 chunk rowへ戻す
        // 変更するのは処理範囲だけで、RLE decode前に書かれるoffset値とpayload順は変えない
        private const int DecodeBitOffsetChunkRowsPerGroup = 2;
        // RLE復元は各pixelがHuffman streamを再走査する。Android実測で8 slot/groupは
        // pass 1/2が30 FPSまで低下したため、50 FPS未満を避ける4 slot/groupへ戻す
        private const int DecodeRleSlotCount = 64;
        // 復号 RLE slot Per groupを示す定数
        private const int DecodeRleSlotsPerGroup = 4;
        // group幅を64の約数以外へ調整しても係数slotを落とさないよう、group数は切り上げる
        private const int DecodeRleSlotGroupCount = (DecodeRleSlotCount + DecodeRleSlotsPerGroup - 1) / DecodeRleSlotsPerGroup;
        // RLE slot passは広い領域を触るためchunk row帯でも分割し、対象外rowは直前のping-pong Textureからコピーする
        // byte[]容量、Huffman payload、係数値は変えずscheduleだけを分割する
        private const int DecodeRleChunkRowsPerGroup = 4;
        // DC scanは全blockのDC値を使うprefix sumで、Android decode中でも特に重い
        // 前段Textureで出力を埋めてから対象row帯だけを更新し、計算結果を維持したままframe分割する
        private const int DecodeDcScanBlockRowsPerGroup = 4;
        // IDCTはblock row帯で分割する一方、各eventでは8 local row/columnをまとめて処理する
        // Android実測でDecodeSymbolsが60 FPSを維持したため、block rowを16行までまとめる
        private const int DecodeIdctLocalRowsPerGroup = 8;
        // 復号 IDCT local group 数を示す定数
        private const int DecodeIdctLocalGroupCount = 8 / DecodeIdctLocalRowsPerGroup;
        // 全係数と全pixelはcompose前に同じshader式で計算され、変更点は処理範囲だけ
        private const int DecodeIdctBlockRowsPerGroup = 16;
        // Decodeでは5 Textureをuploadする。ApplyはGPU転送点なので、byte copyと同じframeに重ならないよう
        // LoadRawTextureDataとApplyを別eventへ分ける
        private const int DecodeUploadTextureCount = 5;
        // 復号 upload step 数を示す定数
        private const int DecodeUploadStepCount = DecodeUploadTextureCount * 2;
        // UdonSharpのbyte[] copyはmain threadで動く。DCTH配置とpayload byteを変えず、
        // Android/Questでstallしにくい単位へ分割する
        // byte copyはUdon loopではなくSystem.Array.Copyを使用する。2KBごとのframe待機が圧縮packと
        // 展開restoreの所要時間を支配しないよう、1stepを8KBまでまとめる
        private const int RuntimeByteCopyChunkBytes = 8192;
        // Runtime chunk offset Build chunk Per stepを示す定数
        private const int RuntimeChunkOffsetBuildChunksPerStep = 64;
        // source alphaは正規化済みRGBA32 byte[]を使ってCPU側で検査する
        // Androidで大きなUdon loopを1 frameに走らせず、opaque画像ではA plane自体を省略する
        // 最新Android実測では8K pixel/stepが30 FPSまで低下したため、30 FPS未満への揺れを避ける
        // alpha判定内容は変えず、1eventの走査量だけを4K pixelへ戻す
        private const int SourceAlphaScanPixelsPerStep = 4096;
        // Huffman payload gatherは各出力byteごとにchunk mipと最大16 block pageを探索する重いpass
        // Android実測で50 FPSを下回ったため4出力rowへ分割し、payload順と容量を維持する
        private const int EncodePayloadRowsPerGroup = 4;
        // block page encodeもAndroid実測で50 FPSを下回ったため、2 slot単位へ分割する
        // 固定page Textureをping-pongし、byte配置は変えない
        private const int EncodeBlockPageSlotCount = 64;
        // 符号化 ブロック page slot Per groupを示す定数
        private const int EncodeBlockPageSlotsPerGroup = 2;
        // ブロック page slot group 数を符号化する
        private const int EncodeBlockPageSlotGroupCount = (EncodeBlockPageSlotCount + EncodeBlockPageSlotsPerGroup - 1) / EncodeBlockPageSlotsPerGroup;
        // 符号化 ブロック page 行 Per groupを示す定数
        private const int EncodeBlockPageRowsPerGroup = 8;
        // AC frequency row passも50 FPS未満だったため、block columnを1列ずつ処理する
        private const int EncodeAcFrequencyBlockColumnsPerGroup = 1;
        // 符号化 AC 頻度 slot 数を示す定数
        private const int EncodeAcFrequencySlotCount = 63;
        // 符号化 AC 頻度 slot Per groupを示す定数
        private const int EncodeAcFrequencySlotsPerGroup = 32;
        // AC 頻度 slot group 数を符号化する
        private const int EncodeAcFrequencySlotGroupCount = (EncodeAcFrequencySlotCount + EncodeAcFrequencySlotsPerGroup - 1) / EncodeAcFrequencySlotsPerGroup;
        // 符号化 DC 頻度 Items Per groupを示す定数
        private const int EncodeDcFrequencyItemsPerGroup = 32;
        // timing診断はtest UI専用。毎stepのTime/string生成を避けるため既定では無効
        private const bool EnableTimingDiagnosticsDefault = false;
        // 通常の進捗文字列はUI専用でdecode中に何度も再構築される
        // 失敗・完了・inspection表示だけを残し、Android hot pathの文字列生成を避ける
        // decode結果はoutputReady後にだけ参照される。開始時の全画面clearはlate join時のhitchになるため行わず、
        // Stop/reset時だけ明示clearする
        private const int MinQuality = 1;
        // 最大 画質を示す定数
        private const int MaxQuality = 100;
        // 最大 量子化 presetを示す定数
        private const int MaxQuantPreset = 5;
        // 最大 Inspection Stop stepを示す定数
        private const int MaxInspectionStopStep = 22;
        // warmup mode Allを示す定数
        private const int WarmupModeAll = 0;
        // warmup mode 圧縮を示す定数
        private const int WarmupModeCompression = 1;
        // warmup mode 展開を示す定数
        private const int WarmupModeExpansion = 2;
        // warmup Shader step 数を示す定数
        private const int WarmupShaderStepCount = 63;
        // warmup 描画 Texture step 数を示す定数
        private const int WarmupRenderTextureStepCount = 63;
        // Material保持用Rendererに設定するMaterial数
        private const int RuntimeMaterialCount = 25;
        // warmup 圧縮 Core step 数を示す定数
        private const int WarmupCompressionCoreStepCount = 18;
        // warmup 展開 Core step 数を示す定数
        private const int WarmupExpansionCoreStepCount = 6;
        // Gamma 符号化 LUT 幅を示す定数
        private const int GammaEncodeLutWidth = 64;
        // Gamma 符号化 LUT 高さを示す定数
        private const int GammaEncodeLutHeight = 64;
        // Gamma 符号化 LUT サイズを示す定数
        private const int GammaEncodeLutSize = GammaEncodeLutWidth * GammaEncodeLutHeight;
        // 最小 Image byte列を示す定数
        private const int MinImageBytes = 1024;
        // 画質 再試行 Reason Noneを示す定数
        private const int QualityRetryReasonNone = 0;
        // 画質 再試行 Reason ブロック 容量を示す定数
        private const int QualityRetryReasonBlockCapacity = 1;
        // 画質 再試行 Reason Total 容量を示す定数
        private const int QualityRetryReasonTotalCapacity = 2;
        // 容量 事前計算 Sample ブロック 上限を示す定数
        private const int CapacityPrepassSampleBlockLimit = 128;
        // 容量 事前計算 Stats Samples Per groupを示す定数
        private const int CapacityPrepassStatsSamplesPerGroup = 32;
        // 1行だけの極小RTはMobile GPUのreadback経路で不安定になり得るため、16 pixelのARGB32として確保する
        // 容量判定に使うY/A/Cb/Crは先頭行の4 pixelだけで、圧縮payloadの形式は変わらない
        private const int CapacityPrepassStatsTextureWidth = 4;
        // 容量 事前計算 Stats Texture 高さを示す定数
        private const int CapacityPrepassStatsTextureHeight = 4;
        // 容量 事前計算 readback Drain Frame 数を示す定数
        private const int CapacityPrepassReadbackDrainFrameCount = 3;
        // full plane encode直後のreadback fenceがAndroid GPU queueを同期停止させないよう、
        // Request前に後続frameを明示的に確保する。PC/Mobileで同じ経路を使用する
        // pass間の投入間隔でqueue蓄積を抑えたうえで、metadata Request前にも短い空frameを置く
        // 長いdrainはoperation timeoutを消費するだけで、queueが詰まった後の回復には使わない
        private const int HuffmanReadbackDrainFrameCount = 12;
        // GPU fence marker サイズを示す定数
        private const int GpuFenceMarkerSize = 4;
        // Huffman Single 値 Texture サイズを示す定数
        private const int HuffmanSingleValueTextureSize = 4;
        // CPU AC Huffman phase Idleを示す定数
        private const int CpuAcHuffmanPhaseIdle = 0;
        // CPU AC Huffman phase request readbackを示す定数
        private const int CpuAcHuffmanPhaseRequestReadback = 1;
        // CPU AC Huffman phase Wait readbackを示す定数
        private const int CpuAcHuffmanPhaseWaitReadback = 2;
        // CPU AC Huffman phase Initialize Leavesを示す定数
        private const int CpuAcHuffmanPhaseInitializeLeaves = 3;
        // CPU AC Huffman phase Merge Nodesを示す定数
        private const int CpuAcHuffmanPhaseMergeNodes = 4;
        // CPU AC Huffman phase Write Lengthsを示す定数
        private const int CpuAcHuffmanPhaseWriteLengths = 5;
        // CPU AC Huffman phase Load Textureを示す定数
        private const int CpuAcHuffmanPhaseLoadTexture = 6;
        // CPU AC Huffman phase Apply Textureを示す定数
        private const int CpuAcHuffmanPhaseApplyTexture = 7;
        // CPU AC Huffman phase Blit Textureを示す定数
        private const int CpuAcHuffmanPhaseBlitTexture = 8;
        // CPU AC Huffman phase Release Textureを示す定数
        private const int CpuAcHuffmanPhaseReleaseTexture = 9;
        // 最大256 symbolの固定長workspaceだけを処理する。GPU treeを避けた安全性を維持しつつ、
        // CPU-only eventの1frame待機を減らすため4分割程度で完了させる
        private const int CpuAcHuffmanInitializeSymbolsPerStep = 128;
        // CPU AC Huffman Merges Per stepを示す定数
        private const int CpuAcHuffmanMergesPerStep = 64;
        // CPU AC Huffman Lengths Per stepを示す定数
        private const int CpuAcHuffmanLengthsPerStep = 128;
        // CPU AC Huffman Node 容量を示す定数
        private const int CpuAcHuffmanNodeCapacity = AcFrequencyBinCount * 2;
        // Android実機のsubmit FPSを基準に、50 FPS未満だったpassは少数ずつ投入し、
        // 59-60 FPSを維持したpassだけを大きくまとめる。出力形式は変えずGPU queueの幅だけを調整する
        private const int LowFpsGpuFenceBatchLimit = 8;
        // Single GPU 即時 batch 上限を示す定数
        private const int SingleGpuImmediateBatchLimit = 1;
        // 最新Android実測で8 callback/frameでも全passが59-60 FPSを維持した処理用
        // shaderの処理範囲やpayload形式は変えず、12 callbackずつ投入して不要なframe待機を減らす
        private const int MeasuredGpuFenceBatchLimit = 48;
        // Measured GPU 即時 batch 上限を示す定数
        private const int MeasuredGpuImmediateBatchLimit = 12;
        // length制限は1 callback内で2 Blitし、8 callback/frameでは16 Blitが同じframeへ集中して30 FPSまで低下した
        // 容量推定末尾も12 Blitを一度に積むと次のfenceで20 FPSまで低下したため、この2区間だけ4 callbackへ抑える
        private const int LowQueueGpuFenceBatchLimit = 16;
        // Low queue GPU 即時 batch 上限を示す定数
        private const int LowQueueGpuImmediateBatchLimit = 4;
        // Normal GPU fence batch 上限を示す定数
        private const int NormalGpuFenceBatchLimit = 64;
        // Normal GPU 即時 batch 上限を示す定数
        private const int NormalGpuImmediateBatchLimit = 16;
        // High FPS GPU fence batch 上限を示す定数
        private const int HighFpsGpuFenceBatchLimit = 256;
        // High FPS GPU 即時 batch 上限を示す定数
        private const int HighFpsGpuImmediateBatchLimit = 64;
        // decode RLEは処理範囲を4 slot/groupへ分割した状態で8 callback/frameでも59-60 FPSだった
        // 過去に低下した8 slot/groupへは戻さず、安全な処理範囲のまま投入数だけ12へ増やす
        private const int DecodeRleGpuFenceBatchLimit = 48;
        // 復号 RLE GPU 即時 batch 上限を示す定数
        private const int DecodeRleGpuImmediateBatchLimit = 12;
        // Android実機でGPU段階を切り分ける間だけ有効にする一時診断
        // 計測中のlog I/Oを避け、固定長配列へ記録して処理終了時にまとめて出力する
        private const int GpuDiagnosticBlitCapacity = 4096;
        // GPU 診断 段階 容量を示す定数
        private const int GpuDiagnosticStageCapacity = 4096;
        // GPU 診断 log chunk Charactersを示す定数
        private const int GpuDiagnosticLogChunkCharacters = 3000;
        // 容量 事前計算 Prepare Sub 段階 数を示す定数
        private const int CapacityPrepassPrepareSubStageCount = 13;
        // 容量 事前計算 候補 Setup step 数を示す定数
        private const int CapacityPrepassCandidateSetupStepCount = 4;
        // 要求Qualityの評価後、予測したQualityを最大2回サンプルで再評価する
        // 未量子化DCTを再利用するため、本圧縮の失敗retryより小さい負荷で候補を絞り込める
        private const int CapacityPrepassCandidateAttemptCount = 3;
        // 圧縮 進捗 入力 readback Endを示す定数
        private const float CompressionProgressSourceReadbackEnd = 0.03f;
        // 圧縮 進捗 入力 alpha Endを示す定数
        private const float CompressionProgressSourceAlphaEnd = 0.045f;
        // 圧縮 進捗 入力 upload Endを示す定数
        private const float CompressionProgressSourceUploadEnd = 0.05f;
        // 圧縮 進捗 容量 Prepare Startを示す定数
        private const float CompressionProgressCapacityPrepareStart = 0.05f;
        // 圧縮 進捗 容量 Prepare Endを示す定数
        private const float CompressionProgressCapacityPrepareEnd = 0.18f;
        // 圧縮 進捗 容量 候補 Endを示す定数
        private const float CompressionProgressCapacityCandidateEnd = 0.48f;
        // 圧縮 進捗 Full 符号化 Startを示す定数
        private const float CompressionProgressFullEncodeStart = 0.5f;
        // 圧縮 進捗 Full 符号化 Endを示す定数
        private const float CompressionProgressFullEncodeEnd = 0.78f;
        // 圧縮 進捗 readback Startを示す定数
        private const float CompressionProgressReadbackStart = 0.78f;
        // 圧縮 進捗 readback Endを示す定数
        private const float CompressionProgressReadbackEnd = 0.9f;
        // 圧縮 進捗 Finalize Startを示す定数
        private const float CompressionProgressFinalizeStart = 0.9f;
        // 圧縮 進捗 Pack Startを示す定数
        private const float CompressionProgressPackStart = 0.91f;
        // 圧縮 進捗 Finalize Endを示す定数
        private const float CompressionProgressFinalizeEnd = 0.99f;
        // 容量 事前計算 符号化 Pass Horizontalを示す定数
        private const int CapacityPrepassEncodePassHorizontal = 0;
        // 容量 事前計算 符号化 Pass Vertical Unquantizedを示す定数
        private const int CapacityPrepassEncodePassVerticalUnquantized = 0;
        // 容量 事前計算 符号化 Pass Previous DC Unquantizedを示す定数
        private const int CapacityPrepassEncodePassPreviousDcUnquantized = 0;
        // 容量 事前計算 符号化 Pass Quantizeを示す定数
        private const int CapacityPrepassEncodePassQuantize = 1;
        // 容量 事前計算 符号化 Pass DC 差分 Quantizeを示す定数
        private const int CapacityPrepassEncodePassDcDeltaQuantize = 2;
        // 容量 事前計算 Stats Passを示す定数
        private const int CapacityPrepassStatsPass = 3;
        // Huffman table Pass 上限 Lengthsを示す定数
        private const int HuffmanTablePassLimitLengths = 1;
        // Huffman table Pass Build Canonical codeを示す定数
        private const int HuffmanTablePassBuildCanonicalCodes = 2;
        // Huffman table Pass Build Raw 長さ Histogramを示す定数
        private const int HuffmanTablePassBuildRawLengthHistogram = 3;
        // Huffman table Pass 上限 長さ Histogramを示す定数
        private const int HuffmanTablePassLimitLengthHistogram = 4;
        // Huffman table Pass Merge Single 値を示す定数
        private const int HuffmanTablePassMergeSingleValue = 5;
        // Huffman 長さ 上限 数を示す定数
        private const int HuffmanLengthLimitCount = 16;
        // Huffman Raw Histogram Lengths Per groupを示す定数
        private const int HuffmanRawHistogramLengthsPerGroup = 16;
        // Huffman Raw Histogram group 数を示す定数
        private const int HuffmanRawHistogramGroupCount = 256 / HuffmanRawHistogramLengthsPerGroup;
        // Coeff To RLE Pass Build Prefix Baseを示す定数
        private const int CoeffToRlePassBuildPrefixBase = 0;
        // Coeff To RLE Pass scan Prefixを示す定数
        private const int CoeffToRlePassScanPrefix = 1;
        // Coeff To RLE Pass Build symbolを示す定数
        private const int CoeffToRlePassBuildSymbols = 2;
        // Coeff To RLE Prefix scan Pass 数を示す定数
        private const int CoeffToRlePrefixScanPassCount = 6;
        // Coeff To RLE step 数を示す定数
        private const int CoeffToRleStepCount = CoeffToRlePrefixScanPassCount + 2;
        // RLE To symbol Fixed Pass Build Prefix Baseを示す定数
        private const int RleToSymbolFixedPassBuildPrefixBase = 0;
        // RLE To symbol Fixed Pass scan Prefixを示す定数
        private const int RleToSymbolFixedPassScanPrefix = 1;
        // RLE To symbol Fixed Pass 復号 symbolを示す定数
        private const int RleToSymbolFixedPassDecodeSymbols = 2;
        // RLE To symbol Fixed Prefix scan Pass 数を示す定数
        private const int RleToSymbolFixedPrefixScanPassCount = 6;
        // RLE To symbol Fixed step 数を示す定数
        private const int RleToSymbolFixedStepCount = RleToSymbolFixedPrefixScanPassCount + 2;
        // Huffman ブロック page Pass Initialize 状態を示す定数
        private const int HuffmanBlockPagePassInitializeState = 0;
        // Huffman ブロック page Pass 出力 groupを示す定数
        private const int HuffmanBlockPagePassOutputGroup = 1;
        // Huffman ブロック page Pass Advance 状態を示す定数
        private const int HuffmanBlockPagePassAdvanceState = 2;
        // Huffman 復号 RLE Pass Initialize 状態を示す定数
        private const int HuffmanDecodeRlePassInitializeState = 0;
        // Huffman 復号 RLE Pass 出力 groupを示す定数
        private const int HuffmanDecodeRlePassOutputGroup = 1;
        // Huffman 復号 RLE Pass Advance 状態を示す定数
        private const int HuffmanDecodeRlePassAdvanceState = 2;
        // Huffman 復号 RLE Pass Build AC 長さ Summaryを示す定数
        private const int HuffmanDecodeRlePassBuildAcLengthSummary = 3;
        // Huffman 復号 RLE Pass Build AC symbol tableを示す定数
        private const int HuffmanDecodeRlePassBuildAcSymbolTable = 4;
        // canonical lookup tableは256 symbol固定。BuildAcSymbolTable passのrolled loop上限が16のため、
        // ここを16より大きくするとgroup後半のsymbolが未構築のまま次のgroupへ飛び、展開結果だけが壊れる
        private const int HuffmanDecodeAcSymbolsPerGroup = 16;
        // Huffman 復号 AC symbol group 数を処理する
        private const int HuffmanDecodeAcSymbolGroupCount = (AcFrequencyBinCount + HuffmanDecodeAcSymbolsPerGroup - 1) / HuffmanDecodeAcSymbolsPerGroup;
        // Huffman 復号 Lookup step 数を示す定数
        private const int HuffmanDecodeLookupStepCount = 2 + HuffmanDecodeAcSymbolGroupCount;
        // 復号 upload And Lookup step 数を示す定数
        private const int DecodeUploadAndLookupStepCount = DecodeUploadStepCount + HuffmanDecodeLookupStepCount;
        // Huffman bit 数 slot 数を示す定数
        private const int HuffmanBitCountSlotCount = 63;
        // bit-count accumulateは実測59-60 FPSだったため8 slotずつまとめる
        private const int HuffmanBitCountSlotsPerGroup = 8;
        // Huffman bit 数 slot group 数を処理する
        private const int HuffmanBitCountSlotGroupCount = (HuffmanBitCountSlotCount + HuffmanBitCountSlotsPerGroup - 1) / HuffmanBitCountSlotsPerGroup;
        // Huffman bit 数 Pass Initializeを示す定数
        private const int HuffmanBitCountPassInitialize = 0;
        // Huffman bit 数 Pass Accumulateを示す定数
        private const int HuffmanBitCountPassAccumulate = 1;
        // Huffman bit 数 step 数を示す定数
        private const int HuffmanBitCountStepCount = 1 + HuffmanBitCountSlotGroupCount;
        // length制限・canonical code生成はAndroid実測で50 FPSを下回ったため8 symbolずつ処理する
        private const int HuffmanTableAcSymbolsPerGroup = 8;
        // Huffman table AC symbol group 数を処理する
        private const int HuffmanTableAcSymbolGroupCount = (AcFrequencyBinCount + HuffmanTableAcSymbolsPerGroup - 1) / HuffmanTableAcSymbolsPerGroup;
        // AC raw lengthはCPUで1本のtreeをframe分割して作る。既存UIの進捗幅を維持するため、
        // 後続substageの開始位置には従来と同じ256 slotを予約する
        private const int HuffmanTableAcRawSymbolGroupCount = AcFrequencyBinCount;
        // length制限histogramのsingle-value passは4x4 RTの先頭1 pixelだけを使い、軽量Blitでtableへ統合する
        private const int HuffmanTableDcRawHistogramBaseSubStage = 1;
        // Huffman table DC Limited Histogram Base Sub 段階を示す定数
        private const int HuffmanTableDcLimitedHistogramBaseSubStage = HuffmanTableDcRawHistogramBaseSubStage + HuffmanRawHistogramGroupCount;
        // Huffman table DC Assign Lengths Sub 段階を示す定数
        private const int HuffmanTableDcAssignLengthsSubStage = HuffmanTableDcLimitedHistogramBaseSubStage + HuffmanLengthLimitCount;
        // Huffman table DC Canonical Sub 段階を示す定数
        private const int HuffmanTableDcCanonicalSubStage = HuffmanTableDcAssignLengthsSubStage + 1;
        // Huffman table AC Raw Base Sub 段階を示す定数
        private const int HuffmanTableAcRawBaseSubStage = HuffmanTableDcCanonicalSubStage + 1;
        // Huffman table AC Raw Histogram Base Sub 段階を示す定数
        private const int HuffmanTableAcRawHistogramBaseSubStage = HuffmanTableAcRawBaseSubStage + HuffmanTableAcRawSymbolGroupCount;
        // Huffman table AC Limited Histogram Base Sub 段階を示す定数
        private const int HuffmanTableAcLimitedHistogramBaseSubStage = HuffmanTableAcRawHistogramBaseSubStage + HuffmanRawHistogramGroupCount;
        // Huffman table AC 上限 Base Sub 段階を示す定数
        private const int HuffmanTableAcLimitBaseSubStage = HuffmanTableAcLimitedHistogramBaseSubStage + HuffmanLengthLimitCount;
        // Huffman table AC Canonical Base Sub 段階を示す定数
        private const int HuffmanTableAcCanonicalBaseSubStage = HuffmanTableAcLimitBaseSubStage + HuffmanTableAcSymbolGroupCount;
        // Huffman bit 数 Base Sub 段階を示す定数
        private const int HuffmanBitCountBaseSubStage = HuffmanTableAcCanonicalBaseSubStage + HuffmanTableAcSymbolGroupCount;
        // Huffman Overflow Base Sub 段階を示す定数
        private const int HuffmanOverflowBaseSubStage = HuffmanBitCountBaseSubStage + HuffmanBitCountStepCount;
        // 容量 事前計算 AC 頻度 Base Sub 段階を示す定数
        private const int CapacityPrepassAcFrequencyBaseSubStage = 9;
        // 容量 事前計算 AC 頻度 Total Sub 段階を示す定数
        private const int CapacityPrepassAcFrequencyTotalSubStage = CapacityPrepassAcFrequencyBaseSubStage + EncodeAcFrequencySlotGroupCount;
        // 容量 事前計算 DC 差分 Sub 段階を示す定数
        private const int CapacityPrepassDcDeltaSubStage = CapacityPrepassAcFrequencyTotalSubStage + 1;
        // 容量 事前計算 DC 頻度 行 Sub 段階を示す定数
        private const int CapacityPrepassDcFrequencyRowsSubStage = CapacityPrepassDcDeltaSubStage + 1;
        // 容量 事前計算 DC 頻度 Total Sub 段階を示す定数
        private const int CapacityPrepassDcFrequencyTotalSubStage = CapacityPrepassDcFrequencyRowsSubStage + 1;
        // 容量 事前計算 DC Raw Sub 段階を示す定数
        private const int CapacityPrepassDcRawSubStage = CapacityPrepassDcFrequencyTotalSubStage + 1;
        // 容量 事前計算 DC Raw Histogram Base Sub 段階を示す定数
        private const int CapacityPrepassDcRawHistogramBaseSubStage = CapacityPrepassDcRawSubStage + 1;
        // 容量 事前計算 DC Limited Histogram Base Sub 段階を示す定数
        private const int CapacityPrepassDcLimitedHistogramBaseSubStage = CapacityPrepassDcRawHistogramBaseSubStage + HuffmanRawHistogramGroupCount;
        // 容量 事前計算 DC Assign Lengths Sub 段階を示す定数
        private const int CapacityPrepassDcAssignLengthsSubStage = CapacityPrepassDcLimitedHistogramBaseSubStage + HuffmanLengthLimitCount;
        // 容量 事前計算 AC Raw Base Sub 段階を示す定数
        private const int CapacityPrepassAcRawBaseSubStage = CapacityPrepassDcAssignLengthsSubStage + 1;
        // 容量 事前計算 AC Raw Histogram Base Sub 段階を示す定数
        private const int CapacityPrepassAcRawHistogramBaseSubStage = CapacityPrepassAcRawBaseSubStage + HuffmanTableAcRawSymbolGroupCount;
        // 容量 事前計算 AC Limited Histogram Base Sub 段階を示す定数
        private const int CapacityPrepassAcLimitedHistogramBaseSubStage = CapacityPrepassAcRawHistogramBaseSubStage + HuffmanRawHistogramGroupCount;
        // 容量 事前計算 AC 上限 Base Sub 段階を示す定数
        private const int CapacityPrepassAcLimitBaseSubStage = CapacityPrepassAcLimitedHistogramBaseSubStage + HuffmanLengthLimitCount;
        // 容量 事前計算 bit 数 Base Sub 段階を示す定数
        private const int CapacityPrepassBitCountBaseSubStage = CapacityPrepassAcLimitBaseSubStage + HuffmanTableAcSymbolGroupCount;
        // 容量 事前計算 Stats Base Sub 段階を示す定数
        private const int CapacityPrepassStatsBaseSubStage = CapacityPrepassBitCountBaseSubStage + HuffmanBitCountStepCount;
        // 総byte数は最大値ぎりぎりでなく97%を内部目標にし、予測誤差による再超過を抑える
        // hard上限はmaxImageBytesのままで、実測値が超えた場合は必ず再試行する
        private const int TotalBytePredictionTargetPercent = 97;
        // full encodeの再実行はMobileで特に高コストなため、総容量予測は境界値より10%保守的にする
        // hard上限とDCTH形式は変えず、最初の実測retryで収束しやすいQualityを選ぶための内部値
        private const int TotalQualityPredictionSafetyPercent = 110;
        // 実測full encodeが上限を超えた後は、同じ重い圧縮を2回以上繰り返さないことを優先する
        // prepassの初期Quality選択には適用せず、実測値を得たretryだけ余白を20%へ広げる
        private const int TotalQualityRetrySafetyPercent = 120;
        // block bit数と量子化強度は完全な比例関係ではないため、64-byte予測には5%の余裕を持たせる
        private const int BlockQualityPredictionSafetyPercent = 105;
        // 既存DCTH v1のMorton順は1軸256 chunk（8192px / 32px）までを前提にしている
        // 壊れたheaderから巨大RenderTextureを確保しないため、encode/decode双方で同じ上限を適用する
        // ただし圧縮側のHuffman block page幅はpadded画像幅の8倍になる。端末のmaxTextureSizeを超える
        // 寸法まで安全という意味ではないため、上限を広げる際はblock page配置の再設計が必要になる

        // 1辺の上限とは別に総pixel数を制限し、細長い画像を許可しつつ4K相当を超えるRT確保を防ぐ
        private const long MaxCodecPixelCount = 16777216L;
        // 最大 codec Texture サイズを示す定数
        private const int MaxCodecTextureSize = 8192;
        // 製品処理では常時有効。入力をRGBA32で一度readbackし、raw値をlinear Texture2Dへ再uploadすることで、
        // DCT前のsampler色空間変換をPC/Mobileで共通化する
        // DCTH byte[] header。全offsetはlittle-endian int32で格納する
        private const int CompressedByteMagic = 1213481796; // "DCTH"
        // 圧縮結果 byte versionを示す定数
        private const int CompressedByteVersion = 1;
        // 圧縮結果 byte segment Parts Per planeを示す定数
        private const int CompressedByteSegmentPartsPerPlane = 4;
        // 圧縮結果 byte segment 数を示す定数
        private const int CompressedByteSegmentCount = 16;
        // 圧縮結果 byte header byte列を示す定数
        private const int CompressedByteHeaderBytes = 128;
        // 圧縮結果 byte offset Magicを示す定数
        private const int CompressedByteOffsetMagic = 0;
        // 圧縮結果 byte offset versionを示す定数
        private const int CompressedByteOffsetVersion = 4;
        // 圧縮結果 byte offset header byte列を示す定数
        private const int CompressedByteOffsetHeaderBytes = 8;
        // 圧縮結果 byte offset Image IDを示す定数
        private const int CompressedByteOffsetImageId = 12;
        // 圧縮結果 byte offset 幅を示す定数
        private const int CompressedByteOffsetWidth = 16;
        // 圧縮結果 byte offset 高さを示す定数
        private const int CompressedByteOffsetHeight = 20;
        // 圧縮結果 byte offset Flagsを示す定数
        private const int CompressedByteOffsetFlags = 24;
        // 圧縮結果 byte offset 画質を示す定数
        private const int CompressedByteOffsetQuality = 28;
        // 圧縮結果 byte offset 量子化 presetを示す定数
        private const int CompressedByteOffsetQuantPreset = 32;
        // 圧縮結果 byte offset alpha modeを示す定数
        private const int CompressedByteOffsetAlphaMode = 36;
        // 圧縮結果 byte offset segment 数を示す定数
        private const int CompressedByteOffsetSegmentCount = 40;
        // 圧縮結果 byte offset Total byte列を示す定数
        private const int CompressedByteOffsetTotalBytes = 44;
        // 圧縮結果 byte offset segment Lengthsを示す定数
        private const int CompressedByteOffsetSegmentLengths = 48;
        // flag Has alphaを示す定数
        private const int FlagHasAlpha = 1;
        // flag Has 色を示す定数
        private const int FlagHasColor = 2;
        // flag Half サイズ Cb Crを示す定数
        private const int FlagHalfSizeCbCr = 4;
        // 以下2bitはDCT処理ではなく、受信側が画像ごとのASTC/BC7表示設定を復元するためのmetadata
        // Polaroid側がDCTH byte[]から直接読むため、削除すると既存画像の表示経路が変わる
        private const int FlagSkipOutputBlockCompression = 8;
        // flag sRGB 出力 ブロック 圧縮を示す定数
        private const int FlagSrgbOutputBlockCompression = 16;
        // bit未設定時はsRGB DCT、設定時はlinear DCTとして復元する
        private const int FlagLinearDctEncoding = 32;

        // Known 圧縮結果 Flagsを示す定数
        private const int KnownCompressedFlags = FlagHasAlpha | FlagHasColor | FlagHalfSizeCbCr
            | FlagSkipOutputBlockCompression | FlagSrgbOutputBlockCompression
            | FlagLinearDctEncoding | FlagLinearOutputTexture;
        // flag Linear 出力 Textureを示す定数
        private const int FlagLinearOutputTexture = 64;
        // Runtime 状態 byte列を示す定数
        private const int RuntimeStateBytes = 104;
        // Runtime 状態 圧縮結果 準備完了を示す定数
        private const int RuntimeStateCompressedReady = 0;
        // Runtime 状態 圧縮結果 失敗を示す定数
        private const int RuntimeStateCompressedFailed = 1;
        // Runtime 状態 Huffman 符号化 完了を示す定数
        private const int RuntimeStateHuffmanEncodeComplete = 4;
        // Runtime 状態 Huffman 符号化 失敗を示す定数
        private const int RuntimeStateHuffmanEncodeFailed = 5;
        // Runtime 状態 実行中を示す定数
        private const int RuntimeStateRunning = 6;
        // Runtime 状態 出力 準備完了を示す定数
        private const int RuntimeStateOutputReady = 8;
        // Runtime 状態 Huffman 復号 完了を示す定数
        private const int RuntimeStateHuffmanDecodeComplete = 9;
        // Runtime 状態 Huffman 復号 失敗を示す定数
        private const int RuntimeStateHuffmanDecodeFailed = 10;
        // Runtime 状態 Huffman readback 待機状態を示す定数
        private const int RuntimeStateHuffmanReadbackPending = 12;
        // Runtime 状態 入力 Normalization readback 待機状態を示す定数
        private const int RuntimeStateSourceNormalizationReadbackPending = 14;
        // Runtime 状態 Initializedを示す定数
        private const int RuntimeStateInitialized = 13;
        // Runtime 状態 再試行 圧縮 On 失敗を示す定数
        private const int RuntimeStateRetryCompressionOnFailure = 100;
        // Runtime 状態 Completed Image IDを示す定数
        private const int RuntimeStateCompletedImageId = 20;
        // Runtime 状態 Huffman readback stepを示す定数
        private const int RuntimeStateHuffmanReadbackStep = 32;
        // Runtime 状態 Huffman readback planeを示す定数
        private const int RuntimeStateHuffmanReadbackPlane = 36;
        // Runtime 状態 Huffman readback種別を示す定数
        private const int RuntimeStateHuffmanReadbackKind = 40;
        // Runtime 状態 量子化 画質を示す定数
        private const int RuntimeStateQuantQuality = 44;
        // Runtime 状態 量子化 presetを示す定数
        private const int RuntimeStateQuantPreset = 48;
        // Runtime 状態 Last Stage1 符号化 Msを示す定数
        private const int RuntimeStateLastStage1EncodeMs = 52;
        // Runtime 状態 Last Huffman 符号化 Msを示す定数
        private const int RuntimeStateLastHuffmanEncodeMs = 60;
        // Runtime 状態 Last Huffman 復号 Msを示す定数
        private const int RuntimeStateLastHuffmanDecodeMs = 64;
        // Runtime 状態 Latest local Image IDを示す定数
        private const int RuntimeStateLatestLocalImageId = 72;
        // Runtime 状態 GPU 段階を示す定数
        private const int RuntimeStateGpuStage = 76;
        // Runtime 状態 復号 planeを示す定数
        private const int RuntimeStateDecodePlane = 80;
        // Runtime 状態 待機状態 GPU 段階 Gap stepを示す定数
        private const int RuntimeStatePendingGpuStageGapStep = 84;
        // Runtime 状態 復号 段階を示す定数
        private const int RuntimeStateDecodeStage = 88;
        // Runtime 状態 復号 local indexを示す定数
        private const int RuntimeStateDecodeLocalIndex = 92;
        // Runtime 状態 復号 bit offset Pingを示す定数
        private const int RuntimeStateDecodeBitOffsetPing = 96;
        // Status byte列を示す定数
        private const int StatusBytes = 512;
        // Status byte offset 長さを示す定数
        private const int StatusByteOffsetLength = 0;
        // Status byte offset dataを示す定数
        private const int StatusByteOffsetData = 4;
        // Timing byte列を示す定数
        private const int TimingBytes = 512;
        // Timing offset Run Start Msを示す定数
        private const int TimingOffsetRunStartMs = 0;
        // Timing offset Run Total Msを示す定数
        private const int TimingOffsetRunTotalMs = 4;
        // Timing offset readback Start Msを示す定数
        private const int TimingOffsetReadbackStartMs = 8;
        // Timing offset readback Total Msを示す定数
        private const int TimingOffsetReadbackTotalMs = 12;
        // Timing offset readback Store Msを示す定数
        private const int TimingOffsetReadbackStoreMs = 16;
        // Timing offset Pack Msを示す定数
        private const int TimingOffsetPackMs = 20;
        // Timing offset Restore Msを示す定数
        private const int TimingOffsetRestoreMs = 24;
        // Timing offset 復号 upload Msを示す定数
        private const int TimingOffsetDecodeUploadMs = 28;
        // Timing offset Compose Msを示す定数
        private const int TimingOffsetComposeMs = 32;
        // Timing offset Huffman Prepare Msを示す定数
        private const int TimingOffsetHuffmanPrepareMs = 36;
        // Timing offset Huffman RLE Msを示す定数
        private const int TimingOffsetHuffmanRleMs = 40;
        // Timing offset Huffman AC 頻度 Msを示す定数
        private const int TimingOffsetHuffmanAcFrequencyMs = 44;
        // Timing offset Huffman DC 頻度 Msを示す定数
        private const int TimingOffsetHuffmanDcFrequencyMs = 48;
        // Timing offset Huffman table Msを示す定数
        private const int TimingOffsetHuffmanTableMs = 52;
        // Timing offset Huffman bit 数 Msを示す定数
        private const int TimingOffsetHuffmanBitCountMs = 56;
        // Timing offset Huffman ブロック page Msを示す定数
        private const int TimingOffsetHuffmanBlockPageMs = 60;
        // Timing offset Huffman chunk 有効 Msを示す定数
        private const int TimingOffsetHuffmanChunkValidMs = 64;
        // Timing offset Huffman payload Gather Msを示す定数
        private const int TimingOffsetHuffmanPayloadGatherMs = 68;
        // Timing offset readback Wait stepを示す定数
        private const int TimingOffsetReadbackWaitSteps = 72;
        // Timing offset readback Store stepを示す定数
        private const int TimingOffsetReadbackStoreSteps = 152;
        // Timing offset Blit Wait Total Msを示す定数
        private const int TimingOffsetBlitWaitTotalMs = 184;
        // Timing offset Blit Wait stepを示す定数
        private const int TimingOffsetBlitWaitSteps = 188;
        // Timing offset GPU 段階 Delay Start Msを示す定数
        private const int TimingOffsetGpuStageDelayStartMs = 220;
        // Timing offset GPU 段階 Gap stepを示す定数
        private const int TimingOffsetGpuStageGapSteps = 224;
        // Timing offset GPU 復号 表示 Gap stepを示す定数
        private const int TimingOffsetGpuDecodeDisplayGapSteps = 368;
        // Timing offset 完了 出力 Msを示す定数
        private const int TimingOffsetCompleteOutputMs = 480;
        // Timing offset 出力 Receiver Msを示す定数
        private const int TimingOffsetOutputReceiverMs = 484;
        // Timing FPS phase Noneを示す定数
        private const int TimingFpsPhaseNone = -1;
        // Timing FPS phase 符号化 DCTを示す定数
        private const int TimingFpsPhaseEncodeDct = 0;
        // Timing FPS phase 符号化 RLEを示す定数
        private const int TimingFpsPhaseEncodeRle = 1;
        // Timing FPS phase 符号化 AC Freqを示す定数
        private const int TimingFpsPhaseEncodeAcFreq = 2;
        // Timing FPS phase 符号化 DC Freqを示す定数
        private const int TimingFpsPhaseEncodeDcFreq = 3;
        // Timing FPS phase 符号化 tableを示す定数
        private const int TimingFpsPhaseEncodeTable = 4;
        // Timing FPS phase 符号化 bit 数を示す定数
        private const int TimingFpsPhaseEncodeBitCount = 5;
        // Timing FPS phase 符号化 Overflowを示す定数
        private const int TimingFpsPhaseEncodeOverflow = 6;
        // Timing FPS phase 符号化 ブロックを示す定数
        private const int TimingFpsPhaseEncodeBlock = 7;
        // Timing FPS phase 符号化 payloadを示す定数
        private const int TimingFpsPhaseEncodePayload = 8;
        // Timing FPS phase 符号化 readbackを示す定数
        private const int TimingFpsPhaseEncodeReadback = 9;
        // Timing FPS phase 復号 uploadを示す定数
        private const int TimingFpsPhaseDecodeUpload = 10;
        // Timing FPS phase 復号 bit offsetを示す定数
        private const int TimingFpsPhaseDecodeBitOffset = 11;
        // Timing FPS phase 復号 RLEを示す定数
        private const int TimingFpsPhaseDecodeRle = 12;
        // Timing FPS phase 復号 scanを示す定数
        private const int TimingFpsPhaseDecodeScan = 13;
        // Timing FPS phase 復号 Fixを示す定数
        private const int TimingFpsPhaseDecodeFix = 14;
        // Timing FPS phase 復号 IDCTを示す定数
        private const int TimingFpsPhaseDecodeIdct = 15;
        // Timing FPS phase 復号 Composeを示す定数
        private const int TimingFpsPhaseDecodeCompose = 16;
        // Timing FPS phase 復号 出力を示す定数
        private const int TimingFpsPhaseDecodeOutput = 17;
        // Timing FPS phase 数を示す定数
        private const int TimingFpsPhaseCount = 18;

        [Header("Input / Output")]

        // 入力 Textureを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Texture sourceTexture;
        // 入力 Texture sRGBを外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool sourceTextureSrgb = true;
        // 出力 Textureを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Texture outputTexture;
        // 出力 準備完了 Receiverを外部へ公開する値
        public UdonBehaviour outputReadyReceiver;
        // 出力 準備完了 イベント名を外部へ公開する値
        public string outputReadyEventName = "_HandleICExpansionComplete";
        // 出力 失敗 イベント名を外部へ公開する値
        public string outputFailedEventName = "_HandleICExpansionFailed";
        // 出力 再試行 イベント名を外部へ公開する値
        public string outputRetryingEventName = "_HandleICCompressionRetrying";
        // 圧縮結果 byte列 準備完了 Receiverを外部へ公開する値
        public UdonBehaviour compressedBytesReadyReceiver;
        // 圧縮結果 byte列 準備完了 イベント名を外部へ公開する値
        public string compressedBytesReadyEventName = "_HandleICCompressionComplete";
        // 圧縮結果 byte列 失敗 イベント名を外部へ公開する値
        public string compressedBytesFailedEventName = "_HandleICCompressionFailed";

        [Header("Compression Settings")]
        // Rangeを処理する
        [Range(MinQuality, MaxQuality)] public int quality = 50;
        // Rangeを処理する
        [Range(0, MaxQuantPreset)] public int quantPreset = MaxQuantPreset;
        // has alphaを外部へ公開する値
        public bool hasAlpha = true;
        // send 色を外部へ公開する値
        public bool sendColor = true;
        // half サイズ Cb Crを外部へ公開する値
        public bool halfSizeCbCr;
        // 符号化 sRGBを外部へ公開する値
        public bool encodeSrgb = true;
        // skip 出力 ブロック 圧縮を外部へ公開する値
        public bool skipOutputBlockCompression;
        // 出力 ブロック 圧縮 sRGBを外部へ公開する値
        public bool outputBlockCompressionSrgb = true;
        // alpha modeを外部へ公開する値
        public int alphaMode { get { return hasAlpha ? 1 : 0; } set { hasAlpha = value != 0; } }

        [Header("Compressed Byte Settings")]
        // 最大 Image byte列を外部へ公開する値
        public int maxImageBytes = 4194304;

        [Header("Failure Retry")]
        // 自動 再試行 Lower 画質を外部へ公開する値
        public bool autoRetryLowerQuality = true;
        // 超過量を取得できない場合や予測結果が同じqualityになった場合だけ使う最低低下幅
        [Range(1, 25)] public int retryQualityStep = 2;
        // Rangeを処理する
        [Range(0, 25)] public int maxQualityRetryCount = 20;
        // 画質 Was Explicitly Changed By 再試行を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool qualityWasExplicitlyChangedByRetry;
        // 画質 Was Explicitly Lowered By 再試行を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool qualityWasExplicitlyLoweredByRetry;
        // 画質 Requested Before 自動 再試行を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int qualityRequestedBeforeAutoRetry;
        // 画質 Before 自動 再試行を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int qualityBeforeAutoRetry;
        // 画質 After 自動 再試行を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int qualityAfterAutoRetry;
        // 画質 自動 再試行 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int qualityAutoRetryCount;
        // last 画質 再試行 Required byte列を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int lastQualityRetryRequiredBytes;
        // last 画質 再試行 Target byte列を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int lastQualityRetryTargetBytes;
        // last 画質 再試行 planeを外部へ公開する値
        [HideInInspector, System.NonSerialized] public int lastQualityRetryPlane;
        // last 圧縮 失敗 Reasonを外部へ公開する値
        [HideInInspector, System.NonSerialized] public string lastCompressionFailureReason;
        // last 圧縮 再試行 Reasonを外部へ公開する値
        [HideInInspector, System.NonSerialized] public string lastCompressionRetryReason;

        // 容量制限APIの事前予測結果。画質指定APIではcapacityPrepassRan=falseのままになる
        [HideInInspector, System.NonSerialized] public bool capacityPrepassRan;
        // 容量 事前計算 Requested 画質を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassRequestedQuality;
        // 容量 事前計算 Selected 画質を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassSelectedQuality;
        // 容量 事前計算 Estimated byte列を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassEstimatedBytes;
        // 容量 事前計算 Actual byte列を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassActualBytes;
        // 容量 事前計算 Sample ブロック 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassSampleBlockCount;
        // 容量 事前計算 Y Sample ブロック 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassYSampleBlockCount;
        // 容量 事前計算 A Sample ブロック 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassASampleBlockCount;
        // 容量 事前計算 Cb Sample ブロック 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassCbSampleBlockCount;
        // 容量 事前計算 Cr Sample ブロック 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassCrSampleBlockCount;
        // 容量 事前計算 Full 圧縮 再試行 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassFullCompressionRetryCount;
        // 容量 事前計算 候補 Attemptを外部へ公開する値
        [HideInInspector, System.NonSerialized] public int capacityPrepassCandidateAttempt;
        // 容量 事前計算 Fallback Reasonを外部へ公開する値
        [HideInInspector, System.NonSerialized] public string capacityPrepassFallbackReason;
        // 容量 事前計算 Duration Msを外部へ公開する値
        [HideInInspector, System.NonSerialized] public float capacityPrepassDurationMs;
        // 容量 Full 圧縮 Duration Msを外部へ公開する値
        [HideInInspector, System.NonSerialized] public float capacityFullCompressionDurationMs;
        // 容量 Total Duration Msを外部へ公開する値
        [HideInInspector, System.NonSerialized] public float capacityTotalDurationMs;

        [Header("Inspection")]
        // 0は通常動作、1..MaxInspectionStopStepはscene UIから使う手動停止位置
        // 各停止経路は診断文字列を書き、Androidの実際の完了状態を隠し得るため既定では有効にしない
        [Range(0, MaxInspectionStopStep)] public int inspectionStopStep;

        [Header("Diagnostics")]
        // enable Timing Diagnosticsを外部へ公開する値
        public bool enableTimingDiagnostics = EnableTimingDiagnosticsDefault;
        // latest Timing Summaryを外部へ公開する値
        [HideInInspector, System.NonSerialized] public string latestTimingSummary;
        private float timingFrameSeconds;
        private float timingWorstFrameSeconds;
        private int timingFrameCount;
        private float[] timingPhaseFrameSeconds;
        private float[] timingPhaseWorstFrameSeconds;
        private int[] timingPhaseFrameCounts;
        private int timingCurrentFpsPhase = TimingFpsPhaseNone;

        [Header("Warmup")]
        // 最小を処理する
        [Min(1f)] public float operationTimeoutSeconds = 60f;
        // warmup 実行中を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool warmupRunning;
        // warmup 完了を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool warmupComplete;
        // warmup 失敗を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool warmupFailed;
        // 圧縮 warmup 完了を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool compressionWarmupComplete;
        // 展開 warmup 完了を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool expansionWarmupComplete;
        // latest warmup Summaryを外部へ公開する値
        [HideInInspector, System.NonSerialized] public string latestWarmupSummary;
        private int warmupStep;
        private int warmupPlane;
        private int warmupShaderStep;
        private int warmupCoreResourceStep;
        private int warmupResourceStep;
        private Texture2D warmupGammaEncodeTexture;
        private byte[] warmupGammaEncodeBytes;
        private Texture2D warmupQuantReciprocalTexture;
        private Color32[] warmupQuantReciprocalPixels;
        private bool warmupGpuReadbackPending;
        private bool warmupGpuReadbackRequestQueued;
        private byte[] warmupGpuReadbackBytes;
        private int warmupGpuReadbackStep;
        private int warmupStartedMs;
        private float warmupFrameSeconds;
        private float warmupWorstFrameSeconds;
        private int warmupFrameCount;
        private int warmupLastOperation;
        private int warmupWorstOperation;
        private int warmupMode;
        private float warmupLastProgressAtRealtime;
        private float operationLastProgressAtRealtime;
        private int operationProgressSignature;
        private Texture warmupPreviousActiveEncodeSourceTexture;
        private RenderTexture warmupSourceTexture;
        private bool warmupPreviousActiveEncodeSourceIsRawStorage;
        private bool warmupPreviousActiveEncodeSrgb;
        private bool warmupPreviousActiveSourceSrgb;

        [Header("Byte Containers")]

        // 入力 byte列を外部へ公開する値
        [HideInInspector, System.NonSerialized] public byte[] sourceBytes;
        // 圧縮結果 byte列を外部へ公開する値
        [HideInInspector, System.NonSerialized] public byte[] compressedBytes;
        // 入力 幅を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int sourceWidth;
        // 入力 高さを外部へ公開する値
        [HideInInspector, System.NonSerialized] public int sourceHeight;
        // 入力 byte 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int sourceByteCount;
        // 圧縮結果 byte 数を外部へ公開する値
        [HideInInspector, System.NonSerialized] public int compressedByteCount;
        // 圧縮 待機状態を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool compressionPending;
        // 圧縮 完了を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool compressionComplete;
        // 圧縮 失敗を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool compressionFailed;
        // expand 完了を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool expandComplete;
        // expand 失敗を外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool expandFailed;
        // 実行中 符号化 sRGBを外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool activeEncodeSrgb;
        // 実行中 入力 sRGBを外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool activeSourceSrgb;

        private byte[] yPayloadBytes;
        private byte[] aPayloadBytes;
        private byte[] cbPayloadBytes;
        private byte[] crPayloadBytes;
        private byte[] yChunkValidBytes;
        private byte[] aChunkValidBytes;
        private byte[] cbChunkValidBytes;
        private byte[] crChunkValidBytes;
        private byte[] yChunkOffsetBytes;
        private byte[] aChunkOffsetBytes;
        private byte[] cbChunkOffsetBytes;
        private byte[] crChunkOffsetBytes;
        private byte[] yDcHuffmanBytes;
        private byte[] aDcHuffmanBytes;
        private byte[] cbDcHuffmanBytes;
        private byte[] crDcHuffmanBytes;
        private byte[] yAcHuffmanBytes;
        private byte[] aAcHuffmanBytes;
        private byte[] cbAcHuffmanBytes;
        private byte[] crAcHuffmanBytes;
        private byte[] runtimeStateBytes;
        private byte[] statusBytes;
        private byte[] timingBytes;
        private byte[] emptyBytes;
        private bool compressedBytesReady { get { return ReadRuntimeStateBool(RuntimeStateCompressedReady); } set { WriteRuntimeStateBool(RuntimeStateCompressedReady, value); } }
        private bool compressedBytesFailed { get { return ReadRuntimeStateBool(RuntimeStateCompressedFailed); } set { WriteRuntimeStateBool(RuntimeStateCompressedFailed, value); } }
        // Runtime 状態 Boolを読み取る
        public bool huffmanEncodeComplete { get { return ReadRuntimeStateBool(RuntimeStateHuffmanEncodeComplete); } set { WriteRuntimeStateBool(RuntimeStateHuffmanEncodeComplete, value); } }
        // Runtime 状態 Boolを読み取る
        public bool huffmanEncodeFailed { get { return ReadRuntimeStateBool(RuntimeStateHuffmanEncodeFailed); } set { WriteRuntimeStateBool(RuntimeStateHuffmanEncodeFailed, value); } }

        [Header("Stage 1 DCT")]
        // DCT/HuffmanのGPU中間Textureは1回の処理中だけ使う内部buffer

        private int stage1PlaneMode;
        private Texture quantTexture;
        private Texture quantReciprocalTexture;
        private RenderTexture work;
        private RenderTexture coefficients;
        private RenderTexture planeYTexture;
        private RenderTexture planeATexture;
        private RenderTexture planeCbTexture;
        private RenderTexture planeCrTexture;
        private RenderTexture rleSymbols;
        private RenderTexture rleAcFrequencyRows;
        private RenderTexture rleAcFrequencyRowsTemp;
        private RenderTexture rleAcFrequency;
        private RenderTexture dcValues;
        private RenderTexture dcDelta;
        private RenderTexture dcFrequencyRows;
        private RenderTexture dcFrequencyRowsTemp;
        private RenderTexture dcFrequency;
        private RenderTexture huffmanRawLengths;
        private RenderTexture huffmanRawLengthSingle;
        private RenderTexture huffmanRawLengthHistogram;
        private RenderTexture huffmanRawLengthHistogramTemp;
        private RenderTexture huffmanLimitedLengthHistogram;
        private RenderTexture huffmanLimitedLengthHistogramTemp;
        private RenderTexture dcHuffmanLengths;
        private RenderTexture dcHuffmanCodes;
        private RenderTexture acHuffmanLengths;
        private RenderTexture acHuffmanCodes;
        private RenderTexture huffmanBlockBits;
        private RenderTexture huffmanBlockOverflowMask;
        private RenderTexture huffmanBlockOverflowMaskTemp;
        private RenderTexture huffmanBlockPage;
        private RenderTexture huffmanBlockPageTemp;
        private RenderTexture huffmanBlockPageState;
        private RenderTexture huffmanBlockPageStateTemp;
        private RenderTexture huffmanBlockValidBytes;
        private RenderTexture huffmanChunkValidBytes;
        private RenderTexture huffmanChunkValidBytesMip;
        private RenderTexture huffmanChunkValidBytesMipTemp;
        private RenderTexture huffmanPayloadGather;
        private RenderTexture huffmanMetadataPack;
        private RenderTexture yHuffmanPayloadTexture;
        private RenderTexture aHuffmanPayloadTexture;
        private RenderTexture cbHuffmanPayloadTexture;
        private RenderTexture crHuffmanPayloadTexture;
        private RenderTexture yHuffmanMetadataTexture;
        private RenderTexture aHuffmanMetadataTexture;
        private RenderTexture cbHuffmanMetadataTexture;
        private RenderTexture crHuffmanMetadataTexture;
        private RenderTexture yChunkValidTexture;
        private RenderTexture aChunkValidTexture;
        private RenderTexture cbChunkValidTexture;
        private RenderTexture crChunkValidTexture;
        private RenderTexture yDcHuffmanCodesTexture;
        private RenderTexture aDcHuffmanCodesTexture;
        private RenderTexture cbDcHuffmanCodesTexture;
        private RenderTexture crDcHuffmanCodesTexture;
        private RenderTexture yAcHuffmanCodesTexture;
        private RenderTexture aAcHuffmanCodesTexture;
        private RenderTexture cbAcHuffmanCodesTexture;
        private RenderTexture crAcHuffmanCodesTexture;
        private RenderTexture huffmanPayloadBitOffset;
        private RenderTexture huffmanPayloadBitOffsetTemp;
        private RenderTexture huffmanDecodeState;
        private RenderTexture huffmanDecodeStateTemp;
        private RenderTexture huffmanAcDecodeLengthSummary;
        private RenderTexture huffmanAcDecodeSymbolTable;
        private RenderTexture huffmanAcDecodeSymbolTableWork;
        private RenderTexture huffmanAcDecodeSymbolTableTemp;
        private RenderTexture rleSymbolsFromHuffmanPayload;
        private RenderTexture dcDeltaFromHuffmanPayload;
        private RenderTexture dcScanFromHuffmanPayload;
        private RenderTexture dcScanFromHuffmanPayloadTemp;
        private RenderTexture symbolFixedFromHuffmanPayload;
        private RenderTexture symbolFixedWorkFromHuffmanPayload;
        private RenderTexture reconstructedFromHuffmanPayload;
        private RenderTexture packedDecodedPlanes;
        // Runtime 状態 Millisを読み取る
        public float lastStage1EncodeMs { get { return ReadRuntimeStateMillis(RuntimeStateLastStage1EncodeMs); } set { WriteRuntimeStateMillis(RuntimeStateLastStage1EncodeMs, value); } }
        // Runtime 状態 Millisを読み取る
        public float lastHuffmanEncodeMs { get { return ReadRuntimeStateMillis(RuntimeStateLastHuffmanEncodeMs); } set { WriteRuntimeStateMillis(RuntimeStateLastHuffmanEncodeMs, value); } }
        // Runtime 状態 Millisを読み取る
        public float lastHuffmanDecodeMs { get { return ReadRuntimeStateMillis(RuntimeStateLastHuffmanDecodeMs); } set { WriteRuntimeStateMillis(RuntimeStateLastHuffmanDecodeMs, value); } }

        [Header("Blit Materials")]
        // 実行時にGPU処理用Materialを複製するための描画無効Renderer
        [SerializeField] private MeshRenderer materialInstanceRenderer;
        // 色 Downsample Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material colorDownsampleMaterial;
        // 符号化 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material encodeMaterial;
        // 符号化 Vertical Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material encodeVerticalMaterial;
        // 容量 事前計算 符号化 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material capacityPrepassEncodeMaterial;
        // 容量 事前計算 Vertical Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material capacityPrepassVerticalMaterial;
        // 容量 事前計算 Post Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material capacityPrepassPostMaterial;
        // coeff To RLE symbol Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material coeffToRleSymbolsMaterial;
        // RLE AC 頻度 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material rleAcFrequencyMaterial;
        // DC 差分 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material dcDeltaMaterial;
        // DC scan Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material dcScanMaterial;
        // DC 頻度 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material dcFrequencyMaterial;
        // Huffman table Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanTableMaterial;
        // Huffman bit 数 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanBitCountMaterial;
        // Huffman ブロック Overflow Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanBlockOverflowMaterial;
        // Huffman ブロック page 符号化 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanBlockPageEncodeMaterial;
        // Huffman ブロック 有効 byte列 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanBlockValidBytesMaterial;
        // Huffman chunk 有効 byte列 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanChunkValidBytesMaterial;
        // Huffman payload Gather Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanPayloadGatherMaterial;
        // Huffman metadata Pack Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanMetadataPackMaterial;
        // Huffman chunk ブロック 有効 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanChunkBlockValidMaterial;
        // Huffman 復号 RLE Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material huffmanDecodeRleMaterial;
        // RLE DC 差分 Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material rleDcDeltaMaterial;
        // RLE To symbol Fixed Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material rleToSymbolFixedMaterial;
        // 復号 symbol Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material decodeSymbolsMaterial;
        // compose RGBA Materialを外部へ公開する値
        [HideInInspector, System.NonSerialized] public Material composeRgbaMaterial;
        // gamma 符号化 LUT byte列を外部へ公開する値
        [HideInInspector] public TextAsset gammaEncodeLutBytes;

        // Runtime 状態 Boolを読み取る
        public bool isRunning { get { return ReadRuntimeStateBool(RuntimeStateRunning); } set { WriteRuntimeStateBool(RuntimeStateRunning, value); } }
        // Runtime 状態 Intを読み取る
        public int latestLocalImageId { get { return ReadRuntimeStateInt(RuntimeStateLatestLocalImageId); } set { WriteRuntimeStateInt(RuntimeStateLatestLocalImageId, value); } }
        // Runtime 状態 Intを読み取る
        public int completedImageId { get { return ReadRuntimeStateInt(RuntimeStateCompletedImageId); } set { WriteRuntimeStateInt(RuntimeStateCompletedImageId, value); } }
        // Runtime 状態 Boolを読み取る
        public bool outputReady { get { return ReadRuntimeStateBool(RuntimeStateOutputReady); } set { WriteRuntimeStateBool(RuntimeStateOutputReady, value); } }
        // Runtime 状態 Boolを読み取る
        public bool huffmanDecodeComplete { get { return ReadRuntimeStateBool(RuntimeStateHuffmanDecodeComplete); } set { WriteRuntimeStateBool(RuntimeStateHuffmanDecodeComplete, value); } }
        // Runtime 状態 Boolを読み取る
        public bool huffmanDecodeFailed { get { return ReadRuntimeStateBool(RuntimeStateHuffmanDecodeFailed); } set { WriteRuntimeStateBool(RuntimeStateHuffmanDecodeFailed, value); } }
        // Status Stringを読み取る
        public string status { get { return ReadStatusString(); } set { WriteStatusString(value); } }
        private bool EnableTimingDiagnostics { get { return enableTimingDiagnostics; } }

        private Texture2D generatedGammaEncodeTexture;
        private Texture2D generatedQuantReciprocalTexture;
        private int[] precomputedQuantScaleByQuality;
        private int[] baseYQuantTables;
        private int[] baseAQuantTables;
        private int[] baseCbCrQuantTables;
        private Color32[] runtimeQuantPixels;
        private Texture2D normalizedSourceTexture;
        private byte[] sourceNormalizationBytes;
        private int sourceNormalizationWidth;
        private int sourceNormalizationHeight;
        private int sourceNormalizationUploadStep;
        private bool activeEncodeHasAlpha;
        private bool sourceAlphaScanPending;
        private int sourceAlphaScanPixelCount;
        private int sourceAlphaScanPixelOffset;
        private Texture2D runtimeQuantTexture;
        private Texture2D generatedOpaqueWhiteTexture;
        private Texture2D generatedNeutralGrayTexture;
        private Texture2D generatedTransparentTexture;
        private Texture2D receivedPayloadTexture;
        private Texture2D receivedChunkValidTexture;
        private Texture2D receivedChunkOffsetTexture;
        private Texture2D receivedDcHuffmanCodesTexture;
        private Texture2D receivedAcHuffmanCodesTexture;
        private RenderTexture colorDownsampleTexture;
        private RenderTexture postprocessedOutputRenderTexture;
        private bool postprocessedOutputTextureSrgb;
        private VRCAsyncGPUReadbackRequest huffmanReadbackRequest;
        private VRCAsyncGPUReadbackRequest gpuFenceReadbackRequest;
        private RenderTexture gpuFenceMarkerTexture;
        private RenderTexture gpuFenceSourceTexture;
        private RenderTexture gpuUnfencedBatchSourceTexture;
        private bool gpuBlitSubmittedSinceSchedule;
        private int gpuUnfencedBatchCount;
        private int gpuUnfencedBatchLimit = NormalGpuFenceBatchLimit;
        private bool gpuFenceMarkerQueued;
        private bool gpuFenceReadbackRequestQueued;

        // 異常終了時に使用中のreadback/fenceを保持し、完了後に全RTを一括解放する
        private bool failedGpuResourceReleasePending;
        private bool failedMainReadbackPending;
        private bool failedFenceReadbackPending;
        private bool gpuFenceReadbackPending;
        private string gpuFenceContinuationEvent = "";
        private int gpuFenceContinuationGapStep = -1;
        [Header("GPU Diagnostics")]
        [Tooltip("Records DCTH stage/FPS data and writes one grouped log after the operation completes.")]
        // 製品実行では記録配列と完了時の大容量logを作らず、Android実機診断が必要な時だけ有効にする
        // 有効時もlogは各stepでは出さず、従来どおり完了・失敗時にまとめて出力する
        public bool enableGpuDiagnosticCapture = false;
        // disable Operation Timeout用GPU Diagnosticsを外部へ公開する値
        [HideInInspector, System.NonSerialized] public bool disableOperationTimeoutForGpuDiagnostics = false;
        private bool gpuDiagnosticActive;
        private bool gpuDiagnosticBlitOverflow;
        private bool gpuDiagnosticStageOverflow;
        private string gpuDiagnosticRunLabel = "";
        private int gpuDiagnosticBlitCount;
        private int[] gpuDiagnosticBlitStageSequences;
        private int[] gpuDiagnosticBlitFrames;
        private float[] gpuDiagnosticBlitFrameSeconds;
        private string[] gpuDiagnosticBlitSources;
        private string[] gpuDiagnosticBlitDestinations;
        private string[] gpuDiagnosticBlitMaterials;
        private int[] gpuDiagnosticBlitPasses;
        private int gpuDiagnosticStageCount;
        private string[] gpuDiagnosticStageEvents;
        private int[] gpuDiagnosticStageCodes;
        private int[] gpuDiagnosticStageEncodePlanes;
        private int[] gpuDiagnosticStageEncodeSubStages;
        private int[] gpuDiagnosticStageDecodePlanes;
        private int[] gpuDiagnosticStageDecodeStages;
        private bool[] gpuDiagnosticStageFences;
        private int[] gpuDiagnosticStageFrames;
        private int[] gpuDiagnosticStageElapsedMillis;
        private int[] gpuDiagnosticStageFrameCounts;
        private float[] gpuDiagnosticStageFrameSeconds;
        private float[] gpuDiagnosticStageWorstFrameSeconds;
        private int[] gpuDiagnosticStageBlitStarts;
        private int[] gpuDiagnosticStageBlitCounts;
        private bool gpuDiagnosticStagePending;
        private int gpuDiagnosticPendingStageSequence = -1;
        private string gpuDiagnosticPendingStageEvent = "";
        private int gpuDiagnosticPendingStageCode = -1;
        private bool gpuDiagnosticPendingStageFence;
        private int gpuDiagnosticPendingStageStartedFrame;
        private float gpuDiagnosticPendingStageStartedAt;
        private int gpuDiagnosticPendingStageBlitStart;
        private int gpuDiagnosticPendingStageFrameCount;
        private float gpuDiagnosticPendingStageFrameSeconds;
        private float gpuDiagnosticPendingStageWorstFrameSeconds;
        private Texture sourceNormalizationReadbackTexture;
        private bool sourceNormalizationReadbackRequestQueued;
        private Texture activeEncodeSourceTexture;
        private bool activeEncodeSourceIsRawStorage;
        // 公開入口で決めた容量制限の有無。quality retry中も同じ条件を維持する
        private bool activeCompressionUsesCapacityLimit;
        private bool capacityPrepassCompletedForRequest;
        private bool capacityPrepassReadbackPending;
        private bool capacityPrepassReadbackRequestQueued;
        private bool capacityPrepassColorDownsampleReady;
        private int capacityPrepassPreparePlane;
        private int capacityPrepassPrepareSubStage;
        private bool capacityPrepassPrepareBlockMapReady;
        private bool capacityPrepassPreparePreviousBlockMapReady;
        private int capacityPrepassCandidatePlane;
        private int capacityPrepassCandidateSubStage;
        private int capacityPrepassCandidateQuality;
        private int capacityPrepassCandidateSetupStep;
        private int capacityPrepassStartedMs;
        private int capacityFullCompressionStartedMs;
        private int capacityTotalStartedMs;
        private byte[] capacityPrepassReadbackBytes;
        private Color32[] capacityPrepassBlockMapPixels;
        private Texture2D capacityPrepassBlockMapTexture;
        private RenderTexture capacityPrepassWork;
        private RenderTexture capacityPrepassPreviousDctWork;
        private RenderTexture capacityPrepassYDct;
        private RenderTexture capacityPrepassADct;
        private RenderTexture capacityPrepassCbDct;
        private RenderTexture capacityPrepassCrDct;
        private RenderTexture capacityPrepassYPreviousDc;
        private RenderTexture capacityPrepassAPreviousDc;
        private RenderTexture capacityPrepassCbPreviousDc;
        private RenderTexture capacityPrepassCrPreviousDc;
        private RenderTexture capacityPrepassQuantizedCoefficients;
        private RenderTexture capacityPrepassRleSymbols;
        private RenderTexture capacityPrepassAcFrequencyRows;
        private RenderTexture capacityPrepassAcFrequency;
        private RenderTexture capacityPrepassDcDelta;
        private RenderTexture capacityPrepassDcFrequencyRows;
        private RenderTexture capacityPrepassDcFrequency;
        private RenderTexture capacityPrepassDcHuffmanLengths;
        private RenderTexture capacityPrepassAcHuffmanLengths;
        private RenderTexture capacityPrepassBlockBits;
        private RenderTexture capacityPrepassStats;
        private RenderTexture capacityPrepassStatsTemp;
        private int capacityPrepassReadbackDrainFramesRemaining;
        private Texture cpuAcHuffmanFrequencyTexture;
        private Texture2D cpuAcHuffmanRawLengthUploadTexture;
        private byte[] cpuAcHuffmanFrequencyBytes;
        private byte[] cpuAcHuffmanRawLengthBytes;
        private float[] cpuAcHuffmanNodeWeights;
        private int[] cpuAcHuffmanNodeParents;
        private int[] cpuAcHuffmanLeafSymbols;
        private int[] cpuAcHuffmanHeapNodes;
        private int cpuAcHuffmanPhase;
        private int cpuAcHuffmanInitializeSymbol;
        private int cpuAcHuffmanLeafCount;
        private int cpuAcHuffmanNodeCount;
        private int cpuAcHuffmanHeapCount;
        private int cpuAcHuffmanMergeCount;
        private int cpuAcHuffmanLengthLeafIndex;
        private int cpuHuffmanSymbolCount;
        private bool currentOperationIsExpansion;
        // encode側substageのcursor。現在ownerがGPU passをframe分割している間だけ必要なので、
        // runtimeState byte[]ではなくlocal fieldとして保持する
        private int encodeSubStage;
        private int decodeRleStatePing;
        private int encodeBlockPageStatePing;
        private int runtimeQuantQuality { get { return ReadRuntimeStateInt(RuntimeStateQuantQuality); } set { WriteRuntimeStateInt(RuntimeStateQuantQuality, value); } }
        private int runtimeQuantPreset { get { return ReadRuntimeStateInt(RuntimeStateQuantPreset); } set { WriteRuntimeStateInt(RuntimeStateQuantPreset, value); } }
        private int huffmanReadbackStep { get { return ReadRuntimeStateInt(RuntimeStateHuffmanReadbackStep); } set { WriteRuntimeStateInt(RuntimeStateHuffmanReadbackStep, value); } }
        private int huffmanReadbackDrainFramesRemaining;
        private bool huffmanReadbackRequestQueued;
        private int huffmanReadbackTotalWeightBytes;
        private int huffmanReadbackCompletedWeightBytes;
        private int huffmanReadbackRegionWidth;
        private int huffmanReadbackRegionHeight;
        private int huffmanReadbackPayloadLength = -1;
        private int huffmanReadbackPlane { get { return ReadRuntimeStateInt(RuntimeStateHuffmanReadbackPlane); } set { WriteRuntimeStateInt(RuntimeStateHuffmanReadbackPlane, value); } }
        private int huffmanReadbackKind { get { return ReadRuntimeStateInt(RuntimeStateHuffmanReadbackKind); } set { WriteRuntimeStateInt(RuntimeStateHuffmanReadbackKind, value); } }
        private int gpuStage { get { return ReadRuntimeStateInt(RuntimeStateGpuStage); } set { WriteRuntimeStateInt(RuntimeStateGpuStage, value); } }
        private int decodePlane { get { return ReadRuntimeStateInt(RuntimeStateDecodePlane); } set { WriteRuntimeStateInt(RuntimeStateDecodePlane, value); } }
        private int decodeStage { get { return ReadRuntimeStateInt(RuntimeStateDecodeStage); } set { WriteRuntimeStateInt(RuntimeStateDecodeStage, value); } }
        private int decodeLocalIndex { get { return ReadRuntimeStateInt(RuntimeStateDecodeLocalIndex); } set { WriteRuntimeStateInt(RuntimeStateDecodeLocalIndex, value); } }
        private int decodeBitOffsetPing { get { return ReadRuntimeStateInt(RuntimeStateDecodeBitOffsetPing); } set { WriteRuntimeStateInt(RuntimeStateDecodeBitOffsetPing, value); } }
        private int decodeUploadStep;
        private float decodeUploadElapsedMs;
        private int restoreSegmentIndex;
        private int restoreDataOffset;
        private int restoreSegmentSourceOffset;
        private int restoreSegmentCopyOffset;
        private int restoreSegmentLength;
        private byte[] restoreSegmentBytes;
        private float restoreElapsedMs;
        private int packSegmentIndex;
        private int packPrepareSegmentIndex;
        private int packDataOffset;
        private int packTotalBytes;
        private int packSegmentCopyOffset;
        private int packSegmentLength;
        private byte[] packSegmentBytes;
        private int packTotalStepCount;
        private int packCompletedStepCount;
        private float packElapsedMs;
        private int pendingPayloadStorePlane;
        private int pendingPayloadStoreLength;
        private int pendingPayloadStoreOffset;
        private int pendingPayloadStoreCompletedStep;
        private int pendingPayloadStoreReadbackWaitMs;
        private float pendingPayloadStoreElapsedMs;
        private byte[] pendingPayloadReadbackBytes;
        private byte[] pendingPayloadStoreBytes;
        private byte[] reusableSourceNormalizationReadbackBytes;
        private byte[] reusableMetadataReadbackBytes;
        private byte[] reusablePayloadReadbackBytes;
        // retry判断にだけ使う一時値。DCTH headerやpayloadへは格納しないためwire形式は変わらない
        private int qualityRetryReason;
        private int qualityRetryRequiredBytes;
        private int qualityRetryTargetBytes;
        private int qualityRetryPlane;
        private bool pendingChunkOffsetBuild;
        private int pendingChunkOffsetPlane;
        private int pendingChunkOffsetOrderIndex;
        private int pendingChunkOffsetCumulative;
        private int pendingChunkOffsetWidth;
        private int pendingChunkOffsetHeight;
        private byte[] pendingChunkOffsetChunkValidBytes;
        private byte[] pendingChunkOffsetBytes;
        private bool pendingDecodePayloadUploadBuild;
        private int pendingDecodePayloadUploadPlane;
        private int pendingDecodePayloadUploadWidth;
        private int pendingDecodePayloadUploadHeight;
        private int pendingDecodePayloadUploadPixelCount;
        private int pendingDecodePayloadUploadPayloadLength;
        private int pendingDecodePayloadUploadOffset;
        private byte[] pendingDecodePayloadSourceBytes;
        private byte[] pendingDecodePayloadUploadBytes;
        private int pendingGpuStageGapStep { get { return ReadRuntimeStateInt(RuntimeStatePendingGpuStageGapStep); } set { WriteRuntimeStateInt(RuntimeStatePendingGpuStageGapStep, value); } }
        private bool huffmanReadbackPending { get { return ReadRuntimeStateBool(RuntimeStateHuffmanReadbackPending); } set { WriteRuntimeStateBool(RuntimeStateHuffmanReadbackPending, value); } }
        private bool sourceNormalizationReadbackPending { get { return ReadRuntimeStateBool(RuntimeStateSourceNormalizationReadbackPending); } set { WriteRuntimeStateBool(RuntimeStateSourceNormalizationReadbackPending, value); } }
        private bool retryCompressionOnFailure { get { return ReadRuntimeStateBool(RuntimeStateRetryCompressionOnFailure); } set { WriteRuntimeStateBool(RuntimeStateRetryCompressionOnFailure, value); } }
        private int nextRequestHandleId;
        private int activeRequestHandleId = InvalidHandleId;
        private int completedRequestHandleId = InvalidHandleId;
        private int failedRequestHandleId = InvalidHandleId;
        private int cancelledRequestHandleId = InvalidHandleId;
        private bool activeRequestIsExpansion;
        private bool completedRequestWasExpansion;
        private bool failedRequestWasExpansion;
        private bool cancelledRequestWasExpansion;
        private float compressionProgress01;
        private int compressionProgressStage = ProgressStageUnknown;
        private bool pendingRequestUsesCapacityLimit;
        private UdonBehaviour activeRequestReceiver;
        private RenderTexture expansionResultTexture;
        private int nextWarmupHandleId;
        private int activeWarmupHandleId = InvalidHandleId;
        private int completedWarmupHandleId = InvalidHandleId;
        private int failedWarmupHandleId = InvalidHandleId;
        private int cancelledWarmupHandleId = InvalidHandleId;
        private int activeWarmupRequestMode = WarmupModeAll;
        private int completedWarmupRequestMode = WarmupModeAll;
        private int failedWarmupRequestMode = WarmupModeAll;
        private int cancelledWarmupRequestMode = WarmupModeAll;
        private UdonBehaviour activeWarmupRequestReceiver;
        private float warmupRequestStartedAtRealtime;

        // 実行中 request handle IDを外部へ公開する値
        public int ActiveRequestHandleId { get { return activeRequestHandleId; } }
        // Completed request handle IDを外部へ公開する値
        public int CompletedRequestHandleId { get { return completedRequestHandleId; } }
        // 失敗 request handle IDを外部へ公開する値
        public int FailedRequestHandleId { get { return failedRequestHandleId; } }
        // 圧縮 結果 byte列を外部へ公開する値
        public byte[] CompressionResultBytes { get { return compressedBytes; } }
        // 圧縮 結果 幅を外部へ公開する値
        public int CompressionResultWidth { get { return sourceWidth; } }
        // 圧縮 結果 高さを外部へ公開する値
        public int CompressionResultHeight { get { return sourceHeight; } }
        // 展開 結果 Textureを外部へ公開する値
        public RenderTexture ExpansionResultTexture { get { return expansionResultTexture; } }
        // 実行中 warmup handle IDを外部へ公開する値
        public int ActiveWarmupHandleId { get { return activeWarmupHandleId; } }
        // Completed warmup handle IDを外部へ公開する値
        public int CompletedWarmupHandleId { get { return completedWarmupHandleId; } }
        // 失敗 warmup handle IDを外部へ公開する値
        public int FailedWarmupHandleId { get { return failedWarmupHandleId; } }

        // Material保持用Rendererからこのライブラリ専用の複製Materialを取得する
        // Owned受付後は入力の寿命をライブラリ側だけで管理する
        private Texture ownedInputTexture;
        private Texture borrowedCopySource;
        private bool inputCopyPending;
        // キャンセル前に予約された遅延イベントが次の要求へ重なっても転写は一度だけ行う
        private bool inputCopySubmitted;
        private int copiedInputHandleId = InvalidHandleId;
        private bool failureNotificationPending;
        private bool cancellationNotificationPending;
        private bool cancelledOperationIsExpansion;
        private UdonBehaviour cancellationReceiver;
        private bool disposalRequested;
        private bool disposed;
        private Material[] ownedRuntimeMaterials;
        public const int TaskStateCancelling = 6;
        public bool IsBusy { get { return isRunning || warmupRunning || inputCopyPending
            || failedGpuResourceReleasePending || activeRequestHandleId != InvalidHandleId
            || activeWarmupHandleId != InvalidHandleId; } }
        public bool CanAcceptRequest { get { return !IsBusy && !disposalRequested; } }
        public bool IsDisposed { get { return disposed; } }

        private void Start()
        {
            InitializeRuntimeMaterials();
        }

        // Materialスロットを各GPU処理へ割り当てる
        private void InitializeRuntimeMaterials()
        {
            if (disposalRequested || ownedRuntimeMaterials != null || materialInstanceRenderer == null)
            {
                return;
            }

            materialInstanceRenderer.enabled = false;
            Material[] runtimeMaterials = materialInstanceRenderer.materials;
            ownedRuntimeMaterials = runtimeMaterials;
            if (runtimeMaterials == null || runtimeMaterials.Length != RuntimeMaterialCount)
            {
                return;
            }

            colorDownsampleMaterial = runtimeMaterials[0];
            encodeMaterial = runtimeMaterials[1];
            encodeVerticalMaterial = runtimeMaterials[2];
            capacityPrepassEncodeMaterial = runtimeMaterials[3];
            capacityPrepassVerticalMaterial = runtimeMaterials[4];
            capacityPrepassPostMaterial = runtimeMaterials[5];
            coeffToRleSymbolsMaterial = runtimeMaterials[6];
            rleAcFrequencyMaterial = runtimeMaterials[7];
            dcDeltaMaterial = runtimeMaterials[8];
            dcScanMaterial = runtimeMaterials[9];
            dcFrequencyMaterial = runtimeMaterials[10];
            huffmanTableMaterial = runtimeMaterials[11];
            huffmanBitCountMaterial = runtimeMaterials[12];
            huffmanBlockOverflowMaterial = runtimeMaterials[13];
            huffmanBlockPageEncodeMaterial = runtimeMaterials[14];
            huffmanBlockValidBytesMaterial = runtimeMaterials[15];
            huffmanChunkValidBytesMaterial = runtimeMaterials[16];
            huffmanPayloadGatherMaterial = runtimeMaterials[17];
            huffmanMetadataPackMaterial = runtimeMaterials[18];
            huffmanChunkBlockValidMaterial = runtimeMaterials[19];
            huffmanDecodeRleMaterial = runtimeMaterials[20];
            rleDcDeltaMaterial = runtimeMaterials[21];
            rleToSymbolFixedMaterial = runtimeMaterials[22];
            decodeSymbolsMaterial = runtimeMaterials[23];
            composeRgbaMaterial = runtimeMaterials[24];
        }

        // 圧縮を要求しhandle IDを返す
        // 専用Textureを受け取る。受付成功後は呼び側から変更・破棄しない
        // InvalidHandleIdを返した場合だけ、所有権が呼び側に残る
        public int RequestCompressionOwned(Texture inputTexture, bool inputSrgb, int qualityValue, int maximumCompressedBytes, UdonBehaviour eventReceiver)
        {
            if (!CanAcceptRequest) return InvalidHandleId;
            int handle = RegisterCompressionRequest(inputTexture, inputSrgb, qualityValue, maximumCompressedBytes, eventReceiver);
            if (handle != InvalidHandleId) ownedInputTexture = inputTexture;
            return handle;
        }

        // 共有asset向けのコピー入口。元Textureは取り込み完了または処理終了まで保持する
        public int RequestCompressionCopy(Texture inputTexture, bool inputSrgb, int qualityValue, int maximumCompressedBytes, UdonBehaviour eventReceiver)
        {
            if (!CanAcceptRequest) return InvalidHandleId;
            int handle = RegisterCompressionRequest(inputTexture, inputSrgb, qualityValue, maximumCompressedBytes, eventReceiver);
            if (handle != InvalidHandleId)
            {
                borrowedCopySource = inputTexture;
                inputCopyPending = true;
                inputCopySubmitted = false;
            }
            return handle;
        }

        // コピー元の解放を許可する状態。Owned入力には使用しない
        public bool IsInputCopyComplete(int handleId) { return handleId != InvalidHandleId && handleId == copiedInputHandleId; }

        public byte[] TakeCompressionResult(int handleId)
        {
            if (handleId == InvalidHandleId || handleId != completedRequestHandleId || completedRequestWasExpansion) return null;
            byte[] result = compressedBytes;
            compressedBytes = GetEmptyBytes();
            return result;
        }

        // 結果を取り出した時点で、出力Textureの所有権が呼び側へ移る
        public RenderTexture TakeExpansionResult(int handleId)
        {
            if (handleId == InvalidHandleId || handleId != completedRequestHandleId || !completedRequestWasExpansion) return null;
            RenderTexture result = expansionResultTexture;
            expansionResultTexture = null;
            if (outputTexture == result) outputTexture = null;
            if (postprocessedOutputRenderTexture == result) postprocessedOutputRenderTexture = null;
            return result;
        }

        // 確保・Blit・Readback要求を別frameへ分ける。色領域は入力の指定を引き継ぐ
        private void PrepareInputCopy()
        {
            if (ownedInputTexture != null) return;
            if (borrowedCopySource == null || borrowedCopySource.width > 8192 || borrowedCopySource.height > 8192
                || (long)borrowedCopySource.width * borrowedCopySource.height > 16777216L)
            {
                FailCompressedBytes("Invalid input copy dimensions.");
                return;
            }
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(borrowedCopySource.width, borrowedCopySource.height, RenderTextureFormat.ARGB32, 0);
            descriptor.sRGB = sourceTextureSrgb;
            descriptor.msaaSamples = 1;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            RenderTexture snapshot = new RenderTexture(descriptor);
            snapshot.name = "IC_Dcth_OwnedInput";
            snapshot.Create();
            ownedInputTexture = snapshot;
            isRunning = true;
            operationLastProgressAtRealtime = Time.realtimeSinceStartup;
            if (!snapshot.IsCreated()) { FailCompressedBytes("Input copy RenderTexture creation failed."); return; }
            SendCustomEventDelayedFrames(nameof(_CopyInputTexture), 1);
        }

        // Blitの発行だけでは元Textureを解放できないため、完了fenceへ進める
        public void _CopyInputTexture()
        {
            if (!inputCopyPending || inputCopySubmitted || activeRequestHandleId == InvalidHandleId) return;
            if (borrowedCopySource == null || ownedInputTexture == null) { FailCompressedBytes("Input copy source was destroyed."); return; }
            inputCopySubmitted = true;
            TrackedBlit(borrowedCopySource, (RenderTexture)ownedInputTexture);
            ScheduleGpuStageDelay(nameof(_CompleteInputCopy));
        }

        // 転写がGPU上で完了した後、元Textureとの関係を切って通知する
        public void _CompleteInputCopy()
        {
            if (!inputCopyPending || !inputCopySubmitted || activeRequestHandleId == InvalidHandleId) return;
            inputCopyPending = false;
            isRunning = false;
            borrowedCopySource = null;
            copiedInputHandleId = activeRequestHandleId;
            sourceTexture = ownedInputTexture;
            SendCustomEventDelayedFrames(nameof(_BeginRequestedCompression), 1);
            if (activeRequestReceiver != null) activeRequestReceiver.SendCustomEvent("_HandleDcthInputCopied");
        }

        private int RegisterCompressionRequest(
            Texture inputTexture,
            bool inputSrgb,
            int qualityValue,
            int maximumCompressedBytes,
            UdonBehaviour eventReceiver)
        {
            if (inputTexture == null || eventReceiver == null || isRunning || warmupRunning
                || failedGpuResourceReleasePending || activeRequestHandleId != InvalidHandleId || activeWarmupHandleId != InvalidHandleId)
            {
                SetStatus(inputTexture == null
                    ? "DCTH compression input texture is missing."
                    : eventReceiver == null
                        ? "DCTH compression event receiver is missing."
                        : "DCTH codec is already running.");
                return InvalidHandleId;
            }

            int handleId = GenerateRequestHandleId();
            activeRequestHandleId = handleId;
            completedRequestHandleId = InvalidHandleId;
            failedRequestHandleId = InvalidHandleId;
            cancelledRequestHandleId = InvalidHandleId;
            activeRequestIsExpansion = false;
            compressionProgress01 = 0f;
            compressionProgressStage = ProgressStagePending;
            activeRequestReceiver = eventReceiver;
            sourceTexture = inputTexture;
            copiedInputHandleId = InvalidHandleId;
            sourceTextureSrgb = inputSrgb;
            quality = Mathf.Clamp(qualityValue, MinQuality, MaxQuality);
            pendingRequestUsesCapacityLimit = maximumCompressedBytes > 0;
            if (pendingRequestUsesCapacityLimit)
            {
                maxImageBytes = Mathf.Max(MinImageBytes, maximumCompressedBytes);
            }
            SendCustomEventDelayedFrames(nameof(_BeginRequestedCompression), 1);
            return handleId;
        }

        // 次frameへ遅延した圧縮要求を開始する
        public void _BeginRequestedCompression()
        {
            if (activeRequestHandleId == InvalidHandleId || activeRequestIsExpansion)
            {
                return;
            }

            if (inputCopyPending) { PrepareInputCopy(); return; }
            if (isRunning) return;
            ResetGpuDiagnostics("compression");
            BeginGpuDiagnosticStage(nameof(_BeginRequestedCompression), -1, false);
            compressionProgress01 = 0.02f;
            compressionProgressStage = ProgressStagePreparing;
            BeginCompressSourceTextureInternal(pendingRequestUsesCapacityLimit);
            if (activeRequestHandleId != InvalidHandleId && (!isRunning || !compressionPending))
            {
                FailCompressedBytes(status != null && status.Length > 0
                    ? status
                    : "DCTH compression did not start.");
            }
        }

        // 展開を要求しhandle IDを返す
        public int RequestExpansion(byte[] inputBytes, UdonBehaviour eventReceiver)
        {
            if (!CanAcceptRequest) return InvalidHandleId;
            if (inputBytes == null || inputBytes.Length < CompressedByteHeaderBytes
                || inputBytes.Length > Mathf.Max(maxImageBytes, MinImageBytes)
                || eventReceiver == null || isRunning
                || warmupRunning || failedGpuResourceReleasePending || activeRequestHandleId != InvalidHandleId
                || activeWarmupHandleId != InvalidHandleId)
            {
                SetStatus(eventReceiver == null
                    ? "DCTH expansion event receiver is missing."
                    : "DCTH expansion request is invalid or already pending.");
                return InvalidHandleId;
            }

            _ClearOutputTexture();
            int handleId = GenerateRequestHandleId();
            activeRequestHandleId = handleId;
            completedRequestHandleId = InvalidHandleId;
            failedRequestHandleId = InvalidHandleId;
            cancelledRequestHandleId = InvalidHandleId;
            activeRequestIsExpansion = true;
            activeRequestReceiver = eventReceiver;
            sourceBytes = inputBytes;
            SendCustomEventDelayedFrames(nameof(_BeginRequestedExpansion), 1);
            return handleId;
        }

        // Requested 展開を開始する
        public void _BeginRequestedExpansion()
        {
            if (activeRequestHandleId == InvalidHandleId || !activeRequestIsExpansion || isRunning)
            {
                return;
            }

            ResetGpuDiagnostics("expansion");
            BeginGpuDiagnosticStage(nameof(_BeginRequestedExpansion), -1, false);
            _LoadSourceBytesToTexture();
            if (activeRequestHandleId != InvalidHandleId && !isRunning && !expandComplete && !outputReady)
            {
                FailHuffmanDecode(status != null && status.Length > 0
                    ? status
                    : "DCTH expansion did not start.");
            }
        }

        // 圧縮をキャンセルする
        public bool CancelCompression(int handleId)
        {
            if (handleId == InvalidHandleId || handleId != activeRequestHandleId || activeRequestIsExpansion)
            {
                return false;
            }
            cancelledRequestHandleId = handleId;
            cancelledRequestWasExpansion = false;
            _StopCompression();
            return true;
        }

        // 展開をキャンセルする
        public bool CancelExpansion(int handleId)
        {
            if (handleId == InvalidHandleId || handleId != activeRequestHandleId || !activeRequestIsExpansion)
            {
                return false;
            }
            cancelledRequestHandleId = handleId;
            cancelledRequestWasExpansion = true;
            _ClearSourceBytes();
            _ClearCompressedBytes();
            // 終了通知の受信先が次の要求を開始できるよう、最後に停止する
            _StopCompression();
            return true;
        }

        // Compress 入力 Texture Internalを開始する
        private void BeginCompressSourceTextureInternal(bool useCapacityLimit)
        {
            activeCompressionUsesCapacityLimit = useCapacityLimit;
            // public入口は新しいsourceTextureを開始する。前回の正規化Textureを残すのは
            // 品質低下retryの内部経路だけなので、通常開始時は参照を必ず切り替える
            activeEncodeSourceTexture = null;
            activeEncodeSourceIsRawStorage = false;
            ResetAutoRetryState();
            if (useCapacityLimit)
            {
                capacityTotalStartedMs = GetNowMillis();
                capacityPrepassRequestedQuality = quality;
            }
            BeginCompressInputTextureToBytes();
        }

        // block圧縮libraryと名称を揃えた共通decode入口
        public void _LoadSourceBytesToTexture()
        {
            compressedBytes = sourceBytes != null ? sourceBytes : GetEmptyBytes();
            compressedBytesReady = compressedBytes.Length >= CompressedByteHeaderBytes;
            compressedBytesFailed = false;
            sourceByteCount = compressedBytes.Length;
            compressedByteCount = compressedBytes.Length;
            BeginDecompressCompressedBytes(true, false);
        }

        // 実行中 展開 Progress01を計算する
        private float CalculateActiveExpansionProgress01()
        {
            if (expandComplete)
            {
                return 1f;
            }
            if (!currentOperationIsExpansion || !isRunning)
            {
                return 0f;
            }

            int byteCount = compressedBytes != null ? compressedBytes.Length : 0;
            if (byteCount > CompressedByteHeaderBytes && restoreDataOffset < byteCount)
            {
                int restoredBytes = restoreDataOffset + Mathf.Max(restoreSegmentCopyOffset, 0);
                float restored = (float)(restoredBytes - CompressedByteHeaderBytes)
                    / Mathf.Max(byteCount - CompressedByteHeaderBytes, 1);
                return Mathf.Clamp01(restored) * 0.15f;
            }

            if (huffmanDecodeComplete)
            {
                return 0.95f;
            }

            int plane = Mathf.Clamp(decodePlane, 0, 4);
            int stage = Mathf.Clamp(decodeStage, 0, 5);
            float stageProgress = GetDecodeStageProgress01(plane, stage);
            float decoded = (plane + (stage + stageProgress) / 6f) / 4f;
            return 0.15f + Mathf.Clamp01(decoded) * 0.8f;
        }

        // 圧縮 状態を返す
        public int GetCompressionState(int handleId)
        {
            if (handleId == InvalidHandleId) return TaskStateUnknown;
            if (handleId == activeRequestHandleId && !activeRequestIsExpansion)
            {
                return isRunning ? TaskStateRunning : TaskStatePending;
            }
            if (handleId == completedRequestHandleId && !completedRequestWasExpansion) return TaskStateSucceeded;
            if (handleId == failedRequestHandleId && !failedRequestWasExpansion) return TaskStateFailed;
            if (handleId == cancelledRequestHandleId && !cancelledRequestWasExpansion) return failedGpuResourceReleasePending ? TaskStateCancelling : TaskStateCancelled;
            return TaskStateUnknown;
        }

        // 圧縮 段階を返す
        public int GetCompressionStage(int handleId)
        {
            if (handleId == completedRequestHandleId && !completedRequestWasExpansion) return ProgressStageComplete;
            if (handleId == activeRequestHandleId && !activeRequestIsExpansion) return compressionProgressStage;
            return ProgressStageUnknown;
        }

        // 圧縮 Progress01を返す
        public float GetCompressionProgress01(int handleId)
        {
            // 実際に完了したsubstage/readback byte数から更新した値を返す
            // UI向けの平滑化や推定時間による水増しは呼び出し側で行い、この値には混ぜない
            if (handleId == completedRequestHandleId && !completedRequestWasExpansion) return 1f;
            if (handleId == activeRequestHandleId && !activeRequestIsExpansion) return Mathf.Clamp01(compressionProgress01);
            return -1f;
        }

        // 容量 事前計算 候補 画質を返す
        public int GetCapacityPrepassCandidateQuality()
        {
            return capacityPrepassCandidateQuality;
        }

        // 容量 事前計算 候補 Attempt Numberを返す
        public int GetCapacityPrepassCandidateAttemptNumber()
        {
            return capacityPrepassCandidateAttempt + 1;
        }

        // 容量 事前計算 候補 Attempt 数を返す
        public int GetCapacityPrepassCandidateAttemptCount()
        {
            return CapacityPrepassCandidateAttemptCount;
        }

        // 圧縮 readback Detailを返す
        public string GetCompressionReadbackDetail()
        {
            if (huffmanReadbackKind != HuffmanReadbackKindMetadata
                && huffmanReadbackKind != HuffmanReadbackKindPayload)
            {
                return "";
            }

            string detail = GetPlaneName(huffmanReadbackPlane)
                + " " + GetHuffmanReadbackKindName(huffmanReadbackKind);
            if (huffmanReadbackRequestQueued)
            {
                detail += " request";
            }
            else if (huffmanReadbackPending)
            {
                detail += " wait";
            }
            if (huffmanReadbackRegionWidth > 0 && huffmanReadbackRegionHeight > 0)
            {
                detail += " " + huffmanReadbackRegionWidth.ToString()
                    + "x" + huffmanReadbackRegionHeight.ToString();
            }
            return detail;
        }

        // 圧縮 処理中 Detailを返す
        public string GetCompressionProcessingDetail()
        {
            int plane = Mathf.Clamp(huffmanReadbackPlane, PlaneY, PlaneCr);
            if (gpuFenceMarkerQueued)
            {
                return GetPlaneName(plane) + " fence marker";
            }
            if (gpuFenceReadbackRequestQueued)
            {
                return GetPlaneName(plane) + " fence request";
            }
            if (gpuFenceReadbackPending)
            {
                return GetPlaneName(plane) + " fence wait";
            }
            if (huffmanReadbackDrainFramesRemaining > 0)
            {
                int completed = HuffmanReadbackDrainFrameCount - huffmanReadbackDrainFramesRemaining;
                return GetPlaneName(plane) + " drain "
                    + completed.ToString() + "/" + HuffmanReadbackDrainFrameCount.ToString();
            }
            if (huffmanReadbackRequestQueued)
            {
                return GetPlaneName(plane) + " request";
            }
            if (huffmanReadbackTotalWeightBytes <= 0)
            {
                return "source";
            }

            string stageName = GetCompressionGpuStageName(gpuStage);
            int completedSubStages = GetCompressionGpuStageCompletedSubStages(plane, gpuStage);
            int totalSubStages = GetCompressionGpuStageSubStageCount(plane, gpuStage);
            return GetPlaneName(plane) + " " + stageName + " "
                + Mathf.Clamp(completedSubStages, 0, totalSubStages).ToString()
                + "/" + Mathf.Max(totalSubStages, 1).ToString();
        }

        // 展開 状態を返す
        public int GetExpansionState(int handleId)
        {
            if (handleId == InvalidHandleId) return TaskStateUnknown;
            if (handleId == activeRequestHandleId && activeRequestIsExpansion)
            {
                return isRunning ? TaskStateRunning : TaskStatePending;
            }
            if (handleId == completedRequestHandleId && completedRequestWasExpansion) return TaskStateSucceeded;
            if (handleId == failedRequestHandleId && failedRequestWasExpansion) return TaskStateFailed;
            if (handleId == cancelledRequestHandleId && cancelledRequestWasExpansion) return failedGpuResourceReleasePending ? TaskStateCancelling : TaskStateCancelled;
            return TaskStateUnknown;
        }

        // 展開 段階を返す
        public int GetExpansionStage(int handleId)
        {
            if (handleId == completedRequestHandleId && completedRequestWasExpansion) return ProgressStageComplete;
            if (handleId != activeRequestHandleId || !activeRequestIsExpansion) return ProgressStageUnknown;
            if (!isRunning) return ProgressStagePending;

            float progress = CalculateActiveExpansionProgress01();
            if (progress < 0.15f) return ProgressStagePreparing;
            if (progress < 0.95f) return ProgressStageProcessing;
            return ProgressStageFinalizing;
        }

        // 展開 Progress01を返す
        public float GetExpansionProgress01(int handleId)
        {
            // 展開も実際のdecode work unitだけを公開し、表示都合の補間は行わない
            if (handleId == completedRequestHandleId && completedRequestWasExpansion) return 1f;
            if (handleId != activeRequestHandleId || !activeRequestIsExpansion) return -1f;
            return isRunning ? CalculateActiveExpansionProgress01() : 0f;
        }

        // 圧縮 warmupをキャンセルする
        public bool CancelCompressionWarmup(int handleId)
        {
            return CancelWarmupRequest(handleId, WarmupModeCompression, "DCTH compression warmup cancelled.");
        }

        // 展開 warmupをキャンセルする
        public bool CancelExpansionWarmup(int handleId)
        {
            return CancelWarmupRequest(handleId, WarmupModeExpansion, "DCTH expansion warmup cancelled.");
        }

        // warmup requestをキャンセルする
        private bool CancelWarmupRequest(int handleId, int requestedMode, string message)
        {
            if (handleId == InvalidHandleId || handleId != activeWarmupHandleId
                || activeWarmupRequestMode != requestedMode)
            {
                return false;
            }

            cancelledWarmupHandleId = activeWarmupHandleId;
            cancelledWarmupRequestMode = activeWarmupRequestMode;
            activeWarmupHandleId = InvalidHandleId;
            activeWarmupRequestReceiver = null;
            warmupRequestStartedAtRealtime = 0f;
            CancelWarmup();
            warmupFailed = false;
            SetStatus(message);
            DumpGpuDiagnostics("warmup-cancelled");
            ReleaseFailedGpuResourcesWhenReadbacksComplete();
            return true;
        }

        // 圧縮 warmup 状態を返す
        public int GetCompressionWarmupState(int handleId)
        {
            return GetWarmupStateForMode(handleId, WarmupModeCompression);
        }

        // 展開 warmup 状態を返す
        public int GetExpansionWarmupState(int handleId)
        {
            return GetWarmupStateForMode(handleId, WarmupModeExpansion);
        }

        // warmup 状態用modeを返す
        private int GetWarmupStateForMode(int handleId, int requestedMode)
        {
            if (handleId == InvalidHandleId) return TaskStateUnknown;
            if (handleId == activeWarmupHandleId && activeWarmupRequestMode == requestedMode)
            {
                return warmupRunning ? TaskStateRunning : TaskStatePending;
            }
            if (handleId == completedWarmupHandleId && completedWarmupRequestMode == requestedMode)
            {
                return TaskStateSucceeded;
            }
            if (handleId == failedWarmupHandleId && failedWarmupRequestMode == requestedMode)
            {
                return TaskStateFailed;
            }
            if (handleId == cancelledWarmupHandleId && cancelledWarmupRequestMode == requestedMode)
            {
                return TaskStateCancelled;
            }
            return TaskStateUnknown;
        }

        // 圧縮 warmup 段階を返す
        public int GetCompressionWarmupStage(int handleId)
        {
            return GetWarmupStageForMode(handleId, WarmupModeCompression);
        }

        // 展開 warmup 段階を返す
        public int GetExpansionWarmupStage(int handleId)
        {
            return GetWarmupStageForMode(handleId, WarmupModeExpansion);
        }

        // warmup 段階用modeを返す
        private int GetWarmupStageForMode(int handleId, int requestedMode)
        {
            if (handleId == completedWarmupHandleId && completedWarmupRequestMode == requestedMode)
            {
                return ProgressStageComplete;
            }
            if (handleId != activeWarmupHandleId || activeWarmupRequestMode != requestedMode)
            {
                return ProgressStageUnknown;
            }
            if (!warmupRunning) return ProgressStagePending;
            if (warmupStep <= 1) return ProgressStagePreparing;
            if (warmupStep == 2) return ProgressStageProcessing;
            return ProgressStageFinalizing;
        }

        // 圧縮 warmup Progress01を返す
        public float GetCompressionWarmupProgress01(int handleId)
        {
            return GetWarmupProgressForMode01(handleId, WarmupModeCompression);
        }

        // 展開 warmup Progress01を返す
        public float GetExpansionWarmupProgress01(int handleId)
        {
            return GetWarmupProgressForMode01(handleId, WarmupModeExpansion);
        }

        // warmup 進捗用Mode01を返す
        private float GetWarmupProgressForMode01(int handleId, int requestedMode)
        {
            if (handleId == completedWarmupHandleId && completedWarmupRequestMode == requestedMode)
            {
                return 1f;
            }
            if (handleId != activeWarmupHandleId || activeWarmupRequestMode != requestedMode)
            {
                return -1f;
            }
            if (!warmupRunning) return 0f;
            if (warmupStep == 0) return 0.02f;
            if (warmupStep == 1)
            {
                int coreStepCount = warmupMode == WarmupModeExpansion
                    ? WarmupExpansionCoreStepCount
                    : WarmupCompressionCoreStepCount;
                return 0.02f + Mathf.Clamp01((float)warmupCoreResourceStep / coreStepCount) * 0.1f;
            }
            if (warmupStep == 2)
            {
                return 0.12f + Mathf.Clamp01((float)warmupResourceStep / WarmupRenderTextureStepCount) * 0.23f;
            }
            if (warmupStep == 3) return 0.36f;
            if (warmupStep == 4)
            {
                return 0.36f + Mathf.Clamp01((float)warmupShaderStep / WarmupShaderStepCount) * 0.56f;
            }
            return warmupGpuReadbackPending ? 0.98f : 0.95f;
        }

        // 復号 段階 Progress01を返す
        private float GetDecodeStageProgress01(int plane, int stage)
        {
            if (plane < 0 || plane >= 4)
            {
                return 1f;
            }
            if (stage == 0)
            {
                return Mathf.Clamp01((float)decodeUploadStep / Mathf.Max(DecodeUploadAndLookupStepCount, 1));
            }
            if (stage == 1)
            {
                int total = DecodeBitOffsetLocalGroupCount * GetDecodeBitOffsetChunkRowGroupCount(plane);
                return Mathf.Clamp01((float)decodeLocalIndex / Mathf.Max(total, 1));
            }
            if (stage == 2)
            {
                int total = DecodeRleSlotGroupCount * GetDecodeRleChunkRowGroupCount(plane);
                return Mathf.Clamp01((float)decodeLocalIndex / Mathf.Max(total, 1));
            }
            if (stage == 4)
            {
                return Mathf.Clamp01((float)decodeLocalIndex / RleToSymbolFixedStepCount);
            }
            if (stage == 3)
            {
                int blockTotal = GetBlockWidth(plane) * GetBlockHeight(plane);
                int scanPassCount = GetDcScanPassCount(blockTotal);
                int rowGroupCount = GetDecodeDcScanBlockRowGroupCount(plane);
                int total = 2 + scanPassCount * (rowGroupCount + 1);
                return Mathf.Clamp01((float)decodeLocalIndex / Mathf.Max(total, 1));
            }

            int idctStepCount = DecodeIdctLocalGroupCount * GetDecodeIdctBlockRowGroupCount(plane);
            int idctTotal = 1 + idctStepCount * 2;
            return Mathf.Clamp01((float)decodeLocalIndex / Mathf.Max(idctTotal, 1));
        }

        // ASTC/BC7と共通の解放IF。DCTH内部ではnullの代わりに共有の空配列を使い、
        // Udon上で繰り返しclearした際の不要な配列割り当てを避ける
        public void _ClearSourceBytes()
        {
            sourceBytes = GetEmptyBytes();
            sourceByteCount = 0;
        }

        // 圧縮結果 byte列を初期化する
        public void _ClearCompressedBytes()
        {
            compressedBytes = GetEmptyBytes();
            compressedByteCount = 0;
        }

        // 出力 Textureを初期化する
        public void _ClearOutputTexture()
        {
            // 実行中の最終RTは内部処理が使用するため、外部clearでは解放しない
            if (IsBusy) return;
            RenderTexture result = expansionResultTexture;
            expansionResultTexture = null;
            if (result != null && result != postprocessedOutputRenderTexture)
            {
                if (result.IsCreated()) result.Release();
                Destroy(result);
            }
            outputReady = false;
            outputTexture = null;
            ReleaseIntermediateRenderTextures();
        }

        // 再試行 圧縮 After 画質 Changeを処理する
        public void _RetryCompressionAfterQualityChange()
        {
            // Mobileのframe遅延はcancelできないため、停止後に残ったretry eventを無視する
            if (!compressionPending || activeRequestIsExpansion)
            {
                return;
            }

            // 容量超過retryは同じタスク内でfull encodeからやり直す
            // 前回の91%を保持するとcompress/finalizeが同じ表示位置で繰り返して見えるため、
            // 実際に再開する工程の先頭へ戻す
            compressionProgress01 = activeCompressionUsesCapacityLimit
                ? CompressionProgressFullEncodeStart
                : 0.2f;
            compressionProgressStage = ProgressStageProcessing;
            BeginCompressInputTextureToBytes();
        }

        // 内部encode scheduler。Y、必要ならA/Cb/Crの順で処理し、各planeを5 byte segmentへまとめる
        private void BeginCompressInputTextureToBytes()
        {
            ResetRunState();
            // 前回の失敗量を次の試行へ持ち越すと、別planeの予測値でqualityを下げてしまう
            ResetQualityRetryPredictionInput();
            if (EnableTimingDiagnostics)
            {
                ResetTimingBytes();
                WriteTimingMillis(TimingOffsetRunStartMs, GetNowMillis());
            }
            Texture activeSourceTexture = GetCompressInputTexture();
            if (activeSourceTexture == null)
            {
                SetStatus("Input texture is missing.");
                return;
            }

            if (!IsValidCodecDimensions(activeSourceTexture.width, activeSourceTexture.height))
            {
                SetStatus("Input texture size is outside the supported 1.." + MaxCodecTextureSize.ToString() + " range.");
                return;
            }

            activeSourceTexture.wrapMode = TextureWrapMode.Clamp;
            activeEncodeSourceTexture = activeSourceTexture;
            activeEncodeSourceIsRawStorage = false;
            activeEncodeSrgb = encodeSrgb;
            activeSourceSrgb = sourceTextureSrgb;

            if (!ValidateStage1())
            {
                return;
            }

            isRunning = true;
            currentOperationIsExpansion = false;
            operationLastProgressAtRealtime = Time.realtimeSinceStartup;
            operationProgressSignature = GetOperationProgressSignature();
            compressionPending = true;
            compressionComplete = false;
            compressionFailed = false;
            expandComplete = false;
            expandFailed = false;
            sourceWidth = Mathf.Max(activeSourceTexture.width, 1);
            sourceHeight = Mathf.Max(activeSourceTexture.height, 1);
            activeEncodeHasAlpha = hasAlpha;
            compressedByteCount = 0;
            retryCompressionOnFailure = true;
            latestLocalImageId++;
            lastStage1EncodeMs = 0f;
            lastHuffmanEncodeMs = 0f;
            lastHuffmanDecodeMs = 0f;
            latestTimingSummary = "";

            if (EnableTimingDiagnostics)
            {
                LogTiming("run start imageId=" + latestLocalImageId.ToString()
                    + " input=" + GetInputWidth().ToString() + "x" + GetInputHeight().ToString()
                    + " padded=" + GetPlaneWidth(PlaneY).ToString() + "x" + GetPlaneHeight(PlaneY).ToString()
                    + " quality=" + quality.ToString()
                    + " preset=" + quantPreset.ToString()
                    + " alpha=" + (hasAlpha ? "1" : "0")
                    + " color=" + (sendColor ? "1" : "0")
                    + " halfCbCr=" + (halfSizeCbCr ? "1" : "0")
                    + " dct=" + GetDctPrecisionLabel());
            }
            BeginSourceNormalizationReadbackOrHuffmanSequence(activeSourceTexture);
        }

        // 内部decode scheduler。byte segmentを復元してpayload Textureへuploadし、GPU Huffman/DCT decodeへ進む
        private bool BeginDecompressCompressedBytes(bool resetDecodeState, bool allowRetryOnFailure)
        {
            if (resetDecodeState)
            {
                ResetDecodeRunState();
                if (EnableTimingDiagnostics)
                {
                    ResetTimingBytes();
                    WriteTimingMillis(TimingOffsetRunStartMs, GetNowMillis());
                }
            }

            retryCompressionOnFailure = allowRetryOnFailure;
            currentOperationIsExpansion = true;
            ClearHuffmanBytes();
            expandComplete = false;
            expandFailed = false;
            return BeginRestorePlaneBytesFromCompressedBytes();
        }

        // 圧縮を停止する
        public void _StopCompression()
        {
            failureNotificationPending = false;
            inputCopyPending = false;
            if (activeRequestHandleId != InvalidHandleId)
            {
                cancelledRequestHandleId = activeRequestHandleId;
                cancelledRequestWasExpansion = activeRequestIsExpansion;
                cancelledOperationIsExpansion = activeRequestIsExpansion;
                cancellationReceiver = activeRequestReceiver;
                cancellationNotificationPending = true;
            }
            DumpGpuDiagnostics("operation-stopped");
            CapturePendingGpuReadbacksForRelease();
            CancelGpuStageDelay();
            CancelWarmup();
            ResetCpuHuffmanState();
            sourceNormalizationReadbackRequestQueued = false;
            sourceNormalizationReadbackPending = false;
            sourceNormalizationReadbackTexture = null;
            ClearPendingSourceNormalizationBytes();
            activeEncodeSourceTexture = null;
            activeEncodeSourceIsRawStorage = false;
            activeCompressionUsesCapacityLimit = false;
            huffmanReadbackPending = false;
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            capacityPrepassReadbackRequestQueued = false;
            capacityPrepassReadbackPending = false;
            capacityPrepassReadbackDrainFramesRemaining = 0;
            sourceAlphaScanPending = false;
            encodeSubStage = 0;
            activeEncodeHasAlpha = false;
            decodeUploadStep = 0;
            decodeUploadElapsedMs = 0f;
            ClearPendingByteCopyState();
            ClearPendingPayloadStore();
            ClearPendingChunkOffsetBuild();
            ClearPendingDecodePayloadUploadBuild();
            isRunning = false;
            operationLastProgressAtRealtime = 0f;
            outputReady = false;
            compressionPending = false;
            compressionComplete = false;
            compressionFailed = false;
            expandComplete = false;
            expandFailed = false;
            currentOperationIsExpansion = false;
            if (!failedGpuResourceReleasePending)
            {
                ClearOutputRenderTextures();
            }
            ResetAutoRetryState();
            activeRequestHandleId = InvalidHandleId;
            activeRequestReceiver = null;
            SetStatus("Stopped.");
            ReleaseFailedGpuResourcesWhenReadbacksComplete();
        }

        // Inspectionを停止する
        private void StopInspection(string message)
        {
            CapturePendingGpuReadbacksForRelease();
            CancelGpuStageDelay();
            CancelWarmup();
            ResetCpuHuffmanState();
            sourceNormalizationReadbackRequestQueued = false;
            sourceNormalizationReadbackPending = false;
            sourceNormalizationReadbackTexture = null;
            ClearPendingSourceNormalizationBytes();
            activeEncodeSourceTexture = null;
            activeEncodeSourceIsRawStorage = false;
            huffmanReadbackPending = false;
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            encodeSubStage = 0;
            activeEncodeHasAlpha = false;
            decodeUploadStep = 0;
            decodeUploadElapsedMs = 0f;
            ClearPendingByteCopyState();
            isRunning = false;
            SetStatus(message);
            ReleaseFailedGpuResourcesWhenReadbacksComplete();
        }

        // runtime設定入口。quality低下は通常byte[]を小さくし、alpha/colorやfull-size CbCrは通常大きくする
        public void ApplySettings(int qualityValue, int quantPresetValue, bool hasAlphaValue, bool sendColorValue, bool halfSizeCbCrValue, int maxImageBytesValue)
        {
            quality = Mathf.Clamp(qualityValue, MinQuality, MaxQuality);
            quantPreset = Mathf.Clamp(quantPresetValue, 0, MaxQuantPreset);
            hasAlpha = hasAlphaValue;
            sendColor = sendColorValue;
            halfSizeCbCr = halfSizeCbCrValue;
            maxImageBytes = Mathf.Max(MinImageBytes, maxImageBytesValue);
            inspectionStopStep = Mathf.Clamp(inspectionStopStep, 0, MaxInspectionStopStep);
        }

        // GPU完了後に作業用RTを解放し、未取得の完成画像だけは保持する
        private void ReleaseIntermediateRenderTextures()
        {
            ResetCpuHuffmanState();
            ReleaseCapacityPrepassResources();
            ReleaseWarmupCoreTemporaryResources();
            warmupSourceTexture = ReleaseRuntimeRenderTexture(warmupSourceTexture);
            // 完成RTは結果として保持する。所有権はTakeExpansionResultで取り出すまで
            // ライブラリに残り、未取得なら次の展開要求やDisposeで破棄する
            bool detachCompletedOutput = outputReady
                && outputTexture != null
                && outputTexture == postprocessedOutputRenderTexture;
            if (detachCompletedOutput)
            {
                // 最終RTを一時作業用RTの解放対象から外し、結果の参照で保持する
                postprocessedOutputRenderTexture = null;
            }
            else if (outputTexture == postprocessedOutputRenderTexture)
            {
                outputTexture = null;
            }

            work = ReleaseRuntimeRenderTexture(work);
            coefficients = ReleaseRuntimeRenderTexture(coefficients);
            planeYTexture = ReleaseRuntimeRenderTexture(planeYTexture);
            planeATexture = ReleaseRuntimeRenderTexture(planeATexture);
            planeCbTexture = ReleaseRuntimeRenderTexture(planeCbTexture);
            planeCrTexture = ReleaseRuntimeRenderTexture(planeCrTexture);
            rleSymbols = ReleaseRuntimeRenderTexture(rleSymbols);
            rleAcFrequencyRows = ReleaseRuntimeRenderTexture(rleAcFrequencyRows);
            rleAcFrequencyRowsTemp = ReleaseRuntimeRenderTexture(rleAcFrequencyRowsTemp);
            rleAcFrequency = ReleaseRuntimeRenderTexture(rleAcFrequency);
            dcValues = ReleaseRuntimeRenderTexture(dcValues);
            dcDelta = ReleaseRuntimeRenderTexture(dcDelta);
            dcFrequencyRows = ReleaseRuntimeRenderTexture(dcFrequencyRows);
            dcFrequencyRowsTemp = ReleaseRuntimeRenderTexture(dcFrequencyRowsTemp);
            dcFrequency = ReleaseRuntimeRenderTexture(dcFrequency);
            huffmanRawLengths = ReleaseRuntimeRenderTexture(huffmanRawLengths);
            huffmanRawLengthSingle = ReleaseRuntimeRenderTexture(huffmanRawLengthSingle);
            huffmanRawLengthHistogram = ReleaseRuntimeRenderTexture(huffmanRawLengthHistogram);
            huffmanRawLengthHistogramTemp = ReleaseRuntimeRenderTexture(huffmanRawLengthHistogramTemp);
            huffmanLimitedLengthHistogram = ReleaseRuntimeRenderTexture(huffmanLimitedLengthHistogram);
            huffmanLimitedLengthHistogramTemp = ReleaseRuntimeRenderTexture(huffmanLimitedLengthHistogramTemp);
            dcHuffmanLengths = ReleaseRuntimeRenderTexture(dcHuffmanLengths);
            dcHuffmanCodes = ReleaseRuntimeRenderTexture(dcHuffmanCodes);
            acHuffmanLengths = ReleaseRuntimeRenderTexture(acHuffmanLengths);
            acHuffmanCodes = ReleaseRuntimeRenderTexture(acHuffmanCodes);
            huffmanBlockBits = ReleaseRuntimeRenderTexture(huffmanBlockBits);
            huffmanBlockOverflowMask = ReleaseRuntimeRenderTexture(huffmanBlockOverflowMask);
            huffmanBlockOverflowMaskTemp = ReleaseRuntimeRenderTexture(huffmanBlockOverflowMaskTemp);
            huffmanBlockPage = ReleaseRuntimeRenderTexture(huffmanBlockPage);
            huffmanBlockPageTemp = ReleaseRuntimeRenderTexture(huffmanBlockPageTemp);
            huffmanBlockPageState = ReleaseRuntimeRenderTexture(huffmanBlockPageState);
            huffmanBlockPageStateTemp = ReleaseRuntimeRenderTexture(huffmanBlockPageStateTemp);
            huffmanBlockValidBytes = ReleaseRuntimeRenderTexture(huffmanBlockValidBytes);
            huffmanChunkValidBytes = ReleaseRuntimeRenderTexture(huffmanChunkValidBytes);
            huffmanChunkValidBytesMip = ReleaseRuntimeRenderTexture(huffmanChunkValidBytesMip);
            huffmanChunkValidBytesMipTemp = ReleaseRuntimeRenderTexture(huffmanChunkValidBytesMipTemp);
            huffmanPayloadGather = ReleaseRuntimeRenderTexture(huffmanPayloadGather);
            huffmanMetadataPack = ReleaseRuntimeRenderTexture(huffmanMetadataPack);
            yHuffmanPayloadTexture = ReleaseRuntimeRenderTexture(yHuffmanPayloadTexture);
            aHuffmanPayloadTexture = ReleaseRuntimeRenderTexture(aHuffmanPayloadTexture);
            cbHuffmanPayloadTexture = ReleaseRuntimeRenderTexture(cbHuffmanPayloadTexture);
            crHuffmanPayloadTexture = ReleaseRuntimeRenderTexture(crHuffmanPayloadTexture);
            yHuffmanMetadataTexture = ReleaseRuntimeRenderTexture(yHuffmanMetadataTexture);
            aHuffmanMetadataTexture = ReleaseRuntimeRenderTexture(aHuffmanMetadataTexture);
            cbHuffmanMetadataTexture = ReleaseRuntimeRenderTexture(cbHuffmanMetadataTexture);
            crHuffmanMetadataTexture = ReleaseRuntimeRenderTexture(crHuffmanMetadataTexture);
            yChunkValidTexture = ReleaseRuntimeRenderTexture(yChunkValidTexture);
            aChunkValidTexture = ReleaseRuntimeRenderTexture(aChunkValidTexture);
            cbChunkValidTexture = ReleaseRuntimeRenderTexture(cbChunkValidTexture);
            crChunkValidTexture = ReleaseRuntimeRenderTexture(crChunkValidTexture);
            yDcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(yDcHuffmanCodesTexture);
            aDcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(aDcHuffmanCodesTexture);
            cbDcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(cbDcHuffmanCodesTexture);
            crDcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(crDcHuffmanCodesTexture);
            yAcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(yAcHuffmanCodesTexture);
            aAcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(aAcHuffmanCodesTexture);
            cbAcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(cbAcHuffmanCodesTexture);
            crAcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(crAcHuffmanCodesTexture);
            huffmanPayloadBitOffset = ReleaseRuntimeRenderTexture(huffmanPayloadBitOffset);
            huffmanPayloadBitOffsetTemp = ReleaseRuntimeRenderTexture(huffmanPayloadBitOffsetTemp);
            huffmanDecodeState = ReleaseRuntimeRenderTexture(huffmanDecodeState);
            huffmanDecodeStateTemp = ReleaseRuntimeRenderTexture(huffmanDecodeStateTemp);
            huffmanAcDecodeLengthSummary = ReleaseRuntimeRenderTexture(huffmanAcDecodeLengthSummary);
            huffmanAcDecodeSymbolTable = ReleaseRuntimeRenderTexture(huffmanAcDecodeSymbolTable);
            huffmanAcDecodeSymbolTableWork = ReleaseRuntimeRenderTexture(huffmanAcDecodeSymbolTableWork);
            huffmanAcDecodeSymbolTableTemp = ReleaseRuntimeRenderTexture(huffmanAcDecodeSymbolTableTemp);
            rleSymbolsFromHuffmanPayload = ReleaseRuntimeRenderTexture(rleSymbolsFromHuffmanPayload);
            dcDeltaFromHuffmanPayload = ReleaseRuntimeRenderTexture(dcDeltaFromHuffmanPayload);
            dcScanFromHuffmanPayload = ReleaseRuntimeRenderTexture(dcScanFromHuffmanPayload);
            dcScanFromHuffmanPayloadTemp = ReleaseRuntimeRenderTexture(dcScanFromHuffmanPayloadTemp);
            symbolFixedFromHuffmanPayload = ReleaseRuntimeRenderTexture(symbolFixedFromHuffmanPayload);
            symbolFixedWorkFromHuffmanPayload = ReleaseRuntimeRenderTexture(symbolFixedWorkFromHuffmanPayload);
            reconstructedFromHuffmanPayload = ReleaseRuntimeRenderTexture(reconstructedFromHuffmanPayload);
            packedDecodedPlanes = ReleaseRuntimeRenderTexture(packedDecodedPlanes);
            colorDownsampleTexture = ReleaseRuntimeRenderTexture(colorDownsampleTexture);
            gpuFenceMarkerTexture = ReleaseRuntimeRenderTexture(gpuFenceMarkerTexture);
            postprocessedOutputRenderTexture = ReleaseRuntimeRenderTexture(postprocessedOutputRenderTexture);
            postprocessedOutputTextureSrgb = false;
            receivedPayloadTexture = ReleaseRuntimeTexture(receivedPayloadTexture);
            receivedChunkValidTexture = ReleaseRuntimeTexture(receivedChunkValidTexture);
            receivedChunkOffsetTexture = ReleaseRuntimeTexture(receivedChunkOffsetTexture);
            receivedDcHuffmanCodesTexture = ReleaseRuntimeTexture(receivedDcHuffmanCodesTexture);
            receivedAcHuffmanCodesTexture = ReleaseRuntimeTexture(receivedAcHuffmanCodesTexture);
        }

        // Completed Huffman plane 描画 Textureを解放する
        private void ReleaseCompletedHuffmanPlaneRenderTextures(int plane)
        {
            if (plane == PlaneY)
            {
                yHuffmanPayloadTexture = ReleaseRuntimeRenderTexture(yHuffmanPayloadTexture);
                yHuffmanMetadataTexture = ReleaseRuntimeRenderTexture(yHuffmanMetadataTexture);
                yChunkValidTexture = ReleaseRuntimeRenderTexture(yChunkValidTexture);
                yDcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(yDcHuffmanCodesTexture);
                yAcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(yAcHuffmanCodesTexture);
            }
            else if (plane == PlaneA)
            {
                aHuffmanPayloadTexture = ReleaseRuntimeRenderTexture(aHuffmanPayloadTexture);
                aHuffmanMetadataTexture = ReleaseRuntimeRenderTexture(aHuffmanMetadataTexture);
                aChunkValidTexture = ReleaseRuntimeRenderTexture(aChunkValidTexture);
                aDcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(aDcHuffmanCodesTexture);
                aAcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(aAcHuffmanCodesTexture);
            }
            else if (plane == PlaneCb)
            {
                cbHuffmanPayloadTexture = ReleaseRuntimeRenderTexture(cbHuffmanPayloadTexture);
                cbHuffmanMetadataTexture = ReleaseRuntimeRenderTexture(cbHuffmanMetadataTexture);
                cbChunkValidTexture = ReleaseRuntimeRenderTexture(cbChunkValidTexture);
                cbDcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(cbDcHuffmanCodesTexture);
                cbAcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(cbAcHuffmanCodesTexture);
            }
            else if (plane == PlaneCr)
            {
                crHuffmanPayloadTexture = ReleaseRuntimeRenderTexture(crHuffmanPayloadTexture);
                crHuffmanMetadataTexture = ReleaseRuntimeRenderTexture(crHuffmanMetadataTexture);
                crChunkValidTexture = ReleaseRuntimeRenderTexture(crChunkValidTexture);
                crDcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(crDcHuffmanCodesTexture);
                crAcHuffmanCodesTexture = ReleaseRuntimeRenderTexture(crAcHuffmanCodesTexture);
            }
        }

        // Stage1を検証する
        private bool ValidateStage1()
        {
            EnsureEncodeCoreRenderTextures();
            EnsureDynamicGammaEncodeTexture();
            EnsureDynamicQuantReciprocalTexture();
            quantTexture = EnsureRuntimeQuantTexture(quality, quantPreset);

            bool ok = quantTexture != null
                && quantReciprocalTexture != null
                && generatedGammaEncodeTexture != null
                && encodeMaterial != null
                && encodeVerticalMaterial != null
                && capacityPrepassEncodeMaterial != null
                && capacityPrepassVerticalMaterial != null
                && capacityPrepassPostMaterial != null
                && composeRgbaMaterial != null
                && work != null
                && coefficients != null
                && (!halfSizeCbCr || colorDownsampleMaterial != null);

            if (!ok)
            {
                SetStatus("Stage 1 skipped: missing DCT reference.");
            }

            return ok;
        }

        // 符号化 Core 描画 Textureを使用可能な状態にする
        private void EnsureEncodeCoreRenderTextures()
        {
            int width = GetPlaneWidth(PlaneY);
            int height = GetPlaneHeight(PlaneY);
            RenderTextureFormat dctFormat = GetSingleChannelDctRenderTextureFormat();
            work = EnsureRuntimeRenderTexture(work, "IC_Library_Work", width, height, dctFormat);
            coefficients = EnsureRuntimeRenderTexture(coefficients, "IC_Library_Coefficients", width, height, dctFormat);
        }

        // 復号 Core 描画 Textureを使用可能な状態にする
        private void EnsureDecodeCoreRenderTextures()
        {
            planeYTexture = ReleaseRuntimeRenderTexture(planeYTexture);
            planeATexture = ReleaseRuntimeRenderTexture(planeATexture);
            planeCbTexture = ReleaseRuntimeRenderTexture(planeCbTexture);
            planeCrTexture = ReleaseRuntimeRenderTexture(planeCrTexture);
            coefficients = ReleaseRuntimeRenderTexture(coefficients);
            EnsureDecodePlaneWorkTextures(PlaneY);
            packedDecodedPlanes = EnsureRuntimeRenderTexture(
                packedDecodedPlanes,
                "IC_Library_PackedDecodedPlanes",
                GetPlaneWidth(PlaneY),
                GetPlaneHeight(PlaneY),
                RenderTextureFormat.ARGBHalf);
            if (packedDecodedPlanes != null)
            {
                TrackedBlit(EnsureNeutralGrayTexture(), packedDecodedPlanes);
            }
        }

        // Postprocessed 出力 Textureを使用可能な状態にする
        private RenderTexture EnsurePostprocessedOutputTexture()
        {
            int width = GetRequestedOutputWidth();
            int height = GetRequestedOutputHeight();
            return EnsurePostprocessedOutputTexture(width, height);
        }

        // Postprocessed 出力 Textureを使用可能な状態にする
        private RenderTexture EnsurePostprocessedOutputTexture(int width, int height)
        {
            if (postprocessedOutputRenderTexture != null && postprocessedOutputTextureSrgb != activeSourceSrgb)
            {
                postprocessedOutputRenderTexture = ReleaseRuntimeRenderTexture(postprocessedOutputRenderTexture);
            }

            RenderTexture output = EnsureRuntimeRenderTexture(postprocessedOutputRenderTexture, "IC_Library_PostprocessedOutput", width, height, GetDisplayRenderTextureFormat(), false, false, activeSourceSrgb);
            postprocessedOutputRenderTexture = output;
            postprocessedOutputTextureSrgb = activeSourceSrgb;
            outputTexture = output;
            return output;
        }

        // 非同期GPU readbackの完了通知を処理する
        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest completedRequest)
        {
            // 実処理は保存中のcurrent requestをpollする。callbackは次run開始後に届く場合があり、
            // 「現在pendingか」だけで振り分けると古いreadbackを新runへ適用してしまう
        }

        // 実行中処理のtimeoutと進行状態を監視する
        public void Update()
        {
            if (!disableOperationTimeoutForGpuDiagnostics)
            {
                float now = Time.realtimeSinceStartup;
                float timeout = Mathf.Max(1f, operationTimeoutSeconds);
                if (activeWarmupHandleId != InvalidHandleId && !warmupRunning
                    && warmupRequestStartedAtRealtime > 0f
                    && now - warmupRequestStartedAtRealtime > timeout)
                {
                    FailWarmup("Warmup request timed out.");
                }
                if (warmupRunning && warmupLastProgressAtRealtime > 0f
                    && now - warmupLastProgressAtRealtime > timeout)
                {
                    FailWarmup("Warmup timed out.");
                }
                // 正常なDCTH展開はAndroidで60秒を超える。総所要時間ではなく、同じcursorのまま
                // readback待機などがtimeout秒以上続いた場合だけ失敗させる
                if (isRunning && operationLastProgressAtRealtime > 0f
                    && now - operationLastProgressAtRealtime > timeout)
                {
                    FailOperationTimeout();
                }
            }
            float frameSeconds = Time.deltaTime;
            RecordGpuDiagnosticFrame(frameSeconds);
            if (frameSeconds <= 0f || frameSeconds > 1f)
            {
                return;
            }

            if (warmupRunning)
            {
                warmupFrameSeconds += frameSeconds;
                warmupFrameCount++;
                if (warmupLastOperation >= 0 && frameSeconds > warmupWorstFrameSeconds)
                {
                    warmupWorstFrameSeconds = frameSeconds;
                    warmupWorstOperation = warmupLastOperation;
                }
            }

            if (!EnableTimingDiagnostics || !isRunning)
            {
                return;
            }

            timingFrameSeconds += frameSeconds;
            timingFrameCount++;
            if (frameSeconds > timingWorstFrameSeconds)
            {
                timingWorstFrameSeconds = frameSeconds;
            }

            RecordTimingPhaseFrame(timingCurrentFpsPhase, frameSeconds);
        }

        // 圧縮 warmupを要求しhandle IDを返す
        public int RequestCompressionWarmup(UdonBehaviour eventReceiver)
        {
            return RequestWarmupInternal(WarmupModeCompression, eventReceiver);
        }

        // 展開 warmupを要求しhandle IDを返す
        public int RequestExpansionWarmup(UdonBehaviour eventReceiver)
        {
            return RequestWarmupInternal(WarmupModeExpansion, eventReceiver);
        }

        // warmup Internalを要求しhandle IDを返す
        private int RequestWarmupInternal(int requestedMode, UdonBehaviour eventReceiver)
        {
            if (!CanAcceptRequest) return InvalidHandleId;
            if (eventReceiver == null || warmupRunning || isRunning
                || failedGpuResourceReleasePending || activeRequestHandleId != InvalidHandleId || activeWarmupHandleId != InvalidHandleId)
            {
                SetStatus(eventReceiver == null
                    ? "DCTH warmup event receiver is missing."
                    : "DCTH codec or warmup is already running.");
                return InvalidHandleId;
            }

            int handleId = GenerateWarmupHandleId();
            activeWarmupHandleId = handleId;
            completedWarmupHandleId = InvalidHandleId;
            failedWarmupHandleId = InvalidHandleId;
            cancelledWarmupHandleId = InvalidHandleId;
            activeWarmupRequestMode = requestedMode;
            activeWarmupRequestReceiver = eventReceiver;
            warmupRequestStartedAtRealtime = Time.realtimeSinceStartup;
            warmupFailed = false;
            SendCustomEventDelayedFrames(nameof(_BeginRequestedWarmup), 1);
            return handleId;
        }

        // Requested warmupを開始する
        public void _BeginRequestedWarmup()
        {
            if (activeWarmupHandleId == InvalidHandleId)
            {
                return;
            }

            bool requestedModeComplete = activeWarmupRequestMode == WarmupModeCompression
                ? compressionWarmupComplete
                : expansionWarmupComplete;
            if (requestedModeComplete)
            {
                warmupMode = activeWarmupRequestMode;
                SetStatus("DCTH warmup already complete.");
                NotifyWarmupComplete();
                return;
            }

            BeginWarmupInternal(activeWarmupRequestMode);
            if (!warmupRunning && activeWarmupHandleId != InvalidHandleId)
            {
                FailWarmup("Warmup did not start.");
            }
        }

        // warmupを開始する
        public void _BeginWarmup()
        {
            BeginWarmupInternal(WarmupModeAll);
        }

        // 圧縮 warmupを開始する
        public void _BeginCompressionWarmup()
        {
            BeginWarmupInternal(WarmupModeCompression);
        }

        // 展開 warmupを開始する
        public void _BeginExpansionWarmup()
        {
            BeginWarmupInternal(WarmupModeExpansion);
        }

        // warmup Internalを開始する
        private void BeginWarmupInternal(int requestedMode)
        {
            bool requestedModeComplete = requestedMode == WarmupModeCompression
                ? compressionWarmupComplete
                : requestedMode == WarmupModeExpansion
                    ? expansionWarmupComplete
                    : compressionWarmupComplete && expansionWarmupComplete;
            if (requestedModeComplete || warmupRunning || isRunning
                || activeRequestHandleId != InvalidHandleId)
            {
                return;
            }

            warmupPreviousActiveEncodeSourceTexture = activeEncodeSourceTexture;
            warmupPreviousActiveEncodeSourceIsRawStorage = activeEncodeSourceIsRawStorage;
            warmupPreviousActiveEncodeSrgb = activeEncodeSrgb;
            warmupPreviousActiveSourceSrgb = activeSourceSrgb;
            warmupSourceTexture = new RenderTexture(
                WarmupTextureSize,
                WarmupTextureSize,
                0,
                RenderTextureFormat.ARGB32);
            activeEncodeSourceTexture = warmupSourceTexture;
            activeEncodeSourceIsRawStorage = false;
            activeEncodeSrgb = true;
            activeSourceSrgb = true;
            ResetGpuDiagnostics(requestedMode == WarmupModeExpansion
                ? "expansion-warmup"
                : "compression-warmup");
            BeginGpuDiagnosticStage(nameof(_BeginRequestedWarmup), -1, false);
            warmupRunning = true;
            warmupFailed = false;
            warmupMode = requestedMode;
            warmupStep = 0;
            warmupPlane = PlaneY;
            warmupShaderStep = 0;
            warmupCoreResourceStep = 0;
            warmupResourceStep = 0;
            warmupGpuReadbackPending = false;
            warmupGpuReadbackRequestQueued = false;
            warmupGpuReadbackStep = 0;
            warmupStartedMs = GetNowMillis();
            warmupLastProgressAtRealtime = Time.realtimeSinceStartup;
            warmupFrameSeconds = 0f;
            warmupWorstFrameSeconds = 0f;
            warmupFrameCount = 0;
            warmupLastOperation = -1;
            warmupWorstOperation = -1;
            latestWarmupSummary = "Warmup running";
            latestTimingSummary = latestWarmupSummary;
            ScheduleWarmupStep();
        }

        // warmup stepを実行する
        public void _RunWarmupStep()
        {
            if (!warmupRunning)
            {
                return;
            }

            if (isRunning)
            {
                FailWarmup("Codec operation started during warmup.");
                return;
            }

            switch (warmupStep)
            {
                case 0:
                    warmupLastOperation = 0;
                    if (!warmupSourceTexture.IsCreated())
                    {
                        // Mobileの初回RenderTexture.Createと最初のBlitを同じframeへ重ねない
                        // shader warmupの内容は変えず、driver初期化時のmain-thread peakだけを分離する
                        warmupSourceTexture.Create();
                        if (!warmupSourceTexture.IsCreated())
                        {
                            warmupSourceTexture.Release();
                            Destroy(warmupSourceTexture);
                            warmupSourceTexture = null;
                            FailWarmup("DCTH warmup source creation failed.");
                            return;
                        }
                        ScheduleWarmupStep();
                        return;
                    }
                    TrackedBlit(Texture2D.whiteTexture, warmupSourceTexture);
                    warmupStep = 1;
                    ScheduleWarmupStep();
                    return;

                case 1:
                    warmupLastOperation = 1;
                    bool hasMoreCoreResources = PrepareNextWarmupCoreResource();
                    if (!warmupRunning)
                    {
                        return;
                    }
                    if (hasMoreCoreResources)
                    {
                        ScheduleWarmupStep();
                        return;
                    }
                    warmupStep = 2;
                    ScheduleWarmupStep();
                    return;

                case 2:
                    warmupLastOperation = 2;
                    if (PrepareNextWarmupRenderTexture())
                    {
                        ScheduleWarmupStep();
                        return;
                    }
                    warmupStep = 3;
                    ScheduleWarmupStep();
                    return;

                case 3:
                    warmupLastOperation = 3;
                    TrackedBlit(EnsureNeutralGrayTexture(), planeYTexture);
                    warmupStep = 4;
                    ScheduleWarmupStep();
                    return;

                case 4:
                    warmupLastOperation = 100 + warmupShaderStep;
                    if (ShouldWarmupShaderStep(warmupShaderStep))
                    {
                        WarmupShaderPass(warmupShaderStep);
                    }
                    warmupShaderStep++;
                    // 最後のshader pass後にも数frameを空ける。Mobile driverが初回compileを遅延処理しても、
                    // capacity statsのclear/readbackと同じframeへGPU queueの待機を集中させない
                    if (warmupShaderStep >= WarmupShaderStepCount)
                    {
                        warmupStep = 5;
                        ScheduleWarmupStep();
                        return;
                    }

                    ScheduleWarmupStep();
                    return;

                case 5:
                    warmupLastOperation = 5;
                    if (warmupMode != WarmupModeExpansion && capacityPrepassStats != null)
                    {
                        TrackedBlit(EnsureTransparentTexture(), capacityPrepassStats);
                    }
                    warmupStep = 6;
                    ScheduleWarmupStep();
                    return;

                case 6:
                    warmupLastOperation = 6;
                    if (warmupMode != WarmupModeExpansion && capacityPrepassStatsTemp != null)
                    {
                        TrackedBlit(EnsureTransparentTexture(), capacityPrepassStatsTemp);
                    }
                    warmupStep = 7;
                    ScheduleWarmupStep();
                    return;

                case 7:
                    warmupLastOperation = 7;
                    if (warmupMode != WarmupModeExpansion
                        && capacityPrepassStats != null
                        && capacityPrepassStatsTemp != null
                        && huffmanBlockBits != null
                        && huffmanChunkValidBytesMaterial != null)
                    {
                        SetupCapacityPrepassStatsMaterial(
                            huffmanChunkValidBytesMaterial,
                            capacityPrepassStats,
                            PlaneY,
                            1,
                            0,
                            Mathf.Max(huffmanBlockBits.width, 1),
                            Mathf.Max(huffmanBlockBits.height, 1));
                        TrackedBlit(
                            huffmanBlockBits,
                            capacityPrepassStatsTemp,
                            huffmanChunkValidBytesMaterial,
                            CapacityPrepassStatsPass);
                        RenderTexture previousStats = capacityPrepassStats;
                        capacityPrepassStats = capacityPrepassStatsTemp;
                        capacityPrepassStatsTemp = previousStats;
                    }
                    else if (warmupMode != WarmupModeExpansion)
                    {
                        FailWarmup("DCTH capacity readback warmup resources are missing.");
                        return;
                    }
                    warmupStep = 8;
                    ScheduleWarmupStep();
                    return;

                case 8:
                    QueueWarmupGpuCompletionReadback();
                    return;

                default:
                    CompleteWarmup();
                    return;
            }
        }

        // warmup plane Or stepを次へ進める
        private void AdvanceWarmupPlaneOrStep(int nextStep)
        {
            warmupPlane++;
            if (warmupPlane > PlaneCr)
            {
                warmupPlane = PlaneY;
                warmupStep = nextStep;
            }

            ScheduleWarmupStep();
        }

        // warmup stepを予約する
        private void ScheduleWarmupStep()
        {
            CompleteGpuDiagnosticStage();
            BeginGpuDiagnosticStage(nameof(_RunWarmupStep), 100000 + warmupLastOperation, false);
            // 総所要時間ではなく、warmup stepが進まない時間だけをtimeoutとして扱う
            warmupLastProgressAtRealtime = Time.realtimeSinceStartup;
            SendCustomEventDelayedFrames(nameof(_RunWarmupStep), 1);
        }

        // warmupをキャンセルする
        private void CancelWarmup()
        {
            CapturePendingGpuReadbacksForRelease();
            if (!failedGpuResourceReleasePending)
            {
                warmupGpuReadbackPending = false;
                warmupGpuReadbackRequestQueued = false;
                ReleaseWarmupCoreTemporaryResources();
            }
            RestoreWarmupSource();
            warmupRunning = false;
            warmupLastProgressAtRealtime = 0f;
            if (!failedGpuResourceReleasePending)
            {
                ReleaseIntermediateRenderTextures();
            }
        }

        // warmup Core Temporary リソースを解放する
        private void ReleaseWarmupCoreTemporaryResources()
        {
            if (warmupGammaEncodeTexture != null)
            {
                Destroy(warmupGammaEncodeTexture);
                warmupGammaEncodeTexture = null;
            }
            warmupGammaEncodeBytes = null;

            if (warmupQuantReciprocalTexture != null)
            {
                Destroy(warmupQuantReciprocalTexture);
                warmupQuantReciprocalTexture = null;
            }
            warmupQuantReciprocalPixels = null;
        }

        // warmup GPU Completion readbackを開始する
        private void BeginWarmupGpuCompletionReadback()
        {
            if (!warmupRunning || warmupGpuReadbackPending)
            {
                return;
            }
            RenderTexture readbackTexture = GetWarmupGpuReadbackTexture(warmupGpuReadbackStep);
            if (readbackTexture == null)
            {
                FailWarmup("DCTH warmup GPU completion texture is missing.");
                return;
            }

            TextureFormat readbackFormat = GetWarmupGpuReadbackFormat(warmupGpuReadbackStep);
            huffmanReadbackRequest = VRCAsyncGPUReadback.Request(
                readbackTexture,
                0,
                readbackFormat,
                this);
            warmupGpuReadbackPending = true;
            warmupStep = 9 + warmupGpuReadbackStep;
            SendCustomEventDelayedFrames(nameof(_PollWarmupGpuCompletionReadback), 1);
        }

        // warmup GPU Completion readbackをqueueへ追加する
        private void QueueWarmupGpuCompletionReadback()
        {
            if (!warmupRunning || warmupGpuReadbackPending || warmupGpuReadbackRequestQueued)
            {
                return;
            }

            RenderTexture readbackTexture = GetWarmupGpuReadbackTexture(warmupGpuReadbackStep);
            if (readbackTexture == null)
            {
                FailWarmup("DCTH warmup GPU completion texture is missing.");
                return;
            }
            TextureFormat readbackFormat = GetWarmupGpuReadbackFormat(warmupGpuReadbackStep);
            warmupGpuReadbackBytes = EnsureByteArray(
                warmupGpuReadbackBytes,
                GetReadbackByteCount(readbackTexture, readbackFormat));
            warmupGpuReadbackRequestQueued = true;
            SendCustomEventDelayedFrames(nameof(_RunNextWarmupGpuCompletionReadback), 1);
        }

        // 次 warmup GPU Completion readbackを実行する
        public void _RunNextWarmupGpuCompletionReadback()
        {
            if (!warmupRunning || warmupGpuReadbackPending || !warmupGpuReadbackRequestQueued)
            {
                warmupGpuReadbackRequestQueued = false;
                return;
            }
            warmupGpuReadbackRequestQueued = false;
            BeginWarmupGpuCompletionReadback();
        }

        // warmup GPU Completion readbackの完了状態を確認する
        public void _PollWarmupGpuCompletionReadback()
        {
            if (!warmupRunning || !warmupGpuReadbackPending)
            {
                warmupGpuReadbackPending = false;
                return;
            }
            if (!huffmanReadbackRequest.done)
            {
                SendCustomEventDelayedFrames(nameof(_PollWarmupGpuCompletionReadback), 1);
                return;
            }

            warmupGpuReadbackPending = false;
            if (huffmanReadbackRequest.hasError
                || !huffmanReadbackRequest.TryGetData(warmupGpuReadbackBytes, 0))
            {
                FailWarmup("DCTH warmup GPU completion readback failed.");
                return;
            }

            warmupGpuReadbackStep++;
            if (warmupGpuReadbackStep < GetWarmupGpuReadbackStepCount())
            {
                QueueWarmupGpuCompletionReadback();
                return;
            }
            CompleteWarmup();
        }

        // warmup GPU readback step 数を返す
        private int GetWarmupGpuReadbackStepCount()
        {
            if (warmupMode == WarmupModeAll)
            {
                return 4;
            }
            return warmupMode == WarmupModeCompression ? 3 : 1;
        }

        // warmup GPU readback Textureを返す
        private RenderTexture GetWarmupGpuReadbackTexture(int step)
        {
            if (warmupMode == WarmupModeExpansion)
            {
                return postprocessedOutputRenderTexture;
            }
            if (step == 0) return capacityPrepassStats;
            if (step == 1) return huffmanMetadataPack;
            if (step == 2) return huffmanPayloadGather;
            return postprocessedOutputRenderTexture;
        }

        // warmup GPU readback formatを返す
        private TextureFormat GetWarmupGpuReadbackFormat(int step)
        {
            if (warmupMode != WarmupModeExpansion && step == 2)
            {
                return TextureFormat.R8;
            }
            return TextureFormat.RGBA32;
        }

        // warmupを失敗状態にする
        private void FailWarmup(string message)
        {
            CancelWarmup();
            warmupComplete = false;
            warmupFailed = true;
            latestWarmupSummary = message;
            SetStatus("Failed: " + message);
            DumpGpuDiagnostics("warmup-failed");
            ReleaseFailedGpuResourcesWhenReadbacksComplete();
            NotifyWarmupFailed();
        }

        // warmup 入力を復元する
        private void RestoreWarmupSource()
        {
            activeEncodeSourceTexture = warmupPreviousActiveEncodeSourceTexture;
            activeEncodeSourceIsRawStorage = warmupPreviousActiveEncodeSourceIsRawStorage;
            activeEncodeSrgb = warmupPreviousActiveEncodeSrgb;
            activeSourceSrgb = warmupPreviousActiveSourceSrgb;
            warmupPreviousActiveEncodeSourceTexture = null;
            // warmupSourceTextureは失敗時のGPU完了確認にも必要になるため、
            // 実際の解放は共通のRT解放処理へまとめる
        }

        // warmupを完了状態にする
        private void CompleteWarmup()
        {
            warmupRunning = false;
            warmupFailed = false;
            if (warmupMode == WarmupModeAll || warmupMode == WarmupModeCompression)
            {
                compressionWarmupComplete = true;
            }
            if (warmupMode == WarmupModeAll || warmupMode == WarmupModeExpansion)
            {
                expansionWarmupComplete = true;
            }
            warmupComplete = true;
            warmupLastProgressAtRealtime = 0f;
            RestoreWarmupSource();
            ReleaseIntermediateRenderTextures();
            int elapsed = Mathf.Max(GetNowMillis() - warmupStartedMs, 0);
            int avgFps = GetFpsFromSeconds(warmupFrameSeconds, warmupFrameCount);
            int worstFps = GetWorstFpsFromSeconds(warmupWorstFrameSeconds);
            latestWarmupSummary = "Warmup complete: total=" + elapsed.ToString()
                + "ms fps(avg/worst)=" + avgFps.ToString() + "/" + worstFps.ToString()
                + " worstOp=" + warmupWorstOperation.ToString()
                + " frameMs=" + Mathf.RoundToInt(warmupWorstFrameSeconds * 1000f).ToString();
            latestTimingSummary = latestWarmupSummary;
            DumpGpuDiagnostics("warmup-complete");
            NotifyWarmupComplete();
        }

        // warmup 完了を通知する
        private void NotifyWarmupComplete()
        {
            if (activeWarmupHandleId == InvalidHandleId)
            {
                return;
            }

            int completedMode = activeWarmupRequestMode;
            completedWarmupHandleId = activeWarmupHandleId;
            completedWarmupRequestMode = completedMode;
            activeWarmupHandleId = InvalidHandleId;
            warmupRequestStartedAtRealtime = 0f;
            if (activeWarmupRequestReceiver != null)
            {
                UdonBehaviour receiver = activeWarmupRequestReceiver;
                activeWarmupRequestReceiver = null;
                receiver.SendCustomEvent(completedMode == WarmupModeExpansion
                    ? ExpansionWarmupSucceededEventName
                    : CompressionWarmupSucceededEventName);
            }
        }

        // warmup 失敗を通知する
        private void NotifyWarmupFailed()
        {
            if (activeWarmupHandleId != InvalidHandleId)
            {
                failedWarmupHandleId = activeWarmupHandleId;
                failedWarmupRequestMode = activeWarmupRequestMode;
                activeWarmupHandleId = InvalidHandleId;
            }
            warmupRequestStartedAtRealtime = 0f;
            if (activeWarmupRequestReceiver != null)
            {
                UdonBehaviour receiver = activeWarmupRequestReceiver;
                activeWarmupRequestReceiver = null;
                receiver.SendCustomEvent(failedWarmupRequestMode == WarmupModeExpansion
                    ? ExpansionWarmupFailedEventName
                    : CompressionWarmupFailedEventName);
            }
        }

        // FPS From 秒を返す
        private int GetFpsFromSeconds(float seconds, int frameCount)
        {
            if (seconds <= 0f || frameCount <= 0)
            {
                return 0;
            }

            return Mathf.RoundToInt(frameCount / seconds);
        }

        // Worst FPS From 秒を返す
        private int GetWorstFpsFromSeconds(float worstFrameSeconds)
        {
            if (worstFrameSeconds <= 0f)
            {
                return 0;
            }

            return Mathf.RoundToInt(1f / worstFrameSeconds);
        }

        // 次 warmup Core Resourceを準備する
        private bool PrepareNextWarmupCoreResource()
        {
            int step = warmupCoreResourceStep++;
            // Android初回起動ではTexture uploadとUdon配列初期化がdriver初期化に重なる
            // 圧縮式・LUT値は変えず、各初期化を1frameに1種類だけ実行してpeakを分散する
            if (step == 0) EnsureRuntimeStateBytes();
            else if (step == 1) EnsureStatusBytes();
            else if (step == 2) EnsureTimingBytes();
            else if (step == 3) EnsureSolidTexture(true);
            else if (step == 4) EnsureNeutralGrayTexture();
            else if (step == 5) EnsureTransparentTexture();
            else if (warmupMode != WarmupModeExpansion && step == 6) BeginWarmupGammaEncodeTexture();
            else if (warmupMode != WarmupModeExpansion && step == 7) LoadWarmupGammaEncodeBytes();
            else if (warmupMode != WarmupModeExpansion && step == 8) UploadWarmupGammaEncodeBytes();
            else if (warmupMode != WarmupModeExpansion && step == 9) CompleteWarmupGammaEncodeTexture();
            else if (warmupMode != WarmupModeExpansion && step == 10) BeginWarmupQuantReciprocalTexture();
            else if (warmupMode != WarmupModeExpansion && step >= 11 && step <= 14)
            {
                FillWarmupQuantReciprocalPixels((step - 11) * 64, 64);
            }
            else if (warmupMode != WarmupModeExpansion && step == 15) UploadWarmupQuantReciprocalPixels();
            else if (warmupMode != WarmupModeExpansion && step == 16) CompleteWarmupQuantReciprocalTexture();
            else if (warmupMode != WarmupModeExpansion && step == 17) quantTexture = EnsureRuntimeQuantTexture(quality, quantPreset);

            if (!warmupRunning)
            {
                return false;
            }

            int stepCount = warmupMode == WarmupModeExpansion
                ? WarmupExpansionCoreStepCount
                : WarmupCompressionCoreStepCount;
            return step + 1 < stepCount;
        }

        // Gamma 符号化 Texture 準備完了かを判定する
        private bool IsGammaEncodeTextureReady()
        {
            return generatedGammaEncodeTexture != null
                && generatedGammaEncodeTexture.width == GammaEncodeLutWidth
                && generatedGammaEncodeTexture.height == GammaEncodeLutHeight
                && generatedGammaEncodeTexture.format == TextureFormat.R8;
        }

        // warmup Gamma 符号化 Textureを開始する
        private void BeginWarmupGammaEncodeTexture()
        {
            if (IsGammaEncodeTextureReady())
            {
                return;
            }
            if (gammaEncodeLutBytes == null)
            {
                FailWarmup("DCTH gamma LUT bytes are missing.");
                return;
            }

            warmupGammaEncodeTexture = new Texture2D(
                GammaEncodeLutWidth,
                GammaEncodeLutHeight,
                TextureFormat.R8,
                false,
                true);
            warmupGammaEncodeTexture.name = "IC_Library_GammaEncodeLut";
            warmupGammaEncodeTexture.filterMode = FilterMode.Point;
            warmupGammaEncodeTexture.wrapMode = TextureWrapMode.Clamp;
        }

        // warmup Gamma 符号化 byte列を読み込む
        private void LoadWarmupGammaEncodeBytes()
        {
            if (IsGammaEncodeTextureReady())
            {
                return;
            }
            warmupGammaEncodeBytes = gammaEncodeLutBytes != null ? gammaEncodeLutBytes.bytes : null;
            if (warmupGammaEncodeBytes == null || warmupGammaEncodeBytes.Length != GammaEncodeLutSize)
            {
                FailWarmup("DCTH gamma LUT byte count is invalid.");
            }
        }

        // warmup Gamma 符号化 byte列をGPUへuploadする
        private void UploadWarmupGammaEncodeBytes()
        {
            if (IsGammaEncodeTextureReady())
            {
                return;
            }
            if (warmupGammaEncodeTexture == null || warmupGammaEncodeBytes == null)
            {
                FailWarmup("DCTH gamma LUT warmup data is missing.");
                return;
            }
            warmupGammaEncodeTexture.LoadRawTextureData(warmupGammaEncodeBytes);
        }

        // warmup Gamma 符号化 Textureを完了状態にする
        private void CompleteWarmupGammaEncodeTexture()
        {
            if (IsGammaEncodeTextureReady())
            {
                warmupGammaEncodeBytes = null;
                return;
            }
            if (warmupGammaEncodeTexture == null)
            {
                FailWarmup("DCTH gamma LUT warmup texture is missing.");
                return;
            }

            warmupGammaEncodeTexture.Apply(false, true);
            if (generatedGammaEncodeTexture != null)
            {
                Destroy(generatedGammaEncodeTexture);
            }
            generatedGammaEncodeTexture = warmupGammaEncodeTexture;
            warmupGammaEncodeTexture = null;
            warmupGammaEncodeBytes = null;
        }

        // 量子化 Reciprocal Texture 準備完了かを判定する
        private bool IsQuantReciprocalTextureReady()
        {
            return generatedQuantReciprocalTexture != null
                && generatedQuantReciprocalTexture.width == 256
                && generatedQuantReciprocalTexture.height == 1;
        }

        // warmup 量子化 Reciprocal Textureを開始する
        private void BeginWarmupQuantReciprocalTexture()
        {
            if (IsQuantReciprocalTextureReady())
            {
                quantReciprocalTexture = generatedQuantReciprocalTexture;
                return;
            }

            warmupQuantReciprocalTexture = new Texture2D(256, 1, TextureFormat.RGBA32, false, true);
            warmupQuantReciprocalTexture.name = "IC_Library_QuantReciprocalLut";
            warmupQuantReciprocalTexture.filterMode = FilterMode.Point;
            warmupQuantReciprocalTexture.wrapMode = TextureWrapMode.Clamp;
            warmupQuantReciprocalPixels = new Color32[256];
        }

        // Fill warmup 量子化 Reciprocal Pixelsを処理する
        private void FillWarmupQuantReciprocalPixels(int start, int count)
        {
            if (IsQuantReciprocalTextureReady())
            {
                return;
            }
            if (warmupQuantReciprocalTexture == null || warmupQuantReciprocalPixels == null)
            {
                FailWarmup("DCTH quant reciprocal LUT warmup data is missing.");
                return;
            }

            int end = Mathf.Min(start + count, warmupQuantReciprocalPixels.Length);
            for (int i = start; i < end; i++)
            {
                int denominator = Mathf.Max(i, 1);
                // round(2^20 / q)を整数だけで求め、既存の事前計算tableと同じRGB little-endian値にする
                int packed = (1048576 + denominator / 2) / denominator;
                warmupQuantReciprocalPixels[i] = new Color32(
                    (byte)(packed & 255),
                    (byte)((packed >> 8) & 255),
                    (byte)((packed >> 16) & 255),
                    255);
            }
        }

        // warmup 量子化 Reciprocal PixelsをGPUへuploadする
        private void UploadWarmupQuantReciprocalPixels()
        {
            if (IsQuantReciprocalTextureReady())
            {
                return;
            }
            if (warmupQuantReciprocalTexture == null || warmupQuantReciprocalPixels == null)
            {
                FailWarmup("DCTH quant reciprocal LUT warmup data is missing.");
                return;
            }
            warmupQuantReciprocalTexture.SetPixels32(warmupQuantReciprocalPixels);
        }

        // warmup 量子化 Reciprocal Textureを完了状態にする
        private void CompleteWarmupQuantReciprocalTexture()
        {
            if (IsQuantReciprocalTextureReady())
            {
                quantReciprocalTexture = generatedQuantReciprocalTexture;
                warmupQuantReciprocalPixels = null;
                return;
            }
            if (warmupQuantReciprocalTexture == null)
            {
                FailWarmup("DCTH quant reciprocal LUT warmup texture is missing.");
                return;
            }

            warmupQuantReciprocalTexture.Apply(false, false);
            if (generatedQuantReciprocalTexture != null)
            {
                Destroy(generatedQuantReciprocalTexture);
            }
            generatedQuantReciprocalTexture = warmupQuantReciprocalTexture;
            quantReciprocalTexture = generatedQuantReciprocalTexture;
            warmupQuantReciprocalTexture = null;
            warmupQuantReciprocalPixels = null;
        }

        // 次 warmup 描画 Textureを準備する
        private bool PrepareNextWarmupRenderTexture()
        {
            int step = warmupResourceStep++;
            bool compression = warmupMode != WarmupModeExpansion;
            bool expansion = warmupMode != WarmupModeCompression;
            int width = GetPlaneWidth(PlaneY);
            int height = GetPlaneHeight(PlaneY);
            int blockWidth = GetBlockWidth(PlaneY);
            int blockHeight = GetBlockHeight(PlaneY);
            int chunkWidth = GetChunkWidth(PlaneY);
            int chunkHeight = GetChunkHeight(PlaneY);
            RenderTextureFormat dctFormat = GetSingleChannelDctRenderTextureFormat();
            RenderTextureFormat planeFormat = GetPlaneRenderTextureFormat();

            if (step == 0) planeYTexture = EnsureRuntimeRenderTexture(planeYTexture, "IC_Library_PlaneY", width, height, planeFormat);
            else if (step == 1) planeATexture = EnsureRuntimeRenderTexture(planeATexture, "IC_Library_PlaneA", GetPlaneWidth(PlaneA), GetPlaneHeight(PlaneA), planeFormat);
            else if (step == 2) planeCbTexture = EnsureRuntimeRenderTexture(planeCbTexture, "IC_Library_PlaneCb", GetPlaneWidth(PlaneCb), GetPlaneHeight(PlaneCb), planeFormat);
            else if (step == 3) planeCrTexture = EnsureRuntimeRenderTexture(planeCrTexture, "IC_Library_PlaneCr", GetPlaneWidth(PlaneCr), GetPlaneHeight(PlaneCr), planeFormat);
            else if (step == 4) work = EnsureRuntimeRenderTexture(work, "IC_Library_Work", width, height, dctFormat);
            else if (step == 5 && compression) coefficients = EnsureRuntimeRenderTexture(coefficients, "IC_Library_Coefficients", width, height, dctFormat);
            else if (step == 6 && compression) colorDownsampleTexture = EnsureRuntimeRenderTexture(colorDownsampleTexture, "IC_Library_ColorDownsample", width, height, planeFormat);
            else if (step == 7 && compression) rleSymbols = EnsureRuntimeRenderTexture(rleSymbols, "IC_Library_RleSymbols", width, height, RenderTextureFormat.ARGB32);
            else if (step == 8 && compression) rleAcFrequencyRows = EnsureRuntimeRenderTexture(rleAcFrequencyRows, "IC_Library_RleAcFrequencyRows", AcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 9 && compression) rleAcFrequencyRowsTemp = EnsureRuntimeRenderTexture(rleAcFrequencyRowsTemp, "IC_Library_RleAcFrequencyRowsTemp", AcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 10 && compression) rleAcFrequency = EnsureRuntimeRenderTexture(rleAcFrequency, "IC_Library_RleAcFrequency", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 11 && compression) dcValues = EnsureRuntimeRenderTexture(dcValues, "IC_Library_DcValues", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 12 && compression) dcDelta = EnsureRuntimeRenderTexture(dcDelta, "IC_Library_DcDelta", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 13 && compression) dcFrequencyRows = EnsureRuntimeRenderTexture(dcFrequencyRows, "IC_Library_DcFrequencyRows", DcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 14 && compression) dcFrequencyRowsTemp = EnsureRuntimeRenderTexture(dcFrequencyRowsTemp, "IC_Library_DcFrequencyRowsTemp", DcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 15 && compression) dcFrequency = EnsureRuntimeRenderTexture(dcFrequency, "IC_Library_DcFrequency", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 16 && compression) huffmanRawLengths = EnsureRuntimeRenderTexture(huffmanRawLengths, "IC_Library_HuffmanRawLengths", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 17 && compression) huffmanRawLengthHistogram = EnsureRuntimeRenderTexture(huffmanRawLengthHistogram, "IC_Library_HuffmanRawLengthHistogram", 256, 1, RenderTextureFormat.ARGB32);
            else if (step == 18 && compression) huffmanLimitedLengthHistogram = EnsureRuntimeRenderTexture(huffmanLimitedLengthHistogram, "IC_Library_HuffmanLimitedLengthHistogram", 16, 1, RenderTextureFormat.ARGB32);
            else if (step == 19 && compression) dcHuffmanLengths = EnsureRuntimeRenderTexture(dcHuffmanLengths, "IC_Library_DcHuffmanLengths", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 20) dcHuffmanCodes = EnsureRuntimeRenderTexture(dcHuffmanCodes, "IC_Library_DcHuffmanCodes", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 21 && compression) acHuffmanLengths = EnsureRuntimeRenderTexture(acHuffmanLengths, "IC_Library_AcHuffmanLengths", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 22) acHuffmanCodes = EnsureRuntimeRenderTexture(acHuffmanCodes, "IC_Library_AcHuffmanCodes", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 23 && compression) huffmanBlockBits = EnsureRuntimeRenderTexture(huffmanBlockBits, "IC_Library_HuffmanBlockBits", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 24 && compression) huffmanBlockOverflowMask = EnsureRuntimeRenderTexture(huffmanBlockOverflowMask, "IC_Library_HuffmanBlockOverflowMask", GetBlockMipAtlasWidth(PlaneY), GetBlockMipAtlasHeight(PlaneY), RenderTextureFormat.ARGB32);
            else if (step == 25 && compression) huffmanBlockOverflowMaskTemp = EnsureRuntimeRenderTexture(huffmanBlockOverflowMaskTemp, "IC_Library_HuffmanBlockOverflowMaskTemp", GetBlockMipAtlasWidth(PlaneY), GetBlockMipAtlasHeight(PlaneY), RenderTextureFormat.ARGB32);
            else if (step == 26 && compression) huffmanBlockPage = EnsureRuntimeRenderTexture(huffmanBlockPage, "IC_Library_HuffmanBlockPage", blockWidth * HuffmanBlockPageBytes, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 27 && compression) huffmanBlockPageTemp = EnsureRuntimeRenderTexture(huffmanBlockPageTemp, "IC_Library_HuffmanBlockPageTemp", blockWidth * HuffmanBlockPageBytes, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 28 && compression) huffmanBlockPageState = EnsureRuntimeRenderTexture(huffmanBlockPageState, "IC_Library_HuffmanBlockPageState", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 29 && compression) huffmanBlockPageStateTemp = EnsureRuntimeRenderTexture(huffmanBlockPageStateTemp, "IC_Library_HuffmanBlockPageStateTemp", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 30 && compression) huffmanBlockValidBytes = EnsureRuntimeRenderTexture(huffmanBlockValidBytes, "IC_Library_HuffmanBlockValidBytes", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 31 && compression) huffmanChunkValidBytes = EnsureRuntimeRenderTexture(huffmanChunkValidBytes, "IC_Library_HuffmanChunkValidBytes", chunkWidth, chunkHeight, RenderTextureFormat.ARGB32);
            else if (step == 32 && compression) huffmanChunkValidBytesMip = EnsureRuntimeRenderTexture(huffmanChunkValidBytesMip, "IC_Library_HuffmanChunkValidBytesMip", GetChunkMipAtlasWidth(PlaneY), GetChunkMipAtlasHeight(PlaneY), RenderTextureFormat.ARGB32);
            else if (step == 33 && compression) huffmanChunkValidBytesMipTemp = EnsureRuntimeRenderTexture(huffmanChunkValidBytesMipTemp, "IC_Library_HuffmanChunkValidBytesMipTemp", GetChunkMipAtlasWidth(PlaneY), GetChunkMipAtlasHeight(PlaneY), RenderTextureFormat.ARGB32);
            else if (step == 34 && compression) huffmanPayloadGather = EnsureRuntimeRenderTexture(huffmanPayloadGather, "IC_Library_HuffmanPayloadGather", GetHuffmanPayloadWidth(PlaneY), GetHuffmanPayloadHeight(PlaneY), RenderTextureFormat.R8);
            else if (step == 35 && compression) huffmanMetadataPack = EnsureRuntimeRenderTexture(huffmanMetadataPack, "IC_Library_HuffmanMetadataPack", GetHuffmanMetadataWidth(), GetHuffmanMetadataHeight(PlaneY), RenderTextureFormat.ARGB32);
            else if (step == 36 && compression) yHuffmanPayloadTexture = EnsureRuntimeRenderTexture(yHuffmanPayloadTexture, "IC_Library_YHuffmanPayload", GetHuffmanPayloadWidth(PlaneY), GetHuffmanPayloadHeight(PlaneY), RenderTextureFormat.R8);
            else if (step == 37 && compression) yHuffmanMetadataTexture = EnsureRuntimeRenderTexture(yHuffmanMetadataTexture, "IC_Library_YHuffmanMetadata", GetHuffmanMetadataWidth(), GetHuffmanMetadataHeight(PlaneY), RenderTextureFormat.ARGB32);
            else if (step == 38 && compression) yChunkValidTexture = EnsureRuntimeRenderTexture(yChunkValidTexture, "IC_Library_YChunkValid", chunkWidth, chunkHeight, RenderTextureFormat.ARGB32);
            else if (step == 39 && compression) yDcHuffmanCodesTexture = EnsureRuntimeRenderTexture(yDcHuffmanCodesTexture, "IC_Library_YDcHuffmanCodes", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 40 && compression) yAcHuffmanCodesTexture = EnsureRuntimeRenderTexture(yAcHuffmanCodesTexture, "IC_Library_YAcHuffmanCodes", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 41 && expansion) reconstructedFromHuffmanPayload = EnsureRuntimeRenderTexture(reconstructedFromHuffmanPayload, "IC_Library_ReconstructedFromHuffmanPayload", width, height, GetDecodeReconstructedRenderTextureFormat());
            else if (step == 42 && expansion) packedDecodedPlanes = EnsureRuntimeRenderTexture(packedDecodedPlanes, "IC_Library_PackedDecodedPlanes", width, height, RenderTextureFormat.ARGBHalf);
            else if (step == 43 && expansion) postprocessedOutputRenderTexture = EnsureRuntimeRenderTexture(postprocessedOutputRenderTexture, "IC_Library_PostprocessedOutput", width, height, GetDisplayRenderTextureFormat(), false, false, activeSourceSrgb);
            else if (step == 44 && expansion) huffmanPayloadBitOffset = EnsureRuntimeRenderTexture(huffmanPayloadBitOffset, "IC_Library_HuffmanPayloadBitOffset", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 45 && expansion) huffmanPayloadBitOffsetTemp = EnsureRuntimeRenderTexture(huffmanPayloadBitOffsetTemp, "IC_Library_HuffmanPayloadBitOffsetTemp", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 46 && expansion) huffmanDecodeState = EnsureRuntimeRenderTexture(huffmanDecodeState, "IC_Library_HuffmanDecodeState", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 47 && expansion) huffmanDecodeStateTemp = EnsureRuntimeRenderTexture(huffmanDecodeStateTemp, "IC_Library_HuffmanDecodeStateTemp", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 48 && expansion) huffmanAcDecodeLengthSummary = EnsureRuntimeRenderTexture(huffmanAcDecodeLengthSummary, "IC_Library_HuffmanAcDecodeLengthSummary", 16, 1, RenderTextureFormat.ARGB32);
            else if (step == 49 && expansion) huffmanAcDecodeSymbolTable = EnsureRuntimeRenderTexture(huffmanAcDecodeSymbolTable, "IC_Library_HuffmanAcDecodeSymbolTable", AcFrequencyBinCount, 16, RenderTextureFormat.R8);
            else if (step == 50 && expansion) huffmanAcDecodeSymbolTableWork = EnsureRuntimeRenderTexture(huffmanAcDecodeSymbolTableWork, "IC_Library_HuffmanAcDecodeSymbolTableWork", AcFrequencyBinCount, 16, RenderTextureFormat.ARGB32);
            else if (step == 51 && expansion) huffmanAcDecodeSymbolTableTemp = EnsureRuntimeRenderTexture(huffmanAcDecodeSymbolTableTemp, "IC_Library_HuffmanAcDecodeSymbolTableTemp", AcFrequencyBinCount, 16, RenderTextureFormat.ARGB32);
            else if (step == 52 && expansion) rleSymbolsFromHuffmanPayload = EnsureRuntimeRenderTexture(rleSymbolsFromHuffmanPayload, "IC_Library_RleSymbolsFromHuffmanPayload", width, height, RenderTextureFormat.ARGB32);
            else if (step == 53 && expansion) dcDeltaFromHuffmanPayload = EnsureRuntimeRenderTexture(dcDeltaFromHuffmanPayload, "IC_Library_DcDeltaFromHuffmanPayload", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 54 && expansion) dcScanFromHuffmanPayload = EnsureRuntimeRenderTexture(dcScanFromHuffmanPayload, "IC_Library_DcScanFromHuffmanPayload", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 55 && expansion) dcScanFromHuffmanPayloadTemp = EnsureRuntimeRenderTexture(dcScanFromHuffmanPayloadTemp, "IC_Library_DcScanFromHuffmanPayloadTemp", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            else if (step == 56 && expansion) symbolFixedFromHuffmanPayload = EnsureRuntimeRenderTexture(symbolFixedFromHuffmanPayload, "IC_Library_SymbolFixedFromHuffmanPayload", width, height, RenderTextureFormat.ARGB32);
            else if (step == 57 && expansion) symbolFixedWorkFromHuffmanPayload = EnsureRuntimeRenderTexture(symbolFixedWorkFromHuffmanPayload, "IC_Library_SymbolFixedWorkFromHuffmanPayload", width, height, RenderTextureFormat.ARGB32);
            else if (step == 58 && compression) capacityPrepassStats = EnsureRuntimeRenderTexture(capacityPrepassStats, "IC_Library_CapacityPrepassStats", CapacityPrepassStatsTextureWidth, CapacityPrepassStatsTextureHeight, RenderTextureFormat.ARGB32);
            else if (step == 59 && compression) capacityPrepassStatsTemp = EnsureRuntimeRenderTexture(capacityPrepassStatsTemp, "IC_Library_CapacityPrepassStatsTemp", CapacityPrepassStatsTextureWidth, CapacityPrepassStatsTextureHeight, RenderTextureFormat.ARGB32);
            else if (step == 60 && compression) huffmanRawLengthSingle = EnsureRuntimeRenderTexture(huffmanRawLengthSingle, "IC_Library_HuffmanRawLengthSingle", HuffmanSingleValueTextureSize, HuffmanSingleValueTextureSize, RenderTextureFormat.ARGB32);
            else if (step == 61 && compression) huffmanLimitedLengthHistogramTemp = EnsureRuntimeRenderTexture(huffmanLimitedLengthHistogramTemp, "IC_Library_HuffmanLimitedLengthHistogramTemp", HuffmanLengthLimitCount, 1, RenderTextureFormat.ARGB32);
            else if (step == 62 && compression) huffmanRawLengthHistogramTemp = EnsureRuntimeRenderTexture(huffmanRawLengthHistogramTemp, "IC_Library_HuffmanRawLengthHistogramTemp", 256, 1, RenderTextureFormat.ARGB32);

            return step < WarmupRenderTextureStepCount;
        }

        // warmup plane リソースを処理する
        private void WarmupPlaneResources(int plane)
        {
            int oldPlane = stage1PlaneMode;
            stage1PlaneMode = ClampPlane(plane);
            if (warmupMode != WarmupModeExpansion)
            {
                EnsurePlaneWorkTextures(stage1PlaneMode);
                EnsureHuffmanRenderTextures(stage1PlaneMode);
            }
            if (warmupMode != WarmupModeCompression)
            {
                EnsureHuffmanDecodeRenderTextures(stage1PlaneMode);
            }
            TrackedBlit(EnsureNeutralGrayTexture(), GetPlaneTexture(stage1PlaneMode));
            stage1PlaneMode = oldPlane;
        }

        // warmup Shader Passを処理する
        private void WarmupShaderPass(int step)
        {
            stage1PlaneMode = PlaneY;
            Texture neutral = EnsureNeutralGrayTexture();
            Texture white = EnsureSolidTexture(true);
            if (step >= 1 && step <= 5)
            {
                int composeOperation = step - 1;
                if (composeOperation <= 3)
                {
                    SetupPackedPlaneMaterial(composeRgbaMaterial, composeOperation);
                    WarmupMaterialPass(composeRgbaMaterial, reconstructedFromHuffmanPayload, packedDecodedPlanes, composeOperation);
                }
                else
                {
                    SetupPackedComposeMaterial(composeRgbaMaterial, postprocessedOutputRenderTexture.width, postprocessedOutputRenderTexture.height);
                    WarmupMaterialPass(composeRgbaMaterial, packedDecodedPlanes, postprocessedOutputRenderTexture, 4);
                }
                return;
            }
            if (step == 57)
            {
                // raw length histogramのlimitとsingle-value mergeは別shader passなので別frameでwarmupする
                // 初回compileを同じcallbackへ重ねないための分離で、実圧縮のtable値には影響しない
                SetupHuffmanSingleValueMergeMaterial(huffmanTableMaterial, null, HuffmanLengthLimitCount, 0, false);
                WarmupMaterialPass(huffmanTableMaterial, huffmanRawLengthSingle, huffmanLimitedLengthHistogramTemp, HuffmanTablePassMergeSingleValue);
                return;
            }
            if (step > 5)
            {
                step -= 4;
            }

            if (step == 0)
            {
                SetupColorDownsampleMaterial(colorDownsampleMaterial, neutral.width, neutral.height);
                WarmupMaterialPass(colorDownsampleMaterial, neutral, colorDownsampleTexture, 0);
            }
            else if (step == 2)
            {
                SetupCoeffToRleSymbolsBuildPrefixBaseMaterial(coeffToRleSymbolsMaterial);
                WarmupMaterialPass(coeffToRleSymbolsMaterial, coefficients, work, CoeffToRlePassBuildPrefixBase);
            }
            else if (step >= 3 && step <= 4)
            {
                SetupDcDeltaMaterial(dcDeltaMaterial);
                WarmupMaterialPass(dcDeltaMaterial, coefficients, dcDelta, step - 3);
            }
            else if (step >= 5 && step <= 6)
            {
                SetupDcFrequencyMaterial(dcFrequencyMaterial);
                WarmupMaterialPass(dcFrequencyMaterial, dcDelta, dcFrequencyRows, step - 5);
            }
            else if (step >= 7 && step <= 8)
            {
                SetupDcScanMaterial(dcScanMaterial);
                WarmupMaterialPass(dcScanMaterial, dcDeltaFromHuffmanPayload, dcScanFromHuffmanPayload, step - 7);
            }
            else if (step >= 9 && step <= 10)
            {
                SetupDecodeSymbolsMaterial(decodeSymbolsMaterial, work, 0, 0);
                WarmupMaterialPass(decodeSymbolsMaterial, symbolFixedFromHuffmanPayload, reconstructedFromHuffmanPayload, step - 9);
            }
            else if (step == 11)
            {
                SetupEncodeMaterial(encodeMaterial);
                SetupHorizontalAccumulationMaterial(encodeMaterial, null, 0);
                WarmupMaterialPass(encodeMaterial, neutral, work, 0);
            }
            else if (step == 12)
            {
                SetupEncodeMaterial(encodeVerticalMaterial);
                WarmupMaterialPass(encodeVerticalMaterial, work, coefficients, 0);
            }
            else if (step == 13)
            {
                SetupCapacityPrepassEncodeMaterial(capacityPrepassEncodeMaterial, warmupPlane, CapacityPrepassSampleBlockLimit);
                SetupHorizontalAccumulationMaterial(capacityPrepassEncodeMaterial, null, 0);
                WarmupMaterialPass(capacityPrepassEncodeMaterial, neutral, work, 0);
            }
            else if (step == 14)
            {
                SetupCapacityPrepassEncodeMaterial(capacityPrepassVerticalMaterial, warmupPlane, CapacityPrepassSampleBlockLimit);
                WarmupMaterialPass(capacityPrepassVerticalMaterial, work, coefficients, 0);
            }
            else if (step >= 15 && step <= 17)
            {
                SetupCapacityPrepassEncodeMaterial(capacityPrepassPostMaterial, warmupPlane, CapacityPrepassSampleBlockLimit);
                WarmupMaterialPass(capacityPrepassPostMaterial, work, coefficients, step - 15);
            }
            else if (step == 18)
            {
                SetupHuffmanBitCountMaterial(huffmanBitCountMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes);
                WarmupMaterialPass(huffmanBitCountMaterial, rleSymbols, huffmanBlockBits, HuffmanBitCountPassInitialize);
            }
            else if (step >= 19 && step <= 20)
            {
                SetupHuffmanBlockOverflowMaterial(huffmanBlockOverflowMaterial);
                WarmupMaterialPass(huffmanBlockOverflowMaterial, huffmanBlockBits, huffmanBlockOverflowMask, step - 19);
            }
            else if (step == 21)
            {
                SetupHuffmanBlockPageEncodeMaterial(huffmanBlockPageEncodeMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes, huffmanBlockPageTemp, huffmanBlockPageStateTemp, 0, -1);
                WarmupMaterialPass(huffmanBlockPageEncodeMaterial, rleSymbols, huffmanBlockPageState, HuffmanBlockPagePassInitializeState);
            }
            else if (step >= 22 && step <= 23)
            {
                SetupHuffmanBlockValidBytesMaterial(huffmanBlockValidBytesMaterial);
                WarmupMaterialPass(huffmanBlockValidBytesMaterial, huffmanBlockBits, huffmanBlockValidBytes, step - 22);
            }
            else if (step == 24)
            {
                SetupHuffmanChunkBlockValidMaterial(huffmanChunkBlockValidMaterial);
                WarmupMaterialPass(huffmanChunkBlockValidMaterial, white, huffmanPayloadBitOffset, 0);
            }
            else if (step >= 25 && step <= 28)
            {
                SetupHuffmanChunkValidBytesMaterial(huffmanChunkValidBytesMaterial);
                WarmupMaterialPass(huffmanChunkValidBytesMaterial, huffmanBlockBits, huffmanChunkValidBytesMip, step - 25);
            }
            else if (step == 29)
            {
                SetupHuffmanDecodeRleMaterial(huffmanDecodeRleMaterial, rleSymbolsFromHuffmanPayload, huffmanDecodeStateTemp, 0, 0);
                WarmupMaterialPass(huffmanDecodeRleMaterial, white, huffmanDecodeState, HuffmanDecodeRlePassInitializeState);
            }
            else if (step == 30)
            {
                SetupHuffmanMetadataPackMaterial(huffmanMetadataPackMaterial);
                WarmupMaterialPass(huffmanMetadataPackMaterial, huffmanChunkValidBytes, huffmanMetadataPack, 0);
            }
            else if (step == 31)
            {
                SetupHuffmanPayloadGatherMaterial(huffmanPayloadGatherMaterial, GetHuffmanPayloadTexture(PlaneY), 0);
                WarmupMaterialPass(huffmanPayloadGatherMaterial, huffmanBlockPage, huffmanPayloadGather, 0);
            }
            else if (step == 33)
            {
                SetupHuffmanLengthLimitMaterial(huffmanTableMaterial, dcFrequency, DcFrequencyBinCount);
                WarmupMaterialPass(huffmanTableMaterial, huffmanRawLengths, dcHuffmanLengths, HuffmanTablePassLimitLengths);
            }
            else if (step == 34)
            {
                SetupHuffmanTableMaterial(huffmanTableMaterial, DcFrequencyBinCount);
                WarmupMaterialPass(huffmanTableMaterial, dcHuffmanLengths, dcHuffmanCodes, HuffmanTablePassBuildCanonicalCodes);
            }
            else if (step >= 35 && step <= 36)
            {
                if (step == 35)
                {
                    SetupRleAcFrequencyRowGroupMaterial(
                        rleAcFrequencyMaterial,
                        null,
                        0,
                        EncodeAcFrequencyBlockColumnsPerGroup,
                        1,
                        EncodeAcFrequencySlotsPerGroup,
                        false);
                }
                else
                {
                    SetupRleAcFrequencyMaterial(rleAcFrequencyMaterial);
                }
                WarmupMaterialPass(rleAcFrequencyMaterial, rleSymbols, rleAcFrequencyRows, step - 35);
            }
            else if (step == 37)
            {
                SetupRleDcDeltaMaterial(rleDcDeltaMaterial);
                WarmupMaterialPass(rleDcDeltaMaterial, rleSymbolsFromHuffmanPayload, dcDeltaFromHuffmanPayload, 0);
            }
            else if (step == 38)
            {
                SetupRleToSymbolFixedBuildPrefixBaseMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload);
                WarmupMaterialPass(rleToSymbolFixedMaterial, rleSymbolsFromHuffmanPayload, symbolFixedFromHuffmanPayload, RleToSymbolFixedPassBuildPrefixBase);
            }
            else if (step == 39)
            {
                SetupRleToSymbolFixedMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload, symbolFixedFromHuffmanPayload, 1);
                WarmupMaterialPass(rleToSymbolFixedMaterial, symbolFixedFromHuffmanPayload, symbolFixedWorkFromHuffmanPayload, RleToSymbolFixedPassScanPrefix);
            }
            else if (step == 40)
            {
                SetupRleToSymbolFixedMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload, symbolFixedWorkFromHuffmanPayload, 1);
                WarmupMaterialPass(rleToSymbolFixedMaterial, rleSymbolsFromHuffmanPayload, symbolFixedFromHuffmanPayload, RleToSymbolFixedPassDecodeSymbols);
            }
            else if (step == 41)
            {
                SetupHuffmanDecodeRleMaterial(huffmanDecodeRleMaterial, rleSymbolsFromHuffmanPayload, huffmanDecodeState, 0, 0);
                WarmupMaterialPass(huffmanDecodeRleMaterial, white, symbolFixedFromHuffmanPayload, HuffmanDecodeRlePassOutputGroup);
            }
            else if (step == 42)
            {
                SetupHuffmanDecodeRleMaterial(huffmanDecodeRleMaterial, rleSymbolsFromHuffmanPayload, huffmanDecodeState, 0, 0);
                WarmupMaterialPass(huffmanDecodeRleMaterial, white, huffmanDecodeStateTemp, HuffmanDecodeRlePassAdvanceState);
            }
            else if (step == 43)
            {
                SetupHuffmanBlockPageEncodeMaterial(huffmanBlockPageEncodeMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes, huffmanBlockPage, huffmanBlockPageState, 0, -1);
                WarmupMaterialPass(huffmanBlockPageEncodeMaterial, rleSymbols, huffmanBlockPageTemp, HuffmanBlockPagePassOutputGroup);
            }
            else if (step == 44)
            {
                SetupHuffmanBlockPageEncodeMaterial(huffmanBlockPageEncodeMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes, huffmanBlockPage, huffmanBlockPageState, 0, -1);
                WarmupMaterialPass(huffmanBlockPageEncodeMaterial, rleSymbols, huffmanBlockPageStateTemp, HuffmanBlockPagePassAdvanceState);
            }
            else if (step == 45)
            {
                SetupCoeffToRleSymbolsMaterial(coeffToRleSymbolsMaterial);
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", work);
                coeffToRleSymbolsMaterial.SetFloat("_PrefixStep", 1f);
                WarmupMaterialPass(coeffToRleSymbolsMaterial, work, rleSymbols, CoeffToRlePassScanPrefix);
            }
            else if (step == 46)
            {
                SetupCoeffToRleSymbolsMaterial(coeffToRleSymbolsMaterial);
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", work);
                WarmupMaterialPass(coeffToRleSymbolsMaterial, coefficients, rleSymbols, CoeffToRlePassBuildSymbols);
            }
            else if (step == 47)
            {
                SetupHuffmanBitCountMaterial(huffmanBitCountMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes);
                huffmanBitCountMaterial.SetTexture("_PreviousBitCountTex", huffmanBlockBits);
                huffmanBitCountMaterial.SetFloat("_BitCountSlotGroup", 0f);
                WarmupMaterialPass(huffmanBitCountMaterial, rleSymbols, huffmanBlockPageState, HuffmanBitCountPassAccumulate);
            }
            else if (step == 48)
            {
                SetupHuffmanAcDecodeLengthSummaryMaterial(huffmanDecodeRleMaterial, acHuffmanCodes, 1, 16);
                WarmupMaterialPass(huffmanDecodeRleMaterial, acHuffmanCodes, huffmanAcDecodeLengthSummary, HuffmanDecodeRlePassBuildAcLengthSummary);
            }
            else if (step == 49)
            {
                SetupHuffmanAcDecodeLookupMaterial(huffmanDecodeRleMaterial, acHuffmanCodes, null, 0, HuffmanDecodeAcSymbolsPerGroup, false);
                WarmupMaterialPass(huffmanDecodeRleMaterial, acHuffmanCodes, huffmanAcDecodeSymbolTableTemp, HuffmanDecodeRlePassBuildAcSymbolTable);
            }
            else if (step == 50)
            {
                SetupHuffmanRawLengthHistogramRangeMaterial(huffmanTableMaterial, rleAcFrequency, AcFrequencyBinCount, null, 0, false);
                WarmupMaterialPass(huffmanTableMaterial, huffmanRawLengths, huffmanRawLengthHistogramTemp, HuffmanTablePassBuildRawLengthHistogram);
            }
            else if (step == 51)
            {
                SetupHuffmanSingleLimitedHistogramMaterial(huffmanTableMaterial, rleAcFrequency, AcFrequencyBinCount, 0);
                WarmupMaterialPass(huffmanTableMaterial, huffmanRawLengthHistogram, huffmanRawLengthSingle, HuffmanTablePassLimitLengthHistogram);
            }
        }

        // Should warmup Shader stepを処理する
        private bool ShouldWarmupShaderStep(int step)
        {
            if (warmupMode == WarmupModeAll)
            {
                return true;
            }

            bool expansionPass = step >= 1 && step <= 5;
            int mappedStep = step > 5 ? step - 4 : step;
            if (mappedStep == 7 || mappedStep == 8
                || mappedStep == 9 || mappedStep == 10
                || mappedStep == 24 || mappedStep == 29
                || mappedStep >= 37 && mappedStep <= 42
                || mappedStep == 48 || mappedStep == 49)
            {
                expansionPass = true;
            }

            return warmupMode == WarmupModeExpansion ? expansionPass : !expansionPass;
        }

        // sourceとdestinationには必ず別Textureを渡し、反復passはping-pong RTで処理する
        // 同一RTへのBlitは結果が未定義になる。Material値は各Blit直前に設定するため、Material自体の複製は不要
        private void TrackedBlit(Texture source, RenderTexture destination)
        {
            RecordGpuDiagnosticBlit(source, destination, null, -1);
            VRCGraphics.Blit(source, destination);
            TrackGpuBlitDestination(destination);
        }

        // ed Blitを記録する
        private void TrackedBlit(Texture source, RenderTexture destination, Material material, int pass)
        {
            RecordGpuDiagnosticBlit(source, destination, material, pass);
            VRCGraphics.Blit(source, destination, material, pass);
            TrackGpuBlitDestination(destination);
        }

        // GPU Blit Destinationを記録する
        private void TrackGpuBlitDestination(RenderTexture destination)
        {
            if (!isRunning || warmupRunning || destination == null)
            {
                return;
            }

            // 同じcallback内の複数Blitは1 batchとして扱い、最後のdestinationだけをfence sourceにする
            gpuBlitSubmittedSinceSchedule = true;
            gpuFenceSourceTexture = destination;
        }

        // warmup Material Passを処理する
        private void WarmupMaterialPass(Material material, Texture source, RenderTexture destination, int pass)
        {
            if (material == null || source == null || destination == null)
            {
                return;
            }

            TrackedBlit(source, destination, material, pass);
        }

        // warmup 符号化 Passesを処理する
        private void WarmupEncodePasses(int plane)
        {
            int oldPlane = stage1PlaneMode;
            Texture oldActiveEncodeSourceTexture = activeEncodeSourceTexture;
            bool oldActiveEncodeSourceIsRawStorage = activeEncodeSourceIsRawStorage;
            bool oldActiveEncodeSrgb = activeEncodeSrgb;
            bool oldActiveSourceSrgb = activeSourceSrgb;
            stage1PlaneMode = ClampPlane(plane);
            Texture neutral = EnsureNeutralGrayTexture();
            Texture white = EnsureSolidTexture(true);

            EnsurePlaneWorkTextures(stage1PlaneMode);
            EnsureHuffmanRenderTextures(stage1PlaneMode);
            TrackedBlit(neutral, work);
            TrackedBlit(neutral, coefficients);
            TrackedBlit(white, dcHuffmanCodes);
            TrackedBlit(white, acHuffmanCodes);

            if (encodeMaterial != null)
            {
                activeEncodeSourceTexture = sourceTexture != null ? sourceTexture : neutral;
                activeEncodeSourceIsRawStorage = false;
                activeEncodeSrgb = encodeSrgb;
                activeSourceSrgb = sourceTextureSrgb;
                SetupEncodeMaterial(encodeMaterial);
                SetupHorizontalAccumulationMaterial(encodeMaterial, null, 0);
                TrackedBlit(activeEncodeSourceTexture, work, encodeMaterial, 0);
            }

            if (coeffToRleSymbolsMaterial != null)
            {
                SetupCoeffToRleSymbolsBuildPrefixBaseMaterial(coeffToRleSymbolsMaterial);
                TrackedBlit(coefficients, work, coeffToRleSymbolsMaterial, CoeffToRlePassBuildPrefixBase);
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", work);
                coeffToRleSymbolsMaterial.SetFloat("_PrefixStep", 1f);
                TrackedBlit(work, rleSymbols, coeffToRleSymbolsMaterial, CoeffToRlePassScanPrefix);
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", work);
                TrackedBlit(coefficients, rleSymbols, coeffToRleSymbolsMaterial, CoeffToRlePassBuildSymbols);
            }

            if (rleAcFrequencyMaterial != null)
            {
                SetupRleAcFrequencyRowGroupMaterial(
                    rleAcFrequencyMaterial,
                    null,
                    0,
                    EncodeAcFrequencyBlockColumnsPerGroup,
                    1,
                    EncodeAcFrequencySlotsPerGroup,
                    false);
                TrackedBlit(rleSymbols, rleAcFrequencyRows, rleAcFrequencyMaterial, 0);
            }

            if (dcDeltaMaterial != null)
            {
                SetupDcDeltaMaterial(dcDeltaMaterial);
                TrackedBlit(coefficients, dcDelta, dcDeltaMaterial, 0);
            }

            if (dcFrequencyMaterial != null)
            {
                SetupDcFrequencyMaterial(dcFrequencyMaterial);
                TrackedBlit(dcDelta, dcFrequencyRows, dcFrequencyMaterial, 0);
            }

            if (huffmanTableMaterial != null)
            {
                // raw treeはDC/ACともCPUで構築するため、動的配列を使う旧GPU passはwarmupでも実行しない
                SetupHuffmanLengthLimitMaterial(huffmanTableMaterial, dcFrequency, DcFrequencyBinCount);
                TrackedBlit(huffmanRawLengths, dcHuffmanLengths, huffmanTableMaterial, HuffmanTablePassLimitLengths);
                SetupHuffmanTableMaterial(huffmanTableMaterial, DcFrequencyBinCount);
                TrackedBlit(dcHuffmanLengths, dcHuffmanCodes, huffmanTableMaterial, HuffmanTablePassBuildCanonicalCodes);
                SetupHuffmanLengthLimitMaterial(huffmanTableMaterial, rleAcFrequency, AcFrequencyBinCount);
                TrackedBlit(huffmanRawLengths, acHuffmanLengths, huffmanTableMaterial, HuffmanTablePassLimitLengths);
                SetupHuffmanTableMaterial(huffmanTableMaterial, AcFrequencyBinCount);
                TrackedBlit(acHuffmanLengths, acHuffmanCodes, huffmanTableMaterial, HuffmanTablePassBuildCanonicalCodes);
            }

            if (huffmanBitCountMaterial != null)
            {
                SetupHuffmanBitCountMaterial(huffmanBitCountMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes);
                TrackedBlit(rleSymbols, huffmanBlockBits, huffmanBitCountMaterial, HuffmanBitCountPassInitialize);
                huffmanBitCountMaterial.SetTexture("_PreviousBitCountTex", huffmanBlockBits);
                huffmanBitCountMaterial.SetFloat("_BitCountSlotGroup", 0f);
                TrackedBlit(rleSymbols, huffmanBlockPageState, huffmanBitCountMaterial, HuffmanBitCountPassAccumulate);
            }

            if (huffmanBlockPageEncodeMaterial != null)
            {
                SetupHuffmanBlockPageEncodeMaterial(huffmanBlockPageEncodeMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes, huffmanBlockPage, huffmanBlockPageStateTemp, 0, -1);
                TrackedBlit(rleSymbols, huffmanBlockPageState, huffmanBlockPageEncodeMaterial, HuffmanBlockPagePassInitializeState);
                SetupHuffmanBlockPageEncodeMaterial(huffmanBlockPageEncodeMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes, huffmanBlockPage, huffmanBlockPageState, 0, -1);
                TrackedBlit(rleSymbols, huffmanBlockPageTemp, huffmanBlockPageEncodeMaterial, HuffmanBlockPagePassOutputGroup);
                TrackedBlit(rleSymbols, huffmanBlockPageStateTemp, huffmanBlockPageEncodeMaterial, HuffmanBlockPagePassAdvanceState);
            }

            if (huffmanBlockValidBytesMaterial != null)
            {
                SetupHuffmanBlockValidBytesMaterial(huffmanBlockValidBytesMaterial);
                TrackedBlit(huffmanBlockBits, huffmanBlockValidBytes, huffmanBlockValidBytesMaterial, 0);
            }

            if (huffmanChunkValidBytesMaterial != null)
            {
                SetupHuffmanChunkValidBytesBuildMaterial(huffmanChunkValidBytesMaterial);
                TrackedBlit(huffmanBlockValidBytes, huffmanChunkValidBytes, huffmanChunkValidBytesMaterial, 0);
            }

            if (huffmanPayloadGatherMaterial != null)
            {
                SetupHuffmanPayloadGatherMaterial(huffmanPayloadGatherMaterial, GetHuffmanPayloadTexture(stage1PlaneMode), 0);
                TrackedBlit(huffmanBlockPage, huffmanPayloadGather, huffmanPayloadGatherMaterial, 0);
            }

            if (huffmanMetadataPackMaterial != null)
            {
                SetupHuffmanMetadataPackMaterial(huffmanMetadataPackMaterial);
                TrackedBlit(huffmanChunkValidBytes, huffmanMetadataPack, huffmanMetadataPackMaterial, 0);
            }

            stage1PlaneMode = oldPlane;
            activeEncodeSourceTexture = oldActiveEncodeSourceTexture;
            activeEncodeSourceIsRawStorage = oldActiveEncodeSourceIsRawStorage;
            activeEncodeSrgb = oldActiveEncodeSrgb;
            activeSourceSrgb = oldActiveSourceSrgb;
        }

        // warmup 復号 Passesを処理する
        private void WarmupDecodePasses(int plane)
        {
            int oldPlane = stage1PlaneMode;
            stage1PlaneMode = ClampPlane(plane);
            EnsurePlaneWorkTextures(stage1PlaneMode);
            EnsureHuffmanRenderTextures(stage1PlaneMode);
            EnsureHuffmanDecodeRenderTextures(stage1PlaneMode);
            quantTexture = EnsureRuntimeQuantTexture(quality, quantPreset);

            TrackedBlit(EnsureNeutralGrayTexture(), reconstructedFromHuffmanPayload);
            TrackedBlit(EnsureSolidTexture(true), dcScanFromHuffmanPayload);
            TrackedBlit(EnsureSolidTexture(true), symbolFixedFromHuffmanPayload);
            TrackedBlit(EnsureSolidTexture(true), symbolFixedWorkFromHuffmanPayload);

            if (dcScanMaterial != null)
            {
                SetupDcScanMaterial(dcScanMaterial);
                TrackedBlit(dcDeltaFromHuffmanPayload, dcScanFromHuffmanPayload, dcScanMaterial, 0);
            }

            if (rleToSymbolFixedMaterial != null)
            {
                SetupRleToSymbolFixedBuildPrefixBaseMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload);
                TrackedBlit(rleSymbolsFromHuffmanPayload, symbolFixedWorkFromHuffmanPayload, rleToSymbolFixedMaterial, RleToSymbolFixedPassBuildPrefixBase);
                RenderRleToSymbolFixedPrefixScan(rleSymbolsFromHuffmanPayload);
            }

            if (decodeSymbolsMaterial != null)
            {
                SetupDecodeSymbolsMaterial(decodeSymbolsMaterial, work, 0, 0);
                TrackedBlit(symbolFixedFromHuffmanPayload, reconstructedFromHuffmanPayload, decodeSymbolsMaterial, 0);
            }

            stage1PlaneMode = oldPlane;
        }

        // 描画 RLE To symbol Fixed Prefix scanを処理する
        private void RenderRleToSymbolFixedPrefixScan(Texture rleSource)
        {
            RenderTexture previous = symbolFixedWorkFromHuffmanPayload;
            RenderTexture target = symbolFixedFromHuffmanPayload;
            for (int scanIndex = 0; scanIndex < RleToSymbolFixedPrefixScanPassCount; scanIndex++)
            {
                SetupRleToSymbolFixedMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload, previous, 1 << scanIndex);
                TrackedBlit(previous, target, rleToSymbolFixedMaterial, RleToSymbolFixedPassScanPrefix);
                RenderTexture swap = previous;
                previous = target;
                target = swap;
            }

            SetupRleToSymbolFixedMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload, previous, 1);
            TrackedBlit(rleSource, symbolFixedFromHuffmanPayload, rleToSymbolFixedMaterial, RleToSymbolFixedPassDecodeSymbols);
        }

        // GPU 段階 Delayを予約する
        private void ScheduleGpuStageDelay(string eventName)
        {
            ScheduleGpuStageDelay(eventName, -1);
        }

        // GPU 段階 Delayを予約する
        private void ScheduleGpuStageDelay(string eventName, int stageGapStep)
        {
            // callback内のBlitは、直前に設定したmaterial parameterを保持したままGPU queueへ投入される
            // 実測で安全だった処理は同一frameへまとめるが、readback・RT再利用・最終結果公開の境界では
            // marker Blitと1pixel readbackを必須にし、未完了のGPU処理をCPU側が追い越さないようにする
            RefreshOperationProgressWatchdog();
            CompleteGpuDiagnosticStage();
            bool submittedBatch = gpuBlitSubmittedSinceSchedule && gpuFenceSourceTexture != null;
            int pendingBatchCount = gpuUnfencedBatchCount + (submittedBatch ? 1 : 0);
            RenderTexture fenceSource = submittedBatch
                ? gpuFenceSourceTexture
                : gpuUnfencedBatchSourceTexture;
            int nextGpuFenceBatchLimit = GetGpuFenceBatchLimit(eventName, stageGapStep);
            int gpuFenceBatchLimit = gpuUnfencedBatchCount > 0
                ? Mathf.Min(gpuUnfencedBatchLimit, nextGpuFenceBatchLimit)
                : nextGpuFenceBatchLimit;
            bool requiresGpuFence = fenceSource != null && pendingBatchCount > 0
                && (pendingBatchCount >= gpuFenceBatchLimit || RequiresGpuFenceBeforeContinuation(eventName));
            BeginGpuDiagnosticStage(eventName, stageGapStep, requiresGpuFence);
            CancelGpuStageDelay();
            if (activeRequestHandleId != InvalidHandleId && !activeRequestIsExpansion
                && isRunning && stageGapStep >= 0 && stageGapStep < GpuEncodeStageGapStepCount)
            {
                if (activeCompressionUsesCapacityLimit && !capacityPrepassCompletedForRequest)
                {
                    compressionProgressStage = ProgressStagePreparing;
                }
                else
                {
                    int plane = stageGapStep / GpuEncodeStageGapKindsPerPlane;
                    int stage = stageGapStep - plane * GpuEncodeStageGapKindsPerPlane;
                    float mappedProgress;
                    if (huffmanReadbackTotalWeightBytes > 0)
                    {
                        // planeごとにencode→metadata/payload readbackを行う実際の順序へ合わせる
                        // 前planeのreadback後も後続planeの圧縮中に進捗が止まって見えないようにする
                        float planeEncodeProgress = GetCompressionPlaneEncodeProgress01(plane, stage);
                        mappedProgress = GetCompressionPlaneSequenceProgress01(plane, planeEncodeProgress * 0.25f);
                    }
                    else
                    {
                        float fullEncodeProgress = GetCompressionFullEncodeProgress01(plane, stage);
                        mappedProgress = activeCompressionUsesCapacityLimit
                            ? CompressionProgressFullEncodeStart
                                + fullEncodeProgress * (CompressionProgressFullEncodeEnd - CompressionProgressFullEncodeStart)
                            : 0.2f + fullEncodeProgress * (CompressionProgressReadbackStart - 0.2f);
                    }
                    compressionProgress01 = Mathf.Max(compressionProgress01, mappedProgress);
                    if (compressionProgressStage != ProgressStageFinalizing)
                    {
                        compressionProgressStage = ProgressStageProcessing;
                    }
                }
            }
            if (EnableTimingDiagnostics)
            {
                pendingGpuStageGapStep = stageGapStep;
                timingCurrentFpsPhase = TimingFpsPhaseNone;
                if (stageGapStep >= 0 && stageGapStep < GpuEncodeStageGapStepCount)
                {
                    int encodeStage = stageGapStep - (stageGapStep / GpuEncodeStageGapKindsPerPlane) * GpuEncodeStageGapKindsPerPlane;
                    if (encodeStage == 0)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseEncodeDct;
                    }
                    else if (encodeStage == 1)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseEncodeRle;
                    }
                    else if (encodeStage == 2)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseEncodeAcFreq;
                    }
                    else if (encodeStage == 3)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseEncodeDcFreq;
                    }
                    else if (encodeStage == 4)
                    {
                        if (encodeSubStage == 4)
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseEncodeBitCount;
                        }
                        else if (encodeSubStage >= 5)
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseEncodeOverflow;
                        }
                        else
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseEncodeTable;
                        }
                    }
                    else if (encodeStage == 5)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseEncodeBlock;
                    }
                    else if (encodeStage == 6)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseEncodePayload;
                    }
                    else if (encodeStage == 7)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseEncodeReadback;
                    }
                }
                else if (stageGapStep >= GpuEncodeStageGapStepCount)
                {
                    int decodeStep = stageGapStep - GpuEncodeStageGapStepCount;
                    int composeStep = GpuDecodeStageGapKindsPerPlane * 4;
                    if (decodeStep == composeStep)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseDecodeCompose;
                    }
                    else if (decodeStep == composeStep + 1)
                    {
                        timingCurrentFpsPhase = TimingFpsPhaseDecodeOutput;
                    }
                    else if (decodeStep >= 0 && decodeStep < composeStep)
                    {
                        int stage = decodeStep - (decodeStep / GpuDecodeStageGapKindsPerPlane) * GpuDecodeStageGapKindsPerPlane;
                        if (stage == 0)
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseDecodeUpload;
                        }
                        else if (stage == 1)
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseDecodeBitOffset;
                        }
                        else if (stage == 2)
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseDecodeRle;
                        }
                        else if (stage == 3)
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseDecodeScan;
                        }
                        else if (stage == 4)
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseDecodeFix;
                        }
                        else if (stage == 5)
                        {
                            timingCurrentFpsPhase = TimingFpsPhaseDecodeIdct;
                        }
                    }
                }
                WriteTimingMillis(TimingOffsetGpuStageDelayStartMs, GetNowMillis());
            }

            if (requiresGpuFence && isRunning)
            {
                gpuFenceSourceTexture = fenceSource;
                gpuFenceContinuationEvent = eventName;
                gpuFenceContinuationGapStep = stageGapStep;
                gpuFenceMarkerQueued = true;
                SendCustomEventDelayedFrames(nameof(_RunGpuFenceMarkerBlit), 1);
                return;
            }

            gpuUnfencedBatchCount = pendingBatchCount;
            gpuUnfencedBatchSourceTexture = fenceSource;
            gpuUnfencedBatchLimit = gpuFenceBatchLimit;

            int immediateBatchLimit = GetGpuImmediateBatchLimit(eventName, stageGapStep);
            bool continueInCurrentFrame = submittedBatch
                && immediateBatchLimit > 1
                && pendingBatchCount % immediateBatchLimit != 0;
            if (continueInCurrentFrame)
            {
                // 各callbackはBlit直前にmaterial parameterを設定する。readbackやCPU-only stepは
                // submittedBatch=falseになるため、従来どおり必ず次frameへ分離される
                SendCustomEvent(eventName);
                return;
            }

            SendCustomEventDelayedFrames(eventName, 1);
        }

        // Requires GPU fence Before Continuationを処理する
        private bool RequiresGpuFenceBeforeContinuation(string eventName)
        {
            return eventName == nameof(_CompleteInputCopy)
                || eventName == nameof(_StartCurrentHuffmanReadbackAfterPlaneRender)
                || eventName == nameof(_BeginCapacityPrepassStatsReadbackDrain)
                || eventName == nameof(_CompleteDecodedOutputAfterCompose)
                || RequiresGpuFenceBeforeCpuHuffmanReadback(eventName);
        }

        // Requires GPU fence Before CPU Huffman readbackを処理する
        private bool RequiresGpuFenceBeforeCpuHuffmanReadback(string eventName)
        {
            if (cpuAcHuffmanPhase != CpuAcHuffmanPhaseIdle)
            {
                return false;
            }

            if (eventName == nameof(_RunCapacityPrepassCandidateStep))
            {
                return capacityPrepassCandidateSubStage == CapacityPrepassDcRawSubStage
                    || capacityPrepassCandidateSubStage == CapacityPrepassAcRawBaseSubStage;
            }

            if (eventName != nameof(_RunCurrentPlaneRoundtripStage))
            {
                return false;
            }

            int plane = Mathf.Clamp(huffmanReadbackPlane, PlaneY, PlaneCr);
            int tableStageGapStep = GetGpuEncodeStageGapStep(plane, 4);
            if (gpuStage != 4 || tableStageGapStep < 0)
            {
                return false;
            }

            // frequency/table GPU batchの直後にCPU用AsyncGPUReadbackを発行しない
            // DC開始とAC開始だけを明示的にfenceし、Request自体は後続の単独frameへ残す
            return encodeSubStage <= 0 || encodeSubStage == HuffmanTableAcRawBaseSubStage;
        }

        // GPU fence batch 上限を返す
        private int GetGpuFenceBatchLimit(string eventName, int stageGapStep)
        {
            if (eventName == nameof(_RunCurrentPlaneRoundtripStage))
            {
                if (stageGapStep < 0 || stageGapStep >= GpuEncodeStageGapStepCount)
                {
                    return NormalGpuFenceBatchLimit;
                }

                int encodeStage = stageGapStep - (stageGapStep / GpuEncodeStageGapKindsPerPlane)
                    * GpuEncodeStageGapKindsPerPlane;
                return GetEncodeGpuFenceBatchLimit(encodeStage);
            }

            if (eventName == nameof(_RunCapacityPrepassCandidateStep))
            {
                return GetCapacityPrepassGpuFenceBatchLimit();
            }

            if (eventName == nameof(_RunCapacityPrepassPrepareStep)
                || eventName == nameof(_RunCapacityPrepassCandidateSetupStep))
            {
                return HighFpsGpuFenceBatchLimit;
            }

            if (eventName == nameof(_RunCurrentHuffmanByteDecodeStage))
            {
                int decodeStep = stageGapStep - GpuEncodeStageGapStepCount;
                if (decodeStep < 0 || decodeStep >= GpuDecodeStageGapKindsPerPlane * 4)
                {
                    return NormalGpuFenceBatchLimit;
                }

                int stage = decodeStep - (decodeStep / GpuDecodeStageGapKindsPerPlane)
                    * GpuDecodeStageGapKindsPerPlane;
                // decode stage 1=bit offset, 2=Huffman RLE, 3=DC scan, 4=RLE to fixed
                // 最新実測で1/3/4はいずれも4 callback/frame時に59-60 FPSだったため同じbatchを使う
                if (stage == 2)
                {
                    return DecodeRleGpuFenceBatchLimit;
                }
                if (stage == 1 || stage == 3 || stage == 4)
                {
                    return MeasuredGpuFenceBatchLimit;
                }
                return HighFpsGpuFenceBatchLimit;
            }

            return NormalGpuFenceBatchLimit;
        }

        // GPU 即時 batch 上限を返す
        private int GetGpuImmediateBatchLimit(string eventName, int stageGapStep)
        {
            if (eventName == nameof(_RunCurrentPlaneRoundtripStage))
            {
                if (stageGapStep < 0 || stageGapStep >= GpuEncodeStageGapStepCount)
                {
                    return NormalGpuImmediateBatchLimit;
                }

                int encodeStage = stageGapStep - (stageGapStep / GpuEncodeStageGapKindsPerPlane)
                    * GpuEncodeStageGapKindsPerPlane;
                return GetEncodeGpuImmediateBatchLimit(encodeStage);
            }

            if (eventName == nameof(_RunCapacityPrepassCandidateStep))
            {
                return GetCapacityPrepassGpuImmediateBatchLimit();
            }

            if (eventName == nameof(_RunCapacityPrepassPrepareStep)
                || eventName == nameof(_RunCapacityPrepassCandidateSetupStep))
            {
                return HighFpsGpuImmediateBatchLimit;
            }

            if (eventName == nameof(_RunCurrentHuffmanByteDecodeStage))
            {
                int decodeStep = stageGapStep - GpuEncodeStageGapStepCount;
                if (decodeStep < 0 || decodeStep >= GpuDecodeStageGapKindsPerPlane * 4)
                {
                    return NormalGpuImmediateBatchLimit;
                }

                int stage = decodeStep - (decodeStep / GpuDecodeStageGapKindsPerPlane)
                    * GpuDecodeStageGapKindsPerPlane;
                // fence側と同じstage分類を使い、同一frameで投入するcallback数だけを8へ増やす
                if (stage == 2)
                {
                    return DecodeRleGpuImmediateBatchLimit;
                }
                if (stage == 1 || stage == 3 || stage == 4)
                {
                    return MeasuredGpuImmediateBatchLimit;
                }
                return HighFpsGpuImmediateBatchLimit;
            }

            return NormalGpuImmediateBatchLimit;
        }

        // 符号化 GPU fence batch 上限を返す
        private int GetEncodeGpuFenceBatchLimit(int encodeStage)
        {
            // Android診断ではRLE/DC frequencyが59-60 FPS、AC frequency/payloadが20-49 FPSだった
            // stage名だけで一律にせず、同じ判定をimmediate/fenceの両方へ適用する
            if (encodeStage == 1 || encodeStage == 3)
            {
                return HighFpsGpuFenceBatchLimit;
            }
            if (encodeStage == 2 || encodeStage == 6)
            {
                return MeasuredGpuFenceBatchLimit;
            }
            if (encodeStage == 4)
            {
                if (IsLimitedHistogramHuffmanTableSubStage(encodeSubStage))
                {
                    return LowQueueGpuFenceBatchLimit;
                }
                return IsHighFpsHuffmanTableSubStage(encodeSubStage)
                    ? HighFpsGpuFenceBatchLimit
                    : MeasuredGpuFenceBatchLimit;
            }
            if (encodeStage == 5)
            {
                int blockPageStepCount = GetEncodeBlockPageStepCount();
                return encodeSubStage >= blockPageStepCount + 2
                    ? HighFpsGpuFenceBatchLimit
                    : MeasuredGpuFenceBatchLimit;
            }
            return encodeStage == 0 ? LowFpsGpuFenceBatchLimit : NormalGpuFenceBatchLimit;
        }

        // 符号化 GPU 即時 batch 上限を返す
        private int GetEncodeGpuImmediateBatchLimit(int encodeStage)
        {
            if (encodeStage == 1 || encodeStage == 3)
            {
                return HighFpsGpuImmediateBatchLimit;
            }
            if (encodeStage == 2)
            {
                return MeasuredGpuImmediateBatchLimit;
            }
            if (encodeStage == 6)
            {
                return MeasuredGpuImmediateBatchLimit;
            }
            if (encodeStage == 4)
            {
                if (IsLimitedHistogramHuffmanTableSubStage(encodeSubStage))
                {
                    return LowQueueGpuImmediateBatchLimit;
                }
                return IsHighFpsHuffmanTableSubStage(encodeSubStage)
                    ? HighFpsGpuImmediateBatchLimit
                    : MeasuredGpuImmediateBatchLimit;
            }
            if (encodeStage == 5)
            {
                int blockPageStepCount = GetEncodeBlockPageStepCount();
                return encodeSubStage >= blockPageStepCount + 2
                    ? HighFpsGpuImmediateBatchLimit
                    : MeasuredGpuImmediateBatchLimit;
            }
            return encodeStage == 0 ? SingleGpuImmediateBatchLimit : NormalGpuImmediateBatchLimit;
        }

        // High FPS Huffman table Sub 段階かを判定する
        private bool IsHighFpsHuffmanTableSubStage(int subStage)
        {
            // raw histogramとbit-countは59-60 FPS。length制限/canonical/overflowだけを低FPS側へ残す
            bool dcRawHistogram = subStage >= HuffmanTableDcRawHistogramBaseSubStage
                && subStage < HuffmanTableDcLimitedHistogramBaseSubStage;
            bool acRawHistogram = subStage >= HuffmanTableAcRawHistogramBaseSubStage
                && subStage < HuffmanTableAcLimitedHistogramBaseSubStage;
            bool bitCount = subStage >= HuffmanBitCountBaseSubStage
                && subStage < HuffmanOverflowBaseSubStage;
            return dcRawHistogram || acRawHistogram || bitCount;
        }

        // Limited Histogram Huffman table Sub 段階かを判定する
        private bool IsLimitedHistogramHuffmanTableSubStage(int subStage)
        {
            // pass 4/5は各callbackで連続2 Blitするため、他のHuffman table passとは別の投入上限を使う
            bool dcLimitedHistogram = subStage >= HuffmanTableDcLimitedHistogramBaseSubStage
                && subStage < HuffmanTableDcAssignLengthsSubStage;
            bool acLimitedHistogram = subStage >= HuffmanTableAcLimitedHistogramBaseSubStage
                && subStage < HuffmanTableAcLimitBaseSubStage;
            return dcLimitedHistogram || acLimitedHistogram;
        }

        // 容量 事前計算 GPU fence batch 上限を返す
        private int GetCapacityPrepassGpuFenceBatchLimit()
        {
            if (IsLowQueueCapacityPrepassSubStage(capacityPrepassCandidateSubStage))
            {
                return LowQueueGpuFenceBatchLimit;
            }
            return IsHighFpsCapacityPrepassSubStage(capacityPrepassCandidateSubStage)
                ? HighFpsGpuFenceBatchLimit
                : MeasuredGpuFenceBatchLimit;
        }

        // 容量 事前計算 GPU 即時 batch 上限を返す
        private int GetCapacityPrepassGpuImmediateBatchLimit()
        {
            if (IsLowQueueCapacityPrepassSubStage(capacityPrepassCandidateSubStage))
            {
                return LowQueueGpuImmediateBatchLimit;
            }
            return IsHighFpsCapacityPrepassSubStage(capacityPrepassCandidateSubStage)
                ? HighFpsGpuImmediateBatchLimit
                : MeasuredGpuImmediateBatchLimit;
        }

        // Low queue 容量 事前計算 Sub 段階かを判定する
        private bool IsLowQueueCapacityPrepassSubStage(int subStage)
        {
            bool dcLimitedHistogram = subStage >= CapacityPrepassDcLimitedHistogramBaseSubStage
                && subStage < CapacityPrepassDcAssignLengthsSubStage;
            bool acLimitedHistogram = subStage >= CapacityPrepassAcLimitedHistogramBaseSubStage
                && subStage < CapacityPrepassAcLimitBaseSubStage;
            // bit-count 8 Blit + stats 4 Blitを同じframeへ積まず、次のGPU fenceへ負荷を持ち越さない
            bool bitCountOrStats = subStage >= CapacityPrepassBitCountBaseSubStage;
            return dcLimitedHistogram || acLimitedHistogram || bitCountOrStats;
        }

        // High FPS 容量 事前計算 Sub 段階かを判定する
        private bool IsHighFpsCapacityPrepassSubStage(int subStage)
        {
            // 容量推定も本圧縮と同じpass単位で分類し、軽いRLE/DC/bit-countまで細分化し続けない
            if (subStage < CapacityPrepassAcFrequencyBaseSubStage)
            {
                return true;
            }
            if (subStage < CapacityPrepassAcFrequencyTotalSubStage)
            {
                return false;
            }
            if (subStage < CapacityPrepassDcRawHistogramBaseSubStage)
            {
                return true;
            }
            if (subStage < CapacityPrepassDcLimitedHistogramBaseSubStage)
            {
                return true;
            }
            if (subStage < CapacityPrepassAcRawHistogramBaseSubStage)
            {
                return false;
            }
            if (subStage < CapacityPrepassAcLimitedHistogramBaseSubStage)
            {
                return true;
            }
            if (subStage < CapacityPrepassBitCountBaseSubStage)
            {
                return false;
            }
            return true;
        }

        // Refresh Operation 進捗 Watchdogを処理する
        private void RefreshOperationProgressWatchdog()
        {
            if (!isRunning)
            {
                return;
            }

            int signature = GetOperationProgressSignature();
            if (signature == operationProgressSignature)
            {
                return;
            }

            operationProgressSignature = signature;
            operationLastProgressAtRealtime = Time.realtimeSinceStartup;
        }

        // Operation 進捗 Signatureを返す
        private int GetOperationProgressSignature()
        {
            // eventを再送するだけのreadback pollでは値が変わらないよう、実作業のcursorだけを含める
            // これにより正常な長時間処理は継続し、同じ段階で停止した場合だけtimeoutできる
            int signature = currentOperationIsExpansion ? 17 : 31;
            signature = signature * 31 + sourceNormalizationUploadStep;
            signature = signature * 31 + sourceAlphaScanPixelOffset;
            signature = signature * 31 + capacityPrepassPreparePlane;
            signature = signature * 31 + capacityPrepassPrepareSubStage;
            signature = signature * 31 + capacityPrepassCandidatePlane;
            signature = signature * 31 + capacityPrepassCandidateSubStage;
            signature = signature * 31 + capacityPrepassCandidateSetupStep;
            signature = signature * 31 + capacityPrepassCandidateAttempt;
            signature = signature * 31 + gpuStage;
            signature = signature * 31 + encodeSubStage;
            signature = signature * 31 + huffmanReadbackPlane;
            signature = signature * 31 + huffmanReadbackKind;
            signature = signature * 31 + huffmanReadbackStep;
            signature = signature * 31 + huffmanReadbackCompletedWeightBytes;
            signature = signature * 31 + pendingPayloadStoreOffset;
            signature = signature * 31 + packPrepareSegmentIndex;
            signature = signature * 31 + packSegmentIndex;
            signature = signature * 31 + packSegmentCopyOffset;
            signature = signature * 31 + packCompletedStepCount;
            signature = signature * 31 + restoreSegmentIndex;
            signature = signature * 31 + restoreSegmentCopyOffset;
            signature = signature * 31 + restoreDataOffset;
            signature = signature * 31 + decodePlane;
            signature = signature * 31 + decodeStage;
            signature = signature * 31 + decodeLocalIndex;
            signature = signature * 31 + decodeUploadStep;
            signature = signature * 31 + pendingChunkOffsetOrderIndex;
            signature = signature * 31 + pendingChunkOffsetCumulative;
            signature = signature * 31 + pendingDecodePayloadUploadOffset;
            signature = signature * 31 + cpuAcHuffmanPhase;
            signature = signature * 31 + cpuAcHuffmanInitializeSymbol;
            signature = signature * 31 + cpuAcHuffmanMergeCount;
            signature = signature * 31 + cpuAcHuffmanLengthLeafIndex;
            return signature;
        }

        // GPU fence marker Blitを実行する
        public void _RunGpuFenceMarkerBlit()
        {
            if ((!isRunning && !failedGpuResourceReleasePending) || !gpuFenceMarkerQueued || gpuFenceSourceTexture == null)
            {
                return;
            }

            gpuFenceMarkerQueued = false;
            gpuFenceMarkerTexture = EnsureRuntimeRenderTexture(
                gpuFenceMarkerTexture,
                "IC_Library_GpuFenceMarker",
                GpuFenceMarkerSize,
                GpuFenceMarkerSize,
                RenderTextureFormat.ARGB32);
            if (gpuFenceMarkerTexture == null)
            {
                FailGpuFence("DCTH GPU fence marker texture could not be created.");
                return;
            }

            RecordGpuDiagnosticBlit(gpuFenceSourceTexture, gpuFenceMarkerTexture, null, -2);
            VRCGraphics.Blit(gpuFenceSourceTexture, gpuFenceMarkerTexture);
            gpuFenceReadbackRequestQueued = true;
            SendCustomEventDelayedFrames(nameof(_RunGpuFenceReadbackRequest), 1);
        }

        // GPU fence readback requestを実行する
        public void _RunGpuFenceReadbackRequest()
        {
            if ((!isRunning && !failedGpuResourceReleasePending) || !gpuFenceReadbackRequestQueued
                || gpuFenceReadbackPending || gpuFenceMarkerTexture == null)
            {
                return;
            }

            gpuFenceReadbackRequestQueued = false;
            gpuFenceReadbackRequest = VRCAsyncGPUReadback.Request(
                gpuFenceMarkerTexture,
                0,
                0,
                1,
                0,
                1,
                0,
                1,
                TextureFormat.RGBA32,
                this);
            gpuFenceReadbackPending = true;
            failedFenceReadbackPending = failedGpuResourceReleasePending;
            SendCustomEventDelayedFrames(nameof(_PollGpuFenceReadback), 1);
        }

        // GPU fence readbackの完了状態を確認する
        public void _PollGpuFenceReadback()
        {
            if ((!isRunning && !failedGpuResourceReleasePending) || !gpuFenceReadbackPending)
            {
                return;
            }

            if (!gpuFenceReadbackRequest.done)
            {
                SendCustomEventDelayedFrames(nameof(_PollGpuFenceReadback), 1);
                return;
            }

            gpuFenceReadbackPending = false;
            if (failedGpuResourceReleasePending)
            {
                failedFenceReadbackPending = false;
                _PollFailedGpuResourceRelease();
                return;
            }
            if (gpuFenceReadbackRequest.hasError)
            {
                FailGpuFence("DCTH GPU fence readback failed.");
                return;
            }

            string continuationEvent = gpuFenceContinuationEvent;
            int continuationGapStep = gpuFenceContinuationGapStep;
            gpuFenceContinuationEvent = "";
            gpuFenceContinuationGapStep = -1;
            gpuFenceSourceTexture = null;
            if (string.IsNullOrEmpty(continuationEvent))
            {
                FailGpuFence("DCTH GPU fence continuation is missing.");
                return;
            }

            if (EnableTimingDiagnostics)
            {
                pendingGpuStageGapStep = continuationGapStep;
                WriteTimingMillis(TimingOffsetGpuStageDelayStartMs, GetNowMillis());
            }
            SendCustomEventDelayedFrames(continuationEvent, 1);
        }

        // GPU fenceを失敗状態にする
        private void FailGpuFence(string message)
        {
            CancelGpuStageDelay();
            if (currentOperationIsExpansion)
            {
                FailHuffmanDecode(message);
            }
            else
            {
                FailHuffmanEncode(message);
            }
        }

        // GPU 段階 Delayをキャンセルする
        private void CancelGpuStageDelay()
        {
            // 異常終了後に未完了fenceを待っている間は、そのfenceのsourceと進行状態を保持する
            if (failedGpuResourceReleasePending)
            {
                if (EnableTimingDiagnostics)
                {
                    pendingGpuStageGapStep = -1;
                    timingCurrentFpsPhase = TimingFpsPhaseNone;
                }
                return;
            }

            gpuBlitSubmittedSinceSchedule = false;
            gpuFenceSourceTexture = null;
            gpuUnfencedBatchSourceTexture = null;
            gpuUnfencedBatchCount = 0;
            gpuUnfencedBatchLimit = NormalGpuFenceBatchLimit;
            gpuFenceMarkerQueued = false;
            gpuFenceReadbackRequestQueued = false;
            gpuFenceReadbackPending = false;
            gpuFenceContinuationEvent = "";
            gpuFenceContinuationGapStep = -1;
            if (EnableTimingDiagnostics)
            {
                pendingGpuStageGapStep = -1;
                timingCurrentFpsPhase = TimingFpsPhaseNone;
            }
        }

        // 解放前に完了を待つGPU readbackと未fenceのBlitを記録する
        private void CapturePendingGpuReadbacksForRelease()
        {
            bool mainRequestActive = warmupGpuReadbackPending
                || sourceNormalizationReadbackPending
                || capacityPrepassReadbackPending
                || huffmanReadbackPending
                || cpuAcHuffmanPhase == CpuAcHuffmanPhaseWaitReadback;
            if (mainRequestActive && !huffmanReadbackRequest.done)
            {
                failedMainReadbackPending = true;
            }

            // 同じライブラリオブジェクトでreadbackを重ねない。既存request完了後に未fenceのBlitだけを1px fenceへ送る
            if (!failedMainReadbackPending
                && !gpuFenceMarkerQueued
                && !gpuFenceReadbackRequestQueued
                && !gpuFenceReadbackPending)
            {
                RenderTexture pendingFenceSource = gpuBlitSubmittedSinceSchedule && gpuFenceSourceTexture != null
                    ? gpuFenceSourceTexture
                    : gpuUnfencedBatchSourceTexture;
                if (pendingFenceSource != null && (gpuBlitSubmittedSinceSchedule || gpuUnfencedBatchCount > 0))
                {
                    gpuFenceSourceTexture = pendingFenceSource;
                    gpuBlitSubmittedSinceSchedule = false;
                    gpuUnfencedBatchSourceTexture = null;
                    gpuUnfencedBatchCount = 0;
                    gpuFenceMarkerQueued = true;
                }
            }

            if (gpuFenceReadbackPending && !gpuFenceReadbackRequest.done)
            {
                failedFenceReadbackPending = true;
            }
            if (gpuFenceMarkerQueued || gpuFenceReadbackRequestQueued
                || failedMainReadbackPending || failedFenceReadbackPending)
            {
                failedGpuResourceReleasePending = true;
            }
        }

        // 異常終了時のGPU処理とreadback完了を待って関連RTを一括解放する
        private void ReleaseFailedGpuResourcesWhenReadbacksComplete()
        {
            if (failedMainReadbackPending && huffmanReadbackRequest.done)
            {
                failedMainReadbackPending = false;
            }
            if (failedFenceReadbackPending && gpuFenceReadbackRequest.done)
            {
                failedFenceReadbackPending = false;
                gpuFenceReadbackPending = false;
            }

            CapturePendingGpuReadbacksForRelease();
            if (gpuFenceMarkerQueued && gpuFenceSourceTexture == null)
            {
                gpuFenceMarkerQueued = false;
            }

            if (gpuFenceMarkerQueued || gpuFenceReadbackRequestQueued
                || failedMainReadbackPending || failedFenceReadbackPending)
            {
                failedGpuResourceReleasePending = true;
                if (failedMainReadbackPending || failedFenceReadbackPending)
                {
                    SendCustomEventDelayedFrames(nameof(_PollFailedGpuResourceRelease), 1);
                }
                else if (gpuFenceMarkerQueued)
                {
                    SendCustomEventDelayedFrames(nameof(_RunGpuFenceMarkerBlit), 1);
                }
                else
                {
                    SendCustomEventDelayedFrames(nameof(_RunGpuFenceReadbackRequest), 1);
                }
                return;
            }

            failedGpuResourceReleasePending = false;
            CancelGpuStageDelay();
            warmupGpuReadbackPending = false;
            warmupGpuReadbackRequestQueued = false;
            sourceNormalizationReadbackRequestQueued = false;
            sourceNormalizationReadbackPending = false;
            huffmanReadbackPending = false;
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            capacityPrepassReadbackRequestQueued = false;
            capacityPrepassReadbackPending = false;
            capacityPrepassReadbackDrainFramesRemaining = 0;
            sourceAlphaScanPending = false;
            ResetCpuHuffmanState();
            ReleaseIntermediateRenderTextures();
            sourceNormalizationReadbackTexture = null;
            ReleaseOwnedInput();
            FlushTerminalNotifications();
        }

        // 失敗 GPU Resource Releaseの完了状態を確認する
        public void _PollFailedGpuResourceRelease()
        {
            if (!failedGpuResourceReleasePending)
            {
                return;
            }

            ReleaseFailedGpuResourcesWhenReadbacksComplete();
        }


        // Record 待機状態 GPU 段階 Gapを処理する
        private void RecordPendingGpuStageGap()
        {
            if (!EnableTimingDiagnostics)
            {
                return;
            }

            int step = pendingGpuStageGapStep;
            pendingGpuStageGapStep = -1;
            if (step < 0 || step >= GpuStageGapStepCount)
            {
                return;
            }

            int startedAt = ReadTimingMillis(TimingOffsetGpuStageDelayStartMs);
            if (startedAt <= 0)
            {
                return;
            }

            WriteTimingGpuStageGapMillis(step, Mathf.Max(GetNowMillis() - startedAt, 0));
        }

        // Huffman readbackの完了状態を確認する
        public void _PollHuffmanReadback()
        {
            if (!isRunning || !huffmanReadbackPending)
            {
                huffmanReadbackPending = false;
                return;
            }

            if (!huffmanReadbackRequest.done)
            {
                SendCustomEventDelayedFrames(nameof(_PollHuffmanReadback), 1);
                return;
            }

            FinishHuffmanReadback();
        }

        // Huffman readback Sequence After 描画を開始する
        public void _BeginHuffmanReadbackSequenceAfterRender()
        {
            if (!isRunning || huffmanEncodeFailed)
            {
                return;
            }

            BeginHuffmanReadbackSequence();
        }

        // 現在 Huffman readback After plane 描画を開始する
        public void _StartCurrentHuffmanReadbackAfterPlaneRender()
        {
            if (!isRunning || huffmanEncodeFailed)
            {
                return;
            }

            if (EnableTimingDiagnostics)
            {
                RecordPendingGpuStageGap();
            }
            // 直前batchは1px GPU fenceの完了後にここへ到達するため、空frame drainは不要
            huffmanReadbackDrainFramesRemaining = 0;
            QueueCurrentHuffmanReadbackRequest();
        }

        // Huffman readback Drain stepを実行する
        public void _RunHuffmanReadbackDrainStep()
        {
            if (!isRunning || huffmanEncodeFailed)
            {
                huffmanReadbackDrainFramesRemaining = 0;
                return;
            }
            if (huffmanReadbackPending || huffmanReadbackRequestQueued
                || huffmanReadbackDrainFramesRemaining <= 0)
            {
                return;
            }

            // delayed-frame eventだけで減算し、開始eventと同じframeのUpdateで待機数が減る経路を作らない
            huffmanReadbackDrainFramesRemaining--;
            float drainProgress = 1f
                - (float)huffmanReadbackDrainFramesRemaining / HuffmanReadbackDrainFrameCount;
            UpdateHuffmanReadbackProgress(drainProgress * 0.15f);
            compressionProgressStage = ProgressStageProcessing;
            if (huffmanReadbackDrainFramesRemaining > 0)
            {
                SendCustomEventDelayedFrames(nameof(_RunHuffmanReadbackDrainStep), 1);
                return;
            }

            huffmanReadbackDrainFramesRemaining = 0;
            QueueCurrentHuffmanReadbackRequest();
        }

        // 現在 plane Roundtrip 段階を実行する
        public void _RunCurrentPlaneRoundtripStage()
        {
            if (!isRunning || huffmanEncodeFailed)
            {
                return;
            }

            if (EnableTimingDiagnostics)
            {
                RecordPendingGpuStageGap();
            }
            int plane = huffmanReadbackPlane;
            if (!ShouldExportHuffmanPlane(plane))
            {
                StartNextHuffmanReadback();
                return;
            }

            if (gpuStage == 0)
            {
                if (!RunPlaneDctEncodeStage(plane))
                {
                    if (isRunning && !huffmanEncodeFailed)
                    {
                        ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 0));
                    }
                    return;
                }

                // RLEの先頭passを飛ばさないよう、prepare待ちを負値で区別する
                encodeSubStage = -1;
                gpuStage = 1;
                ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 1));
                return;
            }

            if (gpuStage == 1)
            {
                if (encodeSubStage < 0)
                {
                    if (!RunPlaneHuffmanPrepareStage(plane)) return;
                    encodeSubStage = 0;
                    ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 1));
                    return;
                }

                if (!RunPlaneHuffmanRleStage(plane))
                {
                    if (isRunning && !huffmanEncodeFailed)
                    {
                        ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 1));
                    }
                    return;
                }
                encodeSubStage = 0;
                gpuStage = 2;
                ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 2));
                return;
            }

            if (gpuStage == 2)
            {
                if (!RunPlaneHuffmanAcFrequencyStage(plane))
                {
                    if (isRunning && !huffmanEncodeFailed)
                    {
                        ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 2));
                    }
                    return;
                }

                encodeSubStage = 0;
                gpuStage = 3;
                ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 3));
                return;
            }

            if (gpuStage == 3)
            {
                if (!RunPlaneHuffmanDcFrequencyStage(plane))
                {
                    if (isRunning && !huffmanEncodeFailed)
                    {
                        ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 3));
                    }
                    return;
                }

                encodeSubStage = 0;
                gpuStage = 4;
                ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 4));
                return;
            }

            if (gpuStage == 4)
            {
                if (!RunPlaneHuffmanTableAndBitCountStage(plane))
                {
                    if (isRunning && !huffmanEncodeFailed)
                    {
                        ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 4));
                    }
                    return;
                }

                encodeSubStage = 0;
                gpuStage = 5;
                ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 5));
                return;
            }

            if (gpuStage == 5)
            {
                if (!RunPlaneHuffmanBlockAndChunkStage(plane))
                {
                    if (isRunning && !huffmanEncodeFailed)
                    {
                        ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 5));
                    }
                    return;
                }

                encodeSubStage = 0;
                gpuStage = 6;
                ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 6));
                return;
            }

            if (gpuStage == 6)
            {
                if (!RunPlaneHuffmanPayloadGatherStage(plane))
                {
                    if (isRunning && !huffmanEncodeFailed)
                    {
                        ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 6));
                    }
                    return;
                }

                encodeSubStage = 0;
                gpuStage = 0;
                huffmanReadbackPlane = plane;
                huffmanReadbackKind = HuffmanReadbackKindMetadata;
                ScheduleGpuStageDelay(nameof(_StartCurrentHuffmanReadbackAfterPlaneRender), GetGpuEncodeStageGapStep(plane, 7));
                return;
            }
        }

        // Compose Decoded 出力 After plane 描画を処理する
        public void _ComposeDecodedOutputAfterPlaneRender()
        {
            if (!isRunning || !huffmanDecodeComplete || huffmanDecodeFailed)
            {
                return;
            }

            if (EnableTimingDiagnostics)
            {
                RecordPendingGpuStageGap();
            }
            float startedAt = GetTimingStart();
            RenderTexture outputTarget = EnsurePostprocessedOutputTexture();
            SetupPackedComposeMaterial(composeRgbaMaterial, outputTarget.width, outputTarget.height);
            TrackedBlit(packedDecodedPlanes, outputTarget, composeRgbaMaterial, 4);
            if (EnableTimingDiagnostics)
            {
                AddTimingMillis(TimingOffsetComposeMs, ElapsedMs(startedAt));
            }
            if (inspectionStopStep == 21)
            {
                StopInspection("Inspection stopped after output compose.");
                return;
            }

            ScheduleGpuStageDelay(nameof(_CompleteDecodedOutputAfterCompose), GetGpuDecodeCompleteGapStep());
        }

        // Decoded 出力 After Composeを完了状態にする
        public void _CompleteDecodedOutputAfterCompose()
        {
            if (!isRunning || !huffmanDecodeComplete || huffmanDecodeFailed)
            {
                return;
            }

            if (EnableTimingDiagnostics)
            {
                RecordPendingGpuStageGap();
            }
            CompleteDecodedOutput(postprocessedOutputRenderTexture);
        }

        // Decoded 出力を完了状態にする
        private void CompleteDecodedOutput(RenderTexture source)
        {
            float startedAt = GetTimingStart();
            if (source == null)
            {
                FailHuffmanDecode("Output display skipped: output RT is missing.");
                return;
            }

            RenderTexture outputTarget = EnsurePostprocessedOutputTexture(source.width, source.height);
            if (source != outputTarget)
            {
                TrackedBlit(source, outputTarget);
            }

            if (EnableTimingDiagnostics)
            {
                AddTimingMillis(TimingOffsetCompleteOutputMs, ElapsedMs(startedAt));
            }
            if (activeRequestHandleId != InvalidHandleId && activeRequestIsExpansion)
            {
                // 最終compose先をそのまま結果として渡す。別RTへBlitして直後に元RTをReleaseすると、
                // Mobile GPUでcopy完了前にmemoryが再利用され、表示中の色が壊れる可能性がある
                expansionResultTexture = outputTarget;
                outputTexture = expansionResultTexture;
            }
            completedImageId = latestLocalImageId;
            outputReady = true;
            expandComplete = true;
            expandFailed = false;
            currentOperationIsExpansion = false;
            isRunning = false;
            if (EnableTimingDiagnostics)
            {
                int runStartMs = ReadTimingMillis(TimingOffsetRunStartMs);
                int totalMs = runStartMs > 0 ? Mathf.Max(GetNowMillis() - runStartMs, 0) : 0;
                WriteTimingMillis(TimingOffsetRunTotalMs, totalMs);
            }
            int compressedByteLength = compressedBytes != null ? compressedBytes.Length : 0;
            UpdateTimingSummary("decode", compressedByteLength);
            SetStatus("Stage 1 Y/A/Cb/Cr compressed byte[] roundtrip completed. Input " + GetInputWidth() + "x" + GetInputHeight()
                + ", padded " + GetPlaneWidth(PlaneY) + "x" + GetPlaneHeight(PlaneY)
                + ", restored " + outputTarget.width.ToString() + "x" + outputTarget.height.ToString()
                + ", compressedBytes " + compressedByteLength.ToString() + ".");
            DumpGpuDiagnostics("expansion-complete");
            // 完了fenceを通過済みなので、呼び出し側へ渡す最終RT以外をここで解放する
            ReleaseIntermediateRenderTextures();
            startedAt = GetTimingStart();
            NotifyExpansionReady();
            if (EnableTimingDiagnostics)
            {
                AddTimingMillis(TimingOffsetOutputReceiverMs, ElapsedMs(startedAt));
            }

            if (inspectionStopStep == 22)
            {
                SetStatus("Inspection stopped after complete output. compressedBytes " + compressedBytes.ToString() + ".");
                return;
            }
        }

        // 入力 Normalization readback Or Huffman Sequenceを開始する
        private void BeginSourceNormalizationReadbackOrHuffmanSequence(Texture source)
        {
            if (source == null)
            {
                BeginCapacityPrepassOrHuffmanSequence();
                return;
            }

            // これは診断用readbackではない。shader DCT前にsourceをraw RGBA32 Texture2Dへ正規化し、
            // PC/Android/iOSで異なるsampler色空間経路へ入らないようにする
            sourceNormalizationReadbackTexture = source;
            sourceNormalizationReadbackRequestQueued = true;
            sourceNormalizationReadbackPending = false;
            huffmanReadbackPending = false;
            compressionProgress01 = Mathf.Max(compressionProgress01, 0.022f);
            SendCustomEventDelayedFrames(nameof(_RequestSourceNormalizationReadback), 1);
        }

        // 入力 Normalization readbackを要求しhandle IDを返す
        public void _RequestSourceNormalizationReadback()
        {
            if (!isRunning || !sourceNormalizationReadbackRequestQueued || sourceNormalizationReadbackPending)
            {
                sourceNormalizationReadbackRequestQueued = false;
                return;
            }
            if (sourceNormalizationReadbackTexture == null)
            {
                sourceNormalizationReadbackRequestQueued = false;
                FailHuffmanEncode("Source normalization readback texture is missing.");
                return;
            }

            // Request発行frameにはBlit、upload、完了処理を重ねない
            sourceNormalizationReadbackRequestQueued = false;
            huffmanReadbackRequest = VRCAsyncGPUReadback.Request(
                sourceNormalizationReadbackTexture,
                0,
                TextureFormat.RGBA32,
                this);
            sourceNormalizationReadbackPending = true;
            compressionProgress01 = Mathf.Max(compressionProgress01, 0.025f);
            SendCustomEventDelayedFrames(nameof(_PollSourceNormalizationReadback), 1);
        }

        // 入力 Normalization readbackの完了状態を確認する
        public void _PollSourceNormalizationReadback()
        {
            if (!isRunning || !sourceNormalizationReadbackPending)
            {
                sourceNormalizationReadbackPending = false;
                return;
            }

            if (!huffmanReadbackRequest.done)
            {
                SendCustomEventDelayedFrames(nameof(_PollSourceNormalizationReadback), 1);
                return;
            }

            FinishSourceNormalizationReadback();
        }

        // 入力 Normalization readbackを完了状態にする
        private void FinishSourceNormalizationReadback()
        {
            if (!sourceNormalizationReadbackPending)
            {
                return;
            }

            sourceNormalizationReadbackPending = false;
            Texture texture = sourceNormalizationReadbackTexture;
            sourceNormalizationReadbackTexture = null;
            if (huffmanReadbackRequest.hasError)
            {
                FailHuffmanEncode("Source normalization readback failed.");
                return;
            }

            // 入力寸法が同じなら正規化readback用のstaging byte[]を再利用する
            // VRCAsyncGPUReadbackのRGBA32転送はTryGetData 1回でしか取得できないため、このcopy自体は分割不可
            // alpha scan、LoadRawTextureData、Applyは別eventへ分け、PC/Mobile共通入力を維持したまま
            // Androidの1 callbackへ正規化処理が集中しないようにする
            reusableSourceNormalizationReadbackBytes = EnsureByteArray(reusableSourceNormalizationReadbackBytes, GetReadbackByteCount(texture, TextureFormat.RGBA32));
            byte[] bytes = reusableSourceNormalizationReadbackBytes;
            if (!huffmanReadbackRequest.TryGetData(bytes, 0))
            {
                FailHuffmanEncode("Source normalization readback data could not be copied.");
                return;
            }

            sourceNormalizationBytes = bytes;
            sourceNormalizationWidth = texture != null ? Mathf.Max(texture.width, 1) : 1;
            sourceNormalizationHeight = texture != null ? Mathf.Max(texture.height, 1) : 1;
            sourceNormalizationUploadStep = 0;
            compressionProgress01 = Mathf.Max(compressionProgress01, CompressionProgressSourceReadbackEnd);
            if (activeEncodeHasAlpha && BeginSourceAlphaScanIfNeeded())
            {
                return;
            }

            ScheduleGpuStageDelay(nameof(_RunSourceNormalizationUploadStep), GetGpuEncodeStageGapStep(PlaneY, 0));
        }

        // 入力 alpha scan If Neededを開始する
        private bool BeginSourceAlphaScanIfNeeded()
        {
            if (sourceNormalizationBytes == null || sourceNormalizationWidth <= 0 || sourceNormalizationHeight <= 0)
            {
                return false;
            }

            int pixelCount = sourceNormalizationWidth * sourceNormalizationHeight;
            if (sourceNormalizationBytes.Length < pixelCount * 4)
            {
                return false;
            }

            sourceAlphaScanPending = true;
            sourceAlphaScanPixelCount = pixelCount;
            sourceAlphaScanPixelOffset = 0;
            ScheduleGpuStageDelay(nameof(_RunSourceAlphaScanStep), GetGpuEncodeStageGapStep(PlaneY, 0));
            return true;
        }

        // 入力 alpha scan stepを実行する
        public void _RunSourceAlphaScanStep()
        {
            if (!isRunning || !sourceAlphaScanPending)
            {
                ClearPendingSourceAlphaScan();
                return;
            }

            byte[] bytes = sourceNormalizationBytes;
            if (bytes == null)
            {
                ClearPendingSourceAlphaScan();
                FailHuffmanEncode("Source normalization alpha scan buffer is missing.");
                return;
            }

            int end = Mathf.Min(sourceAlphaScanPixelOffset + SourceAlphaScanPixelsPerStep, sourceAlphaScanPixelCount);
            for (int pixel = sourceAlphaScanPixelOffset; pixel < end; pixel++)
            {
                int alphaOffset = pixel * 4 + 3;
                if (alphaOffset >= bytes.Length || bytes[alphaOffset] != 255)
                {
                    // 非opaque alphaは保持が必要なので、このrunでは通常のA plane encodeを続ける
                    ClearPendingSourceAlphaScan();
                    ScheduleGpuStageDelay(nameof(_RunSourceNormalizationUploadStep), GetGpuEncodeStageGapStep(PlaneY, 0));
                    return;
                }
            }

            sourceAlphaScanPixelOffset = end;
            float scanProgress = sourceAlphaScanPixelCount > 0
                ? Mathf.Clamp01((float)sourceAlphaScanPixelOffset / sourceAlphaScanPixelCount)
                : 1f;
            compressionProgress01 = Mathf.Max(
                compressionProgress01,
                CompressionProgressSourceReadbackEnd
                    + scanProgress * (CompressionProgressSourceAlphaEnd - CompressionProgressSourceReadbackEnd));
            if (sourceAlphaScanPixelOffset < sourceAlphaScanPixelCount)
            {
                ScheduleGpuStageDelay(nameof(_RunSourceAlphaScanStep), GetGpuEncodeStageGapStep(PlaneY, 0));
                return;
            }

            // opaqueな写真/camera TextureはA plane不要。userのhasAlpha設定自体は変えず、
            // このDCTHだけalphaを省いてAndroid decodeの1 plane分を削減する
            activeEncodeHasAlpha = false;
            ClearPendingSourceAlphaScan();
            ScheduleGpuStageDelay(nameof(_RunSourceNormalizationUploadStep), GetGpuEncodeStageGapStep(PlaneY, 0));
        }

        // 入力 Normalization upload stepを実行する
        public void _RunSourceNormalizationUploadStep()
        {
            if (!isRunning)
            {
                return;
            }

            if (sourceNormalizationUploadStep <= 0)
            {
                if (!PrepareNormalizedSourceTextureData())
                {
                    ClearPendingSourceNormalizationBytes();
                    FailHuffmanEncode("Source normalization upload data is invalid.");
                    return;
                }

                sourceNormalizationUploadStep = 1;
                compressionProgress01 = Mathf.Max(
                    compressionProgress01,
                    CompressionProgressSourceAlphaEnd
                        + (CompressionProgressSourceUploadEnd - CompressionProgressSourceAlphaEnd) * 0.5f);
                ScheduleGpuStageDelay(nameof(_RunSourceNormalizationUploadStep), GetGpuEncodeStageGapStep(PlaneY, 0));
                return;
            }

            // Applyが正規化RGBA32 sourceのGPU upload点。readback byte copyやLoadRawTextureDataと
            // 別eventにし、Mobileの1 callbackへ全正規化処理を重ねない
            normalizedSourceTexture.Apply(false, false);
            activeEncodeSourceTexture = normalizedSourceTexture;
            activeEncodeSourceIsRawStorage = true;
            compressionProgress01 = Mathf.Max(compressionProgress01, CompressionProgressSourceUploadEnd);
            ClearPendingSourceNormalizationBytes();
            BeginCapacityPrepassOrHuffmanSequence();
        }

        // Normalized 入力 Texture dataを準備する
        private bool PrepareNormalizedSourceTextureData()
        {
            byte[] rgba32Bytes = sourceNormalizationBytes;
            if (rgba32Bytes == null)
            {
                return false;
            }

            int width = Mathf.Max(sourceNormalizationWidth, 1);
            int height = Mathf.Max(sourceNormalizationHeight, 1);
            if (rgba32Bytes.Length < width * height * 4)
            {
                return false;
            }

            if (normalizedSourceTexture == null
                || normalizedSourceTexture.width != width
                || normalizedSourceTexture.height != height
                || normalizedSourceTexture.format != TextureFormat.RGBA32)
            {
                if (normalizedSourceTexture != null)
                {
                    Destroy(normalizedSourceTexture);
                }

                normalizedSourceTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                normalizedSourceTexture.name = "IC_Library_NormalizedRawSource";
            }

            normalizedSourceTexture.filterMode = FilterMode.Point;
            normalizedSourceTexture.wrapMode = TextureWrapMode.Clamp;
            // LoadRawTextureDataもbuffer全体を扱うUnity API。Apply/DCT開始とは分離し、
            // Mobileで避けられないsource upload処理を1 frameにつき最大1つに抑える
            normalizedSourceTexture.LoadRawTextureData(rgba32Bytes);
            return true;
        }

        // 待機状態 入力 Normalization byte列を初期化する
        private void ClearPendingSourceNormalizationBytes()
        {
            sourceNormalizationBytes = null;
            sourceNormalizationWidth = 0;
            sourceNormalizationHeight = 0;
            sourceNormalizationUploadStep = 0;
            ClearPendingSourceAlphaScan();
        }

        // 待機状態 入力 alpha scanを初期化する
        private void ClearPendingSourceAlphaScan()
        {
            sourceAlphaScanPending = false;
            sourceAlphaScanPixelCount = 0;
            sourceAlphaScanPixelOffset = 0;
        }

        // 容量 事前計算 Or Huffman Sequenceを開始する
        private void BeginCapacityPrepassOrHuffmanSequence()
        {
            if (activeCompressionUsesCapacityLimit && !capacityPrepassCompletedForRequest)
            {
                BeginCapacityPrepass();
                return;
            }

            // retryではprepassを再実行せず、最初の本圧縮開始時刻からの累積時間を記録する
            if (activeCompressionUsesCapacityLimit && capacityFullCompressionStartedMs <= 0)
            {
                capacityFullCompressionStartedMs = GetNowMillis();
            }
            BeginHuffmanReadbackSequence();
        }

        // 容量 事前計算を開始する
        private void BeginCapacityPrepass()
        {
            // 新しい容量制限要求ごとに1回だけ実行する。retryから戻った場合は
            // capacityPrepassCompletedForRequestがtrueなので、この初期化へ入り直さない
            capacityPrepassRan = true;
            capacityPrepassRequestedQuality = quality;
            capacityPrepassSelectedQuality = quality;
            capacityPrepassEstimatedBytes = 0;
            capacityPrepassStartedMs = GetNowMillis();
            capacityPrepassPreparePlane = PlaneY;
            capacityPrepassPrepareSubStage = 0;
            capacityPrepassPrepareBlockMapReady = false;
            capacityPrepassPreparePreviousBlockMapReady = false;
            capacityPrepassColorDownsampleReady = false;
            capacityPrepassCandidateSetupStep = 0;
            capacityPrepassCandidateAttempt = 0;
            capacityPrepassYSampleBlockCount = 0;
            capacityPrepassASampleBlockCount = 0;
            capacityPrepassCbSampleBlockCount = 0;
            capacityPrepassCrSampleBlockCount = 0;
            capacityPrepassSampleBlockCount = 0;
            ReleaseCapacityPrepassResources();
            SetStatus("Estimating DCTH size: preparing samples.");
            UpdateCapacityPrepassPrepareProgress();
            ScheduleGpuStageDelay(nameof(_RunCapacityPrepassPrepareStep), GetGpuEncodeStageGapStep(PlaneY, 0));
        }

        // Update 容量 事前計算 Prepare 進捗を処理する
        private void UpdateCapacityPrepassPrepareProgress()
        {
            float planeProgress = Mathf.Clamp(capacityPrepassPreparePlane, 0, 4);
            if (capacityPrepassPreparePlane < 4)
            {
                planeProgress += (float)Mathf.Clamp(
                    capacityPrepassPrepareSubStage,
                    0,
                    CapacityPrepassPrepareSubStageCount) / CapacityPrepassPrepareSubStageCount;
            }
            float progress01 = Mathf.Clamp01(planeProgress / 4f);
            compressionProgress01 = Mathf.Max(
                compressionProgress01,
                CompressionProgressCapacityPrepareStart
                    + progress01 * (CompressionProgressCapacityPrepareEnd - CompressionProgressCapacityPrepareStart));
            compressionProgressStage = ProgressStagePreparing;
        }

        // Update 容量 事前計算 候補 進捗を処理する
        private void UpdateCapacityPrepassCandidateProgress()
        {
            float withinAttempt = 0f;
            if (capacityPrepassCandidateSetupStep < CapacityPrepassCandidateSetupStepCount)
            {
                withinAttempt = 0.05f * Mathf.Clamp01(
                    (float)capacityPrepassCandidateSetupStep / CapacityPrepassCandidateSetupStepCount);
            }
            else
            {
                int maxStatsGroups = (CapacityPrepassSampleBlockLimit + CapacityPrepassStatsSamplesPerGroup - 1)
                    / CapacityPrepassStatsSamplesPerGroup;
                int subStageCount = CapacityPrepassStatsBaseSubStage + maxStatsGroups;
                float planeProgress = Mathf.Clamp(capacityPrepassCandidatePlane, 0, 4);
                if (capacityPrepassCandidatePlane < 4)
                {
                    planeProgress += (float)Mathf.Clamp(capacityPrepassCandidateSubStage, 0, subStageCount)
                        / Mathf.Max(subStageCount, 1);
                }
                withinAttempt = 0.05f + Mathf.Clamp01(planeProgress / 4f) * 0.95f;
            }

            float combinedProgress = (
                capacityPrepassCandidateAttempt + Mathf.Clamp01(withinAttempt))
                / CapacityPrepassCandidateAttemptCount;
            compressionProgress01 = Mathf.Max(
                compressionProgress01,
                CompressionProgressCapacityPrepareEnd
                    + Mathf.Clamp01(combinedProgress)
                        * (CompressionProgressCapacityCandidateEnd - CompressionProgressCapacityPrepareEnd));
            compressionProgressStage = ProgressStagePreparing;
        }

        // 容量 事前計算 Prepare stepを実行する
        public void _RunCapacityPrepassPrepareStep()
        {
            if (!isRunning || !activeCompressionUsesCapacityLimit || capacityPrepassCompletedForRequest)
            {
                return;
            }

            UpdateCapacityPrepassPrepareProgress();
            while (capacityPrepassPreparePlane < 4 && !ShouldExportHuffmanPlane(capacityPrepassPreparePlane))
            {
                capacityPrepassPreparePlane++;
                capacityPrepassPrepareSubStage = 0;
                capacityPrepassPrepareBlockMapReady = false;
                capacityPrepassPreparePreviousBlockMapReady = false;
            }
            if (capacityPrepassPreparePlane >= 4)
            {
                BeginCapacityPrepassCandidate(capacityPrepassRequestedQuality);
                return;
            }

            int plane = capacityPrepassPreparePlane;
            int sampleCount = GetCapacityPrepassSampleCount(plane);
            if (sampleCount <= 0)
            {
                CompleteCapacityPrepassWithFallback("sample");
                return;
            }

            if (capacityPrepassPrepareSubStage == 0 && !capacityPrepassPrepareBlockMapReady)
            {
                if (!PrepareCapacityPrepassBlockMap(plane, sampleCount, false))
                {
                    CompleteCapacityPrepassWithFallback("sample-map");
                    return;
                }
                capacityPrepassPrepareBlockMapReady = true;
                ScheduleGpuStageDelay(nameof(_RunCapacityPrepassPrepareStep), GetGpuEncodeStageGapStep(plane, 0));
                return;
            }

            if (ShouldDownsampleEncodeSourceForPlane(plane) && !capacityPrepassColorDownsampleReady)
            {
                // half-size Cb/Crも本圧縮と同じdownsample結果からsampleする
                // full-size sourceを直接sampleすると色planeだけ予測条件が変わるため、ここで1回だけ生成する
                Texture source = GetCompressInputTexture();
                if (source == null || colorDownsampleMaterial == null)
                {
                    CompleteCapacityPrepassWithFallback("downsample");
                    return;
                }

                colorDownsampleTexture = EnsureRuntimeRenderTexture(
                    colorDownsampleTexture,
                    "IC_Library_ColorDownsample",
                    GetPlaneSourceWidth(plane),
                    GetPlaneSourceHeight(plane),
                    GetPlaneRenderTextureFormat());
                SetupColorDownsampleMaterial(colorDownsampleMaterial, source.width, source.height);
                TrackedBlit(source, colorDownsampleTexture, colorDownsampleMaterial, 0);
                capacityPrepassColorDownsampleReady = true;
            }

            if (capacityPrepassPrepareSubStage == 0)
            {
                AdvanceCapacityPrepassPrepareSubStage(plane);
                return;
            }

            int sampleBlockWidth = GetCapacityPrepassSampleBlockWidth(sampleCount);
            int sampleBlockHeight = GetCapacityPrepassSampleBlockHeight(sampleCount);
            int coefficientWidth = sampleBlockWidth * DctBlockSize;
            int coefficientHeight = sampleBlockHeight * DctBlockSize;
            int resourceState = EnsureNextCapacityPrepassPrepareTexture(
                plane,
                coefficientWidth,
                coefficientHeight,
                sampleBlockWidth,
                sampleBlockHeight);
            if (resourceState < 0)
            {
                CompleteCapacityPrepassWithFallback("prep-rt");
                return;
            }
            if (resourceState == 0)
            {
                ScheduleGpuStageDelay(nameof(_RunCapacityPrepassPrepareStep), GetGpuEncodeStageGapStep(plane, 0));
                return;
            }

            Texture encodeSource = GetDctHorizontalSourceForPlane(plane);
            RenderTexture dctTexture = GetCapacityPrepassDctTexture(plane);
            RenderTexture previousDcTexture = GetCapacityPrepassPreviousDcTexture(plane);
            if (encodeSource == null || dctTexture == null || previousDcTexture == null)
            {
                CompleteCapacityPrepassWithFallback("prep-src");
                return;
            }

            SetupCapacityPrepassEncodeMaterial(capacityPrepassEncodeMaterial, plane, sampleCount);
            // 選択した現在blockの未量子化DCTを保存し、全Quality候補で再利用する
            if (capacityPrepassPrepareSubStage >= 1 && capacityPrepassPrepareSubStage <= 4)
            {
                int horizontalStage = capacityPrepassPrepareSubStage - 1;
                RenderTexture horizontalTarget = (horizontalStage & 1) == 0 ? capacityPrepassPreviousDctWork : capacityPrepassWork;
                RenderTexture horizontalPrevious = horizontalStage <= 0
                    ? null
                    : ((horizontalStage & 1) == 0 ? capacityPrepassWork : capacityPrepassPreviousDctWork);
                SetupHorizontalAccumulationMaterial(capacityPrepassEncodeMaterial, horizontalPrevious, horizontalStage);
                TrackedBlit(encodeSource, horizontalTarget, capacityPrepassEncodeMaterial, CapacityPrepassEncodePassHorizontal);
                AdvanceCapacityPrepassPrepareSubStage(plane);
                return;
            }
            if (capacityPrepassPrepareSubStage == 5)
            {
                SetupCapacityPrepassEncodeMaterial(capacityPrepassVerticalMaterial, plane, sampleCount);
                TrackedBlit(capacityPrepassWork, dctTexture, capacityPrepassVerticalMaterial, CapacityPrepassEncodePassVerticalUnquantized);
                AdvanceCapacityPrepassPrepareSubStage(plane);
                return;
            }
            if (capacityPrepassPrepareSubStage == 6 && !capacityPrepassPreparePreviousBlockMapReady)
            {
                if (!PrepareCapacityPrepassBlockMap(plane, sampleCount, true))
                {
                    CompleteCapacityPrepassWithFallback("dc-map");
                    return;
                }
                capacityPrepassPreparePreviousBlockMapReady = true;
                ScheduleGpuStageDelay(nameof(_RunCapacityPrepassPrepareStep), GetGpuEncodeStageGapStep(plane, 0));
                return;
            }
            if (capacityPrepassPrepareSubStage == 6)
            {
                AdvanceCapacityPrepassPrepareSubStage(plane);
                return;
            }
            SetupCapacityPrepassEncodeMaterial(capacityPrepassEncodeMaterial, plane, sampleCount);
            // DC差分はraster-order直前blockを参照するため、同じsample数のpredecessor mapを別途DCTする
            // 画像全blockのDCをCPUへ戻さず、必要な128 block以下だけをGPUで処理する
            if (capacityPrepassPrepareSubStage >= 7 && capacityPrepassPrepareSubStage <= 10)
            {
                int horizontalStage = capacityPrepassPrepareSubStage - 7;
                RenderTexture horizontalTarget = (horizontalStage & 1) == 0 ? capacityPrepassPreviousDctWork : capacityPrepassWork;
                RenderTexture horizontalPrevious = horizontalStage <= 0
                    ? null
                    : ((horizontalStage & 1) == 0 ? capacityPrepassWork : capacityPrepassPreviousDctWork);
                SetupHorizontalAccumulationMaterial(capacityPrepassEncodeMaterial, horizontalPrevious, horizontalStage);
                TrackedBlit(encodeSource, horizontalTarget, capacityPrepassEncodeMaterial, CapacityPrepassEncodePassHorizontal);
                AdvanceCapacityPrepassPrepareSubStage(plane);
                return;
            }
            if (capacityPrepassPrepareSubStage == 11)
            {
                SetupCapacityPrepassEncodeMaterial(capacityPrepassVerticalMaterial, plane, sampleCount);
                TrackedBlit(capacityPrepassWork, capacityPrepassPreviousDctWork, capacityPrepassVerticalMaterial, CapacityPrepassEncodePassVerticalUnquantized);
                AdvanceCapacityPrepassPrepareSubStage(plane);
                return;
            }
            SetupCapacityPrepassEncodeMaterial(capacityPrepassPostMaterial, plane, sampleCount);
            TrackedBlit(capacityPrepassPreviousDctWork, previousDcTexture, capacityPrepassPostMaterial, CapacityPrepassEncodePassPreviousDcUnquantized);
            SetCapacityPrepassSampleCount(plane, sampleCount);

            capacityPrepassPreparePlane++;
            capacityPrepassPrepareSubStage = 0;
            capacityPrepassPrepareBlockMapReady = false;
            capacityPrepassPreparePreviousBlockMapReady = false;
            ScheduleGpuStageDelay(nameof(_RunCapacityPrepassPrepareStep), GetGpuEncodeStageGapStep(plane, 0));
        }

        // 容量 事前計算 Prepare Sub 段階を次へ進める
        private void AdvanceCapacityPrepassPrepareSubStage(int plane)
        {
            capacityPrepassPrepareSubStage++;
            ScheduleGpuStageDelay(nameof(_RunCapacityPrepassPrepareStep), GetGpuEncodeStageGapStep(plane, 0));
        }

        // 容量 事前計算 候補を開始する
        private void BeginCapacityPrepassCandidate(int candidateQuality)
        {
            capacityPrepassCandidateQuality = Mathf.Clamp(candidateQuality, MinQuality, capacityPrepassRequestedQuality);
            capacityPrepassCandidatePlane = PlaneY;
            capacityPrepassCandidateSubStage = 0;
            capacityPrepassCandidateSetupStep = 0;
            SetStatus(
                "Estimating DCTH size at quality " + capacityPrepassCandidateQuality.ToString()
                + " (" + (capacityPrepassCandidateAttempt + 1).ToString()
                + "/" + CapacityPrepassCandidateAttemptCount.ToString() + ").");
            UpdateCapacityPrepassCandidateProgress();
            ScheduleGpuStageDelay(nameof(_RunCapacityPrepassCandidateSetupStep), GetGpuEncodeStageGapStep(PlaneY, 0));
        }

        // 容量 事前計算 候補 Setup stepを実行する
        public void _RunCapacityPrepassCandidateSetupStep()
        {
            if (!isRunning || !activeCompressionUsesCapacityLimit || capacityPrepassCompletedForRequest)
            {
                return;
            }

            UpdateCapacityPrepassCandidateProgress();
            if (capacityPrepassCandidateSetupStep == 0)
            {
                capacityPrepassStats = EnsureRuntimeRenderTexture(
                    capacityPrepassStats,
                    "IC_Library_CapacityPrepassStats",
                    CapacityPrepassStatsTextureWidth,
                    CapacityPrepassStatsTextureHeight,
                    RenderTextureFormat.ARGB32);
                if (capacityPrepassStats == null)
                {
                    CompleteCapacityPrepassWithFallback("stats-rt");
                    return;
                }
            }
            else if (capacityPrepassCandidateSetupStep == 1)
            {
                capacityPrepassStatsTemp = EnsureRuntimeRenderTexture(
                    capacityPrepassStatsTemp,
                    "IC_Library_CapacityPrepassStatsTemp",
                    CapacityPrepassStatsTextureWidth,
                    CapacityPrepassStatsTextureHeight,
                    RenderTextureFormat.ARGB32);
                if (capacityPrepassStatsTemp == null)
                {
                    CompleteCapacityPrepassWithFallback("stats-temp");
                    return;
                }
            }
            else if (capacityPrepassCandidateSetupStep == 2)
            {
                TrackedBlit(EnsureTransparentTexture(), capacityPrepassStats);
            }
            else if (capacityPrepassCandidateSetupStep == 3)
            {
                TrackedBlit(EnsureTransparentTexture(), capacityPrepassStatsTemp);
            }

            capacityPrepassCandidateSetupStep++;
            if (capacityPrepassCandidateSetupStep < CapacityPrepassCandidateSetupStepCount)
            {
                ScheduleGpuStageDelay(nameof(_RunCapacityPrepassCandidateSetupStep), GetGpuEncodeStageGapStep(PlaneY, 0));
                return;
            }
            ScheduleGpuStageDelay(nameof(_RunCapacityPrepassCandidateStep), GetGpuEncodeStageGapStep(PlaneY, 0));
        }

        // 容量 事前計算 候補 stepを実行する
        public void _RunCapacityPrepassCandidateStep()
        {
            if (!isRunning || !activeCompressionUsesCapacityLimit || capacityPrepassCompletedForRequest)
            {
                return;
            }

            UpdateCapacityPrepassCandidateProgress();
            while (capacityPrepassCandidatePlane < 4 && !ShouldExportHuffmanPlane(capacityPrepassCandidatePlane))
            {
                capacityPrepassCandidatePlane++;
                capacityPrepassCandidateSubStage = 0;
            }
            if (capacityPrepassCandidatePlane >= 4)
            {
                _BeginCapacityPrepassStatsReadbackDrain();
                return;
            }

            int plane = capacityPrepassCandidatePlane;
            int sampleCount = GetStoredCapacityPrepassSampleCount(plane);
            int sampleBlockWidth = GetCapacityPrepassSampleBlockWidth(sampleCount);
            int sampleBlockHeight = GetCapacityPrepassSampleBlockHeight(sampleCount);
            int coefficientWidth = sampleBlockWidth * DctBlockSize;
            int coefficientHeight = sampleBlockHeight * DctBlockSize;
            RenderTexture unquantizedDct = GetCapacityPrepassDctTexture(plane);
            RenderTexture previousDc = GetCapacityPrepassPreviousDcTexture(plane);
            if (sampleCount <= 0 || unquantizedDct == null || previousDc == null)
            {
                CompleteCapacityPrepassWithFallback("candidate-src");
                return;
            }

            int resourceState = EnsureNextCapacityPrepassCandidateTexture(
                coefficientWidth,
                coefficientHeight,
                sampleBlockWidth,
                sampleBlockHeight);
            if (resourceState < 0)
            {
                CompleteCapacityPrepassWithFallback("candidate-rt");
                return;
            }
            if (resourceState == 0)
            {
                ScheduleGpuStageDelay(nameof(_RunCapacityPrepassCandidateStep), GetGpuEncodeStageGapStep(plane, 5));
                return;
            }
            quantTexture = EnsureRuntimeQuantTexture(capacityPrepassCandidateQuality, quantPreset);
            SetupCapacityPrepassEncodeMaterial(capacityPrepassPostMaterial, plane, sampleCount);
            capacityPrepassPostMaterial.SetTexture("_SamplePreviousDcTex", previousDc);
            // ここから下はQuality依存部分。未量子化DCTを再利用し、候補ごとに量子化以降だけを実行する
            if (capacityPrepassCandidateSubStage == 0)
            {
                TrackedBlit(unquantizedDct, capacityPrepassQuantizedCoefficients, capacityPrepassPostMaterial, CapacityPrepassEncodePassQuantize);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            // 本圧縮と同じRLE symbolを作り、sample内のAC symbol頻度を集計する
            SetupCapacityPrepassCoeffToRleMaterial(coeffToRleSymbolsMaterial, coefficientWidth, coefficientHeight);
            if (capacityPrepassCandidateSubStage == 1)
            {
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", EnsureNeutralGrayTexture());
                TrackedBlit(capacityPrepassQuantizedCoefficients, capacityPrepassWork, coeffToRleSymbolsMaterial, CoeffToRlePassBuildPrefixBase);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            if (capacityPrepassCandidateSubStage >= 2
                && capacityPrepassCandidateSubStage <= CoeffToRlePrefixScanPassCount + 1)
            {
                int scanIndex = capacityPrepassCandidateSubStage - 2;
                RenderTexture previousPrefix = (scanIndex & 1) == 0 ? capacityPrepassWork : capacityPrepassRleSymbols;
                RenderTexture targetPrefix = (scanIndex & 1) == 0 ? capacityPrepassRleSymbols : capacityPrepassWork;
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", previousPrefix);
                coeffToRleSymbolsMaterial.SetFloat("_PrefixStep", 1 << scanIndex);
                TrackedBlit(previousPrefix, targetPrefix, coeffToRleSymbolsMaterial, CoeffToRlePassScanPrefix);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            if (capacityPrepassCandidateSubStage == CoeffToRleStepCount)
            {
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", capacityPrepassWork);
                TrackedBlit(capacityPrepassQuantizedCoefficients, capacityPrepassRleSymbols, coeffToRleSymbolsMaterial, CoeffToRlePassBuildSymbols);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            int capacityAcFrequencyStep = capacityPrepassCandidateSubStage - CapacityPrepassAcFrequencyBaseSubStage;
            if (capacityAcFrequencyStep >= 0 && capacityAcFrequencyStep < EncodeAcFrequencySlotGroupCount)
            {
                int slotStart = 1 + capacityAcFrequencyStep * EncodeAcFrequencySlotsPerGroup;
                int slotCount = Mathf.Min(EncodeAcFrequencySlotsPerGroup, 64 - slotStart);
                RenderTexture previousRows = capacityAcFrequencyStep <= 0
                    ? null
                    : ((capacityAcFrequencyStep & 1) == 0 ? rleAcFrequencyRowsTemp : capacityPrepassAcFrequencyRows);
                RenderTexture targetRows = (capacityAcFrequencyStep & 1) == 0
                    ? capacityPrepassAcFrequencyRows
                    : rleAcFrequencyRowsTemp;
                SetupCapacityPrepassAcFrequencyRowGroupMaterial(
                    rleAcFrequencyMaterial,
                    coefficientWidth,
                    coefficientHeight,
                    sampleBlockWidth,
                    sampleBlockHeight,
                    previousRows,
                    slotStart,
                    slotCount,
                    capacityAcFrequencyStep > 0);
                TrackedBlit(capacityPrepassRleSymbols, targetRows, rleAcFrequencyMaterial, 0);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }
            if (capacityPrepassCandidateSubStage == CapacityPrepassAcFrequencyTotalSubStage)
            {
                RenderTexture completedRows = ((EncodeAcFrequencySlotGroupCount - 1) & 1) == 0
                    ? capacityPrepassAcFrequencyRows
                    : rleAcFrequencyRowsTemp;
                SetupCapacityPrepassAcFrequencyMaterial(rleAcFrequencyMaterial, coefficientWidth, coefficientHeight, sampleBlockWidth, sampleBlockHeight);
                TrackedBlit(completedRows, capacityPrepassAcFrequency, rleAcFrequencyMaterial, 1);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            // 現在blockと直前blockを同じQuant tableで量子化し、本圧縮と同じDC delta categoryを作る
            if (capacityPrepassCandidateSubStage == CapacityPrepassDcDeltaSubStage)
            {
                TrackedBlit(unquantizedDct, capacityPrepassDcDelta, capacityPrepassPostMaterial, CapacityPrepassEncodePassDcDeltaQuantize);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }
            SetupCapacityPrepassDcFrequencyMaterial(dcFrequencyMaterial, sampleBlockWidth, sampleBlockHeight);
            if (capacityPrepassCandidateSubStage == CapacityPrepassDcFrequencyRowsSubStage)
            {
                TrackedBlit(capacityPrepassDcDelta, capacityPrepassDcFrequencyRows, dcFrequencyMaterial, 0);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }
            if (capacityPrepassCandidateSubStage == CapacityPrepassDcFrequencyTotalSubStage)
            {
                TrackedBlit(capacityPrepassDcFrequencyRows, capacityPrepassDcFrequency, dcFrequencyMaterial, 1);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            // sample頻度からHuffman code lengthを構築する。canonical code値は容量計算に不要なので生成しない
            SetupHuffmanTableMaterial(huffmanTableMaterial, DcFrequencyBinCount);
            if (capacityPrepassCandidateSubStage == CapacityPrepassDcRawSubStage)
            {
                EnsureHuffmanLengthHistogramTextures();
                // 16 symbolのDCもACと同じCPU経路へ通し、Mobileで旧tree shaderを初回実行する経路を残さない
                int cpuState = RunCpuHuffmanRawLengthStep(capacityPrepassDcFrequency, DcFrequencyBinCount);
                if (cpuState < 0)
                {
                    FailHuffmanEncode("Capacity prepass DC Huffman tree readback or build failed.");
                    return;
                }

                if (cpuState > 0)
                {
                    capacityPrepassCandidateSubStage = CapacityPrepassDcRawHistogramBaseSubStage;
                }
                ScheduleGpuStageDelay(nameof(_RunCapacityPrepassCandidateStep), GetGpuEncodeStageGapStep(plane, 5));
                return;
            }
            SetupHuffmanLengthLimitMaterial(huffmanTableMaterial, capacityPrepassDcFrequency, DcFrequencyBinCount);
            if (capacityPrepassCandidateSubStage >= CapacityPrepassDcRawHistogramBaseSubStage
                && capacityPrepassCandidateSubStage < CapacityPrepassDcLimitedHistogramBaseSubStage)
            {
                int group = capacityPrepassCandidateSubStage - CapacityPrepassDcRawHistogramBaseSubStage;
                int lengthStart = group * HuffmanRawHistogramLengthsPerGroup;
                RenderTexture previous = group <= 0 ? null : ((group & 1) == 0 ? huffmanRawLengthHistogram : huffmanRawLengthHistogramTemp);
                RenderTexture target = (group & 1) == 0 ? huffmanRawLengthHistogramTemp : huffmanRawLengthHistogram;
                SetupHuffmanRawLengthHistogramRangeMaterial(
                    huffmanTableMaterial,
                    capacityPrepassDcFrequency,
                    DcFrequencyBinCount,
                    previous,
                    lengthStart,
                    group > 0);
                TrackedBlit(huffmanRawLengths, target, huffmanTableMaterial, HuffmanTablePassBuildRawLengthHistogram);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }
            if (capacityPrepassCandidateSubStage >= CapacityPrepassDcLimitedHistogramBaseSubStage
                && capacityPrepassCandidateSubStage < CapacityPrepassDcAssignLengthsSubStage)
            {
                int lengthIndex = capacityPrepassCandidateSubStage - CapacityPrepassDcLimitedHistogramBaseSubStage;
                SetupHuffmanSingleLimitedHistogramMaterial(huffmanTableMaterial, capacityPrepassDcFrequency, DcFrequencyBinCount, lengthIndex);
                TrackedBlit(huffmanRawLengthHistogram, huffmanRawLengthSingle, huffmanTableMaterial, HuffmanTablePassLimitLengthHistogram);
                RenderTexture previous = lengthIndex <= 0 ? null : ((lengthIndex & 1) == 0 ? huffmanLimitedLengthHistogram : huffmanLimitedLengthHistogramTemp);
                RenderTexture target = (lengthIndex & 1) == 0 ? huffmanLimitedLengthHistogramTemp : huffmanLimitedLengthHistogram;
                SetupHuffmanSingleValueMergeMaterial(huffmanTableMaterial, previous, HuffmanLengthLimitCount, lengthIndex, lengthIndex > 0);
                TrackedBlit(huffmanRawLengthSingle, target, huffmanTableMaterial, HuffmanTablePassMergeSingleValue);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }
            if (capacityPrepassCandidateSubStage == CapacityPrepassDcAssignLengthsSubStage)
            {
                TrackedBlit(huffmanRawLengths, capacityPrepassDcHuffmanLengths, huffmanTableMaterial, HuffmanTablePassLimitLengths);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            if (capacityPrepassCandidateSubStage >= CapacityPrepassAcRawBaseSubStage
                && capacityPrepassCandidateSubStage < CapacityPrepassAcRawHistogramBaseSubStage)
            {
                // ACの旧GPU passは256 fragmentがそれぞれ最大256 nodeを再走査するため、容量推定でも共通CPU treeを使う
                int cpuState = RunCpuHuffmanRawLengthStep(capacityPrepassAcFrequency, AcFrequencyBinCount);
                if (cpuState < 0)
                {
                    FailHuffmanEncode("Capacity prepass AC Huffman tree readback or build failed.");
                    return;
                }

                capacityPrepassCandidateSubStage = cpuState > 0
                    ? CapacityPrepassAcRawHistogramBaseSubStage
                    : GetCpuHuffmanProgressSubStage(
                        CapacityPrepassAcRawBaseSubStage,
                        CapacityPrepassAcRawHistogramBaseSubStage);
                ScheduleGpuStageDelay(nameof(_RunCapacityPrepassCandidateStep), GetGpuEncodeStageGapStep(plane, 5));
                return;
            }

            if (capacityPrepassCandidateSubStage >= CapacityPrepassAcRawHistogramBaseSubStage
                && capacityPrepassCandidateSubStage < CapacityPrepassAcLimitedHistogramBaseSubStage)
            {
                int group = capacityPrepassCandidateSubStage - CapacityPrepassAcRawHistogramBaseSubStage;
                int lengthStart = group * HuffmanRawHistogramLengthsPerGroup;
                RenderTexture previous = group <= 0 ? null : ((group & 1) == 0 ? huffmanRawLengthHistogram : huffmanRawLengthHistogramTemp);
                RenderTexture target = (group & 1) == 0 ? huffmanRawLengthHistogramTemp : huffmanRawLengthHistogram;
                SetupHuffmanRawLengthHistogramRangeMaterial(
                    huffmanTableMaterial,
                    capacityPrepassAcFrequency,
                    AcFrequencyBinCount,
                    previous,
                    lengthStart,
                    group > 0);
                TrackedBlit(huffmanRawLengths, target, huffmanTableMaterial, HuffmanTablePassBuildRawLengthHistogram);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            if (capacityPrepassCandidateSubStage >= CapacityPrepassAcLimitedHistogramBaseSubStage
                && capacityPrepassCandidateSubStage < CapacityPrepassAcLimitBaseSubStage)
            {
                int lengthIndex = capacityPrepassCandidateSubStage - CapacityPrepassAcLimitedHistogramBaseSubStage;
                SetupHuffmanSingleLimitedHistogramMaterial(huffmanTableMaterial, capacityPrepassAcFrequency, AcFrequencyBinCount, lengthIndex);
                TrackedBlit(huffmanRawLengthHistogram, huffmanRawLengthSingle, huffmanTableMaterial, HuffmanTablePassLimitLengthHistogram);
                RenderTexture previous = lengthIndex <= 0 ? null : ((lengthIndex & 1) == 0 ? huffmanLimitedLengthHistogram : huffmanLimitedLengthHistogramTemp);
                RenderTexture target = (lengthIndex & 1) == 0 ? huffmanLimitedLengthHistogramTemp : huffmanLimitedLengthHistogram;
                SetupHuffmanSingleValueMergeMaterial(huffmanTableMaterial, previous, HuffmanLengthLimitCount, lengthIndex, lengthIndex > 0);
                TrackedBlit(huffmanRawLengthSingle, target, huffmanTableMaterial, HuffmanTablePassMergeSingleValue);
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            if (capacityPrepassCandidateSubStage >= CapacityPrepassAcLimitBaseSubStage
                && capacityPrepassCandidateSubStage < CapacityPrepassBitCountBaseSubStage)
            {
                int group = capacityPrepassCandidateSubStage - CapacityPrepassAcLimitBaseSubStage;
                int symbolStart = group * HuffmanTableAcSymbolsPerGroup;
                RenderTexture previous = group <= 0 ? null : ((group & 1) == 0 ? capacityPrepassAcHuffmanLengths : acHuffmanCodes);
                RenderTexture target = (group & 1) == 0 ? acHuffmanCodes : capacityPrepassAcHuffmanLengths;
                SetupHuffmanLengthLimitMaterial(huffmanTableMaterial, capacityPrepassAcFrequency, AcFrequencyBinCount);
                SetupHuffmanTableRangeMaterial(huffmanTableMaterial, previous, symbolStart, HuffmanTableAcSymbolsPerGroup, group > 0);
                TrackedBlit(huffmanRawLengths, target, huffmanTableMaterial, HuffmanTablePassLimitLengths);
                if (group + 1 >= HuffmanTableAcSymbolGroupCount)
                {
                    huffmanRawLengthHistogram = ReleaseRuntimeRenderTexture(huffmanRawLengthHistogram);
                    huffmanRawLengthHistogramTemp = ReleaseRuntimeRenderTexture(huffmanRawLengthHistogramTemp);
                    huffmanLimitedLengthHistogram = ReleaseRuntimeRenderTexture(huffmanLimitedLengthHistogram);
                    huffmanLimitedLengthHistogramTemp = ReleaseRuntimeRenderTexture(huffmanLimitedLengthHistogramTemp);
                }
                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            SetupCapacityPrepassHuffmanBitCountMaterial(
                huffmanBitCountMaterial,
                coefficientWidth,
                coefficientHeight,
                sampleBlockWidth,
                sampleBlockHeight);
            int capacityBitCountStep = capacityPrepassCandidateSubStage - CapacityPrepassBitCountBaseSubStage;
            if (capacityBitCountStep >= 0 && capacityBitCountStep < HuffmanBitCountStepCount)
            {
                if (capacityBitCountStep == 0)
                {
                    TrackedBlit(capacityPrepassRleSymbols, capacityPrepassBlockBits, huffmanBitCountMaterial, HuffmanBitCountPassInitialize);
                }
                else
                {
                    int slotGroup = capacityBitCountStep - 1;
                    RenderTexture previousBitCount = IsDecodeSlotGroupStepSourceMain(slotGroup) ? capacityPrepassBlockBits : huffmanBlockPageState;
                    RenderTexture targetBitCount = IsDecodeSlotGroupStepSourceMain(slotGroup) ? huffmanBlockPageState : capacityPrepassBlockBits;
                    huffmanBitCountMaterial.SetTexture("_PreviousBitCountTex", previousBitCount);
                    huffmanBitCountMaterial.SetFloat("_BitCountSlotGroup", slotGroup);
                    TrackedBlit(capacityPrepassRleSymbols, targetBitCount, huffmanBitCountMaterial, HuffmanBitCountPassAccumulate);
                }

                AdvanceCapacityPrepassCandidateSubStage(plane);
                return;
            }

            int capacityStatsGroupCount = Mathf.Max((sampleCount + CapacityPrepassStatsSamplesPerGroup - 1) / CapacityPrepassStatsSamplesPerGroup, 1);
            int capacityStatsGroup = capacityPrepassCandidateSubStage - CapacityPrepassStatsBaseSubStage;
            if (capacityStatsGroup >= 0 && capacityStatsGroup < capacityStatsGroupCount)
            {
                int sampleStart = capacityStatsGroup * CapacityPrepassStatsSamplesPerGroup;
                // planeごとのtotal bitsと最大block bytesを4x1 RGBA32へ集約する
                // CPUへreadbackするのは全plane合計16 bytesだけで、大きな係数TextureはGPU上に残す
                SetupCapacityPrepassStatsMaterial(
                    huffmanChunkValidBytesMaterial,
                    capacityPrepassStats,
                    plane,
                    sampleCount,
                    sampleStart,
                    sampleBlockWidth,
                    sampleBlockHeight);
                TrackedBlit(capacityPrepassBlockBits, capacityPrepassStatsTemp, huffmanChunkValidBytesMaterial, CapacityPrepassStatsPass);
                RenderTexture previousStats = capacityPrepassStats;
                capacityPrepassStats = capacityPrepassStatsTemp;
                capacityPrepassStatsTemp = previousStats;

                if (capacityStatsGroup + 1 < capacityStatsGroupCount)
                {
                    AdvanceCapacityPrepassCandidateSubStage(plane);
                    return;
                }

                capacityPrepassCandidatePlane++;
                capacityPrepassCandidateSubStage = 0;
                // 全planeのGPU queueを最後のreadbackで一括待機せず、各planeの小さいstatsを途中でreadbackして
                // 次のplaneへ進む前にGPU queueを区切る。判定に使うのは最後の累積statsなのでpayloadは変わらない
                while (capacityPrepassCandidatePlane < 4 && !ShouldExportHuffmanPlane(capacityPrepassCandidatePlane))
                {
                    capacityPrepassCandidatePlane++;
                }
                _BeginCapacityPrepassStatsReadbackDrain();
                return;
            }

            CompleteCapacityPrepassWithFallback("stage");
        }

        // 容量 事前計算 候補 Sub 段階を次へ進める
        private void AdvanceCapacityPrepassCandidateSubStage(int plane)
        {
            capacityPrepassCandidateSubStage++;
            ScheduleGpuStageDelay(nameof(_RunCapacityPrepassCandidateStep), GetGpuEncodeStageGapStep(plane, 5));
        }

        // 容量 事前計算 Stats readbackを要求しhandle IDを返す
        private void RequestCapacityPrepassStatsReadback()
        {
            if (capacityPrepassStats == null || capacityPrepassReadbackPending)
            {
                if (capacityPrepassStats == null)
                {
                    CompleteCapacityPrepassWithFallback("rb-rt");
                }
                return;
            }

            // Quality候補1つにつき16 bytesだけを非同期readbackする。prepassのDCT Texture自体はreadbackしない
            huffmanReadbackRequest = VRCAsyncGPUReadback.Request(capacityPrepassStats, 0, TextureFormat.RGBA32, this);
            capacityPrepassReadbackPending = true;
            UpdateCapacityPrepassCandidateProgress();
            compressionProgressStage = ProgressStageReadback;
            SendCustomEventDelayedFrames(nameof(_PollCapacityPrepassStatsReadback), 1);
        }

        // 容量 事前計算 Stats readback Drainを開始する
        public void _BeginCapacityPrepassStatsReadbackDrain()
        {
            if (capacityPrepassReadbackPending || capacityPrepassReadbackRequestQueued
                || capacityPrepassReadbackDrainFramesRemaining > 0)
            {
                return;
            }

            if ((gpuBlitSubmittedSinceSchedule && gpuFenceSourceTexture != null)
                || (gpuUnfencedBatchCount > 0 && gpuUnfencedBatchSourceTexture != null))
            {
                int completedPlane = Mathf.Clamp(capacityPrepassCandidatePlane - 1, PlaneY, PlaneCr);
                ScheduleGpuStageDelay(
                    nameof(_BeginCapacityPrepassStatsReadbackDrain),
                    GetGpuEncodeStageGapStep(completedPlane, 7));
                return;
            }

            // statsを生成したbatchは1px GPU fence完了済み。Requestだけを次frameで発行する
            capacityPrepassReadbackDrainFramesRemaining = 0;
            capacityPrepassReadbackBytes = EnsureByteArray(
                capacityPrepassReadbackBytes,
                CapacityPrepassStatsTextureWidth * CapacityPrepassStatsTextureHeight * 4);
            capacityPrepassReadbackRequestQueued = true;
            compressionProgressStage = ProgressStageReadback;
            SendCustomEventDelayedFrames(nameof(_RunCapacityPrepassStatsReadbackRequest), 1);
        }

        // 容量 事前計算 Stats readback Drain stepを実行する
        public void _RunCapacityPrepassStatsReadbackDrainStep()
        {
            if (!isRunning || !activeCompressionUsesCapacityLimit || capacityPrepassCompletedForRequest)
            {
                capacityPrepassReadbackDrainFramesRemaining = 0;
                return;
            }

            capacityPrepassReadbackDrainFramesRemaining--;
            if (capacityPrepassReadbackDrainFramesRemaining > 0)
            {
                SendCustomEventDelayedFrames(nameof(_RunCapacityPrepassStatsReadbackDrainStep), 1);
                return;
            }

            capacityPrepassReadbackDrainFramesRemaining = 0;
            if (capacityPrepassReadbackPending || capacityPrepassReadbackRequestQueued)
            {
                return;
            }

            // byte[]確保はRequest発行の1 frame前に済ませる
            capacityPrepassReadbackBytes = EnsureByteArray(
                capacityPrepassReadbackBytes,
                CapacityPrepassStatsTextureWidth * CapacityPrepassStatsTextureHeight * 4);
            capacityPrepassReadbackRequestQueued = true;
            SendCustomEventDelayedFrames(nameof(_RunCapacityPrepassStatsReadbackRequest), 1);
        }

        // 容量 事前計算 Stats readback requestを実行する
        public void _RunCapacityPrepassStatsReadbackRequest()
        {
            if (!isRunning || !activeCompressionUsesCapacityLimit || capacityPrepassCompletedForRequest
                || !capacityPrepassReadbackRequestQueued || capacityPrepassReadbackPending)
            {
                capacityPrepassReadbackRequestQueued = false;
                return;
            }

            // このeventではRequestだけを発行し、done確認は次frame以降のpollへ分離する
            capacityPrepassReadbackRequestQueued = false;
            RequestCapacityPrepassStatsReadback();
        }

        // 容量 事前計算 Stats readbackの完了状態を確認する
        public void _PollCapacityPrepassStatsReadback()
        {
            if (!isRunning || !capacityPrepassReadbackPending)
            {
                capacityPrepassReadbackPending = false;
                return;
            }
            if (!huffmanReadbackRequest.done)
            {
                SendCustomEventDelayedFrames(nameof(_PollCapacityPrepassStatsReadback), 1);
                return;
            }

            capacityPrepassReadbackPending = false;
            if (huffmanReadbackRequest.hasError
                || !huffmanReadbackRequest.TryGetData(capacityPrepassReadbackBytes, 0))
            {
                CompleteCapacityPrepassWithFallback(
                    huffmanReadbackRequest.hasError ? "rb-error" : "rb-data");
                return;
            }

            // 途中checkpointでは、readbackが完了してから次のplaneのGPU処理を開始する
            // 最終planeの累積statsを取得した時だけ従来の容量判定へ進む
            if (capacityPrepassCandidatePlane < 4)
            {
                compressionProgressStage = ProgressStageProcessing;
                ScheduleGpuStageDelay(
                    nameof(_RunCapacityPrepassCandidateStep),
                    GetGpuEncodeStageGapStep(capacityPrepassCandidatePlane, 5));
                return;
            }
            FinishCapacityPrepassCandidate();
        }

        // 容量 事前計算 候補を完了状態にする
        private void FinishCapacityPrepassCandidate()
        {
            // 97%内部目標とsample内64-byte上限を両方満たす候補だけをfitとする
            int estimatedBytes = GetCapacityPrepassEstimatedTotalBytes(capacityPrepassReadbackBytes);
            bool blockCapacityFits = CapacityPrepassSampleBlocksFitPage(capacityPrepassReadbackBytes);
            bool totalCapacityFits = estimatedBytes <= GetTotalBytePredictionTarget(maxImageBytes);
            bool fits = blockCapacityFits && totalCapacityFits;
            int candidateQuality = capacityPrepassCandidateQuality;

            if (fits)
            {
                CompleteCapacityPrepass(candidateQuality, estimatedBytes);
                return;
            }

            // sampleの超過率を既存の量子化強度予測へ渡す。予測したQualityも同じsampleで
            // 再評価してから本圧縮へ進み、非線形な量子化/Huffman容量による大幅な外れを抑える
            int selectedQuality = PredictCapacityPrepassQuality(
                candidateQuality,
                estimatedBytes,
                capacityPrepassReadbackBytes);
            if (selectedQuality < candidateQuality
                && capacityPrepassCandidateAttempt + 1 < CapacityPrepassCandidateAttemptCount)
            {
                capacityPrepassCandidateAttempt++;
                BeginCapacityPrepassCandidate(selectedQuality);
                return;
            }
            CompleteCapacityPrepass(selectedQuality, 0);
        }

        // Predict 容量 事前計算 画質を処理する
        private int PredictCapacityPrepassQuality(int currentQuality, int estimatedBytes, byte[] statsBytes)
        {
            int predictedQuality = currentQuality;
            int totalTarget = GetTotalBytePredictionTarget(maxImageBytes);
            if (estimatedBytes > totalTarget)
            {
                SetQualityRetryPredictionInput(
                    QualityRetryReasonTotalCapacity,
                    estimatedBytes,
                    totalTarget,
                    -1);
                predictedQuality = Mathf.Min(
                    predictedQuality,
                    PredictLowerQualityFromByteTarget(currentQuality));
            }

            if (statsBytes != null && statsBytes.Length >= CapacityPrepassStatsTextureWidth * 4)
            {
                for (int plane = 0; plane < 4; plane++)
                {
                    int blockBytes = statsBytes[plane * 4 + 3];
                    if (!ShouldExportHuffmanPlane(plane) || blockBytes <= HuffmanBlockPageBytes)
                    {
                        continue;
                    }

                    SetQualityRetryPredictionInput(
                        QualityRetryReasonBlockCapacity,
                        blockBytes,
                        HuffmanBlockPageBytes,
                        plane);
                    predictedQuality = Mathf.Min(
                        predictedQuality,
                        PredictLowerQualityFromByteTarget(currentQuality));
                }
            }

            ResetQualityRetryPredictionInput();
            if (predictedQuality >= currentQuality)
            {
                int step = Mathf.Clamp(retryQualityStep, 1, MaxQuality - MinQuality);
                predictedQuality = Mathf.Max(MinQuality, currentQuality - step);
            }
            return Mathf.Clamp(predictedQuality, MinQuality, currentQuality);
        }

        // 容量 事前計算を完了状態にする
        private void CompleteCapacityPrepass(int selectedQuality, int estimatedBytes)
        {
            int now = GetNowMillis();
            capacityPrepassCompletedForRequest = true;
            capacityPrepassSelectedQuality = Mathf.Clamp(selectedQuality, MinQuality, capacityPrepassRequestedQuality);
            capacityPrepassEstimatedBytes = Mathf.Max(estimatedBytes, 0);
            capacityPrepassDurationMs = Mathf.Max(now - capacityPrepassStartedMs, 0);
            quality = capacityPrepassSelectedQuality;
            quantTexture = EnsureRuntimeQuantTexture(quality, quantPreset);
            ReleaseCapacityPrepassResources();
            compressionProgress01 = Mathf.Max(compressionProgress01, CompressionProgressFullEncodeStart);
            compressionProgressStage = ProgressStageProcessing;
            SetStatus(string.IsNullOrEmpty(capacityPrepassFallbackReason)
                ? "DCTH size estimate completed. Compressing at quality " + quality.ToString() + "."
                : "DCTH size estimate unavailable (" + capacityPrepassFallbackReason
                    + "). Compressing at quality " + quality.ToString() + ".");
            capacityFullCompressionStartedMs = GetNowMillis();
            BeginHuffmanReadbackSequence();
        }

        // 容量 事前計算 With Fallbackを完了状態にする
        private void CompleteCapacityPrepassWithFallback(string reason)
        {
            // prepass失敗はDCTH生成失敗にはしない。Qualityを1段階下げて本圧縮へ進み、
            // 64-byte block上限と最大byte数は既存の実測retryで最終保証する
            capacityPrepassFallbackReason = string.IsNullOrEmpty(reason) ? "unknown" : reason;
            int before = Mathf.Clamp(capacityPrepassRequestedQuality, MinQuality, MaxQuality);
            int selectedQuality = before;
            if (autoRetryLowerQuality
                && before > MinQuality
                && qualityAutoRetryCount < maxQualityRetryCount)
            {
                int step = Mathf.Clamp(retryQualityStep, 1, MaxQuality - MinQuality);
                selectedQuality = Mathf.Max(MinQuality, before - step);
                qualityWasExplicitlyChangedByRetry = true;
                qualityWasExplicitlyLoweredByRetry = true;
                qualityBeforeAutoRetry = before;
                qualityAfterAutoRetry = selectedQuality;
                qualityAutoRetryCount++;
                lastQualityRetryRequiredBytes = 0;
                lastQualityRetryTargetBytes = maxImageBytes;
                lastQualityRetryPlane = -1;
                lastCompressionRetryReason = "DCTH size estimation failed: " + capacityPrepassFallbackReason + ".";
                SetStatus("DCTH size estimation failed (" + capacityPrepassFallbackReason
                    + "). Retrying full compression at quality "
                    + before.ToString() + " -> " + selectedQuality.ToString() + ".");
                NotifyCompressionRetrying();
            }
            CompleteCapacityPrepass(selectedQuality, 0);
        }

        // 容量 事前計算 ブロック Mapを準備する
        private bool PrepareCapacityPrepassBlockMap(int plane, int sampleCount, bool mapPreviousDcBlock)
        {
            int blockWidth = GetBlockWidth(plane);
            int blockHeight = GetBlockHeight(plane);
            int totalBlockCount = blockWidth * blockHeight;
            if (sampleCount <= 0 || totalBlockCount <= 0)
            {
                return false;
            }

            if (capacityPrepassBlockMapTexture == null
                || capacityPrepassBlockMapTexture.width != CapacityPrepassSampleBlockLimit
                || capacityPrepassBlockMapTexture.height != 1)
            {
                capacityPrepassBlockMapTexture = ReleaseRuntimeTexture(capacityPrepassBlockMapTexture);
                capacityPrepassBlockMapTexture = new Texture2D(CapacityPrepassSampleBlockLimit, 1, TextureFormat.RGBA32, false, true);
                capacityPrepassBlockMapTexture.name = "IC_Library_CapacityPrepassBlockMap";
                capacityPrepassBlockMapTexture.filterMode = FilterMode.Point;
                capacityPrepassBlockMapTexture.wrapMode = TextureWrapMode.Clamp;
            }
            if (capacityPrepassBlockMapPixels == null || capacityPrepassBlockMapPixels.Length != CapacityPrepassSampleBlockLimit)
            {
                capacityPrepassBlockMapPixels = new Color32[CapacityPrepassSampleBlockLimit];
            }

            Color32[] pixels = capacityPrepassBlockMapPixels;
            for (int sampleIndex = 0; sampleIndex < CapacityPrepassSampleBlockLimit; sampleIndex++)
            {
                pixels[sampleIndex] = new Color32(0, 0, 0, 0);
            }

            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                int sourceIndex = sampleIndex;
                if (totalBlockCount > CapacityPrepassSampleBlockLimit)
                {
                    // 全blockを128個の重ならない区間へ分け、区間内offsetだけを複数周期の整数patternでずらす
                    // 単純格子と規則模様が一致するのを避けつつ、乱数状態を持たないためPC/Mobileで選択が一致する
                    int rangeStart = sampleIndex * totalBlockCount / sampleCount;
                    int rangeEnd = (sampleIndex + 1) * totalBlockCount / sampleCount;
                    int rangeLength = Mathf.Max(rangeEnd - rangeStart, 1);
                    int deterministicOffset = sampleIndex * 73
                        + sampleIndex * sampleIndex * 19
                        + (sampleIndex & 3) * 101
                        + (sampleIndex % 7) * 31
                        + plane * 47;
                    sourceIndex = rangeStart + deterministicOffset % rangeLength;
                }

                if (mapPreviousDcBlock && sourceIndex <= 0)
                {
                    // 先頭blockのDC predictorは0。存在しないblockをsampleしないようsentinelを格納し、
                    // DC抽出passで0へ戻す。通常座標は最大1023なので0xffffとは衝突しない
                    pixels[sampleIndex] = new Color32(255, 255, 255, 255);
                    continue;
                }
                if (mapPreviousDcBlock)
                {
                    sourceIndex--;
                }

                int sourceBlockX = sourceIndex - sourceIndex / blockWidth * blockWidth;
                int sourceBlockY = sourceIndex / blockWidth;
                pixels[sampleIndex] = new Color32(
                    (byte)(sourceBlockX & 255),
                    (byte)((sourceBlockX >> 8) & 255),
                    (byte)(sourceBlockY & 255),
                    (byte)((sourceBlockY >> 8) & 255));
            }

            capacityPrepassBlockMapTexture.SetPixels32(pixels);
            capacityPrepassBlockMapTexture.Apply(false, false);
            return true;
        }

        // 容量 事前計算 Estimated Total byte列を返す
        private int GetCapacityPrepassEstimatedTotalBytes(byte[] statsBytes)
        {
            long totalBytes = CompressedByteHeaderBytes;
            for (int plane = 0; plane < 4; plane++)
            {
                if (!ShouldExportHuffmanPlane(plane))
                {
                    continue;
                }

                int sampleCount = GetStoredCapacityPrepassSampleCount(plane);
                int sampleBits = DecodeUInt24(statsBytes, plane * 4);
                int chunkCount = GetChunkWidth(plane) * GetChunkHeight(plane);
                long scaledChunkBits = (long)sampleBits * (long)(ChunkSize * ChunkSize);
                long sampleBitDenominator = (long)Mathf.Max(sampleCount, 1);
                long bytesPerChunk = (scaledChunkBits + sampleBitDenominator * 8L - 1L) / (sampleBitDenominator * 8L);

                // payloadだけをsample blockの平均bit数から4x4 chunkへ構造的に拡張する
                // header、Huffman table、chunk metadataは寸法で確定するため近似係数を使わず正確に足す
                totalBytes += bytesPerChunk * (long)chunkCount;
                totalBytes += GetHuffmanMetadataChunkValidBytes(plane);
                totalBytes += DcFrequencyBinCount * 4;
                totalBytes += AcFrequencyBinCount * 4;
                // chunk-offset segmentは現行DCTHでは空で、decode時にchunkValidから再構築するため0 byte
            }

            return totalBytes > 2147483647L ? 2147483647 : (int)totalBytes;
        }

        // 容量 事前計算 Sample ブロック Fit pageを処理する
        private bool CapacityPrepassSampleBlocksFitPage(byte[] statsBytes)
        {
            if (statsBytes == null || statsBytes.Length < CapacityPrepassStatsTextureWidth * 4)
            {
                return false;
            }
            for (int plane = 0; plane < 4; plane++)
            {
                if (ShouldExportHuffmanPlane(plane) && statsBytes[plane * 4 + 3] > HuffmanBlockPageBytes)
                {
                    return false;
                }
            }

            return true;
        }

        // 容量 事前計算 Sample 数を返す
        private int GetCapacityPrepassSampleCount(int plane)
        {
            int totalBlocks = GetBlockWidth(plane) * GetBlockHeight(plane);
            return Mathf.Min(Mathf.Max(totalBlocks, 0), CapacityPrepassSampleBlockLimit);
        }

        // 容量 事前計算 Sample ブロック 幅を返す
        private int GetCapacityPrepassSampleBlockWidth(int sampleCount)
        {
            // 128 blockは16x8、それ未満はNx1にして未使用blockを作らない
            // frequency/Huffman推定へpadding blockが混ざらず、小画像では存在するblockを重複なく全て使える
            return sampleCount >= CapacityPrepassSampleBlockLimit ? 16 : Mathf.Max(sampleCount, 1);
        }

        // 容量 事前計算 Sample ブロック 高さを返す
        private int GetCapacityPrepassSampleBlockHeight(int sampleCount)
        {
            return sampleCount >= CapacityPrepassSampleBlockLimit ? 8 : 1;
        }

        // 容量 事前計算 Sample 数を設定する
        private void SetCapacityPrepassSampleCount(int plane, int sampleCount)
        {
            if (plane == PlaneY) capacityPrepassYSampleBlockCount = sampleCount;
            else if (plane == PlaneA) capacityPrepassASampleBlockCount = sampleCount;
            else if (plane == PlaneCb) capacityPrepassCbSampleBlockCount = sampleCount;
            else capacityPrepassCrSampleBlockCount = sampleCount;
            capacityPrepassSampleBlockCount = capacityPrepassYSampleBlockCount
                + capacityPrepassASampleBlockCount
                + capacityPrepassCbSampleBlockCount
                + capacityPrepassCrSampleBlockCount;
        }

        // Stored 容量 事前計算 Sample 数を返す
        private int GetStoredCapacityPrepassSampleCount(int plane)
        {
            if (plane == PlaneY) return capacityPrepassYSampleBlockCount;
            if (plane == PlaneA) return capacityPrepassASampleBlockCount;
            if (plane == PlaneCb) return capacityPrepassCbSampleBlockCount;
            return capacityPrepassCrSampleBlockCount;
        }

        // 待機状態 payload Storeを初期化する
        private void ClearPendingPayloadStore()
        {
            pendingPayloadReadbackBytes = null;
            pendingPayloadStoreBytes = null;
            pendingPayloadStorePlane = 0;
            pendingPayloadStoreLength = 0;
            pendingPayloadStoreOffset = 0;
            pendingPayloadStoreCompletedStep = 0;
            pendingPayloadStoreReadbackWaitMs = 0;
            pendingPayloadStoreElapsedMs = 0f;
        }

        // 待機状態 chunk offset Buildを初期化する
        private void ClearPendingChunkOffsetBuild()
        {
            pendingChunkOffsetBuild = false;
            pendingChunkOffsetPlane = 0;
            pendingChunkOffsetOrderIndex = 0;
            pendingChunkOffsetCumulative = 0;
            pendingChunkOffsetWidth = 0;
            pendingChunkOffsetHeight = 0;
            pendingChunkOffsetChunkValidBytes = null;
            pendingChunkOffsetBytes = null;
        }

        // 待機状態 復号 payload upload Buildを初期化する
        private void ClearPendingDecodePayloadUploadBuild()
        {
            pendingDecodePayloadUploadBuild = false;
            pendingDecodePayloadUploadPlane = 0;
            pendingDecodePayloadUploadWidth = 0;
            pendingDecodePayloadUploadHeight = 0;
            pendingDecodePayloadUploadPixelCount = 0;
            pendingDecodePayloadUploadPayloadLength = 0;
            pendingDecodePayloadUploadOffset = 0;
            pendingDecodePayloadSourceBytes = null;
            pendingDecodePayloadUploadBytes = null;
        }

        // 待機状態 byte Copy 状態を初期化する
        private void ClearPendingByteCopyState()
        {
            // frame分割中は大きなDCTH/payload bufferを参照し得る
            // Stop/reset時に参照を切り、Questで古い配列が次回GCまで残らないようにする
            restoreSegmentIndex = 0;
            restoreDataOffset = 0;
            restoreSegmentSourceOffset = 0;
            restoreSegmentCopyOffset = 0;
            restoreSegmentLength = 0;
            restoreSegmentBytes = null;
            restoreElapsedMs = 0f;
            packSegmentIndex = 0;
            packPrepareSegmentIndex = 0;
            packDataOffset = 0;
            packTotalBytes = 0;
            packSegmentCopyOffset = 0;
            packSegmentLength = 0;
            packSegmentBytes = null;
            packTotalStepCount = 0;
            packCompletedStepCount = 0;
            packElapsedMs = 0f;
            huffmanReadbackTotalWeightBytes = 0;
            huffmanReadbackCompletedWeightBytes = 0;
            ClearPendingPayloadStore();
            ClearPendingChunkOffsetBuild();
            ClearPendingDecodePayloadUploadBuild();
        }

        // Huffman readback Sequenceを開始する
        private void BeginHuffmanReadbackSequence()
        {
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            huffmanReadbackStep = 0;
            huffmanReadbackCompletedWeightBytes = 0;
            huffmanReadbackRegionWidth = 0;
            huffmanReadbackRegionHeight = 0;
            huffmanReadbackPayloadLength = -1;
            huffmanReadbackTotalWeightBytes = GetHuffmanReadbackPlaneWeightBytes(PlaneY)
                + GetHuffmanReadbackPlaneWeightBytes(PlaneA)
                + GetHuffmanReadbackPlaneWeightBytes(PlaneCb)
                + GetHuffmanReadbackPlaneWeightBytes(PlaneCr);
            StartNextHuffmanReadback();
        }

        // Update Huffman readback 進捗を処理する
        private void UpdateHuffmanReadbackProgress(float currentStepProgress)
        {
            if (huffmanReadbackTotalWeightBytes > 0
                && huffmanReadbackStep < HuffmanReadbackStepCount)
            {
                int planeIndex = huffmanReadbackStep / HuffmanReadbackKindsPerPlane;
                int plane = GetHuffmanExportPlaneForReadbackIndex(planeIndex);
                int kind = GetHuffmanReadbackKindForStep(huffmanReadbackStep);
                float localPlaneProgress = kind == HuffmanReadbackKindMetadata
                    ? 0.25f + Mathf.Clamp01(currentStepProgress) * 0.1f
                    : 0.35f + Mathf.Clamp01(currentStepProgress) * 0.65f;
                compressionProgress01 = Mathf.Max(
                    compressionProgress01,
                    GetCompressionPlaneSequenceProgress01(plane, localPlaneProgress));
                return;
            }

            int currentStepWeightBytes = huffmanReadbackStep < HuffmanReadbackStepCount
                ? GetHuffmanReadbackStepWeightBytes(huffmanReadbackStep)
                : 0;
            float completedWeightBytes = huffmanReadbackCompletedWeightBytes
                + currentStepWeightBytes * Mathf.Clamp01(currentStepProgress);
            float readbackProgress = huffmanReadbackTotalWeightBytes > 0
                ? Mathf.Clamp01(completedWeightBytes / huffmanReadbackTotalWeightBytes)
                : 1f;
            float mappedProgress = CompressionProgressReadbackStart
                + readbackProgress * (CompressionProgressReadbackEnd - CompressionProgressReadbackStart);
            compressionProgress01 = Mathf.Max(compressionProgress01, mappedProgress);
        }

        // 圧縮 plane Sequence Progress01を返す
        private float GetCompressionPlaneSequenceProgress01(int plane, float localPlaneProgress)
        {
            int priorWeightBytes = 0;
            if (plane >= PlaneA) priorWeightBytes += GetHuffmanReadbackPlaneWeightBytes(PlaneY);
            if (plane >= PlaneCb) priorWeightBytes += GetHuffmanReadbackPlaneWeightBytes(PlaneA);
            if (plane >= PlaneCr) priorWeightBytes += GetHuffmanReadbackPlaneWeightBytes(PlaneCb);
            int planeWeightBytes = GetHuffmanReadbackPlaneWeightBytes(plane);
            float sequenceProgress = huffmanReadbackTotalWeightBytes > 0
                ? (priorWeightBytes + planeWeightBytes * Mathf.Clamp01(localPlaneProgress))
                    / huffmanReadbackTotalWeightBytes
                : 0f;
            return CompressionProgressFullEncodeStart
                + Mathf.Clamp01(sequenceProgress)
                    * (CompressionProgressReadbackEnd - CompressionProgressFullEncodeStart);
        }

        // Huffman readback plane Weight byte列を返す
        private int GetHuffmanReadbackPlaneWeightBytes(int plane)
        {
            if (!ShouldExportHuffmanPlane(plane))
            {
                return 0;
            }

            int metadataBytes = GetHuffmanMetadataWidth() * GetHuffmanMetadataHeight(plane) * 4;
            int payloadBytes = GetHuffmanPayloadWidth(plane) * GetHuffmanPayloadHeight(plane);
            return Mathf.Max(metadataBytes + payloadBytes, 1);
        }

        // Huffman readback step Weight byte列を返す
        private int GetHuffmanReadbackStepWeightBytes(int step)
        {
            int planeIndex = step / HuffmanReadbackKindsPerPlane;
            int plane = GetHuffmanExportPlaneForReadbackIndex(planeIndex);
            if (!ShouldExportHuffmanPlane(plane))
            {
                return 0;
            }

            int kind = GetHuffmanReadbackKindForStep(step);
            if (kind == HuffmanReadbackKindMetadata)
            {
                return Mathf.Max(GetHuffmanMetadataWidth() * GetHuffmanMetadataHeight(plane) * 4, 1);
            }
            if (kind == HuffmanReadbackKindPayload)
            {
                int width = GetHuffmanPayloadWidth(plane);
                int height = GetHuffmanPayloadReadbackHeight(plane, huffmanReadbackPayloadLength);
                return Mathf.Max(width * height, 1);
            }

            return 0;
        }

        // Huffman payload readback 高さを返す
        private int GetHuffmanPayloadReadbackHeight(int plane, int payloadLength)
        {
            int width = GetHuffmanPayloadWidth(plane);
            int maximumHeight = GetHuffmanPayloadHeight(plane);
            int clampedLength = Mathf.Clamp(payloadLength, 1, GetHuffmanPayloadCapacity(plane));
            return Mathf.Clamp((clampedLength + width - 1) / width, 1, maximumHeight);
        }

        // 現在 Huffman readback 進捗 stepを完了状態にする
        private void CompleteCurrentHuffmanReadbackProgressStep()
        {
            huffmanReadbackCompletedWeightBytes += GetHuffmanReadbackStepWeightBytes(huffmanReadbackStep);
            huffmanReadbackStep++;
        }

        // 次 Huffman readbackを開始する
        private void StartNextHuffmanReadback()
        {
            while (huffmanReadbackStep < HuffmanReadbackStepCount)
            {
                int planeIndex = huffmanReadbackStep / HuffmanReadbackKindsPerPlane;
                int plane = GetHuffmanExportPlaneForReadbackIndex(planeIndex);
                int kind = GetHuffmanReadbackKindForStep(huffmanReadbackStep);
                if (!ShouldExportHuffmanPlane(plane))
                {
                    huffmanReadbackCompletedWeightBytes += GetHuffmanReadbackPlaneWeightBytes(plane);
                    huffmanReadbackStep += HuffmanReadbackKindsPerPlane - GetHuffmanReadbackSlotForStep(huffmanReadbackStep);
                    UpdateHuffmanReadbackProgress(0f);
                    continue;
                }

                if (!ShouldReadbackHuffmanKind(kind))
                {
                    CompleteCurrentHuffmanReadbackProgressStep();
                    UpdateHuffmanReadbackProgress(0f);
                    continue;
                }

                UpdateHuffmanReadbackProgress(0f);

                if (kind == HuffmanReadbackKindMetadata)
                {
                    huffmanReadbackPlane = plane;
                    huffmanReadbackKind = kind;
                    gpuStage = 0;
                    ScheduleGpuStageDelay(nameof(_RunCurrentPlaneRoundtripStage), GetGpuEncodeStageGapStep(plane, 0));
                    return;
                }

                QueueCurrentHuffmanReadbackRequest();
                return;
            }

            huffmanEncodeComplete = true;
            UpdateHuffmanReadbackProgress(0f);
            BeginBuildCompressedBytesFromPlaneBytes();
        }

        // 圧縮 To byte列を完了状態にする
        private void CompleteCompressionToBytes()
        {
            CancelGpuStageDelay();
            gpuFenceMarkerTexture = ReleaseRuntimeRenderTexture(gpuFenceMarkerTexture);
            huffmanReadbackPending = false;
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            isRunning = false;
            outputReady = false;
            huffmanDecodeComplete = false;
            huffmanDecodeFailed = false;
            ClearHuffmanBytes();
            if (EnableTimingDiagnostics)
            {
                int runStartMs = ReadTimingMillis(TimingOffsetRunStartMs);
                int totalMs = runStartMs > 0 ? Mathf.Max(GetNowMillis() - runStartMs, 0) : 0;
                WriteTimingMillis(TimingOffsetRunTotalMs, totalMs);
            }
            int bytes = compressedBytes != null ? compressedBytes.Length : 0;
            compressedByteCount = bytes;
            if (activeCompressionUsesCapacityLimit)
            {
                int now = GetNowMillis();
                capacityPrepassActualBytes = bytes;
                capacityFullCompressionDurationMs = capacityFullCompressionStartedMs > 0
                    ? Mathf.Max(now - capacityFullCompressionStartedMs, 0)
                    : 0f;
                capacityTotalDurationMs = capacityTotalStartedMs > 0
                    ? Mathf.Max(now - capacityTotalStartedMs, 0)
                    : capacityPrepassDurationMs + capacityFullCompressionDurationMs;
            }
            compressionPending = false;
            compressionComplete = true;
            compressionFailed = false;
            currentOperationIsExpansion = false;
            UpdateTimingSummary("compress", bytes);
            SetStatus("Compression to byte[] completed. bytes=" + bytes.ToString() + ".");
            DumpGpuDiagnostics("compression-complete");
            // 全readback完了後なので、byte[]以外の中間RTをここで一括解放する
            ReleaseIntermediateRenderTextures();
            NotifyCompressedBytesReady();
        }

        // 現在 Huffman readbackを開始する
        private void StartCurrentHuffmanReadback()
        {
            if (huffmanReadbackPending)
            {
                return;
            }
            if (huffmanReadbackStep >= HuffmanReadbackStepCount)
            {
                StartNextHuffmanReadback();
                return;
            }

            int planeIndex = huffmanReadbackStep / HuffmanReadbackKindsPerPlane;
            int plane = GetHuffmanExportPlaneForReadbackIndex(planeIndex);
            int kind = GetHuffmanReadbackKindForStep(huffmanReadbackStep);
            if (!ShouldExportHuffmanPlane(plane))
            {
                StartNextHuffmanReadback();
                return;
            }

            if (!ShouldReadbackHuffmanKind(kind))
            {
                CompleteCurrentHuffmanReadbackProgressStep();
                StartNextHuffmanReadback();
                return;
            }

            RenderTexture texture = GetHuffmanReadbackTexture(plane, kind);
            if (texture == null)
            {
                FailHuffmanEncode("Huffman byte[] export failed: readback texture is missing.");
                return;
            }

            huffmanReadbackPlane = plane;
            huffmanReadbackKind = kind;
            int mipLevel = 0;
            TextureFormat readbackFormat = GetHuffmanReadbackTextureFormat(kind);
            huffmanReadbackRegionWidth = Mathf.Max(texture.width, 1);
            huffmanReadbackRegionHeight = Mathf.Max(texture.height, 1);
            if (kind == HuffmanReadbackKindPayload)
            {
                if (huffmanReadbackPayloadLength < 0)
                {
                    FailHuffmanEncode("Huffman byte[] export failed: payload length is unavailable before readback.");
                    return;
                }
                huffmanReadbackRegionHeight = GetHuffmanPayloadReadbackHeight(
                    plane,
                    huffmanReadbackPayloadLength);
            }
            if (EnableTimingDiagnostics)
            {
                WriteTimingMillis(TimingOffsetReadbackStartMs, GetNowMillis());
            }
            if (EnableTimingDiagnostics)
            {
                LogTiming("readback request plane=" + GetPlaneName(plane)
                    + " kind=" + GetHuffmanReadbackKindName(kind)
                    + " size=" + texture.width.ToString() + "x" + texture.height.ToString()
                    + " mip=" + mipLevel.ToString());
            }
            if (kind == HuffmanReadbackKindPayload)
            {
                // payload RTはblock/page最大容量で確保されるが、metadata取得後は実payload長が確定している
                // 有効rowだけをreadbackし、AndroidでRequest時に発生する転送・driver同期負荷を抑える
                huffmanReadbackRequest = VRCAsyncGPUReadback.Request(
                    texture,
                    mipLevel,
                    0,
                    huffmanReadbackRegionWidth,
                    0,
                    huffmanReadbackRegionHeight,
                    0,
                    1,
                    readbackFormat,
                    this);
            }
            else
            {
                huffmanReadbackRequest = VRCAsyncGPUReadback.Request(texture, mipLevel, readbackFormat, this);
            }
            huffmanReadbackPending = true;
            UpdateHuffmanReadbackProgress(0.15f);
            compressionProgressStage = ProgressStageReadback;
            SendCustomEventDelayedFrames(nameof(_PollHuffmanReadback), 1);
        }

        // 現在 Huffman readback requestをqueueへ追加する
        private void QueueCurrentHuffmanReadbackRequest()
        {
            if (!isRunning || huffmanEncodeFailed || huffmanReadbackPending || huffmanReadbackRequestQueued)
            {
                return;
            }

            huffmanReadbackRequestQueued = true;
            // capacity prepassの値が残っていても、最初のY metadataまで到達した時点の
            // 実工程を必ず反映する。UI上の33%表示とreadback工程が矛盾する状態を残さない
            UpdateHuffmanReadbackProgress(0f);
            // Request自体がdriver待機で長引いた場合も、直前の描画frameにreadback対象を表示しておく
            compressionProgressStage = ProgressStageReadback;
            SendCustomEventDelayedFrames(nameof(_RunCurrentHuffmanReadbackRequest), 1);
        }

        // 現在 Huffman readback requestを実行する
        public void _RunCurrentHuffmanReadbackRequest()
        {
            if (!isRunning || huffmanEncodeFailed || !huffmanReadbackRequestQueued || huffmanReadbackPending)
            {
                huffmanReadbackRequestQueued = false;
                return;
            }

            // TryGetDataや次passとは別frameでRequestだけを発行する
            huffmanReadbackRequestQueued = false;
            StartCurrentHuffmanReadback();
        }

        // Huffman readbackを完了状態にする
        private void FinishHuffmanReadback()
        {
            if (!huffmanReadbackPending)
            {
                return;
            }

            huffmanReadbackPending = false;
            int completedStep = huffmanReadbackStep;
            int readbackWaitMs = 0;
            if (EnableTimingDiagnostics)
            {
                int readbackStartedAt = ReadTimingMillis(TimingOffsetReadbackStartMs);
                if (readbackStartedAt > 0)
                {
                    readbackWaitMs = Mathf.Max(GetNowMillis() - readbackStartedAt, 0);
                    AddTimingMillis(TimingOffsetReadbackTotalMs, readbackWaitMs);
                    WriteTimingReadbackStepMillis(TimingOffsetReadbackWaitSteps, completedStep, readbackWaitMs);
                    if (huffmanReadbackKind == HuffmanReadbackKindMetadata)
                    {
                        AddTimingMillis(TimingOffsetBlitWaitTotalMs, readbackWaitMs);
                        WriteTimingReadbackStepMillis(TimingOffsetBlitWaitSteps, completedStep, readbackWaitMs);
                    }
                }
            }

            if (huffmanReadbackRequest.hasError)
            {
                FailHuffmanEncode("Huffman byte[] export failed: GPU readback hasError.");
                return;
            }

            bool timingEnabled = EnableTimingDiagnostics;
            float storeStartedAt = timingEnabled ? Time.realtimeSinceStartup : 0f;
            int width = Mathf.Max(huffmanReadbackRegionWidth, 1);
            int height = Mathf.Max(huffmanReadbackRegionHeight, 1);
            int pixelCount = width * height;
            if (huffmanReadbackKind == HuffmanReadbackKindPayload)
            {
                // 領域readbackはrow境界まで含むため、有効DCTH segmentより末尾が少し大きい場合がある
                // 一時bufferは再利用し、後段で有効長だけを専用byte[]へ分割copyする
                reusablePayloadReadbackBytes = EnsureByteArray(reusablePayloadReadbackBytes, pixelCount);
                byte[] payloadBytes = reusablePayloadReadbackBytes;
                if (!huffmanReadbackRequest.TryGetData(payloadBytes, 0))
                {
                    FailHuffmanEncode("Huffman byte[] export failed: TryGetData failed for R8 payload.");
                    return;
                }

                UpdateHuffmanReadbackProgress(0.55f);
                StoreHuffmanReadbackPayloadBytes(huffmanReadbackPlane, payloadBytes, completedStep, readbackWaitMs);
                return;
            }

            if (huffmanReadbackKind == HuffmanReadbackKindMetadata)
            {
                // metadataは直後にexact-lengthのchunk/table配列へcopyするため、
                // RGBA readback staging bufferをplane間で再利用してもpack済みDCTHには影響しない
                reusableMetadataReadbackBytes = EnsureByteArray(reusableMetadataReadbackBytes, pixelCount * 4);
                byte[] rgba = reusableMetadataReadbackBytes;
                if (!huffmanReadbackRequest.TryGetData(rgba, 0))
                {
                    FailHuffmanEncode("Huffman byte[] export failed: TryGetData failed for RGBA metadata.");
                    return;
                }

                StoreHuffmanReadbackMetadataBytes(huffmanReadbackPlane, rgba);
                int storeMs = timingEnabled ? Mathf.RoundToInt(ElapsedMs(storeStartedAt)) : 0;
                if (timingEnabled)
                {
                    AddTimingMillis(TimingOffsetReadbackStoreMs, storeMs);
                    WriteTimingReadbackStepMillis(TimingOffsetReadbackStoreSteps, completedStep, storeMs);
                    LogTiming("readback complete plane=" + GetPlaneName(huffmanReadbackPlane)
                        + " kind=" + GetHuffmanReadbackKindName(huffmanReadbackKind)
                        + " bytes=" + rgba.Length.ToString()
                        + " waitMs=" + readbackWaitMs.ToString()
                        + " waitTotalMs=" + ReadTimingMillis(TimingOffsetReadbackTotalMs).ToString()
                        + " storeMs=" + storeMs.ToString()
                        + " storeTotalMs=" + ReadTimingMillis(TimingOffsetReadbackStoreMs).ToString());
                }
                if (huffmanEncodeFailed)
                {
                    return;
                }

                UpdateHuffmanReadbackProgress(1f);

                if (inspectionStopStep == 11)
                {
                    StopInspection("Inspection stopped after metadata readback. plane=" + GetPlaneName(huffmanReadbackPlane)
                        + " bytes=" + rgba.Length.ToString() + ".");
                    return;
                }

                CompleteCurrentHuffmanReadbackProgressStep();
                StartNextHuffmanReadback();
                return;
            }

            FailHuffmanEncode("Huffman byte[] export failed: unexpected readback kind "
                + GetHuffmanReadbackKindName(huffmanReadbackKind) + ".");
        }

        // Huffman Export plane用readback indexを返す
        private int GetHuffmanExportPlaneForReadbackIndex(int planeIndex)
        {
            return Mathf.Clamp(planeIndex, 0, 3);
        }

        // Huffman readback slot用stepを返す
        private int GetHuffmanReadbackSlotForStep(int step)
        {
            return Mathf.Clamp(step - (step / HuffmanReadbackKindsPerPlane) * HuffmanReadbackKindsPerPlane, 0, HuffmanReadbackKindsPerPlane - 1);
        }

        // Huffman readback種別用stepを返す
        private int GetHuffmanReadbackKindForStep(int step)
        {
            return GetHuffmanReadbackSlotForStep(step) == 0 ? HuffmanReadbackKindMetadata : HuffmanReadbackKindPayload;
        }

        // Store Huffman readback metadata byte列を処理する
        private void StoreHuffmanReadbackMetadataBytes(int plane, byte[] rgba)
        {
            int metadataWidth = GetHuffmanMetadataWidth();
            int metadataHeight = GetHuffmanMetadataHeight(plane);
            int metadataBytes = metadataWidth * metadataHeight * 4;
            if (rgba == null || rgba.Length < metadataBytes)
            {
                FailHuffmanEncode("Huffman byte[] export failed: metadata readback is too small"
                    + " plane=" + plane.ToString()
                    + " bytes=" + (rgba != null ? rgba.Length : 0).ToString()
                    + " required=" + metadataBytes.ToString() + ".");
                return;
            }

            int chunkValidBytes = GetHuffmanMetadataChunkValidBytes(plane);
            byte[] chunkValid = CopyByteRange(rgba, GetHuffmanMetadataChunkOffset(), chunkValidBytes);
            byte[] dcCodes = CopyByteRange(rgba, 4, DcFrequencyBinCount * 4);
            byte[] acCodes = CopyByteRange(rgba, metadataWidth * 4, AcFrequencyBinCount * 4);
            ValidateHuffmanTableMetadata(plane, dcCodes, acCodes);
            if (huffmanEncodeFailed)
            {
                return;
            }

            ValidateHuffmanBlockOverflowCapacity(plane, rgba);
            if (huffmanEncodeFailed)
            {
                return;
            }

            SetChunkValidBytesForPlane(plane, chunkValid);
            huffmanReadbackPayloadLength = GetHuffmanPayloadTotalBytesForPlane(plane);
            int maximumPayloadReadbackBytes = GetHuffmanPayloadWidth(plane) * GetHuffmanPayloadHeight(plane);
            int actualPayloadReadbackBytes = GetHuffmanPayloadWidth(plane)
                * GetHuffmanPayloadReadbackHeight(plane, huffmanReadbackPayloadLength);
            huffmanReadbackTotalWeightBytes = Mathf.Max(
                huffmanReadbackTotalWeightBytes - maximumPayloadReadbackBytes + actualPayloadReadbackBytes,
                1);
            // offsetはdecode時にchunkValidから再構築するため、encode結果には保持しない
            SetChunkOffsetBytesForPlane(plane, GetEmptyBytes());
            SetDcHuffmanBytesForPlane(plane, dcCodes);
            SetAcHuffmanBytesForPlane(plane, acCodes);
        }

        // Store Huffman readback payload byte列を処理する
        private void StoreHuffmanReadbackPayloadBytes(int plane, byte[] payloadBytes, int completedStep, int readbackWaitMs)
        {
            int payloadLength = Mathf.Max(huffmanReadbackPayloadLength, 0);
            int payloadCapacity = GetHuffmanPayloadCapacity(plane);
            if (payloadLength > payloadCapacity || payloadLength > payloadBytes.Length)
            {
                FailHuffmanEncode("Huffman byte[] export failed: payload " + payloadLength.ToString()
                    + " bytes exceeds capacity " + payloadCapacity.ToString()
                    + " bytes for plane " + plane.ToString() + ".");
                return;
            }

            pendingPayloadStorePlane = plane;
            pendingPayloadStoreLength = payloadLength;
            pendingPayloadStoreOffset = 0;
            pendingPayloadStoreCompletedStep = completedStep;
            pendingPayloadStoreReadbackWaitMs = readbackWaitMs;
            pendingPayloadStoreElapsedMs = 0f;
            pendingPayloadReadbackBytes = payloadBytes;
            pendingPayloadStoreBytes = new byte[payloadLength];
            // GPU readbackは有効row末尾までを返す。DCTHには有効payload長だけを格納するが、
            // readback callback内の大きなArray.CopyはAndroidを停止させるため分割する
            ScheduleGpuStageDelay(nameof(_RunHuffmanReadbackPayloadStoreStep), GetGpuEncodeStageGapStep(plane, 7));
            compressionProgressStage = ProgressStageReadback;
        }

        // Huffman readback payload Store stepを実行する
        public void _RunHuffmanReadbackPayloadStoreStep()
        {
            if (!isRunning || huffmanEncodeFailed)
            {
                return;
            }

            compressionProgressStage = ProgressStageReadback;
            float startedAt = GetTimingStart();
            if (pendingPayloadReadbackBytes == null || pendingPayloadStoreBytes == null)
            {
                FailHuffmanEncode("Huffman byte[] export failed: pending payload store is missing.");
                return;
            }

            int remaining = pendingPayloadStoreLength - pendingPayloadStoreOffset;
            int copyCount = Mathf.Min(RuntimeByteCopyChunkBytes, Mathf.Max(remaining, 0));
            if (copyCount > 0)
            {
                CopyBytes(pendingPayloadReadbackBytes, pendingPayloadStoreOffset, pendingPayloadStoreBytes, pendingPayloadStoreOffset, copyCount);
                pendingPayloadStoreOffset += copyCount;
            }

            float copyProgress = pendingPayloadStoreLength > 0
                ? Mathf.Clamp01((float)pendingPayloadStoreOffset / pendingPayloadStoreLength)
                : 1f;
            UpdateHuffmanReadbackProgress(0.55f + copyProgress * 0.45f);

            if (EnableTimingDiagnostics)
            {
                pendingPayloadStoreElapsedMs += ElapsedMs(startedAt);
            }
            if (pendingPayloadStoreOffset < pendingPayloadStoreLength)
            {
                ScheduleGpuStageDelay(nameof(_RunHuffmanReadbackPayloadStoreStep), GetGpuEncodeStageGapStep(pendingPayloadStorePlane, 7));
                return;
            }

            SetPayloadBytesForPlane(pendingPayloadStorePlane, pendingPayloadStoreBytes);
            int plane = pendingPayloadStorePlane;
            int completedStep = pendingPayloadStoreCompletedStep;
            int readbackWaitMs = pendingPayloadStoreReadbackWaitMs;
            int payloadBytes = pendingPayloadStoreBytes != null ? pendingPayloadStoreBytes.Length : 0;
            int storeMs = EnableTimingDiagnostics ? Mathf.RoundToInt(pendingPayloadStoreElapsedMs) : 0;
            if (EnableTimingDiagnostics)
            {
                AddTimingMillis(TimingOffsetReadbackStoreMs, storeMs);
                WriteTimingReadbackStepMillis(TimingOffsetReadbackStoreSteps, completedStep, storeMs);
                LogTiming("readback payload stored plane=" + GetPlaneName(plane)
                    + " bytes=" + payloadBytes.ToString()
                    + " waitMs=" + readbackWaitMs.ToString()
                    + " waitTotalMs=" + ReadTimingMillis(TimingOffsetReadbackTotalMs).ToString()
                    + " storeMs=" + storeMs.ToString()
                    + " storeTotalMs=" + ReadTimingMillis(TimingOffsetReadbackStoreMs).ToString());
            }

            ClearPendingPayloadStore();
            ReleaseCompletedHuffmanPlaneRenderTextures(plane);
            if (inspectionStopStep == 12)
            {
                StopInspection("Inspection stopped after payload readback. plane=" + GetPlaneName(plane)
                    + " bytes=" + payloadBytes.ToString() + ".");
                return;
            }

            CompleteCurrentHuffmanReadbackProgressStep();
            huffmanReadbackPayloadLength = -1;
            StartNextHuffmanReadback();
        }

        // Huffman table metadataを検証する
        private void ValidateHuffmanTableMetadata(int plane, byte[] dcCodes, byte[] acCodes)
        {
            int dcOver16 = CountHuffmanCodeLengths(dcCodes, 0, DcFrequencyBinCount, 17, 255);
            int dcMaxLength = GetMaxHuffmanCodeLength(dcCodes, 0, DcFrequencyBinCount);
            int acOver16 = CountHuffmanCodeLengths(acCodes, 0, AcFrequencyBinCount, 17, 255);
            int acMaxLength = GetMaxHuffmanCodeLength(acCodes, 0, AcFrequencyBinCount);
            if (dcOver16 <= 0 && dcMaxLength <= 16 && acOver16 <= 0 && acMaxLength <= 16)
            {
                return;
            }

            int dcNonZero = CountHuffmanCodeLengths(dcCodes, 0, DcFrequencyBinCount, 1, 16);
            int dcLengthSum = GetHuffmanCodeLengthSum(dcCodes, 0, DcFrequencyBinCount);
            int acNonZero = CountHuffmanCodeLengths(acCodes, 0, AcFrequencyBinCount, 1, 16);
            int acLengthSum = GetHuffmanCodeLengthSum(acCodes, 0, AcFrequencyBinCount);
            FailHuffmanEncode("IC Huffman table invalid plane=" + GetPlaneName(plane)
                + " dc=" + dcNonZero.ToString() + "/" + dcMaxLength.ToString() + "/" + dcOver16.ToString() + "/" + dcLengthSum.ToString()
                + " ac=" + acNonZero.ToString() + "/" + acMaxLength.ToString() + "/" + acOver16.ToString() + "/" + acLengthSum.ToString()
                + " q=" + quality.ToString()
                + " preset=" + quantPreset.ToString()
                + " detail=code length must be 0..16.");
        }

        // Huffman ブロック Overflow 容量を検証する
        private void ValidateHuffmanBlockOverflowCapacity(int plane, byte[] overflowPixel)
        {
            // metadata先頭RGBには、全8x8 blockのうち最大のHuffman使用bytesがUInt24で入る
            // 失敗flagだけでなく超過量を使うことで、qualityを固定幅で何度も下げずに済む
            int requiredBlockBytes = DecodeUInt24(overflowPixel, 0);
            if (requiredBlockBytes > HuffmanBlockPageBytes)
            {
                int dcOffset = 4;
                int acOffset = GetHuffmanMetadataWidth() * 4;
                int dcNonZero = CountHuffmanCodeLengths(overflowPixel, dcOffset, DcFrequencyBinCount, 1, 16);
                int dcOver16 = CountHuffmanCodeLengths(overflowPixel, dcOffset, DcFrequencyBinCount, 17, 255);
                int dcMaxLength = GetMaxHuffmanCodeLength(overflowPixel, dcOffset, DcFrequencyBinCount);
                int dcLengthSum = GetHuffmanCodeLengthSum(overflowPixel, dcOffset, DcFrequencyBinCount);
                int acNonZero = CountHuffmanCodeLengths(overflowPixel, acOffset, AcFrequencyBinCount, 1, 16);
                int acOver16 = CountHuffmanCodeLengths(overflowPixel, acOffset, AcFrequencyBinCount, 17, 255);
                int acMaxLength = GetMaxHuffmanCodeLength(overflowPixel, acOffset, AcFrequencyBinCount);
                int acLengthSum = GetHuffmanCodeLengthSum(overflowPixel, acOffset, AcFrequencyBinCount);
                SetQualityRetryPredictionInput(QualityRetryReasonBlockCapacity, requiredBlockBytes, HuffmanBlockPageBytes, plane);
                FailHuffmanEncodeWithQualityRetry("IC capacity plane=" + plane.ToString()
                    + " required=" + requiredBlockBytes.ToString()
                    + " dc=" + dcNonZero.ToString() + "/" + dcMaxLength.ToString() + "/" + dcOver16.ToString() + "/" + dcLengthSum.ToString()
                    + " ac=" + acNonZero.ToString() + "/" + acMaxLength.ToString() + "/" + acOver16.ToString() + "/" + acLengthSum.ToString()
                    + " q=" + quality.ToString()
                    + " preset=" + quantPreset.ToString()
                    + " cap=" + HuffmanBlockPageBytes.ToString()
                    + " detail=block page capacity exceeded.");
            }
        }

        // 数 Huffman code Lengthsを処理する
        private int CountHuffmanCodeLengths(byte[] bytes, int offset, int symbolCount, int minLength, int maxLength)
        {
            if (bytes == null || symbolCount <= 0)
            {
                return 0;
            }

            int count = 0;
            for (int symbol = 0; symbol < symbolCount; symbol++)
            {
                int byteOffset = offset + symbol * 4 + 3;
                if (byteOffset < 0 || byteOffset >= bytes.Length)
                {
                    break;
                }

                int length = bytes[byteOffset];
                if (length >= minLength && length <= maxLength)
                {
                    count++;
                }
            }

            return count;
        }

        // 最大 Huffman code 長さを返す
        private int GetMaxHuffmanCodeLength(byte[] bytes, int offset, int symbolCount)
        {
            if (bytes == null || symbolCount <= 0)
            {
                return 0;
            }

            int maxLength = 0;
            for (int symbol = 0; symbol < symbolCount; symbol++)
            {
                int byteOffset = offset + symbol * 4 + 3;
                if (byteOffset < 0 || byteOffset >= bytes.Length)
                {
                    break;
                }

                if (bytes[byteOffset] > maxLength)
                {
                    maxLength = bytes[byteOffset];
                }
            }

            return maxLength;
        }

        // Huffman code 長さ Sumを返す
        private int GetHuffmanCodeLengthSum(byte[] bytes, int offset, int symbolCount)
        {
            if (bytes == null || symbolCount <= 0)
            {
                return 0;
            }

            int sum = 0;
            for (int symbol = 0; symbol < symbolCount; symbol++)
            {
                int byteOffset = offset + symbol * 4 + 3;
                if (byteOffset < 0 || byteOffset >= bytes.Length)
                {
                    break;
                }

                sum += bytes[byteOffset];
            }

            return sum;
        }

        // Build 圧縮結果 byte列 From plane byte列を開始する
        private bool BeginBuildCompressedBytesFromPlaneBytes()
        {
            compressionProgress01 = Mathf.Max(compressionProgress01, CompressionProgressFinalizeStart);
            compressionProgressStage = ProgressStageFinalizing;
            int width = GetInputWidth();
            int height = GetInputHeight();
            if (!IsValidCodecDimensions(width, height))
            {
                FailCompressedBytes("Compressed byte[] pack failed: image size is invalid.");
                return false;
            }

            packPrepareSegmentIndex = 0;
            packTotalBytes = CompressedByteHeaderBytes;
            packTotalStepCount = 1;
            packElapsedMs = 0f;
            ScheduleGpuStageDelay(nameof(_RunCompressedBytePackPrepareStep), GetGpuEncodeStageGapStep(PlaneY, 7));
            return true;
        }

        // 圧縮結果 byte Pack Prepare stepを実行する
        public void _RunCompressedBytePackPrepareStep()
        {
            if (!isRunning || huffmanEncodeFailed)
            {
                return;
            }

            compressionProgressStage = ProgressStageFinalizing;
            float prepareProgress = (float)packPrepareSegmentIndex / (CompressedByteSegmentCount + 1);
            compressionProgress01 = Mathf.Max(
                compressionProgress01,
                CompressionProgressFinalizeStart
                    + prepareProgress * (CompressionProgressPackStart - CompressionProgressFinalizeStart));

            float startedAt = GetTimingStart();
            if (packPrepareSegmentIndex < CompressedByteSegmentCount)
            {
                int segment = packPrepareSegmentIndex;
                byte[] segmentBytes = ShouldPackCompressedSegment(segment) ? GetCompressedSegmentBytes(segment) : null;
                int length = segmentBytes != null ? segmentBytes.Length : 0;
                int plane = segment / CompressedByteSegmentPartsPerPlane;
                if (ShouldExportHuffmanPlane(plane) && length <= 0)
                {
                    FailCompressedBytes("Compressed byte[] pack failed: segment " + segment.ToString() + " is empty.");
                    return;
                }

                packTotalBytes += length;
                int copySteps = length > 0
                    ? (length + RuntimeByteCopyChunkBytes - 1) / RuntimeByteCopyChunkBytes
                    : 1;
                packTotalStepCount += copySteps;
                packPrepareSegmentIndex++;
                if (EnableTimingDiagnostics)
                {
                    packElapsedMs += ElapsedMs(startedAt);
                }
                ScheduleGpuStageDelay(nameof(_RunCompressedBytePackPrepareStep), GetGpuEncodeStageGapStep(PlaneY, 7));
                return;
            }

            if (activeCompressionUsesCapacityLimit)
            {
                // 予測はQuality選択用、最終保証は従来どおり実際に組み立てるbyte[]長で判定する
                capacityPrepassActualBytes = packTotalBytes;
            }

            // 画質設定モードではユーザー指定の容量上限を適用しない
            // codec内部のblock page上限は形式上の固定値なので、どちらのモードでも別途検証する
            if (activeCompressionUsesCapacityLimit
                && maxImageBytes > 0
                && packTotalBytes > maxImageBytes)
            {
                // 公開引数はhard上限。予測は97%を内部目標にし、成功判定ではhard上限を使い続ける
                int targetBytes = GetTotalBytePredictionTarget(maxImageBytes);
                SetQualityRetryPredictionInput(QualityRetryReasonTotalCapacity, packTotalBytes, targetBytes, PlaneY);
                FailCompressedBytesWithQualityRetry("Compressed byte[] pack failed: capacity " + maxImageBytes.ToString() + " bytes is smaller than " + packTotalBytes.ToString() + " bytes.");
                return;
            }

            int width = GetInputWidth();
            int height = GetInputHeight();
            byte[] bytes = new byte[packTotalBytes];
            WriteInt(bytes, CompressedByteOffsetMagic, CompressedByteMagic);
            WriteInt(bytes, CompressedByteOffsetVersion, CompressedByteVersion);
            WriteInt(bytes, CompressedByteOffsetHeaderBytes, CompressedByteHeaderBytes);
            WriteInt(bytes, CompressedByteOffsetImageId, latestLocalImageId);
            WriteInt(bytes, CompressedByteOffsetWidth, width);
            WriteInt(bytes, CompressedByteOffsetHeight, height);
            WriteInt(bytes, CompressedByteOffsetFlags, GetOutputByteFlags());
            WriteInt(bytes, CompressedByteOffsetQuality, quality);
            WriteInt(bytes, CompressedByteOffsetQuantPreset, quantPreset);
            WriteInt(bytes, CompressedByteOffsetAlphaMode, GetOutputAlphaMode());
            WriteInt(bytes, CompressedByteOffsetSegmentCount, CompressedByteSegmentCount);
            WriteInt(bytes, CompressedByteOffsetTotalBytes, packTotalBytes);

            compressedBytes = bytes;
            packSegmentIndex = 0;
            packDataOffset = CompressedByteHeaderBytes;
            packSegmentCopyOffset = 0;
            packSegmentLength = 0;
            packSegmentBytes = null;
            packTotalStepCount = Mathf.Max(packTotalStepCount, 1);
            packCompletedStepCount = 0;
            if (EnableTimingDiagnostics)
            {
                packElapsedMs += ElapsedMs(startedAt);
            }
            compressedBytesReady = false;
            compressedBytesFailed = false;
            compressedByteCount = packTotalBytes;
            compressionProgress01 = Mathf.Max(compressionProgress01, CompressionProgressPackStart);
            // segment copyをevent分割し、1frameへ負荷を集中させない
            // 全GPU readback直後のcompression終端でMobileがhitchするのを避ける
            ScheduleGpuStageDelay(nameof(_RunCompressedBytePackStep), GetGpuEncodeStageGapStep(PlaneY, 7));
        }

        // 圧縮結果 byte Pack stepを実行する
        public void _RunCompressedBytePackStep()
        {
            if (!isRunning || huffmanEncodeFailed)
            {
                return;
            }

            float packProgress = packTotalStepCount > 0
                ? Mathf.Clamp01((float)packCompletedStepCount / packTotalStepCount)
                : 0f;
            compressionProgress01 = Mathf.Max(
                compressionProgress01,
                CompressionProgressPackStart
                    + packProgress * (CompressionProgressFinalizeEnd - CompressionProgressPackStart));
            compressionProgressStage = ProgressStageFinalizing;

            float startedAt = GetTimingStart();
            byte[] bytes = compressedBytes;
            if (bytes == null || bytes.Length != packTotalBytes)
            {
                FailCompressedBytes("Compressed byte[] pack failed: output buffer is missing.");
                return;
            }

            if (packSegmentIndex < CompressedByteSegmentCount)
            {
                if (packSegmentCopyOffset <= 0)
                {
                    packSegmentBytes = ShouldPackCompressedSegment(packSegmentIndex) ? GetCompressedSegmentBytes(packSegmentIndex) : null;
                    packSegmentLength = packSegmentBytes != null ? packSegmentBytes.Length : 0;
                    if (packDataOffset < 0 || packSegmentLength < 0 || packSegmentLength > bytes.Length - packDataOffset)
                    {
                        FailCompressedBytes("Compressed byte[] pack failed: segment " + packSegmentIndex.ToString() + " length is invalid.");
                        return;
                    }

                    WriteInt(bytes, CompressedByteOffsetSegmentLengths + packSegmentIndex * 4, packSegmentLength);
                }

                int remaining = packSegmentLength - packSegmentCopyOffset;
                int copyCount = Mathf.Min(RuntimeByteCopyChunkBytes, Mathf.Max(remaining, 0));
                if (copyCount > 0)
                {
                    CopyBytes(packSegmentBytes, packSegmentCopyOffset, bytes, packDataOffset + packSegmentCopyOffset, copyCount);
                    packSegmentCopyOffset += copyCount;
                }

                if (packSegmentCopyOffset < packSegmentLength)
                {
                    if (EnableTimingDiagnostics)
                    {
                        packElapsedMs += ElapsedMs(startedAt);
                    }
                    packCompletedStepCount++;
                    ScheduleGpuStageDelay(nameof(_RunCompressedBytePackStep), GetGpuEncodeStageGapStep(PlaneY, 7));
                    return;
                }

                int length = packSegmentLength;
                if (packDataOffset < 0 || length < 0 || length > bytes.Length - packDataOffset)
                {
                    FailCompressedBytes("Compressed byte[] pack failed: segment " + packSegmentIndex.ToString() + " length is invalid.");
                    return;
                }

                packDataOffset += length;
                packSegmentIndex++;
                packSegmentCopyOffset = 0;
                packSegmentLength = 0;
                packSegmentBytes = null;
                if (EnableTimingDiagnostics)
                {
                    packElapsedMs += ElapsedMs(startedAt);
                }
                packCompletedStepCount++;
                ScheduleGpuStageDelay(nameof(_RunCompressedBytePackStep), GetGpuEncodeStageGapStep(PlaneY, 7));
                return;
            }

            if (EnableTimingDiagnostics)
            {
                packElapsedMs += ElapsedMs(startedAt);
            }
            if (packDataOffset != packTotalBytes)
            {
                FailCompressedBytes("Compressed byte[] pack failed: payload length is invalid.");
                return;
            }

            compressedBytesReady = true;
            compressedBytesFailed = false;
            compressedByteCount = packTotalBytes;
            packCompletedStepCount = packTotalStepCount;
            compressionProgress01 = Mathf.Max(compressionProgress01, CompressionProgressFinalizeEnd);

            if (EnableTimingDiagnostics)
            {
                AddTimingMillis(TimingOffsetPackMs, packElapsedMs);
                LogTiming("compressed byte pack ms=" + FormatTimingMs(packElapsedMs)
                    + " totalBytes=" + packTotalBytes.ToString()
                    + " maxImageBytes=" + maxImageBytes.ToString());
            }

            if (inspectionStopStep == 13)
            {
                int outputBytes = compressedBytes != null ? compressedBytes.Length : 0;
                StopInspection("Inspection stopped after compressed byte[] pack. bytes=" + outputBytes.ToString() + ".");
                return;
            }

            CompleteCompressionToBytes();
        }

        // Restore plane byte列 From 圧縮結果 byte列を開始する
        private bool BeginRestorePlaneBytesFromCompressedBytes()
        {
            float startedAt = GetTimingStart();
            byte[] bytes = compressedBytes;
            if (bytes == null || bytes.Length < CompressedByteHeaderBytes)
            {
                FailCompressedBytes("Compressed byte[] restore failed: byte container is missing.");
                return false;
            }

            int magic = ReadInt(bytes, CompressedByteOffsetMagic);
            int version = ReadInt(bytes, CompressedByteOffsetVersion);
            int headerBytes = ReadInt(bytes, CompressedByteOffsetHeaderBytes);
            int width = ReadInt(bytes, CompressedByteOffsetWidth);
            int height = ReadInt(bytes, CompressedByteOffsetHeight);
            int flags = ReadInt(bytes, CompressedByteOffsetFlags);
            int storedQuality = ReadInt(bytes, CompressedByteOffsetQuality);
            int storedQuantPreset = ReadInt(bytes, CompressedByteOffsetQuantPreset);
            int storedAlphaMode = ReadInt(bytes, CompressedByteOffsetAlphaMode);
            int segmentCount = ReadInt(bytes, CompressedByteOffsetSegmentCount);
            int totalBytes = ReadInt(bytes, CompressedByteOffsetTotalBytes);
            if (magic != CompressedByteMagic || version != CompressedByteVersion || headerBytes != CompressedByteHeaderBytes || segmentCount != CompressedByteSegmentCount || totalBytes != bytes.Length || (flags & ~KnownCompressedFlags) != 0 || !IsValidCodecDimensions(width, height))
            {
                FailCompressedBytes("Compressed byte[] restore failed: header is invalid.");
                return false;
            }

            hasAlpha = (flags & FlagHasAlpha) != 0;
            sendColor = (flags & FlagHasColor) != 0;
            halfSizeCbCr = (flags & FlagHalfSizeCbCr) != 0;
            skipOutputBlockCompression = (flags & FlagSkipOutputBlockCompression) != 0;
            outputBlockCompressionSrgb = (flags & FlagSrgbOutputBlockCompression) != 0;
            encodeSrgb = (flags & FlagLinearDctEncoding) == 0;
            activeEncodeSrgb = encodeSrgb;
            activeSourceSrgb = (flags & FlagLinearOutputTexture) == 0;
            quality = Mathf.Clamp(storedQuality, MinQuality, MaxQuality);
            quantPreset = Mathf.Clamp(storedQuantPreset, 0, MaxQuantPreset);
            if (Mathf.Clamp(storedAlphaMode, 0, 1) != alphaMode)
            {
                FailCompressedBytes("Compressed byte[] restore failed: alpha header is inconsistent with hasAlpha.");
                return false;
            }

            // 1つのDCTH byte[]からpayload、metadata、tableを取り出す
            // Android late join時にGPU decode開始前から停止しないよう、1 eventで1 segmentずつcopyする
            restoreSegmentIndex = 0;
            restoreDataOffset = headerBytes;
            restoreSegmentSourceOffset = headerBytes;
            restoreSegmentCopyOffset = 0;
            restoreSegmentLength = 0;
            restoreSegmentBytes = null;
            restoreElapsedMs = EnableTimingDiagnostics ? ElapsedMs(startedAt) : 0f;
            isRunning = true;
            operationLastProgressAtRealtime = Time.realtimeSinceStartup;
            operationProgressSignature = GetOperationProgressSignature();
            sourceByteCount = bytes.Length;
            compressedByteCount = bytes.Length;
            ScheduleGpuStageDelay(nameof(_RunCompressedByteRestoreStep), GetGpuDecodeStageGapStep(PlaneY, 0));
            return true;
        }

        // 圧縮結果 byte Restore stepを実行する
        public void _RunCompressedByteRestoreStep()
        {
            if (!isRunning || !currentOperationIsExpansion)
            {
                return;
            }

            float startedAt = GetTimingStart();
            byte[] bytes = compressedBytes;
            if (bytes == null || bytes.Length < CompressedByteHeaderBytes
                || bytes.Length > Mathf.Max(maxImageBytes, MinImageBytes))
            {
                FailCompressedBytes("Compressed byte[] restore failed: byte container is missing or too large.");
                return;
            }

            int width = ReadInt(bytes, CompressedByteOffsetWidth);
            int height = ReadInt(bytes, CompressedByteOffsetHeight);
            int flags = ReadInt(bytes, CompressedByteOffsetFlags);
            sourceWidth = width;
            sourceHeight = height;

            if (restoreSegmentIndex < CompressedByteSegmentCount)
            {
                if (restoreSegmentBytes == null)
                {
                    restoreSegmentLength = ReadInt(bytes, CompressedByteOffsetSegmentLengths + restoreSegmentIndex * 4);
                    restoreSegmentSourceOffset = restoreDataOffset;
                    restoreSegmentCopyOffset = 0;
                    if (restoreSegmentLength < 0 || restoreDataOffset < 0 || restoreSegmentLength > bytes.Length - restoreDataOffset
                        || !IsValidCompressedSegmentLength(restoreSegmentIndex, restoreSegmentLength))
                    {
                        FailCompressedBytes("Compressed byte[] restore failed: segment " + restoreSegmentIndex.ToString() + " length is invalid.");
                        return;
                    }

                    restoreSegmentBytes = new byte[restoreSegmentLength];
                }

                int remaining = restoreSegmentLength - restoreSegmentCopyOffset;
                int copyCount = Mathf.Min(RuntimeByteCopyChunkBytes, Mathf.Max(remaining, 0));
                if (copyCount > 0)
                {
                    CopyBytes(bytes, restoreSegmentSourceOffset + restoreSegmentCopyOffset, restoreSegmentBytes, restoreSegmentCopyOffset, copyCount);
                    restoreSegmentCopyOffset += copyCount;
                }

                if (restoreSegmentCopyOffset < restoreSegmentLength)
                {
                    if (EnableTimingDiagnostics)
                    {
                        restoreElapsedMs += ElapsedMs(startedAt);
                    }
                    ScheduleGpuStageDelay(nameof(_RunCompressedByteRestoreStep), GetGpuDecodeStageGapStep(PlaneY, 0));
                    return;
                }

                SetCompressedSegmentBytes(restoreSegmentIndex, restoreSegmentBytes);
                restoreDataOffset += restoreSegmentLength;
                restoreSegmentIndex++;
                restoreSegmentCopyOffset = 0;
                restoreSegmentLength = 0;
                restoreSegmentBytes = null;
                if (EnableTimingDiagnostics)
                {
                    restoreElapsedMs += ElapsedMs(startedAt);
                }
                ScheduleGpuStageDelay(nameof(_RunCompressedByteRestoreStep), GetGpuDecodeStageGapStep(PlaneY, 0));
                return;
            }

            if (EnableTimingDiagnostics)
            {
                restoreElapsedMs += ElapsedMs(startedAt);
            }
            if (restoreDataOffset != bytes.Length)
            {
                FailCompressedBytes("Compressed byte[] restore failed: payload length is invalid.");
                return;
            }

            compressedBytesReady = true;
            compressedBytesFailed = false;
            sourceBytes = compressedBytes;
            sourceByteCount = bytes.Length;
            compressedByteCount = bytes.Length;
            sourceWidth = width;
            sourceHeight = height;
            if (EnableTimingDiagnostics)
            {
                AddTimingMillis(TimingOffsetRestoreMs, restoreElapsedMs);
                LogTiming("compressed byte restore ms=" + FormatTimingMs(restoreElapsedMs)
                    + " totalBytes=" + bytes.Length.ToString()
                    + " width=" + width.ToString()
                    + " height=" + height.ToString()
                    + " flags=" + flags.ToString());
            }

            ContinueAfterCompressedByteRestore();
        }

        // After 圧縮結果 byte Restoreを継続する
        private void ContinueAfterCompressedByteRestore()
        {
            if (!ValidateRestoredCompressedSegments())
            {
                FailCompressedBytes("Compressed byte[] restore failed: Huffman table is invalid.");
                return;
            }

            if (inspectionStopStep == 14)
            {
                int bytes = compressedBytes != null ? compressedBytes.Length : 0;
                StopInspection("Inspection stopped after compressed byte[] restore. bytes=" + bytes.ToString() + ".");
                return;
            }

            quantTexture = EnsureRuntimeQuantTexture(quality, quantPreset);
            EnsureDecodeCoreRenderTextures();
            int imageId = GetCompressedImageId();
            if (imageId > 0)
            {
                latestLocalImageId = imageId;
            }

            sourceByteCount = compressedBytes != null ? compressedBytes.Length : 0;
            compressedByteCount = sourceByteCount;
            DecodeHuffmanBytesAndBeginOutputRoundtrip();
        }

        // Huffman byte列 And Begin 出力 Roundtripを復号する
        private void DecodeHuffmanBytesAndBeginOutputRoundtrip()
        {
            decodePlane = 0;
            decodeStage = 0;
            decodeUploadStep = 0;
            decodeUploadElapsedMs = 0f;
            huffmanDecodeComplete = false;
            ScheduleGpuStageDelay(nameof(_RunNextHuffmanByteDecode), GetGpuDecodeStageGapStep(PlaneY, 0));
        }

        // 次 Huffman byte 復号を実行する
        public void _RunNextHuffmanByteDecode()
        {
            if (!isRunning || huffmanDecodeFailed)
            {
                return;
            }

            if (decodePlane >= 4)
            {
                CompleteHuffmanByteDecodeAndScheduleCompose();
                return;
            }

            int plane = decodePlane;

            if (plane == PlaneA && !hasAlpha)
            {
                if (EnableTimingDiagnostics)
                {
                    RecordPendingGpuStageGap();
                }
                PackDecodedPlane(EnsureSolidTexture(true), plane);
                decodePlane++;
                decodeStage = 0;
                decodeUploadStep = 0;
                decodeUploadElapsedMs = 0f;
                ScheduleNextDecodeOrCompose();
                return;
            }

            if ((plane == PlaneCb || plane == PlaneCr) && !sendColor)
            {
                if (EnableTimingDiagnostics)
                {
                    RecordPendingGpuStageGap();
                }
                PackDecodedPlane(EnsureNeutralGrayTexture(), plane);
                decodePlane++;
                decodeStage = 0;
                decodeUploadStep = 0;
                decodeUploadElapsedMs = 0f;
                ScheduleNextDecodeOrCompose();
                return;
            }

            decodeStage = 0;
            decodeUploadStep = 0;
            decodeUploadElapsedMs = 0f;
            _RunCurrentHuffmanByteDecodeStage();
        }

        // 現在 Huffman byte 復号 段階を実行する
        public void _RunCurrentHuffmanByteDecodeStage()
        {
            if (!isRunning || huffmanDecodeFailed)
            {
                return;
            }

            if (EnableTimingDiagnostics)
            {
                RecordPendingGpuStageGap();
            }
            int plane = decodePlane;
            if (plane < 0 || plane >= 4)
            {
                ScheduleNextDecodeOrCompose();
                return;
            }

            stage1PlaneMode = plane;
            if (decodeStage == 0)
            {
                if (decodeUploadStep <= 0)
                {
                    EnsureDecodePlaneWorkTextures(plane);
                    EnsureHuffmanDecodeRenderTextures(plane);
                    if (!ValidateHuffmanDecode())
                    {
                        return;
                    }

                    decodeUploadStep = 0;
                    decodeUploadElapsedMs = 0f;
                }

                if (decodeUploadStep < DecodeUploadStepCount)
                {
                    float startedAt = GetTimingStart();
                    if (!UploadPlaneHuffmanDecodeTextureStep(plane, decodeUploadStep))
                    {
                        if (pendingChunkOffsetBuild || pendingDecodePayloadUploadBuild)
                        {
                            if (EnableTimingDiagnostics)
                            {
                                decodeUploadElapsedMs += ElapsedMs(startedAt);
                            }
                            return;
                        }

                        FailHuffmanDecode("Huffman byte[] decode skipped: payload or metadata byte[] is missing.");
                        return;
                    }

                    if (EnableTimingDiagnostics)
                    {
                        decodeUploadElapsedMs += ElapsedMs(startedAt);
                    }
                    decodeUploadStep++;
                    if (decodeUploadStep < DecodeUploadStepCount)
                    {
                        ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 0));
                        return;
                    }

                    float uploadElapsed = decodeUploadElapsedMs;
                    if (EnableTimingDiagnostics)
                    {
                        AddTimingMillis(TimingOffsetDecodeUploadMs, decodeUploadElapsedMs);
                        LogTiming("huffman decode upload plane=" + GetPlaneName(plane)
                            + " ms=" + FormatTimingMs(uploadElapsed));
                    }
                    decodeUploadElapsedMs = 0f;
                    if (inspectionStopStep == 15)
                    {
                        StopInspection("Inspection stopped after decode upload. plane=" + GetPlaneName(plane)
                            + " ms=" + FormatTimingMs(uploadElapsed) + ".");
                        return;
                    }

                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 0));
                    return;
                }

                if (decodeUploadStep == DecodeUploadStepCount)
                {
                    SetupHuffmanAcDecodeLengthSummaryMaterial(huffmanDecodeRleMaterial, receivedAcHuffmanCodesTexture, 0, HuffmanDecodeAcSymbolsPerGroup);
                    TrackedBlit(receivedAcHuffmanCodesTexture, huffmanAcDecodeLengthSummary, huffmanDecodeRleMaterial, HuffmanDecodeRlePassBuildAcLengthSummary);
                    decodeUploadStep++;
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 0));
                    return;
                }

                int symbolGroup = decodeUploadStep - DecodeUploadStepCount - 1;
                if (symbolGroup < HuffmanDecodeAcSymbolGroupCount)
                {
                    RenderTexture previousSymbolTable = symbolGroup <= 0
                        ? null
                        : ((symbolGroup & 1) == 0 ? huffmanAcDecodeSymbolTableWork : huffmanAcDecodeSymbolTableTemp);
                    RenderTexture targetSymbolTable = (symbolGroup & 1) == 0
                        ? huffmanAcDecodeSymbolTableTemp
                        : huffmanAcDecodeSymbolTableWork;
                    SetupHuffmanAcDecodeLookupMaterial(
                        huffmanDecodeRleMaterial,
                        receivedAcHuffmanCodesTexture,
                        previousSymbolTable,
                        symbolGroup * HuffmanDecodeAcSymbolsPerGroup,
                        HuffmanDecodeAcSymbolsPerGroup,
                        symbolGroup > 0);
                    TrackedBlit(receivedAcHuffmanCodesTexture, targetSymbolTable, huffmanDecodeRleMaterial, HuffmanDecodeRlePassBuildAcSymbolTable);
                    decodeUploadStep++;
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 0));
                    return;
                }

                TrackedBlit(huffmanAcDecodeSymbolTableWork, huffmanAcDecodeSymbolTable);
                huffmanAcDecodeSymbolTableWork = ReleaseRuntimeRenderTexture(huffmanAcDecodeSymbolTableWork);
                huffmanAcDecodeSymbolTableTemp = ReleaseRuntimeRenderTexture(huffmanAcDecodeSymbolTableTemp);
                decodeUploadStep = 0;
                SetupHuffmanChunkBlockValidMaterialBase(huffmanChunkBlockValidMaterial);
                decodeStage = 1;
                ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 1));
                return;
            }

            if (decodeStage == 1)
            {
                if (decodeLocalIndex <= 0)
                {
                    decodeLocalIndex = 0;
                    decodeBitOffsetPing = 0;
                }

                int rowGroupCount = GetDecodeBitOffsetChunkRowGroupCount(plane);
                int totalStepCount = DecodeBitOffsetLocalGroupCount * rowGroupCount;
                int bitOffsetLocalBlock = GetDecodeBitOffsetLocalBlockFromStep(decodeLocalIndex, rowGroupCount);
                int bitOffsetChunkRowGroup = GetDecodeBitOffsetChunkRowGroupFromStep(decodeLocalIndex, rowGroupCount);
                int chunkRowStart = bitOffsetChunkRowGroup * DecodeBitOffsetChunkRowsPerGroup;
                int chunkRowCount = Mathf.Min(DecodeBitOffsetChunkRowsPerGroup, Mathf.Max(GetChunkHeight(plane) - chunkRowStart, 1));

                // 対象外chunk rowは直前のping-pong Textureからcopyする
                // local-block group→row group順に進め、同じchunkの直前offsetを読みながら小さいrow帯だけ処理する
                RenderTexture previous = GetCurrentHuffmanPayloadBitOffsetTexture();
                RenderTexture target = GetNextHuffmanPayloadBitOffsetTexture();
                SetupHuffmanChunkBlockValidMaterialRangeOnly(huffmanChunkBlockValidMaterial, previous, chunkRowStart, chunkRowCount, 0, GetChunkWidth(plane), bitOffsetLocalBlock);
                TrackedBlit(receivedPayloadTexture, target, huffmanChunkBlockValidMaterial, 0); // h
                decodeBitOffsetPing = decodeBitOffsetPing == 0 ? 1 : 0;
                decodeLocalIndex++;

                if (decodeLocalIndex < totalStepCount)
                {
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 1));
                    return;
                }

                decodeLocalIndex = 0;
                if (inspectionStopStep == 16)
                {
                    StopInspection("Inspection stopped after decode bitOffset. plane=" + GetPlaneName(plane)
                        + " passes=" + totalStepCount.ToString()
                        + " localBlocksPerChunk=" + DecodeBitOffsetLocalBlocksPerGroup.ToString() + ".");
                    return;
                }

                decodeStage = 2;
                ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 2));
                return;
            }

            if (decodeStage == 2)
            {
                if (decodeLocalIndex <= 0)
                {
                    decodeLocalIndex = 0;
                    decodeRleStatePing = 0;
                }

                int rowGroupCount = GetDecodeRleChunkRowGroupCount(plane);
                int stateInitializeStepCount = rowGroupCount;
                int fullGroupWorkCount = Mathf.Max(DecodeRleSlotGroupCount - 1, 0) * rowGroupCount * 2;
                int totalStepCount = stateInitializeStepCount + fullGroupWorkCount + rowGroupCount;

                // このstageは係数slotとchunk-row groupの2軸で意図的に分割する
                // 対象外row/slotを前段Textureからcopyし、最終係数を変えずAndroid 1 frameのscan量を減らす
                if (decodeLocalIndex < stateInitializeStepCount)
                {
                    int decodeChunkRowGroup = decodeLocalIndex;
                    RenderTexture previousState = GetCurrentHuffmanDecodeStateTexture();
                    RenderTexture targetState = GetNextHuffmanDecodeStateTexture();
                    SetupHuffmanDecodeRleMaterial(huffmanDecodeRleMaterial, rleSymbolsFromHuffmanPayload, previousState, 0, decodeChunkRowGroup);
                    TrackedBlit(receivedPayloadTexture, targetState, huffmanDecodeRleMaterial, HuffmanDecodeRlePassInitializeState);
                    decodeRleStatePing = decodeRleStatePing == 0 ? 1 : 0;
                }
                else
                {
                    int workIndex = decodeLocalIndex - stateInitializeStepCount;
                    int decodeSlotGroup;
                    int decodeChunkRowGroup;
                    bool advanceState;
                    int workPerFullGroup = rowGroupCount * 2;
                    if (workIndex < fullGroupWorkCount)
                    {
                        decodeSlotGroup = workIndex / workPerFullGroup;
                        int workInGroup = workIndex - decodeSlotGroup * workPerFullGroup;
                        advanceState = workInGroup >= rowGroupCount;
                        decodeChunkRowGroup = advanceState ? workInGroup - rowGroupCount : workInGroup;
                    }
                    else
                    {
                        decodeSlotGroup = DecodeRleSlotGroupCount - 1;
                        decodeChunkRowGroup = workIndex - fullGroupWorkCount;
                        advanceState = false;
                    }

                    if (advanceState)
                    {
                        RenderTexture previousState = GetCurrentHuffmanDecodeStateTexture();
                        RenderTexture targetState = GetNextHuffmanDecodeStateTexture();
                        SetupHuffmanDecodeRleMaterial(huffmanDecodeRleMaterial, rleSymbolsFromHuffmanPayload, previousState, decodeSlotGroup, decodeChunkRowGroup);
                        TrackedBlit(receivedPayloadTexture, targetState, huffmanDecodeRleMaterial, HuffmanDecodeRlePassAdvanceState);
                        decodeRleStatePing = decodeRleStatePing == 0 ? 1 : 0;
                    }
                    else
                    {
                        int outputStep = decodeSlotGroup * rowGroupCount + decodeChunkRowGroup;
                        RenderTexture previous = IsDecodeSlotGroupStepSourceMain(outputStep) ? rleSymbolsFromHuffmanPayload : symbolFixedFromHuffmanPayload;
                        RenderTexture target = IsDecodeSlotGroupStepSourceMain(outputStep) ? symbolFixedFromHuffmanPayload : rleSymbolsFromHuffmanPayload;
                        SetupHuffmanDecodeRleMaterial(huffmanDecodeRleMaterial, previous, GetCurrentHuffmanDecodeStateTexture(), decodeSlotGroup, decodeChunkRowGroup);
                        TrackedBlit(receivedPayloadTexture, target, huffmanDecodeRleMaterial, HuffmanDecodeRlePassOutputGroup);
                    }
                }
                decodeLocalIndex++;
                if (decodeLocalIndex < totalStepCount)
                {
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 2));
                    return;
                }

                decodeLocalIndex = 0;
                int outputStepCount = DecodeRleSlotGroupCount * rowGroupCount;
                if (!IsDecodeSlotGroupStepSourceMain(outputStepCount))
                {
                    // 将来group数を変えて最終ping-pong先がtempになっても、後段入力は
                    // rleSymbolsFromHuffmanPayloadへ固定する
                    TrackedBlit(symbolFixedFromHuffmanPayload, rleSymbolsFromHuffmanPayload);
                }

                if (inspectionStopStep == 17)
                {
                    StopInspection("Inspection stopped after decode RLE. plane=" + GetPlaneName(plane)
                        + " passes=" + totalStepCount.ToString() + ".");
                    return;
                }

                decodeStage = 3;
                ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 3));
                return;
            }

            if (decodeStage == 3)
            {
                if (decodeLocalIndex <= 0)
                {
                    // DC scanは全DCT blockのprefix sum。delta抽出とbase scan copyを
                    // 1 eventにまとめるだけでもQuestでhitchするためsetupから分ける
                    SetupRleDcDeltaMaterial(rleDcDeltaMaterial);
                    TrackedBlit(rleSymbolsFromHuffmanPayload, dcDeltaFromHuffmanPayload, rleDcDeltaMaterial, 0);
                    decodeLocalIndex = 1;
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 3));
                    return;
                }

                if (decodeLocalIndex == 1)
                {
                    // 分割経路はraw DC deltaのcopyから開始する。後続scanも同じ方法でping-pong先を埋め、
                    // active row帯だけを上書きする
                    SetupDcScanMaterial(dcScanMaterial);
                    TrackedBlit(dcDeltaFromHuffmanPayload, dcScanFromHuffmanPayload, dcScanMaterial, 0);
                    decodeLocalIndex = 2;
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 3));
                    return;
                }

                int blockTotal = GetBlockWidth(plane) * GetBlockHeight(plane);
                int scanPassCount = GetDcScanPassCount(blockTotal);
                int rowGroupCount = GetDecodeDcScanBlockRowGroupCount(plane);
                int scanWorkIndex = decodeLocalIndex - 2;
                int workPerScanPass = rowGroupCount + 1;
                int totalScanWorkCount = scanPassCount * workPerScanPass;
                if (scanWorkIndex < totalScanWorkCount)
                {
                    int scanPassIndex = scanWorkIndex / workPerScanPass;
                    int scanSubStep = scanWorkIndex - scanPassIndex * workPerScanPass;
                    int scanStep = GetDcScanStepForPass(scanPassIndex);
                    bool sourceIsMain = IsDcScanStepSourceMain(scanStep);
                    RenderTexture source = sourceIsMain ? dcScanFromHuffmanPayload : dcScanFromHuffmanPayloadTemp;
                    RenderTexture destination = sourceIsMain ? dcScanFromHuffmanPayloadTemp : dcScanFromHuffmanPayload;

                    SetupDcScanMaterial(dcScanMaterial);
                    if (scanSubStep <= 0)
                    {
                        TrackedBlit(source, destination, dcScanMaterial, 0);
                    }
                    else
                    {
                        int rowGroup = scanSubStep - 1;
                        dcScanMaterial.SetFloat("_ScanStep", scanStep);
                        dcScanMaterial.SetFloat("_ScanBlockRowGroup", rowGroup);
                        dcScanMaterial.SetFloat("_ScanBlockRowsPerGroup", DecodeDcScanBlockRowsPerGroup);
                        TrackedBlit(source, destination, dcScanMaterial, 1);
                    }

                    decodeLocalIndex++;
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 3));
                    return;
                }

                if (IsDcScanResultInTemp(blockTotal))
                {
                    TrackedBlit(dcScanFromHuffmanPayloadTemp, dcScanFromHuffmanPayload, dcScanMaterial, 0);
                }

                decodeLocalIndex = 0;
                if (inspectionStopStep == 18)
                {
                    StopInspection("Inspection stopped after decode DC scan. plane=" + GetPlaneName(plane) + ".");
                    return;
                }

                decodeStage = 4;
                ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 4));
                return;
            }

            if (decodeStage == 4)
            {
                if (decodeLocalIndex <= 0)
                {
                    decodeLocalIndex = 0;
                }

                if (decodeLocalIndex == 0)
                {
                    SetupRleToSymbolFixedBuildPrefixBaseMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload);
                    TrackedBlit(rleSymbolsFromHuffmanPayload, symbolFixedWorkFromHuffmanPayload, rleToSymbolFixedMaterial, RleToSymbolFixedPassBuildPrefixBase);
                }
                else if (decodeLocalIndex <= RleToSymbolFixedPrefixScanPassCount)
                {
                    int scanIndex = decodeLocalIndex - 1;
                    RenderTexture previous = (scanIndex & 1) == 0 ? symbolFixedWorkFromHuffmanPayload : symbolFixedFromHuffmanPayload;
                    RenderTexture target = (scanIndex & 1) == 0 ? symbolFixedFromHuffmanPayload : symbolFixedWorkFromHuffmanPayload;
                    SetupRleToSymbolFixedMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload, previous, 1 << scanIndex);
                    TrackedBlit(previous, target, rleToSymbolFixedMaterial, RleToSymbolFixedPassScanPrefix);
                }
                else
                {
                    SetupRleToSymbolFixedMaterial(rleToSymbolFixedMaterial, dcScanFromHuffmanPayload, symbolFixedWorkFromHuffmanPayload, 1);
                    TrackedBlit(rleSymbolsFromHuffmanPayload, symbolFixedFromHuffmanPayload, rleToSymbolFixedMaterial, RleToSymbolFixedPassDecodeSymbols);
                }

                decodeLocalIndex++;
                if (decodeLocalIndex < RleToSymbolFixedStepCount)
                {
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 4));
                    return;
                }

                decodeLocalIndex = 0;
                if (inspectionStopStep == 19)
                {
                    StopInspection("Inspection stopped after decode symbolFixed. plane=" + GetPlaneName(plane)
                        + " passes=" + RleToSymbolFixedStepCount.ToString() + ".");
                    return;
                }

                decodeStage = 5;
                ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 5));
                return;
            }

            if (decodeStage == 5)
            {
                int rowGroupCount = GetDecodeIdctBlockRowGroupCount(plane);
                int idctStepCount = DecodeIdctLocalGroupCount * rowGroupCount;
                if (decodeLocalIndex <= 0)
                {
                    // IDCTは画像寸法のTextureを触るためMobile GPUで重い。ここでは全row warmup passを行わない
                    // 後続の分割passが全local row/columnとblock-row帯を上書きするので、同一ping-pong chain内で
                    // 後から再計算されるpixelに古い値が残っていても最終結果へ影響しない
                    decodeLocalIndex = 1;
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 5));
                    return;
                }

                if (decodeLocalIndex <= idctStepCount)
                {
                    int idctStep = decodeLocalIndex - 1;
                    int localGroup = GetDecodeIdctLocalGroupFromStep(idctStep, rowGroupCount);
                    int blockRowGroup = GetDecodeIdctBlockRowGroupFromStep(idctStep, rowGroupCount);
                    RenderTexture previous = IsDecodeSlotGroupStepSourceMain(idctStep) ? work : reconstructedFromHuffmanPayload;
                    RenderTexture target = IsDecodeSlotGroupStepSourceMain(idctStep) ? reconstructedFromHuffmanPayload : work;
                    SetupDecodeSymbolsMaterial(decodeSymbolsMaterial, previous, localGroup, blockRowGroup);
                    TrackedBlit(symbolFixedFromHuffmanPayload, target, decodeSymbolsMaterial, 0);
                    decodeLocalIndex++;
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 5));
                    return;
                }

                // 水平IDCTで上書きされる前の中間値を、開始時の1回だけworkへ保存する
                if (decodeLocalIndex == idctStepCount + 1 && !IsDecodeSlotGroupStepSourceMain(idctStepCount))
                {
                    TrackedBlit(reconstructedFromHuffmanPayload, work);
                }

                if (decodeLocalIndex <= idctStepCount * 2)
                {
                    int idctStep = decodeLocalIndex - idctStepCount - 1;
                    int localGroup = GetDecodeIdctLocalGroupFromStep(idctStep, rowGroupCount);
                    int blockRowGroup = GetDecodeIdctBlockRowGroupFromStep(idctStep, rowGroupCount);
                    RenderTexture previous = IsDecodeSlotGroupStepSourceMain(idctStep) ? reconstructedFromHuffmanPayload : symbolFixedFromHuffmanPayload;
                    RenderTexture target = IsDecodeSlotGroupStepSourceMain(idctStep) ? symbolFixedFromHuffmanPayload : reconstructedFromHuffmanPayload;
                    SetupDecodeSymbolsMaterial(decodeSymbolsMaterial, previous, localGroup, blockRowGroup);
                    TrackedBlit(work, target, decodeSymbolsMaterial, 1);
                    decodeLocalIndex++;
                    ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 5));
                    return;
                }

                if (!IsDecodeSlotGroupStepSourceMain(idctStepCount))
                {
                    TrackedBlit(symbolFixedFromHuffmanPayload, reconstructedFromHuffmanPayload);
                }

                PackDecodedPlane(reconstructedFromHuffmanPayload, plane);
                decodeLocalIndex = 0;
                if (inspectionStopStep == 20)
                {
                    StopInspection("Inspection stopped after decode IDCT/copy. plane=" + GetPlaneName(plane) + ".");
                    return;
                }

                decodePlane++;
                decodeStage = 0;
                decodeUploadStep = 0;
                decodeUploadElapsedMs = 0f;
                ScheduleNextDecodeOrCompose();
                return;
            }

            ScheduleNextDecodeOrCompose();
        }

        // 次 復号 Or Composeを予約する
        private void ScheduleNextDecodeOrCompose()
        {
            if (decodePlane >= 4)
            {
                CompleteHuffmanByteDecodeAndScheduleCompose();
                return;
            }

            ScheduleGpuStageDelay(nameof(_RunNextHuffmanByteDecode), GetGpuDecodeStageGapStep(decodePlane, 0));
        }

        // Huffman byte 復号 And Schedule Composeを完了状態にする
        private void CompleteHuffmanByteDecodeAndScheduleCompose()
        {
            if (EnableTimingDiagnostics)
            {
                RecordPendingGpuStageGap();
            }
            huffmanDecodeComplete = true;
            ScheduleGpuStageDelay(nameof(_ComposeDecodedOutputAfterPlaneRender), GetGpuDecodeComposeGapStep());
        }

        // plane DCT 符号化 段階を実行する
        private bool RunPlaneDctEncodeStage(int plane)
        {
            stage1PlaneMode = plane;
            EnsurePlaneWorkTextures(plane);

            float startedAt = GetTimingStart();
            SetupEncodeMaterial(encodeMaterial);
            if (encodeSubStage <= 0 && ShouldDownsampleEncodeSourceForPlane(plane))
            {
                if (!RunColorDownsampleForPlane(plane, startedAt))
                {
                    return false;
                }

                encodeSubStage = 1;
                return false;
            }

            if (encodeSubStage <= 0)
            {
                encodeSubStage = 1;
            }

            if (encodeSubStage >= 1 && encodeSubStage <= 4)
            {
                Texture encodeSource = GetDctHorizontalSourceForPlane(plane);
                if (encodeSource == null)
                {
                    huffmanEncodeFailed = true;
                    SetStatus("DCT encode failed: input texture is missing. plane=" + GetPlaneName(plane));
                    return false;
                }

                // horizontal/vertical DCTはいずれも画像全体を処理する
                // 別eventで実行し、Android/Questの1 frameへDCT encode全体を集中させない
                int horizontalStage = encodeSubStage - 1;
                RenderTexture horizontalTarget = (horizontalStage & 1) == 0 ? coefficients : work;
                RenderTexture horizontalPrevious = horizontalStage <= 0
                    ? null
                    : ((horizontalStage & 1) == 0 ? work : coefficients);
                SetupHorizontalAccumulationMaterial(encodeMaterial, horizontalPrevious, horizontalStage);
                TrackedBlit(encodeSource, horizontalTarget, encodeMaterial, 0);
                if (EnableTimingDiagnostics)
                {
                    float elapsed = ElapsedMs(startedAt);
                    lastStage1EncodeMs += elapsed;
                    LogTiming("stage1 dct horizontal plane=" + GetPlaneName(plane)
                        + " part=" + horizontalStage.ToString()
                        + " ms=" + FormatTimingMs(elapsed)
                        + " totalStage1EncodeMs=" + FormatTimingMs(lastStage1EncodeMs));
                }

                if (horizontalStage < 3)
                {
                    encodeSubStage++;
                    return false;
                }

                if (inspectionStopStep == 1)
                {
                    float stopElapsed = ElapsedMs(startedAt);
                    lastStage1EncodeMs += stopElapsed;
                    isRunning = false;
                    huffmanReadbackPending = false;
                    SetStatus("Inspection stopped after first DCT encode pass. plane=" + GetPlaneName(plane)
                        + " pass=EncodeDcthHorizontalParts ms=" + FormatTimingMs(stopElapsed));
                    return false;
                }

                encodeSubStage = 5;
                return false;
            }

            SetupEncodeMaterial(encodeVerticalMaterial);
            TrackedBlit(work, coefficients, encodeVerticalMaterial, 0);
            if (inspectionStopStep == 2)
            {
                float stopElapsed = ElapsedMs(startedAt);
                lastStage1EncodeMs += stopElapsed;
                isRunning = false;
                huffmanReadbackPending = false;
                SetStatus("Inspection stopped after second DCT encode pass. plane=" + GetPlaneName(plane)
                    + " pass=EncodeDcthVerticalQuantize ms=" + FormatTimingMs(stopElapsed));
                return false;
            }

            if (EnableTimingDiagnostics)
            {
                float elapsed = ElapsedMs(startedAt);
                lastStage1EncodeMs += elapsed;
                LogTiming("stage1 dct vertical/quantize plane=" + GetPlaneName(plane)
                    + " ms=" + FormatTimingMs(elapsed)
                    + " totalStage1EncodeMs=" + FormatTimingMs(lastStage1EncodeMs));
            }

            encodeSubStage = 0;
            return true;
        }

        // Should Downsample 符号化 入力用planeを処理する
        private bool ShouldDownsampleEncodeSourceForPlane(int plane)
        {
            return halfSizeCbCr && (plane == PlaneCb || plane == PlaneCr);
        }

        // 色 Downsample用planeを実行する
        private bool RunColorDownsampleForPlane(int plane, float startedAt)
        {
            Texture source = GetCompressInputTexture();
            if (source == null || colorDownsampleMaterial == null)
            {
                huffmanEncodeFailed = true;
                SetStatus("DCT encode failed: color downsample input is missing. plane=" + GetPlaneName(plane));
                return false;
            }

            // half-size Cb/Crでbyte[]を小さく保つ。downsample Blitは独立eventにし、
            // Android/Questでhorizontal DCTと同じframeに重ねない
            int width = Mathf.Max(GetPlaneSourceWidth(plane), 1);
            int height = Mathf.Max(GetPlaneSourceHeight(plane), 1);
            colorDownsampleTexture = EnsureRuntimeRenderTexture(
                colorDownsampleTexture,
                "IC_Library_ColorDownsample",
                width,
                height,
                GetPlaneRenderTextureFormat());
            SetupColorDownsampleMaterial(colorDownsampleMaterial, source.width, source.height);
            TrackedBlit(source, colorDownsampleTexture, colorDownsampleMaterial, 0);
            if (EnableTimingDiagnostics)
            {
                float elapsed = ElapsedMs(startedAt);
                lastStage1EncodeMs += elapsed;
                LogTiming("stage1 color downsample plane=" + GetPlaneName(plane)
                    + " ms=" + FormatTimingMs(elapsed)
                    + " totalStage1EncodeMs=" + FormatTimingMs(lastStage1EncodeMs));
            }

            return true;
        }

        // DCT Horizontal 入力用planeを返す
        private Texture GetDctHorizontalSourceForPlane(int plane)
        {
            return ShouldDownsampleEncodeSourceForPlane(plane) ? (Texture)colorDownsampleTexture : GetCompressInputTexture();
        }

        // plane作業data Textureを使用可能な状態にする
        private void EnsurePlaneWorkTextures(int plane)
        {
            int oldPlane = stage1PlaneMode;
            stage1PlaneMode = plane;
            int width = GetPlaneWidth(plane);
            int height = GetPlaneHeight(plane);
            RenderTextureFormat dctFormat = GetSingleChannelDctRenderTextureFormat();
            work = EnsureRuntimeRenderTexture(work, "IC_Library_Work", width, height, dctFormat);
            coefficients = EnsureRuntimeRenderTexture(coefficients, "IC_Library_Coefficients", width, height, dctFormat);
            stage1PlaneMode = oldPlane;
        }

        // 復号 plane作業data Textureを使用可能な状態にする
        private void EnsureDecodePlaneWorkTextures(int plane)
        {
            int oldPlane = stage1PlaneMode;
            stage1PlaneMode = plane;
            int width = GetPlaneWidth(plane);
            int height = GetPlaneHeight(plane);
            RenderTextureFormat dctFormat = GetSingleChannelDctRenderTextureFormat();
            work = EnsureRuntimeRenderTexture(work, "IC_Library_Work", width, height, dctFormat);
            reconstructedFromHuffmanPayload = EnsureRuntimeRenderTexture(
                reconstructedFromHuffmanPayload,
                "IC_Library_ReconstructedFromHuffmanPayload",
                width,
                height,
                GetDecodeReconstructedRenderTextureFormat());
            stage1PlaneMode = oldPlane;
        }

        // Pack Decoded planeを処理する
        private void PackDecodedPlane(Texture decodedPlane, int plane)
        {
            if (decodedPlane == null || packedDecodedPlanes == null || composeRgbaMaterial == null)
            {
                FailHuffmanDecode("Decoded plane packing failed: required texture or material is missing.");
                return;
            }

            int clampedPlane = ClampPlane(plane);
            SetupPackedPlaneMaterial(composeRgbaMaterial, clampedPlane);
            TrackedBlit(decodedPlane, packedDecodedPlanes, composeRgbaMaterial, clampedPlane);
        }

        // DCT演算値だけを保持するRTはRのみを使用する。RGBA symbolの一時保持には使わない
        private RenderTextureFormat GetSingleChannelDctRenderTextureFormat()
        {
            return RenderTextureFormat.RFloat;
        }

        // reconstructedはIDCTのscalar partial sum/outputだけを保持する
        private RenderTextureFormat GetDecodeReconstructedRenderTextureFormat()
        {
            return RenderTextureFormat.RFloat;
        }

        // plane 描画 Texture formatを返す
        private RenderTextureFormat GetPlaneRenderTextureFormat()
        {
            return RenderTextureFormat.ARGBFloat;
        }

        // 表示 描画 Texture formatを返す
        private RenderTextureFormat GetDisplayRenderTextureFormat()
        {
            return RenderTextureFormat.ARGB32;
        }

        // DCT Precision Labelを返す
        private string GetDctPrecisionLabel()
        {
            return "Float"
                + " coeffRT=" + GetRenderTextureFormatLabel(GetSingleChannelDctRenderTextureFormat())
                + " symbolScratchRT=ARGB32"
                + " reconstructedRT=" + GetRenderTextureFormatLabel(GetDecodeReconstructedRenderTextureFormat())
                + " planeRT=" + GetRenderTextureFormatLabel(GetPlaneRenderTextureFormat())
                + " outputRT=" + GetRenderTextureFormatLabel(GetDisplayRenderTextureFormat());
        }

        // 描画 Texture format Labelを返す
        private string GetRenderTextureFormatLabel(RenderTextureFormat format)
        {
            if (format == RenderTextureFormat.ARGBFloat)
            {
                return "ARGBFloat";
            }

            if (format == RenderTextureFormat.RFloat)
            {
                return "RFloat";
            }

            if (format == RenderTextureFormat.ARGB32)
            {
                return "ARGB32";
            }

            return "Other";
        }

        // Runtime 描画 Textureを使用可能な状態にする
        private RenderTexture EnsureRuntimeRenderTexture(RenderTexture current, string textureName, int width, int height, RenderTextureFormat format)
        {
            return EnsureRuntimeRenderTexture(current, textureName, width, height, format, false, false);
        }

        // Runtime 描画 Textureを使用可能な状態にする
        private RenderTexture EnsureRuntimeRenderTexture(RenderTexture current, string textureName, int width, int height, RenderTextureFormat format, bool useMipMap, bool autoGenerateMips)
        {
            return EnsureRuntimeRenderTexture(current, textureName, width, height, format, useMipMap, autoGenerateMips, false);
        }

        // Runtime 描画 Textureを使用可能な状態にする
        private RenderTexture EnsureRuntimeRenderTexture(RenderTexture current, string textureName, int width, int height, RenderTextureFormat format, bool useMipMap, bool autoGenerateMips, bool useSrgb)
        {
            if (current != null
                && current.width == width
                && current.height == height
                && current.format == format
                && current.useMipMap == useMipMap
                && current.autoGenerateMips == autoGenerateMips)
            {
                current.filterMode = FilterMode.Point;
                current.wrapMode = TextureWrapMode.Clamp;
                current.antiAliasing = 1;
                current.anisoLevel = 0;
                if (!current.IsCreated())
                {
                    current.Create();
                }

                if (current.IsCreated())
                {
                    return current;
                }

                Debug.LogError("[DcthCodecLibrary] RenderTexture.Create failed: " + textureName
                    + " format=" + GetRenderTextureFormatLabel(format)
                    + " size=" + width + "x" + height);
                return ReleaseRuntimeRenderTexture(current);
            }

            ReleaseRuntimeRenderTexture(current);
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(width, height, format, 0);
            descriptor.sRGB = useSrgb;
            descriptor.msaaSamples = 1;
            descriptor.useMipMap = useMipMap;
            descriptor.autoGenerateMips = autoGenerateMips;
            RenderTexture created = new RenderTexture(descriptor);
            created.name = textureName;
            created.filterMode = FilterMode.Point;
            created.wrapMode = TextureWrapMode.Clamp;
            created.antiAliasing = 1;
            created.anisoLevel = 0;
            created.useMipMap = useMipMap;
            created.autoGenerateMips = autoGenerateMips;
            created.Create();
            if (created.IsCreated())
            {
                return created;
            }

            Debug.LogError("[DcthCodecLibrary] RenderTexture.Create failed: " + textureName
                + " format=" + GetRenderTextureFormatLabel(format)
                + " size=" + width + "x" + height);
            return ReleaseRuntimeRenderTexture(created);
        }

        // Runtime 描画 Textureを解放する
        private RenderTexture ReleaseRuntimeRenderTexture(RenderTexture texture)
        {
            if (texture == null)
            {
                return null;
            }

            if (texture.IsCreated())
            {
                texture.Release();
            }

            Destroy(texture);
            return null;
        }

        // Runtime Textureを解放する
        private Texture2D ReleaseRuntimeTexture(Texture2D texture)
        {
            if (texture == null)
            {
                return null;
            }

            Destroy(texture);
            return null;
        }

        // 容量 事前計算 Texture 準備完了かを判定する
        private bool IsCapacityPrepassTextureReady(RenderTexture texture, int width, int height, RenderTextureFormat format)
        {
            return texture != null
                && texture.width == width
                && texture.height == height
                && texture.format == format
                && texture.IsCreated();
        }

        // 初回のRT生成を1フレームへ集中させない。0=1枚生成、1=準備済み、-1=生成失敗
        private int EnsureNextCapacityPrepassPrepareTexture(int plane, int coefficientWidth, int coefficientHeight, int blockWidth, int blockHeight)
        {
            RenderTextureFormat dctFormat = GetSingleChannelDctRenderTextureFormat();
            if (!IsCapacityPrepassTextureReady(capacityPrepassWork, coefficientWidth, coefficientHeight, dctFormat))
            {
                capacityPrepassWork = EnsureRuntimeRenderTexture(capacityPrepassWork, "IC_Library_CapacityPrepassWork", coefficientWidth, coefficientHeight, dctFormat);
                return capacityPrepassWork != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassPreviousDctWork, coefficientWidth, coefficientHeight, dctFormat))
            {
                capacityPrepassPreviousDctWork = EnsureRuntimeRenderTexture(capacityPrepassPreviousDctWork, "IC_Library_CapacityPrepassPreviousDctWork", coefficientWidth, coefficientHeight, dctFormat);
                return capacityPrepassPreviousDctWork != null ? 0 : -1;
            }
            if (plane == PlaneY)
            {
                if (!IsCapacityPrepassTextureReady(capacityPrepassYDct, coefficientWidth, coefficientHeight, dctFormat))
                {
                    capacityPrepassYDct = EnsureRuntimeRenderTexture(capacityPrepassYDct, "IC_Library_CapacityPrepassYDct", coefficientWidth, coefficientHeight, dctFormat);
                    return capacityPrepassYDct != null ? 0 : -1;
                }
                if (!IsCapacityPrepassTextureReady(capacityPrepassYPreviousDc, blockWidth, blockHeight, dctFormat))
                {
                    capacityPrepassYPreviousDc = EnsureRuntimeRenderTexture(capacityPrepassYPreviousDc, "IC_Library_CapacityPrepassYPreviousDc", blockWidth, blockHeight, dctFormat);
                    return capacityPrepassYPreviousDc != null ? 0 : -1;
                }
            }
            else if (plane == PlaneA)
            {
                if (!IsCapacityPrepassTextureReady(capacityPrepassADct, coefficientWidth, coefficientHeight, dctFormat))
                {
                    capacityPrepassADct = EnsureRuntimeRenderTexture(capacityPrepassADct, "IC_Library_CapacityPrepassADct", coefficientWidth, coefficientHeight, dctFormat);
                    return capacityPrepassADct != null ? 0 : -1;
                }
                if (!IsCapacityPrepassTextureReady(capacityPrepassAPreviousDc, blockWidth, blockHeight, dctFormat))
                {
                    capacityPrepassAPreviousDc = EnsureRuntimeRenderTexture(capacityPrepassAPreviousDc, "IC_Library_CapacityPrepassAPreviousDc", blockWidth, blockHeight, dctFormat);
                    return capacityPrepassAPreviousDc != null ? 0 : -1;
                }
            }
            else if (plane == PlaneCb)
            {
                if (!IsCapacityPrepassTextureReady(capacityPrepassCbDct, coefficientWidth, coefficientHeight, dctFormat))
                {
                    capacityPrepassCbDct = EnsureRuntimeRenderTexture(capacityPrepassCbDct, "IC_Library_CapacityPrepassCbDct", coefficientWidth, coefficientHeight, dctFormat);
                    return capacityPrepassCbDct != null ? 0 : -1;
                }
                if (!IsCapacityPrepassTextureReady(capacityPrepassCbPreviousDc, blockWidth, blockHeight, dctFormat))
                {
                    capacityPrepassCbPreviousDc = EnsureRuntimeRenderTexture(capacityPrepassCbPreviousDc, "IC_Library_CapacityPrepassCbPreviousDc", blockWidth, blockHeight, dctFormat);
                    return capacityPrepassCbPreviousDc != null ? 0 : -1;
                }
            }
            else
            {
                if (!IsCapacityPrepassTextureReady(capacityPrepassCrDct, coefficientWidth, coefficientHeight, dctFormat))
                {
                    capacityPrepassCrDct = EnsureRuntimeRenderTexture(capacityPrepassCrDct, "IC_Library_CapacityPrepassCrDct", coefficientWidth, coefficientHeight, dctFormat);
                    return capacityPrepassCrDct != null ? 0 : -1;
                }
                if (!IsCapacityPrepassTextureReady(capacityPrepassCrPreviousDc, blockWidth, blockHeight, dctFormat))
                {
                    capacityPrepassCrPreviousDc = EnsureRuntimeRenderTexture(capacityPrepassCrPreviousDc, "IC_Library_CapacityPrepassCrPreviousDc", blockWidth, blockHeight, dctFormat);
                    return capacityPrepassCrPreviousDc != null ? 0 : -1;
                }
            }
            return 1;
        }

        // 容量 事前計算 DCT Textureを返す
        private RenderTexture GetCapacityPrepassDctTexture(int plane)
        {
            if (plane == PlaneY) return capacityPrepassYDct;
            if (plane == PlaneA) return capacityPrepassADct;
            if (plane == PlaneCb) return capacityPrepassCbDct;
            return capacityPrepassCrDct;
        }

        // 容量 事前計算 Previous DC Textureを返す
        private RenderTexture GetCapacityPrepassPreviousDcTexture(int plane)
        {
            if (plane == PlaneY) return capacityPrepassYPreviousDc;
            if (plane == PlaneA) return capacityPrepassAPreviousDc;
            if (plane == PlaneCb) return capacityPrepassCbPreviousDc;
            return capacityPrepassCrPreviousDc;
        }

        // 候補評価用RTも必要になったものを1枚ずつ生成し、初回だけの生成負荷を複数フレームへ分散する
        private int EnsureNextCapacityPrepassCandidateTexture(int coefficientWidth, int coefficientHeight, int blockWidth, int blockHeight)
        {
            RenderTextureFormat dctFormat = GetSingleChannelDctRenderTextureFormat();
            if (!IsCapacityPrepassTextureReady(capacityPrepassQuantizedCoefficients, coefficientWidth, coefficientHeight, dctFormat))
            {
                capacityPrepassQuantizedCoefficients = EnsureRuntimeRenderTexture(capacityPrepassQuantizedCoefficients, "IC_Library_CapacityPrepassQuantized", coefficientWidth, coefficientHeight, dctFormat);
                return capacityPrepassQuantizedCoefficients != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassRleSymbols, coefficientWidth, coefficientHeight, RenderTextureFormat.ARGB32))
            {
                capacityPrepassRleSymbols = EnsureRuntimeRenderTexture(capacityPrepassRleSymbols, "IC_Library_CapacityPrepassRle", coefficientWidth, coefficientHeight, RenderTextureFormat.ARGB32);
                return capacityPrepassRleSymbols != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassAcFrequencyRows, AcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32))
            {
                capacityPrepassAcFrequencyRows = EnsureRuntimeRenderTexture(capacityPrepassAcFrequencyRows, "IC_Library_CapacityPrepassAcFrequencyRows", AcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
                return capacityPrepassAcFrequencyRows != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(rleAcFrequencyRowsTemp, AcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32))
            {
                rleAcFrequencyRowsTemp = EnsureRuntimeRenderTexture(rleAcFrequencyRowsTemp, "IC_Library_RleAcFrequencyRowsTemp", AcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
                return rleAcFrequencyRowsTemp != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassAcFrequency, AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32))
            {
                capacityPrepassAcFrequency = EnsureRuntimeRenderTexture(capacityPrepassAcFrequency, "IC_Library_CapacityPrepassAcFrequency", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                return capacityPrepassAcFrequency != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassDcDelta, blockWidth, blockHeight, RenderTextureFormat.ARGB32))
            {
                capacityPrepassDcDelta = EnsureRuntimeRenderTexture(capacityPrepassDcDelta, "IC_Library_CapacityPrepassDcDelta", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
                return capacityPrepassDcDelta != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassDcFrequencyRows, DcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32))
            {
                capacityPrepassDcFrequencyRows = EnsureRuntimeRenderTexture(capacityPrepassDcFrequencyRows, "IC_Library_CapacityPrepassDcFrequencyRows", DcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
                return capacityPrepassDcFrequencyRows != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassDcFrequency, DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32))
            {
                capacityPrepassDcFrequency = EnsureRuntimeRenderTexture(capacityPrepassDcFrequency, "IC_Library_CapacityPrepassDcFrequency", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                return capacityPrepassDcFrequency != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(huffmanRawLengths, AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32))
            {
                huffmanRawLengths = EnsureRuntimeRenderTexture(huffmanRawLengths, "IC_Library_HuffmanRawLengths", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                return huffmanRawLengths != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(huffmanRawLengthSingle, HuffmanSingleValueTextureSize, HuffmanSingleValueTextureSize, RenderTextureFormat.ARGB32))
            {
                huffmanRawLengthSingle = EnsureRuntimeRenderTexture(huffmanRawLengthSingle, "IC_Library_HuffmanRawLengthSingle", HuffmanSingleValueTextureSize, HuffmanSingleValueTextureSize, RenderTextureFormat.ARGB32);
                return huffmanRawLengthSingle != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(huffmanRawLengthHistogram, 256, 1, RenderTextureFormat.ARGB32))
            {
                huffmanRawLengthHistogram = EnsureRuntimeRenderTexture(huffmanRawLengthHistogram, "IC_Library_HuffmanRawLengthHistogram", 256, 1, RenderTextureFormat.ARGB32);
                return huffmanRawLengthHistogram != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(huffmanRawLengthHistogramTemp, 256, 1, RenderTextureFormat.ARGB32))
            {
                huffmanRawLengthHistogramTemp = EnsureRuntimeRenderTexture(huffmanRawLengthHistogramTemp, "IC_Library_HuffmanRawLengthHistogramTemp", 256, 1, RenderTextureFormat.ARGB32);
                return huffmanRawLengthHistogramTemp != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(huffmanLimitedLengthHistogram, 16, 1, RenderTextureFormat.ARGB32))
            {
                huffmanLimitedLengthHistogram = EnsureRuntimeRenderTexture(huffmanLimitedLengthHistogram, "IC_Library_HuffmanLimitedLengthHistogram", 16, 1, RenderTextureFormat.ARGB32);
                return huffmanLimitedLengthHistogram != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(huffmanLimitedLengthHistogramTemp, HuffmanLengthLimitCount, 1, RenderTextureFormat.ARGB32))
            {
                huffmanLimitedLengthHistogramTemp = EnsureRuntimeRenderTexture(huffmanLimitedLengthHistogramTemp, "IC_Library_HuffmanLimitedLengthHistogramTemp", HuffmanLengthLimitCount, 1, RenderTextureFormat.ARGB32);
                return huffmanLimitedLengthHistogramTemp != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassDcHuffmanLengths, DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32))
            {
                capacityPrepassDcHuffmanLengths = EnsureRuntimeRenderTexture(capacityPrepassDcHuffmanLengths, "IC_Library_CapacityPrepassDcLengths", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                return capacityPrepassDcHuffmanLengths != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassAcHuffmanLengths, AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32))
            {
                capacityPrepassAcHuffmanLengths = EnsureRuntimeRenderTexture(capacityPrepassAcHuffmanLengths, "IC_Library_CapacityPrepassAcLengths", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                return capacityPrepassAcHuffmanLengths != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(acHuffmanCodes, AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32))
            {
                acHuffmanCodes = EnsureRuntimeRenderTexture(acHuffmanCodes, "IC_Library_AcHuffmanCodes", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                return acHuffmanCodes != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(capacityPrepassBlockBits, blockWidth, blockHeight, RenderTextureFormat.ARGB32))
            {
                capacityPrepassBlockBits = EnsureRuntimeRenderTexture(capacityPrepassBlockBits, "IC_Library_CapacityPrepassBlockBits", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
                return capacityPrepassBlockBits != null ? 0 : -1;
            }
            if (!IsCapacityPrepassTextureReady(huffmanBlockPageState, blockWidth, blockHeight, RenderTextureFormat.ARGB32))
            {
                huffmanBlockPageState = EnsureRuntimeRenderTexture(huffmanBlockPageState, "IC_Library_HuffmanBlockPageState", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
                return huffmanBlockPageState != null ? 0 : -1;
            }
            return 1;
        }

        // 容量 事前計算 リソースを解放する
        private void ReleaseCapacityPrepassResources()
        {
            capacityPrepassReadbackRequestQueued = false;
            capacityPrepassReadbackPending = false;
            capacityPrepassReadbackDrainFramesRemaining = 0;
            capacityPrepassBlockMapTexture = ReleaseRuntimeTexture(capacityPrepassBlockMapTexture);
            capacityPrepassWork = ReleaseRuntimeRenderTexture(capacityPrepassWork);
            capacityPrepassPreviousDctWork = ReleaseRuntimeRenderTexture(capacityPrepassPreviousDctWork);
            capacityPrepassYDct = ReleaseRuntimeRenderTexture(capacityPrepassYDct);
            capacityPrepassADct = ReleaseRuntimeRenderTexture(capacityPrepassADct);
            capacityPrepassCbDct = ReleaseRuntimeRenderTexture(capacityPrepassCbDct);
            capacityPrepassCrDct = ReleaseRuntimeRenderTexture(capacityPrepassCrDct);
            capacityPrepassYPreviousDc = ReleaseRuntimeRenderTexture(capacityPrepassYPreviousDc);
            capacityPrepassAPreviousDc = ReleaseRuntimeRenderTexture(capacityPrepassAPreviousDc);
            capacityPrepassCbPreviousDc = ReleaseRuntimeRenderTexture(capacityPrepassCbPreviousDc);
            capacityPrepassCrPreviousDc = ReleaseRuntimeRenderTexture(capacityPrepassCrPreviousDc);
            capacityPrepassQuantizedCoefficients = ReleaseRuntimeRenderTexture(capacityPrepassQuantizedCoefficients);
            capacityPrepassRleSymbols = ReleaseRuntimeRenderTexture(capacityPrepassRleSymbols);
            capacityPrepassAcFrequencyRows = ReleaseRuntimeRenderTexture(capacityPrepassAcFrequencyRows);
            capacityPrepassAcFrequency = ReleaseRuntimeRenderTexture(capacityPrepassAcFrequency);
            capacityPrepassDcDelta = ReleaseRuntimeRenderTexture(capacityPrepassDcDelta);
            capacityPrepassDcFrequencyRows = ReleaseRuntimeRenderTexture(capacityPrepassDcFrequencyRows);
            capacityPrepassDcFrequency = ReleaseRuntimeRenderTexture(capacityPrepassDcFrequency);
            huffmanRawLengths = ReleaseRuntimeRenderTexture(huffmanRawLengths);
            huffmanRawLengthSingle = ReleaseRuntimeRenderTexture(huffmanRawLengthSingle);
            huffmanRawLengthHistogram = ReleaseRuntimeRenderTexture(huffmanRawLengthHistogram);
            huffmanRawLengthHistogramTemp = ReleaseRuntimeRenderTexture(huffmanRawLengthHistogramTemp);
            huffmanLimitedLengthHistogram = ReleaseRuntimeRenderTexture(huffmanLimitedLengthHistogram);
            huffmanLimitedLengthHistogramTemp = ReleaseRuntimeRenderTexture(huffmanLimitedLengthHistogramTemp);
            capacityPrepassDcHuffmanLengths = ReleaseRuntimeRenderTexture(capacityPrepassDcHuffmanLengths);
            capacityPrepassAcHuffmanLengths = ReleaseRuntimeRenderTexture(capacityPrepassAcHuffmanLengths);
            capacityPrepassBlockBits = ReleaseRuntimeRenderTexture(capacityPrepassBlockBits);
            capacityPrepassStats = ReleaseRuntimeRenderTexture(capacityPrepassStats);
            capacityPrepassStatsTemp = ReleaseRuntimeRenderTexture(capacityPrepassStatsTemp);
        }

        // Add Huffman 段階 Timingを処理する
        private void AddHuffmanStageTiming(int offset, float elapsed, int plane, string label)
        {
            if (!EnableTimingDiagnostics)
            {
                return;
            }

            AddTimingMillis(offset, elapsed);
            lastHuffmanEncodeMs += elapsed;
            LogTiming("huffman encode " + label + " plane=" + GetPlaneName(plane)
                + " ms=" + FormatTimingMs(elapsed)
                + " totalHuffmanEncodeMs=" + FormatTimingMs(lastHuffmanEncodeMs));
        }

        // plane Huffman Prepare 段階を実行する
        private bool RunPlaneHuffmanPrepareStage(int plane)
        {
            stage1PlaneMode = plane;
            float startedAt = GetTimingStart();
            EnsureHuffmanRenderTextures(plane);
            if (!ValidateHuffmanEncode())
            {
                FailHuffmanEncode("Huffman encode skipped: missing reference.");
                return false;
            }

            if (EnableTimingDiagnostics)
            {
                AddHuffmanStageTiming(TimingOffsetHuffmanPrepareMs, ElapsedMs(startedAt), plane, "prepare");
            }
            return !huffmanEncodeFailed;
        }

        // plane Huffman RLE 段階を実行する
        private bool RunPlaneHuffmanRleStage(int plane)
        {
            stage1PlaneMode = plane;
            float startedAt = GetTimingStart();
            SetupCoeffToRleSymbolsMaterial(coeffToRleSymbolsMaterial);
            if (encodeSubStage <= 0)
            {
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", EnsureNeutralGrayTexture());
                TrackedBlit(coefficients, work, coeffToRleSymbolsMaterial, CoeffToRlePassBuildPrefixBase);
                encodeSubStage = 1;
                return false;
            }

            if (encodeSubStage <= CoeffToRlePrefixScanPassCount)
            {
                int scanIndex = encodeSubStage - 1;
                RenderTexture previous = (scanIndex & 1) == 0 ? work : rleSymbols;
                RenderTexture target = (scanIndex & 1) == 0 ? rleSymbols : work;
                coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", previous);
                coeffToRleSymbolsMaterial.SetFloat("_PrefixStep", 1 << scanIndex);
                TrackedBlit(previous, target, coeffToRleSymbolsMaterial, CoeffToRlePassScanPrefix);
                encodeSubStage++;
                return false;
            }

            coeffToRleSymbolsMaterial.SetTexture("_PrefixTex", work);
            TrackedBlit(coefficients, rleSymbols, coeffToRleSymbolsMaterial, CoeffToRlePassBuildSymbols);
            if (inspectionStopStep == 3)
            {
                float stopElapsed = ElapsedMs(startedAt);
                AddHuffmanStageTiming(TimingOffsetHuffmanRleMs, stopElapsed, plane, "rle");
                isRunning = false;
                huffmanReadbackPending = false;
                SetStatus("Inspection stopped after CoeffToRleSymbols. plane=" + GetPlaneName(plane)
                    + " pass=CoefficientToRleSymbols ms=" + FormatTimingMs(stopElapsed));
                return false;
            }

            if (EnableTimingDiagnostics)
            {
                AddHuffmanStageTiming(TimingOffsetHuffmanRleMs, ElapsedMs(startedAt), plane, "rle");
            }
            encodeSubStage = 0;
            return true;
        }

        // plane Huffman AC 頻度 段階を実行する
        private bool RunPlaneHuffmanAcFrequencyStage(int plane)
        {
            stage1PlaneMode = plane;
            float startedAt = GetTimingStart();
            int blockWidth = GetBlockWidth(plane);
            int columnGroupCount = Mathf.Max((blockWidth + EncodeAcFrequencyBlockColumnsPerGroup - 1) / EncodeAcFrequencyBlockColumnsPerGroup, 1);
            int frequencyStepCount = columnGroupCount * EncodeAcFrequencySlotGroupCount;
            // AC frequencyは全係数slotを走査する。row histogramと全体reductionを別eventへ分け、
            // Mobile GPUへ同一frameで両方を要求しない
            if (encodeSubStage < frequencyStepCount)
            {
                int step = Mathf.Max(encodeSubStage, 0);
                int columnGroup = step / EncodeAcFrequencySlotGroupCount;
                int slotGroup = step - columnGroup * EncodeAcFrequencySlotGroupCount;
                int blockColumnStart = columnGroup * EncodeAcFrequencyBlockColumnsPerGroup;
                int blockColumnCount = Mathf.Min(EncodeAcFrequencyBlockColumnsPerGroup, blockWidth - blockColumnStart);
                int slotStart = 1 + slotGroup * EncodeAcFrequencySlotsPerGroup;
                int slotCount = Mathf.Min(EncodeAcFrequencySlotsPerGroup, 64 - slotStart);
                RenderTexture previousRows = step <= 0 ? null : ((step & 1) == 0 ? rleAcFrequencyRowsTemp : rleAcFrequencyRows);
                RenderTexture targetRows = (step & 1) == 0 ? rleAcFrequencyRows : rleAcFrequencyRowsTemp;
                SetupRleAcFrequencyRowGroupMaterial(
                    rleAcFrequencyMaterial,
                    previousRows,
                    blockColumnStart,
                    blockColumnCount,
                    slotStart,
                    slotCount,
                    step > 0);
                TrackedBlit(rleSymbols, targetRows, rleAcFrequencyMaterial, 0);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanAcFrequencyMs, ElapsedMs(startedAt), plane, "ac frequency rows");
                }
                encodeSubStage = step + 1;
                return false;
            }

            RenderTexture completedRows = ((frequencyStepCount - 1) & 1) == 0 ? rleAcFrequencyRows : rleAcFrequencyRowsTemp;
            SetupRleAcFrequencyMaterial(rleAcFrequencyMaterial);
            TrackedBlit(completedRows, rleAcFrequency, rleAcFrequencyMaterial, 1);
            if (inspectionStopStep == 4)
            {
                float stopElapsed = ElapsedMs(startedAt);
                AddHuffmanStageTiming(TimingOffsetHuffmanAcFrequencyMs, stopElapsed, plane, "ac frequency");
                isRunning = false;
                huffmanReadbackPending = false;
                SetStatus("Inspection stopped after RleAcFrequency. plane=" + GetPlaneName(plane)
                    + " pass=RleAcFrequencyRows+RleAcFrequencyTotal ms=" + FormatTimingMs(stopElapsed));
                return false;
            }

            if (EnableTimingDiagnostics)
            {
                AddHuffmanStageTiming(TimingOffsetHuffmanAcFrequencyMs, ElapsedMs(startedAt), plane, "ac frequency");
            }
            encodeSubStage = 0;
            return true;
        }

        // plane Huffman DC 頻度 段階を実行する
        private bool RunPlaneHuffmanDcFrequencyStage(int plane)
        {
            stage1PlaneMode = plane;
            float startedAt = GetTimingStart();
            int blockWidth = GetBlockWidth(plane);
            int blockHeight = GetBlockHeight(plane);
            int rowGroupCount = Mathf.Max((blockWidth + EncodeDcFrequencyItemsPerGroup - 1) / EncodeDcFrequencyItemsPerGroup, 1);
            int totalGroupCount = Mathf.Max((blockHeight + EncodeDcFrequencyItemsPerGroup - 1) / EncodeDcFrequencyItemsPerGroup, 1);
            int rowBaseSubStage = 2;
            int totalBaseSubStage = rowBaseSubStage + rowGroupCount;
            int completeSubStage = totalBaseSubStage + totalGroupCount;
            // DCは値抽出→delta encode→row histogram→全体histogramの4依存pass
            // Android/Questでencode stageが一度にhitchしないよう個別eventにする
            if (encodeSubStage <= 0)
            {
                SetupDcDeltaMaterial(dcDeltaMaterial);
                TrackedBlit(coefficients, dcValues, dcDeltaMaterial, 0);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanDcFrequencyMs, ElapsedMs(startedAt), plane, "dc values");
                }
                encodeSubStage = 1;
                return false;
            }

            if (encodeSubStage == 1)
            {
                SetupDcDeltaMaterial(dcDeltaMaterial);
                TrackedBlit(dcValues, dcDelta, dcDeltaMaterial, 1);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanDcFrequencyMs, ElapsedMs(startedAt), plane, "dc delta");
                }
                encodeSubStage = 2;
                return false;
            }

            if (encodeSubStage >= rowBaseSubStage && encodeSubStage < totalBaseSubStage)
            {
                int group = encodeSubStage - rowBaseSubStage;
                bool targetIsMain = (group & 1) == ((rowGroupCount - 1) & 1);
                RenderTexture previous = group <= 0 ? null : (targetIsMain ? dcFrequencyRowsTemp : dcFrequencyRows);
                RenderTexture target = targetIsMain ? dcFrequencyRows : dcFrequencyRowsTemp;
                SetupDcFrequencyGroupMaterial(
                    dcFrequencyMaterial,
                    previous,
                    group * EncodeDcFrequencyItemsPerGroup,
                    Mathf.Min(EncodeDcFrequencyItemsPerGroup, blockWidth - group * EncodeDcFrequencyItemsPerGroup),
                    group > 0);
                TrackedBlit(dcDelta, target, dcFrequencyMaterial, 0);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanDcFrequencyMs, ElapsedMs(startedAt), plane, "dc frequency rows");
                }
                encodeSubStage++;
                return false;
            }

            if (encodeSubStage >= totalBaseSubStage && encodeSubStage < completeSubStage)
            {
                int group = encodeSubStage - totalBaseSubStage;
                bool targetIsMain = (group & 1) == ((totalGroupCount - 1) & 1);
                RenderTexture previous = group <= 0 ? null : (targetIsMain ? dcHuffmanLengths : dcFrequency);
                RenderTexture target = targetIsMain ? dcFrequency : dcHuffmanLengths;
                SetupDcFrequencyGroupMaterial(
                    dcFrequencyMaterial,
                    previous,
                    group * EncodeDcFrequencyItemsPerGroup,
                    Mathf.Min(EncodeDcFrequencyItemsPerGroup, blockHeight - group * EncodeDcFrequencyItemsPerGroup),
                    group > 0);
                TrackedBlit(dcFrequencyRows, target, dcFrequencyMaterial, 1);
                encodeSubStage++;
                return false;
            }

            if (inspectionStopStep == 5)
            {
                float stopElapsed = ElapsedMs(startedAt);
                AddHuffmanStageTiming(TimingOffsetHuffmanDcFrequencyMs, stopElapsed, plane, "dc delta/frequency");
                isRunning = false;
                huffmanReadbackPending = false;
                SetStatus("Inspection stopped after DcFrequency. plane=" + GetPlaneName(plane)
                    + " pass=DcDelta+DcFrequency ms=" + FormatTimingMs(stopElapsed));
                return false;
            }

            if (EnableTimingDiagnostics)
            {
                AddHuffmanStageTiming(TimingOffsetHuffmanDcFrequencyMs, ElapsedMs(startedAt), plane, "dc delta/frequency");
            }
            encodeSubStage = 0;
            return true;
        }

        // plane Huffman table And bit 数 段階を実行する
        private bool RunPlaneHuffmanTableAndBitCountStage(int plane)
        {
            stage1PlaneMode = plane;
            float startedAt;
            // Huffman table生成はRTが小さくてもcanonical code loopが重い
            // DC length/code、AC length/code、bit count、overflow mipを分割し、Questの1 stepへ集中させない
            if (encodeSubStage <= 0)
            {
                EnsureHuffmanLengthHistogramTextures();
                // DC/ACを共通CPU経路に統一し、symbol数が少ないDCにも旧GPU tree-buildのdriverリスクを残さない
                int cpuState = RunCpuHuffmanRawLengthStep(dcFrequency, DcFrequencyBinCount);
                if (cpuState < 0)
                {
                    FailHuffmanEncode("DC Huffman tree readback or build failed.");
                    return false;
                }

                if (cpuState > 0)
                {
                    encodeSubStage = HuffmanTableDcRawHistogramBaseSubStage;
                }
                return false;
            }

            if (encodeSubStage >= HuffmanTableDcRawHistogramBaseSubStage
                && encodeSubStage < HuffmanTableDcLimitedHistogramBaseSubStage)
            {
                startedAt = GetTimingStart();
                int group = encodeSubStage - HuffmanTableDcRawHistogramBaseSubStage;
                int lengthStart = group * HuffmanRawHistogramLengthsPerGroup;
                RenderTexture previous = group <= 0 ? null : ((group & 1) == 0 ? huffmanRawLengthHistogram : huffmanRawLengthHistogramTemp);
                RenderTexture target = (group & 1) == 0 ? huffmanRawLengthHistogramTemp : huffmanRawLengthHistogram;
                SetupHuffmanRawLengthHistogramRangeMaterial(
                    huffmanTableMaterial,
                    dcFrequency,
                    DcFrequencyBinCount,
                    previous,
                    lengthStart,
                    group > 0);
                TrackedBlit(huffmanRawLengths, target, huffmanTableMaterial, HuffmanTablePassBuildRawLengthHistogram);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanTableMs, ElapsedMs(startedAt), plane, "dc table raw histogram group");
                }
                encodeSubStage++;
                return false;
            }

            if (encodeSubStage >= HuffmanTableDcLimitedHistogramBaseSubStage
                && encodeSubStage < HuffmanTableDcAssignLengthsSubStage)
            {
                startedAt = GetTimingStart();
                int lengthIndex = encodeSubStage - HuffmanTableDcLimitedHistogramBaseSubStage;
                SetupHuffmanSingleLimitedHistogramMaterial(huffmanTableMaterial, dcFrequency, DcFrequencyBinCount, lengthIndex);
                TrackedBlit(huffmanRawLengthHistogram, huffmanRawLengthSingle, huffmanTableMaterial, HuffmanTablePassLimitLengthHistogram);
                RenderTexture previous = lengthIndex <= 0 ? null : ((lengthIndex & 1) == 0 ? huffmanLimitedLengthHistogram : huffmanLimitedLengthHistogramTemp);
                RenderTexture target = (lengthIndex & 1) == 0 ? huffmanLimitedLengthHistogramTemp : huffmanLimitedLengthHistogram;
                SetupHuffmanSingleValueMergeMaterial(huffmanTableMaterial, previous, HuffmanLengthLimitCount, lengthIndex, lengthIndex > 0);
                TrackedBlit(huffmanRawLengthSingle, target, huffmanTableMaterial, HuffmanTablePassMergeSingleValue);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanTableMs, ElapsedMs(startedAt), plane, "dc table limited histogram single+merge");
                }
                encodeSubStage++;
                return false;
            }

            if (encodeSubStage == HuffmanTableDcAssignLengthsSubStage)
            {
                startedAt = GetTimingStart();
                SetupHuffmanLengthLimitMaterial(huffmanTableMaterial, dcFrequency, DcFrequencyBinCount);
                TrackedBlit(huffmanRawLengths, dcHuffmanLengths, huffmanTableMaterial, HuffmanTablePassLimitLengths);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanTableMs, ElapsedMs(startedAt), plane, "dc table limited lengths");
                }
                encodeSubStage = HuffmanTableDcCanonicalSubStage;
                return false;
            }

            if (encodeSubStage == HuffmanTableDcCanonicalSubStage)
            {
                startedAt = GetTimingStart();
                SetupHuffmanTableMaterial(huffmanTableMaterial, DcFrequencyBinCount);
                TrackedBlit(dcHuffmanLengths, dcHuffmanCodes, huffmanTableMaterial, HuffmanTablePassBuildCanonicalCodes);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanTableMs, ElapsedMs(startedAt), plane, "dc table codes");
                }
                encodeSubStage = HuffmanTableAcRawBaseSubStage;
                return false;
            }

            if (encodeSubStage >= HuffmanTableAcRawBaseSubStage && encodeSubStage < HuffmanTableAcRawHistogramBaseSubStage)
            {
                // 実圧縮のACも容量推定と同じCPU treeへ通し、初回だけ別の危険なGPU経路へ入る差を作らない
                int cpuState = RunCpuHuffmanRawLengthStep(rleAcFrequency, AcFrequencyBinCount);
                if (cpuState < 0)
                {
                    FailHuffmanEncode("AC Huffman tree readback or build failed.");
                    return false;
                }

                encodeSubStage = cpuState > 0
                    ? HuffmanTableAcRawHistogramBaseSubStage
                    : GetCpuHuffmanProgressSubStage(
                        HuffmanTableAcRawBaseSubStage,
                        HuffmanTableAcRawHistogramBaseSubStage);
                return false;
            }

            if (encodeSubStage >= HuffmanTableAcLimitBaseSubStage && encodeSubStage < HuffmanTableAcCanonicalBaseSubStage)
            {
                startedAt = GetTimingStart();
                int group = encodeSubStage - HuffmanTableAcLimitBaseSubStage;
                int symbolStart = group * HuffmanTableAcSymbolsPerGroup;
                RenderTexture previous = group <= 0 ? null : ((group & 1) == 0 ? acHuffmanLengths : acHuffmanCodes);
                RenderTexture target = (group & 1) == 0 ? acHuffmanCodes : acHuffmanLengths;
                SetupHuffmanLengthLimitMaterial(huffmanTableMaterial, rleAcFrequency, AcFrequencyBinCount);
                SetupHuffmanTableRangeMaterial(huffmanTableMaterial, previous, symbolStart, HuffmanTableAcSymbolsPerGroup, group > 0);
                TrackedBlit(huffmanRawLengths, target, huffmanTableMaterial, HuffmanTablePassLimitLengths);
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanTableMs, ElapsedMs(startedAt), plane, "ac table limited lengths group");
                }
                encodeSubStage++;
                if (group + 1 >= HuffmanTableAcSymbolGroupCount)
                {
                    huffmanRawLengthHistogram = ReleaseRuntimeRenderTexture(huffmanRawLengthHistogram);
                    huffmanRawLengthHistogramTemp = ReleaseRuntimeRenderTexture(huffmanRawLengthHistogramTemp);
                    huffmanLimitedLengthHistogram = ReleaseRuntimeRenderTexture(huffmanLimitedLengthHistogram);
                    huffmanLimitedLengthHistogramTemp = ReleaseRuntimeRenderTexture(huffmanLimitedLengthHistogramTemp);
                }
                return false;
            }

            if (encodeSubStage >= HuffmanTableAcRawHistogramBaseSubStage
                && encodeSubStage < HuffmanTableAcLimitedHistogramBaseSubStage)
            {
                startedAt = GetTimingStart();
                int group = encodeSubStage - HuffmanTableAcRawHistogramBaseSubStage;
                int lengthStart = group * HuffmanRawHistogramLengthsPerGroup;
                RenderTexture previous = group <= 0 ? null : ((group & 1) == 0 ? huffmanRawLengthHistogram : huffmanRawLengthHistogramTemp);
                RenderTexture target = (group & 1) == 0 ? huffmanRawLengthHistogramTemp : huffmanRawLengthHistogram;
                SetupHuffmanRawLengthHistogramRangeMaterial(
                    huffmanTableMaterial,
                    rleAcFrequency,
                    AcFrequencyBinCount,
                    previous,
                    lengthStart,
                    group > 0);
                TrackedBlit(huffmanRawLengths, target, huffmanTableMaterial, HuffmanTablePassBuildRawLengthHistogram);
                encodeSubStage++;
                return false;
            }

            if (encodeSubStage >= HuffmanTableAcLimitedHistogramBaseSubStage
                && encodeSubStage < HuffmanTableAcLimitBaseSubStage)
            {
                startedAt = GetTimingStart();
                int lengthIndex = encodeSubStage - HuffmanTableAcLimitedHistogramBaseSubStage;
                SetupHuffmanSingleLimitedHistogramMaterial(huffmanTableMaterial, rleAcFrequency, AcFrequencyBinCount, lengthIndex);
                TrackedBlit(huffmanRawLengthHistogram, huffmanRawLengthSingle, huffmanTableMaterial, HuffmanTablePassLimitLengthHistogram);
                RenderTexture previous = lengthIndex <= 0 ? null : ((lengthIndex & 1) == 0 ? huffmanLimitedLengthHistogram : huffmanLimitedLengthHistogramTemp);
                RenderTexture target = (lengthIndex & 1) == 0 ? huffmanLimitedLengthHistogramTemp : huffmanLimitedLengthHistogram;
                SetupHuffmanSingleValueMergeMaterial(huffmanTableMaterial, previous, HuffmanLengthLimitCount, lengthIndex, lengthIndex > 0);
                TrackedBlit(huffmanRawLengthSingle, target, huffmanTableMaterial, HuffmanTablePassMergeSingleValue);
                encodeSubStage++;
                return false;
            }

            if (encodeSubStage >= HuffmanTableAcCanonicalBaseSubStage && encodeSubStage < HuffmanBitCountBaseSubStage)
            {
                startedAt = GetTimingStart();
                int group = encodeSubStage - HuffmanTableAcCanonicalBaseSubStage;
                int symbolStart = group * HuffmanTableAcSymbolsPerGroup;
                RenderTexture previous = group <= 0 ? null : ((group & 1) == 0 ? acHuffmanCodes : huffmanRawLengths);
                RenderTexture target = (group & 1) == 0 ? huffmanRawLengths : acHuffmanCodes;
                SetupHuffmanTableMaterial(huffmanTableMaterial, AcFrequencyBinCount);
                SetupHuffmanTableRangeMaterial(huffmanTableMaterial, previous, symbolStart, HuffmanTableAcSymbolsPerGroup, group > 0);
                TrackedBlit(acHuffmanLengths, target, huffmanTableMaterial, HuffmanTablePassBuildCanonicalCodes);
                float tableElapsed = EnableTimingDiagnostics || (inspectionStopStep == 6 && group + 1 >= HuffmanTableAcSymbolGroupCount)
                    ? ElapsedMs(startedAt)
                    : 0f;
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanTableMs, tableElapsed, plane, "ac table codes group");
                }
                encodeSubStage++;
                if (group + 1 >= HuffmanTableAcSymbolGroupCount && inspectionStopStep == 6)
                {
                    isRunning = false;
                    huffmanReadbackPending = false;
                    SetStatus("Inspection stopped after HuffmanTable. plane=" + GetPlaneName(plane)
                        + " pass=CanonicalTableDcAc ms=" + FormatTimingMs(tableElapsed));
                    return false;
                }

                return false;
            }

            if (encodeSubStage >= HuffmanBitCountBaseSubStage && encodeSubStage < HuffmanOverflowBaseSubStage)
            {
                startedAt = GetTimingStart();
                SetupHuffmanBitCountMaterial(huffmanBitCountMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes);
                int bitCountStep = encodeSubStage - HuffmanBitCountBaseSubStage;
                if (bitCountStep == 0)
                {
                    TrackedBlit(rleSymbols, huffmanBlockBits, huffmanBitCountMaterial, HuffmanBitCountPassInitialize);
                }
                else
                {
                    int slotGroup = bitCountStep - 1;
                    RenderTexture previous = IsDecodeSlotGroupStepSourceMain(slotGroup) ? huffmanBlockBits : huffmanBlockPageState;
                    RenderTexture target = IsDecodeSlotGroupStepSourceMain(slotGroup) ? huffmanBlockPageState : huffmanBlockBits;
                    huffmanBitCountMaterial.SetTexture("_PreviousBitCountTex", previous);
                    huffmanBitCountMaterial.SetFloat("_BitCountSlotGroup", slotGroup);
                    TrackedBlit(rleSymbols, target, huffmanBitCountMaterial, HuffmanBitCountPassAccumulate);
                }

                encodeSubStage++;
                if (EnableTimingDiagnostics && encodeSubStage >= HuffmanOverflowBaseSubStage)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanBitCountMs, ElapsedMs(startedAt), plane, "bit count");
                }
                return false;
            }

            if (RunHuffmanBlockOverflowManualMipStep(plane))
            {
                if (inspectionStopStep == 7)
                {
                    encodeSubStage = 0;
                    isRunning = false;
                    huffmanReadbackPending = false;
                    SetStatus("Inspection stopped after HuffmanBitCount. plane=" + GetPlaneName(plane)
                        + " pass=BitCount+OverflowMask+OverflowMip");
                    return false;
                }

                encodeSubStage = 0;
                return true;
            }

            return false;
        }

        // plane Huffman ブロック And chunk 段階を実行する
        private bool RunPlaneHuffmanBlockAndChunkStage(int plane)
        {
            stage1PlaneMode = plane;
            float startedAt = GetTimingStart();
            int blockPageStepCount = GetEncodeBlockPageStepCount();
            if (encodeSubStage < blockPageStepCount)
            {
                if (encodeSubStage < 0)
                {
                    encodeSubStage = 0;
                }

                if (encodeSubStage == 0)
                {
                    encodeBlockPageStatePing = 0;
                    SetupHuffmanBlockPageEncodeMaterial(huffmanBlockPageEncodeMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes, huffmanBlockPage, huffmanBlockPageStateTemp, 0, -1);
                    TrackedBlit(rleSymbols, huffmanBlockPageState, huffmanBlockPageEncodeMaterial, HuffmanBlockPagePassInitializeState);
                }
                else
                {
                    int workIndex = encodeSubStage - 1;
                    int fullGroupWorkCount = Mathf.Max(EncodeBlockPageSlotGroupCount - 1, 0) * 2;
                    int slotGroup;
                    bool advanceState;
                    if (workIndex < fullGroupWorkCount)
                    {
                        slotGroup = workIndex / 2;
                        advanceState = workIndex - slotGroup * 2 != 0;
                    }
                    else
                    {
                        slotGroup = EncodeBlockPageSlotGroupCount - 1;
                        advanceState = false;
                    }

                    if (advanceState)
                    {
                        RenderTexture currentState = GetCurrentHuffmanBlockPageStateTexture();
                        RenderTexture nextState = GetNextHuffmanBlockPageStateTexture();
                        SetupHuffmanBlockPageEncodeMaterial(huffmanBlockPageEncodeMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes, huffmanBlockPage, currentState, slotGroup, -1);
                        TrackedBlit(rleSymbols, nextState, huffmanBlockPageEncodeMaterial, HuffmanBlockPagePassAdvanceState);
                        encodeBlockPageStatePing = encodeBlockPageStatePing == 0 ? 1 : 0;
                    }
                    else
                    {
                        RenderTexture previous = IsDecodeSlotGroupStepSourceMain(slotGroup) ? huffmanBlockPage : huffmanBlockPageTemp;
                        RenderTexture target = IsDecodeSlotGroupStepSourceMain(slotGroup) ? huffmanBlockPageTemp : huffmanBlockPage;
                        SetupHuffmanBlockPageEncodeMaterial(huffmanBlockPageEncodeMaterial, dcDelta, dcHuffmanCodes, acHuffmanCodes, previous, GetCurrentHuffmanBlockPageStateTexture(), slotGroup, -1);
                        TrackedBlit(rleSymbols, target, huffmanBlockPageEncodeMaterial, HuffmanBlockPagePassOutputGroup);
                    }
                }

                encodeSubStage++;
                return false;
            }

            if (encodeSubStage == blockPageStepCount)
            {
                SetupHuffmanBlockValidBytesMaterial(huffmanBlockValidBytesMaterial);
                TrackedBlit(huffmanBlockBits, huffmanBlockValidBytes, huffmanBlockValidBytesMaterial, 0);
                float blockPageElapsed = EnableTimingDiagnostics || inspectionStopStep == 8 ? ElapsedMs(startedAt) : 0f;
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanBlockPageMs, blockPageElapsed, plane, "block page/valid");
                }
                if (inspectionStopStep == 8)
                {
                    encodeSubStage = 0;
                    isRunning = false;
                    huffmanReadbackPending = false;
                    SetStatus("Inspection stopped after HuffmanBlockPage. plane=" + GetPlaneName(plane)
                        + " pass=BlockPage+BlockValidBytes ms=" + FormatTimingMs(blockPageElapsed));
                    return false;
                }

                encodeSubStage = blockPageStepCount + 1;
                return false;
            }

            if (encodeSubStage == blockPageStepCount + 1)
            {
                SetupHuffmanChunkValidBytesBuildMaterial(huffmanChunkValidBytesMaterial);
                TrackedBlit(huffmanBlockBits, huffmanChunkValidBytes, huffmanChunkValidBytesMaterial, 0);
                encodeSubStage = blockPageStepCount + 2;
                return false;
            }

            if (RunHuffmanChunkValidManualMipStep(plane, blockPageStepCount + 2))
            {
                float chunkValidElapsed = EnableTimingDiagnostics || inspectionStopStep == 9 ? ElapsedMs(startedAt) : 0f;
                if (EnableTimingDiagnostics)
                {
                    AddHuffmanStageTiming(TimingOffsetHuffmanChunkValidMs, chunkValidElapsed, plane, "chunk valid/mip");
                }
                if (inspectionStopStep == 9)
                {
                    encodeSubStage = 0;
                    isRunning = false;
                    huffmanReadbackPending = false;
                    SetStatus("Inspection stopped after HuffmanChunkValid. plane=" + GetPlaneName(plane)
                        + " pass=ChunkValidBytes+ChunkValidMip ms=" + FormatTimingMs(chunkValidElapsed));
                    return false;
                }

                encodeSubStage = 0;
                return true;
            }

            return false;
        }

        // Huffman ブロック Overflow Manual Mip stepを実行する
        private bool RunHuffmanBlockOverflowManualMipStep(int plane)
        {
            // overflow検証ではRenderTexture.GenerateMipsを使わない。必要なのは
            // quality予測に使う最大block bytesであり、自動mipはMobile stall要因かつfloat平均になる
            // manual atlas上のUInt24整数maxとして明示計算する
            int mipCount = GetBlockMipCount(plane);
            if (encodeSubStage == HuffmanOverflowBaseSubStage)
            {
                SetupHuffmanBlockOverflowMaterial(huffmanBlockOverflowMaterial);
                TrackedBlit(huffmanBlockBits, huffmanBlockOverflowMask, huffmanBlockOverflowMaterial, 0);
                encodeSubStage = HuffmanOverflowBaseSubStage + 1;
                return mipCount <= 1;
            }

            int level = encodeSubStage - HuffmanOverflowBaseSubStage;
            if (level < mipCount)
            {
                RenderTexture source = (level & 1) == 1 ? huffmanBlockOverflowMask : huffmanBlockOverflowMaskTemp;
                RenderTexture target = (level & 1) == 1 ? huffmanBlockOverflowMaskTemp : huffmanBlockOverflowMask;
                SetupHuffmanBlockOverflowManualMipMaterial(huffmanBlockOverflowMaterial, plane, level);
                TrackedBlit(source, target, huffmanBlockOverflowMaterial, 1);
                encodeSubStage++;
                return false;
            }

            if (((mipCount - 1) & 1) == 1)
            {
                TrackedBlit(huffmanBlockOverflowMaskTemp, huffmanBlockOverflowMask);
            }

            return true;
        }

        // Huffman chunk 有効 Manual Mip stepを実行する
        private bool RunHuffmanChunkValidManualMipStep(int plane, int baseSubStage)
        {
            // chunk-valid mip atlasは1 frameにつき1 levelずつ明示構築する
            // GenerateMipsはMobile stall要因で、payload gatherにはchunk offset用の厳密な整数byte sumが必要
            int mipCount = GetChunkMipCount(plane);
            if (encodeSubStage == baseSubStage)
            {
                SetupHuffmanChunkValidBytesMaterial(huffmanChunkValidBytesMaterial);
                TrackedBlit(huffmanBlockBits, huffmanChunkValidBytesMip, huffmanChunkValidBytesMaterial, 1);
                encodeSubStage = baseSubStage + 1;
                return mipCount <= 1;
            }

            int level = encodeSubStage - baseSubStage;
            if (level < mipCount)
            {
                RenderTexture source = (level & 1) == 1 ? huffmanChunkValidBytesMip : huffmanChunkValidBytesMipTemp;
                RenderTexture target = (level & 1) == 1 ? huffmanChunkValidBytesMipTemp : huffmanChunkValidBytesMip;
                SetupHuffmanChunkValidBytesManualMipMaterial(huffmanChunkValidBytesMaterial, plane, level);
                TrackedBlit(source, target, huffmanChunkValidBytesMaterial, 2);
                encodeSubStage++;
                return false;
            }

            if (((mipCount - 1) & 1) == 1)
            {
                TrackedBlit(huffmanChunkValidBytesMipTemp, huffmanChunkValidBytesMip);
            }

            return true;
        }

        // plane Huffman payload Gather 段階を実行する
        private bool RunPlaneHuffmanPayloadGatherStage(int plane)
        {
            stage1PlaneMode = plane;
            float startedAt = GetTimingStart();
            int rowGroupCount = GetEncodePayloadRowGroupCount(plane);
            bool needsPayloadCopy = !IsDecodeSlotGroupStepSourceMain(rowGroupCount);
            int payloadCopySubStage = rowGroupCount;
            int metadataPackSubStage = payloadCopySubStage + (needsPayloadCopy ? 1 : 0);
            int metadataCopySubStage = metadataPackSubStage + 1;
            if (encodeSubStage < rowGroupCount)
            {
                if (encodeSubStage < 0)
                {
                    encodeSubStage = 0;
                }

                RenderTexture payloadTexture = GetHuffmanPayloadTexture(plane);
                RenderTexture previous = IsDecodeSlotGroupStepSourceMain(encodeSubStage) ? payloadTexture : huffmanPayloadGather;
                RenderTexture target = IsDecodeSlotGroupStepSourceMain(encodeSubStage) ? huffmanPayloadGather : payloadTexture;
                SetupHuffmanPayloadGatherMaterial(huffmanPayloadGatherMaterial, previous, encodeSubStage);
                TrackedBlit(huffmanBlockPage, target, huffmanPayloadGatherMaterial, 0);
                encodeSubStage++;
                return false;
            }

            if (needsPayloadCopy && encodeSubStage == payloadCopySubStage)
            {
                TrackedBlit(huffmanPayloadGather, GetHuffmanPayloadTexture(plane));
                encodeSubStage++;
                return false;
            }

            if (encodeSubStage == metadataPackSubStage)
            {
                SetupHuffmanMetadataPackMaterial(huffmanMetadataPackMaterial);
                TrackedBlit(huffmanChunkValidBytes, huffmanMetadataPack, huffmanMetadataPackMaterial, 0);
                encodeSubStage++;
                return false;
            }

            if (encodeSubStage == metadataCopySubStage)
            {
                TrackedBlit(huffmanMetadataPack, GetHuffmanMetadataTexture(plane));
            }
            float payloadGatherElapsed = EnableTimingDiagnostics || inspectionStopStep == 10 ? ElapsedMs(startedAt) : 0f;
            if (EnableTimingDiagnostics)
            {
                AddHuffmanStageTiming(TimingOffsetHuffmanPayloadGatherMs, payloadGatherElapsed, plane, "payload gather/metadata");
            }
            if (inspectionStopStep == 10)
            {
                encodeSubStage = 0;
                isRunning = false;
                huffmanReadbackPending = false;
                SetStatus("Inspection stopped after HuffmanPayloadGather. plane=" + GetPlaneName(plane)
                    + " pass=MetadataPack+PayloadGather+PayloadCopy ms=" + FormatTimingMs(payloadGatherElapsed));
                return false;
            }

            encodeSubStage = 0;
            return true;
        }

        // 圧縮 GPU 段階名を返す
        private string GetCompressionGpuStageName(int stage)
        {
            if (stage == 0) return "dct";
            if (stage == 1) return "rle";
            if (stage == 2) return "acfreq";
            if (stage == 3) return "dcfreq";
            if (stage == 4) return "table";
            if (stage == 5) return "block";
            if (stage == 6) return "payload";
            return "settle";
        }

        // 圧縮 GPU 段階 Completed Sub Stagesを返す
        private int GetCompressionGpuStageCompletedSubStages(int plane, int stage)
        {
            if (stage == 0)
            {
                return ShouldDownsampleEncodeSourceForPlane(plane)
                    ? Mathf.Max(encodeSubStage, 0)
                    : Mathf.Max(encodeSubStage - 1, 0);
            }
            if (stage == 1)
            {
                return Mathf.Max(encodeSubStage + 1, 0);
            }
            return Mathf.Max(encodeSubStage, 0);
        }

        // 圧縮 GPU 段階 Sub 段階 数を返す
        private int GetCompressionGpuStageSubStageCount(int plane, int stage)
        {
            if (stage == 0) return ShouldDownsampleEncodeSourceForPlane(plane) ? 6 : 5;
            if (stage == 1) return CoeffToRleStepCount + 1;
            if (stage == 2)
            {
                int columnGroups = Mathf.Max(
                    (GetBlockWidth(plane) + EncodeAcFrequencyBlockColumnsPerGroup - 1)
                        / EncodeAcFrequencyBlockColumnsPerGroup,
                    1);
                return columnGroups * EncodeAcFrequencySlotGroupCount + 1;
            }
            if (stage == 3)
            {
                int rowGroups = Mathf.Max(
                    (GetBlockWidth(plane) + EncodeDcFrequencyItemsPerGroup - 1)
                        / EncodeDcFrequencyItemsPerGroup,
                    1);
                int totalGroups = Mathf.Max(
                    (GetBlockHeight(plane) + EncodeDcFrequencyItemsPerGroup - 1)
                        / EncodeDcFrequencyItemsPerGroup,
                    1);
                return 2 + rowGroups + totalGroups;
            }
            if (stage == 4)
            {
                int mipCount = GetBlockMipCount(plane);
                return HuffmanOverflowBaseSubStage + mipCount + ((mipCount - 1) & 1);
            }
            if (stage == 5)
            {
                int mipCount = GetChunkMipCount(plane);
                return GetEncodeBlockPageStepCount() + 2 + mipCount + ((mipCount - 1) & 1);
            }
            if (stage == 6)
            {
                int rowGroupCount = GetEncodePayloadRowGroupCount(plane);
                int payloadCopyCount = IsDecodeSlotGroupStepSourceMain(rowGroupCount) ? 0 : 1;
                return rowGroupCount + payloadCopyCount + 2;
            }
            return 1;
        }

        // 圧縮 plane 符号化作業data Unit 数を返す
        private int GetCompressionPlaneEncodeWorkUnitCount(int plane)
        {
            // stageを均等配分すると、反復数の多いHuffman table中の表示幅が小さくなり進捗が止まって見える
            // 実際に完了したsubstage数を表示用work unitとして数え、処理内容を変えずに連続して進める
            int total = 0;
            for (int stage = 0; stage < GpuEncodeStageGapKindsPerPlane; stage++)
            {
                total += Mathf.Max(GetCompressionGpuStageSubStageCount(plane, stage), 1);
            }
            return Mathf.Max(total, 1);
        }

        // 圧縮 plane 符号化 Completed作業data Unitsを返す
        private int GetCompressionPlaneEncodeCompletedWorkUnits(int plane, int currentStage)
        {
            int clampedStage = Mathf.Clamp(currentStage, 0, GpuEncodeStageGapKindsPerPlane - 1);
            int completed = 0;
            for (int stage = 0; stage < clampedStage; stage++)
            {
                completed += Mathf.Max(GetCompressionGpuStageSubStageCount(plane, stage), 1);
            }

            int currentStageTotal = Mathf.Max(GetCompressionGpuStageSubStageCount(plane, clampedStage), 1);
            completed += Mathf.Clamp(
                GetCompressionGpuStageCompletedSubStages(plane, clampedStage),
                0,
                currentStageTotal);
            return completed;
        }

        // 圧縮 plane 符号化 Progress01を返す
        private float GetCompressionPlaneEncodeProgress01(int plane, int stage)
        {
            int total = GetCompressionPlaneEncodeWorkUnitCount(plane);
            int completed = Mathf.Clamp(GetCompressionPlaneEncodeCompletedWorkUnits(plane, stage), 0, total);
            return Mathf.Clamp01((float)completed / total);
        }

        // 圧縮 Full 符号化 Progress01を返す
        private float GetCompressionFullEncodeProgress01(int currentPlane, int currentStage)
        {
            int clampedPlane = Mathf.Clamp(currentPlane, PlaneY, PlaneCr);
            int total = 0;
            int completed = 0;
            for (int plane = PlaneY; plane <= PlaneCr; plane++)
            {
                if (!ShouldExportHuffmanPlane(plane))
                {
                    continue;
                }

                int planeTotal = GetCompressionPlaneEncodeWorkUnitCount(plane);
                total += planeTotal;
                if (plane < clampedPlane)
                {
                    completed += planeTotal;
                }
                else if (plane == clampedPlane)
                {
                    completed += Mathf.Clamp(
                        GetCompressionPlaneEncodeCompletedWorkUnits(plane, currentStage),
                        0,
                        planeTotal);
                }
            }

            return total > 0 ? Mathf.Clamp01((float)completed / total) : 0f;
        }

        // 符号化 payload 行 group 数を返す
        private int GetEncodePayloadRowGroupCount(int plane)
        {
            int height = Mathf.Max(GetHuffmanPayloadHeight(plane), 1);
            return Mathf.Max((height + EncodePayloadRowsPerGroup - 1) / EncodePayloadRowsPerGroup, 1);
        }

        // 符号化 ブロック page step 数を返す
        private int GetEncodeBlockPageStepCount()
        {
            return 1 + Mathf.Max(EncodeBlockPageSlotGroupCount - 1, 0) * 2 + 1;
        }

        // DC scan step 入力 Mainかを判定する
        private bool IsDcScanStepSourceMain(int step)
        {
            int exponent = 0;
            int value = Mathf.Max(step, 1);
            while (value > 1)
            {
                value >>= 1;
                exponent++;
            }

            return exponent % 2 == 0;
        }

        // DC scan 結果 In Tempかを判定する
        private bool IsDcScanResultInTemp(int blockTotal)
        {
            int passes = 0;
            for (int step = 1; step < blockTotal; step <<= 1)
            {
                passes++;
            }

            return passes % 2 != 0;
        }

        // DC scan Pass 数を返す
        private int GetDcScanPassCount(int blockTotal)
        {
            int passes = 0;
            for (int step = 1; step < blockTotal; step <<= 1)
            {
                passes++;
            }

            return passes;
        }

        // DC scan step用Passを返す
        private int GetDcScanStepForPass(int passIndex)
        {
            int step = 1;
            int count = Mathf.Max(passIndex, 0);
            for (int i = 0; i < count; i++)
            {
                step <<= 1;
            }

            return step;
        }

        // 復号 slot group step 入力 Mainかを判定する
        private bool IsDecodeSlotGroupStepSourceMain(int step)
        {
            return step - (step / 2) * 2 == 0;
        }

        // 復号 RLE chunk 行 group 数を返す
        private int GetDecodeRleChunkRowGroupCount(int plane)
        {
            int chunkHeight = Mathf.Max(GetChunkHeight(plane), 1);
            return Mathf.Max((chunkHeight + DecodeRleChunkRowsPerGroup - 1) / DecodeRleChunkRowsPerGroup, 1);
        }

        // 復号 DC scan ブロック 行 group 数を返す
        private int GetDecodeDcScanBlockRowGroupCount(int plane)
        {
            int blockHeight = Mathf.Max(GetBlockHeight(plane), 1);
            return Mathf.Max((blockHeight + DecodeDcScanBlockRowsPerGroup - 1) / DecodeDcScanBlockRowsPerGroup, 1);
        }

        // 復号 IDCT ブロック 行 group 数を返す
        private int GetDecodeIdctBlockRowGroupCount(int plane)
        {
            int blockHeight = Mathf.Max(GetBlockHeight(plane), 1);
            return Mathf.Max((blockHeight + DecodeIdctBlockRowsPerGroup - 1) / DecodeIdctBlockRowsPerGroup, 1);
        }

        // 復号 IDCT local group From stepを返す
        private int GetDecodeIdctLocalGroupFromStep(int step, int rowGroupCount)
        {
            int safeRowGroupCount = Mathf.Max(rowGroupCount, 1);
            return Mathf.Clamp(step / safeRowGroupCount, 0, DecodeIdctLocalGroupCount - 1);
        }

        // 復号 IDCT ブロック 行 group From stepを返す
        private int GetDecodeIdctBlockRowGroupFromStep(int step, int rowGroupCount)
        {
            int safeRowGroupCount = Mathf.Max(rowGroupCount, 1);
            int localGroup = step / safeRowGroupCount;
            int rowGroup = step - localGroup * safeRowGroupCount;
            return Mathf.Clamp(rowGroup, 0, safeRowGroupCount - 1);
        }

        // 復号 bit offset chunk 行 group 数を返す
        private int GetDecodeBitOffsetChunkRowGroupCount(int plane)
        {
            int chunkHeight = Mathf.Max(GetChunkHeight(plane), 1);
            return Mathf.Max((chunkHeight + DecodeBitOffsetChunkRowsPerGroup - 1) / DecodeBitOffsetChunkRowsPerGroup, 1);
        }

        // 復号 bit offset local ブロック From stepを返す
        private int GetDecodeBitOffsetLocalBlockFromStep(int step, int rowGroupCount)
        {
            int safeRowGroupCount = Mathf.Max(rowGroupCount, 1);
            int localGroup = Mathf.Clamp(step / safeRowGroupCount, 0, DecodeBitOffsetLocalGroupCount - 1);
            return Mathf.Clamp(localGroup * DecodeBitOffsetLocalBlocksPerGroup, 0, DecodeBitOffsetLocalBlockCount - 1);
        }

        // 復号 bit offset chunk 行 group From stepを返す
        private int GetDecodeBitOffsetChunkRowGroupFromStep(int step, int rowGroupCount)
        {
            int safeRowGroupCount = Mathf.Max(rowGroupCount, 1);
            return Mathf.Clamp(step - (step / safeRowGroupCount) * safeRowGroupCount, 0, safeRowGroupCount - 1);
        }

        // 復号 RLE slot group From stepを返す
        private int GetDecodeRleSlotGroupFromStep(int step, int rowGroupCount)
        {
            int safeRowGroupCount = Mathf.Max(rowGroupCount, 1);
            return Mathf.Clamp(step / safeRowGroupCount, 0, DecodeRleSlotGroupCount - 1);
        }

        // 復号 RLE chunk 行 group From stepを返す
        private int GetDecodeRleChunkRowGroupFromStep(int step, int rowGroupCount)
        {
            int safeRowGroupCount = Mathf.Max(rowGroupCount, 1);
            int slotGroup = step / safeRowGroupCount;
            int rowGroup = step - slotGroup * safeRowGroupCount;
            return Mathf.Clamp(rowGroup, 0, safeRowGroupCount - 1);
        }

        // Huffman 長さ Histogram Textureを使用可能な状態にする
        private void EnsureHuffmanLengthHistogramTextures()
        {
            huffmanRawLengthHistogram = EnsureRuntimeRenderTexture(huffmanRawLengthHistogram, "IC_Library_HuffmanRawLengthHistogram", 256, 1, RenderTextureFormat.ARGB32);
            huffmanRawLengthHistogramTemp = EnsureRuntimeRenderTexture(huffmanRawLengthHistogramTemp, "IC_Library_HuffmanRawLengthHistogramTemp", 256, 1, RenderTextureFormat.ARGB32);
            huffmanLimitedLengthHistogram = EnsureRuntimeRenderTexture(huffmanLimitedLengthHistogram, "IC_Library_HuffmanLimitedLengthHistogram", 16, 1, RenderTextureFormat.ARGB32);
            huffmanLimitedLengthHistogramTemp = EnsureRuntimeRenderTexture(huffmanLimitedLengthHistogramTemp, "IC_Library_HuffmanLimitedLengthHistogramTemp", HuffmanLengthLimitCount, 1, RenderTextureFormat.ARGB32);
        }

        // Huffman 描画 Textureを使用可能な状態にする
        private void EnsureHuffmanRenderTextures(int plane)
        {
            int width = GetPlaneWidth(plane);
            int height = GetPlaneHeight(plane);
            int blockWidth = GetBlockWidth(plane);
            int blockHeight = GetBlockHeight(plane);
            int chunkWidth = GetChunkWidth(plane);
            int chunkHeight = GetChunkHeight(plane);

            rleSymbols = EnsureRuntimeRenderTexture(rleSymbols, "IC_Library_RleSymbols", width, height, RenderTextureFormat.ARGB32);
            rleAcFrequencyRows = EnsureRuntimeRenderTexture(rleAcFrequencyRows, "IC_Library_RleAcFrequencyRows", AcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
            rleAcFrequencyRowsTemp = EnsureRuntimeRenderTexture(rleAcFrequencyRowsTemp, "IC_Library_RleAcFrequencyRowsTemp", AcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
            rleAcFrequency = EnsureRuntimeRenderTexture(rleAcFrequency, "IC_Library_RleAcFrequency", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            dcValues = EnsureRuntimeRenderTexture(dcValues, "IC_Library_DcValues", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            dcDelta = EnsureRuntimeRenderTexture(dcDelta, "IC_Library_DcDelta", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            dcFrequencyRows = EnsureRuntimeRenderTexture(dcFrequencyRows, "IC_Library_DcFrequencyRows", DcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
            dcFrequencyRowsTemp = EnsureRuntimeRenderTexture(dcFrequencyRowsTemp, "IC_Library_DcFrequencyRowsTemp", DcFrequencyBinCount, blockHeight, RenderTextureFormat.ARGB32);
            dcFrequency = EnsureRuntimeRenderTexture(dcFrequency, "IC_Library_DcFrequency", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            huffmanRawLengths = EnsureRuntimeRenderTexture(huffmanRawLengths, "IC_Library_HuffmanRawLengths", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            huffmanRawLengthSingle = EnsureRuntimeRenderTexture(huffmanRawLengthSingle, "IC_Library_HuffmanRawLengthSingle", HuffmanSingleValueTextureSize, HuffmanSingleValueTextureSize, RenderTextureFormat.ARGB32);
            huffmanRawLengthHistogram = EnsureRuntimeRenderTexture(huffmanRawLengthHistogram, "IC_Library_HuffmanRawLengthHistogram", 256, 1, RenderTextureFormat.ARGB32);
            huffmanRawLengthHistogramTemp = EnsureRuntimeRenderTexture(huffmanRawLengthHistogramTemp, "IC_Library_HuffmanRawLengthHistogramTemp", 256, 1, RenderTextureFormat.ARGB32);
            huffmanLimitedLengthHistogram = EnsureRuntimeRenderTexture(huffmanLimitedLengthHistogram, "IC_Library_HuffmanLimitedLengthHistogram", 16, 1, RenderTextureFormat.ARGB32);
            huffmanLimitedLengthHistogramTemp = EnsureRuntimeRenderTexture(huffmanLimitedLengthHistogramTemp, "IC_Library_HuffmanLimitedLengthHistogramTemp", HuffmanLengthLimitCount, 1, RenderTextureFormat.ARGB32);
            dcHuffmanLengths = EnsureRuntimeRenderTexture(dcHuffmanLengths, "IC_Library_DcHuffmanLengths", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            dcHuffmanCodes = EnsureRuntimeRenderTexture(dcHuffmanCodes, "IC_Library_DcHuffmanCodes", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            acHuffmanLengths = EnsureRuntimeRenderTexture(acHuffmanLengths, "IC_Library_AcHuffmanLengths", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            acHuffmanCodes = EnsureRuntimeRenderTexture(acHuffmanCodes, "IC_Library_AcHuffmanCodes", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            huffmanBlockBits = EnsureRuntimeRenderTexture(huffmanBlockBits, "IC_Library_HuffmanBlockBits", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            huffmanBlockOverflowMask = EnsureRuntimeRenderTexture(huffmanBlockOverflowMask, "IC_Library_HuffmanBlockOverflowMask", GetBlockMipAtlasWidth(plane), GetBlockMipAtlasHeight(plane), RenderTextureFormat.ARGB32);
            huffmanBlockOverflowMaskTemp = EnsureRuntimeRenderTexture(huffmanBlockOverflowMaskTemp, "IC_Library_HuffmanBlockOverflowMaskTemp", GetBlockMipAtlasWidth(plane), GetBlockMipAtlasHeight(plane), RenderTextureFormat.ARGB32);
            // pageの横方向は1 blockあたり64 byteを1pixelずつ並べるため、RT幅はpadded画像幅の8倍になる
            // 単純にMaxcodecTextureSizeだけを広げるとRenderTexture.Create失敗や端末フリーズにつながる
            huffmanBlockPage = EnsureRuntimeRenderTexture(huffmanBlockPage, "IC_Library_HuffmanBlockPage", blockWidth * HuffmanBlockPageBytes, blockHeight, RenderTextureFormat.ARGB32);
            huffmanBlockPageTemp = EnsureRuntimeRenderTexture(huffmanBlockPageTemp, "IC_Library_HuffmanBlockPageTemp", blockWidth * HuffmanBlockPageBytes, blockHeight, RenderTextureFormat.ARGB32);
            huffmanBlockPageState = EnsureRuntimeRenderTexture(huffmanBlockPageState, "IC_Library_HuffmanBlockPageState", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            huffmanBlockPageStateTemp = EnsureRuntimeRenderTexture(huffmanBlockPageStateTemp, "IC_Library_HuffmanBlockPageStateTemp", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            huffmanBlockValidBytes = EnsureRuntimeRenderTexture(huffmanBlockValidBytes, "IC_Library_HuffmanBlockValidBytes", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            huffmanChunkValidBytes = EnsureRuntimeRenderTexture(huffmanChunkValidBytes, "IC_Library_HuffmanChunkValidBytes", chunkWidth, chunkHeight, RenderTextureFormat.ARGB32);
            huffmanChunkValidBytesMip = EnsureRuntimeRenderTexture(huffmanChunkValidBytesMip, "IC_Library_HuffmanChunkValidBytesMip", GetChunkMipAtlasWidth(plane), GetChunkMipAtlasHeight(plane), RenderTextureFormat.ARGB32);
            huffmanChunkValidBytesMipTemp = EnsureRuntimeRenderTexture(huffmanChunkValidBytesMipTemp, "IC_Library_HuffmanChunkValidBytesMipTemp", GetChunkMipAtlasWidth(plane), GetChunkMipAtlasHeight(plane), RenderTextureFormat.ARGB32);
            huffmanPayloadGather = EnsureRuntimeRenderTexture(huffmanPayloadGather, "IC_Library_HuffmanPayloadGather", GetHuffmanPayloadWidth(plane), GetHuffmanPayloadHeight(plane), RenderTextureFormat.R8);
            huffmanMetadataPack = EnsureRuntimeRenderTexture(huffmanMetadataPack, "IC_Library_HuffmanMetadataPack", GetHuffmanMetadataWidth(), GetHuffmanMetadataHeight(plane), RenderTextureFormat.ARGB32);
            EnsurePlaneHuffmanOutputTextures(plane);
        }

        // Huffman 復号 描画 Textureを使用可能な状態にする
        private void EnsureHuffmanDecodeRenderTextures(int plane)
        {
            int width = GetPlaneWidth(plane);
            int height = GetPlaneHeight(plane);
            int blockWidth = GetBlockWidth(plane);
            int blockHeight = GetBlockHeight(plane);

            huffmanPayloadBitOffset = EnsureRuntimeRenderTexture(huffmanPayloadBitOffset, "IC_Library_HuffmanPayloadBitOffset", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            huffmanPayloadBitOffsetTemp = EnsureRuntimeRenderTexture(huffmanPayloadBitOffsetTemp, "IC_Library_HuffmanPayloadBitOffsetTemp", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            huffmanDecodeState = EnsureRuntimeRenderTexture(huffmanDecodeState, "IC_Library_HuffmanDecodeState", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            huffmanDecodeStateTemp = EnsureRuntimeRenderTexture(huffmanDecodeStateTemp, "IC_Library_HuffmanDecodeStateTemp", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            huffmanAcDecodeLengthSummary = EnsureRuntimeRenderTexture(huffmanAcDecodeLengthSummary, "IC_Library_HuffmanAcDecodeLengthSummary", 16, 1, RenderTextureFormat.ARGB32);
            huffmanAcDecodeSymbolTable = EnsureRuntimeRenderTexture(huffmanAcDecodeSymbolTable, "IC_Library_HuffmanAcDecodeSymbolTable", AcFrequencyBinCount, 16, RenderTextureFormat.R8);
            huffmanAcDecodeSymbolTableWork = EnsureRuntimeRenderTexture(huffmanAcDecodeSymbolTableWork, "IC_Library_HuffmanAcDecodeSymbolTableWork", AcFrequencyBinCount, 16, RenderTextureFormat.ARGB32);
            huffmanAcDecodeSymbolTableTemp = EnsureRuntimeRenderTexture(huffmanAcDecodeSymbolTableTemp, "IC_Library_HuffmanAcDecodeSymbolTableTemp", AcFrequencyBinCount, 16, RenderTextureFormat.ARGB32);
            rleSymbolsFromHuffmanPayload = EnsureRuntimeRenderTexture(rleSymbolsFromHuffmanPayload, "IC_Library_RleSymbolsFromHuffmanPayload", width, height, RenderTextureFormat.ARGB32);
            dcDeltaFromHuffmanPayload = EnsureRuntimeRenderTexture(dcDeltaFromHuffmanPayload, "IC_Library_DcDeltaFromHuffmanPayload", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            dcScanFromHuffmanPayload = EnsureRuntimeRenderTexture(dcScanFromHuffmanPayload, "IC_Library_DcScanFromHuffmanPayload", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            dcScanFromHuffmanPayloadTemp = EnsureRuntimeRenderTexture(dcScanFromHuffmanPayloadTemp, "IC_Library_DcScanFromHuffmanPayloadTemp", blockWidth, blockHeight, RenderTextureFormat.ARGB32);
            symbolFixedFromHuffmanPayload = EnsureRuntimeRenderTexture(symbolFixedFromHuffmanPayload, "IC_Library_SymbolFixedFromHuffmanPayload", width, height, RenderTextureFormat.ARGB32);
            symbolFixedWorkFromHuffmanPayload = EnsureRuntimeRenderTexture(symbolFixedWorkFromHuffmanPayload, "IC_Library_SymbolFixedWorkFromHuffmanPayload", width, height, RenderTextureFormat.ARGB32);
            // IDCT partial sumは負値や1超を取り得るため、scalarでもfloat formatが必要
            reconstructedFromHuffmanPayload = EnsureRuntimeRenderTexture(reconstructedFromHuffmanPayload, "IC_Library_ReconstructedFromHuffmanPayload", width, height, GetDecodeReconstructedRenderTextureFormat());
        }

        // plane Huffman 出力 Textureを使用可能な状態にする
        private void EnsurePlaneHuffmanOutputTextures(int plane)
        {
            int width = GetPlaneWidth(plane);
            int height = GetPlaneHeight(plane);
            int chunkWidth = GetChunkWidth(plane);
            int chunkHeight = GetChunkHeight(plane);
            if (plane == PlaneY)
            {
                yHuffmanPayloadTexture = EnsureRuntimeRenderTexture(yHuffmanPayloadTexture, "IC_Library_YHuffmanPayload", GetHuffmanPayloadWidth(plane), GetHuffmanPayloadHeight(plane), RenderTextureFormat.R8);
                yHuffmanMetadataTexture = EnsureRuntimeRenderTexture(yHuffmanMetadataTexture, "IC_Library_YHuffmanMetadata", GetHuffmanMetadataWidth(), GetHuffmanMetadataHeight(plane), RenderTextureFormat.ARGB32);
                yChunkValidTexture = EnsureRuntimeRenderTexture(yChunkValidTexture, "IC_Library_YChunkValid", chunkWidth, chunkHeight, RenderTextureFormat.ARGB32);
                yDcHuffmanCodesTexture = EnsureRuntimeRenderTexture(yDcHuffmanCodesTexture, "IC_Library_YDcHuffmanCodes", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                yAcHuffmanCodesTexture = EnsureRuntimeRenderTexture(yAcHuffmanCodesTexture, "IC_Library_YAcHuffmanCodes", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            }
            else if (plane == PlaneA)
            {
                aHuffmanPayloadTexture = EnsureRuntimeRenderTexture(aHuffmanPayloadTexture, "IC_Library_AHuffmanPayload", GetHuffmanPayloadWidth(plane), GetHuffmanPayloadHeight(plane), RenderTextureFormat.R8);
                aHuffmanMetadataTexture = EnsureRuntimeRenderTexture(aHuffmanMetadataTexture, "IC_Library_AHuffmanMetadata", GetHuffmanMetadataWidth(), GetHuffmanMetadataHeight(plane), RenderTextureFormat.ARGB32);
                aChunkValidTexture = EnsureRuntimeRenderTexture(aChunkValidTexture, "IC_Library_AChunkValid", chunkWidth, chunkHeight, RenderTextureFormat.ARGB32);
                aDcHuffmanCodesTexture = EnsureRuntimeRenderTexture(aDcHuffmanCodesTexture, "IC_Library_ADcHuffmanCodes", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                aAcHuffmanCodesTexture = EnsureRuntimeRenderTexture(aAcHuffmanCodesTexture, "IC_Library_AAcHuffmanCodes", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            }
            else if (plane == PlaneCb)
            {
                cbHuffmanPayloadTexture = EnsureRuntimeRenderTexture(cbHuffmanPayloadTexture, "IC_Library_CbHuffmanPayload", GetHuffmanPayloadWidth(plane), GetHuffmanPayloadHeight(plane), RenderTextureFormat.R8);
                cbHuffmanMetadataTexture = EnsureRuntimeRenderTexture(cbHuffmanMetadataTexture, "IC_Library_CbHuffmanMetadata", GetHuffmanMetadataWidth(), GetHuffmanMetadataHeight(plane), RenderTextureFormat.ARGB32);
                cbChunkValidTexture = EnsureRuntimeRenderTexture(cbChunkValidTexture, "IC_Library_CbChunkValid", chunkWidth, chunkHeight, RenderTextureFormat.ARGB32);
                cbDcHuffmanCodesTexture = EnsureRuntimeRenderTexture(cbDcHuffmanCodesTexture, "IC_Library_CbDcHuffmanCodes", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                cbAcHuffmanCodesTexture = EnsureRuntimeRenderTexture(cbAcHuffmanCodesTexture, "IC_Library_CbAcHuffmanCodes", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            }
            else
            {
                crHuffmanPayloadTexture = EnsureRuntimeRenderTexture(crHuffmanPayloadTexture, "IC_Library_CrHuffmanPayload", GetHuffmanPayloadWidth(plane), GetHuffmanPayloadHeight(plane), RenderTextureFormat.R8);
                crHuffmanMetadataTexture = EnsureRuntimeRenderTexture(crHuffmanMetadataTexture, "IC_Library_CrHuffmanMetadata", GetHuffmanMetadataWidth(), GetHuffmanMetadataHeight(plane), RenderTextureFormat.ARGB32);
                crChunkValidTexture = EnsureRuntimeRenderTexture(crChunkValidTexture, "IC_Library_CrChunkValid", chunkWidth, chunkHeight, RenderTextureFormat.ARGB32);
                crDcHuffmanCodesTexture = EnsureRuntimeRenderTexture(crDcHuffmanCodesTexture, "IC_Library_CrDcHuffmanCodes", DcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
                crAcHuffmanCodesTexture = EnsureRuntimeRenderTexture(crAcHuffmanCodesTexture, "IC_Library_CrAcHuffmanCodes", AcFrequencyBinCount, 1, RenderTextureFormat.ARGB32);
            }
        }

        // Huffman 符号化を検証する
        private bool ValidateHuffmanEncode()
        {
            bool ok = coeffToRleSymbolsMaterial != null
                && rleAcFrequencyMaterial != null
                && dcDeltaMaterial != null
                && dcScanMaterial != null
                && dcFrequencyMaterial != null
                && huffmanTableMaterial != null
                && huffmanBitCountMaterial != null
                && huffmanBlockOverflowMaterial != null
                && huffmanBlockPageEncodeMaterial != null
                && huffmanBlockPageState != null
                && huffmanBlockPageStateTemp != null
                && huffmanBlockValidBytesMaterial != null
                && huffmanChunkValidBytesMaterial != null
                && huffmanPayloadGatherMaterial != null
                && huffmanMetadataPackMaterial != null;
            if (!ok)
            {
                SetStatus("Huffman encode skipped: missing reference.");
            }

            return ok;
        }

        // Huffman 復号を検証する
        private bool ValidateHuffmanDecode()
        {
            bool ok = quantTexture != null
                && huffmanChunkBlockValidMaterial != null
                && huffmanDecodeRleMaterial != null
                && rleDcDeltaMaterial != null
                && dcScanMaterial != null
                && rleToSymbolFixedMaterial != null
                && decodeSymbolsMaterial != null
                && work != null
                && huffmanPayloadBitOffset != null
                && huffmanDecodeState != null
                && huffmanDecodeStateTemp != null
                && huffmanAcDecodeLengthSummary != null
                && huffmanAcDecodeSymbolTable != null
                && huffmanAcDecodeSymbolTableWork != null
                && huffmanAcDecodeSymbolTableTemp != null
                && rleSymbolsFromHuffmanPayload != null
                && dcDeltaFromHuffmanPayload != null
                && dcScanFromHuffmanPayload != null
                && dcScanFromHuffmanPayloadTemp != null
                && symbolFixedFromHuffmanPayload != null
                && symbolFixedWorkFromHuffmanPayload != null
                && reconstructedFromHuffmanPayload != null
                && packedDecodedPlanes != null
                && composeRgbaMaterial != null;
            if (!ok)
            {
                FailHuffmanDecode("Huffman byte[] decode skipped: missing decode reference.");
            }

            return ok;
        }

        // plane Huffman 復号 Texture stepをGPUへuploadする
        private bool UploadPlaneHuffmanDecodeTextureStep(int plane, int step)
        {
            int textureStep = step / 2;
            if (step != textureStep * 2)
            {
                Texture2D texture = GetPreparedHuffmanDecodeUploadTexture(textureStep);
                if (texture == null)
                {
                    return false;
                }

                texture.Apply(false, false);
                return true;
            }

            int payloadWidth = GetHuffmanPayloadWidth(plane);
            int payloadHeight = GetDecodePayloadTextureHeight(plane);
            int chunkWidth = GetChunkWidth(plane);
            int chunkHeight = GetChunkHeight(plane);

            if (textureStep == 0)
            {
                byte[] payloadBytes = GetPayloadBytesForPlane(plane);
                if (!EnsureDecodePayloadUploadBytesReady(plane, payloadBytes, payloadWidth, payloadHeight))
                {
                    return false;
                }

                receivedPayloadTexture = LoadRedBytesIntoTexture(receivedPayloadTexture, "IC_Library_ReceivedPayload", GetDecodePayloadUploadBytes(plane, payloadBytes, payloadWidth, payloadHeight), payloadWidth, payloadHeight);
                return receivedPayloadTexture != null;
            }

            if (textureStep == 1)
            {
                receivedChunkValidTexture = LoadRgbaBytesIntoTexture(receivedChunkValidTexture, "IC_Library_ReceivedChunkValid", GetChunkValidBytesForPlane(plane), chunkWidth, chunkHeight);
                return receivedChunkValidTexture != null;
            }

            if (textureStep == 2)
            {
                byte[] chunkValid = GetChunkValidBytesForPlane(plane);
                byte[] chunkOffset = GetChunkOffsetBytesForPlane(plane);
                int expectedChunkOffsetBytes = Mathf.Max(chunkWidth * chunkHeight * 4, 4);
                if (chunkOffset == null || chunkOffset.Length != expectedChunkOffsetBytes)
                {
                    BeginChunkOffsetBuildForDecode(plane, chunkValid, chunkWidth, chunkHeight);
                    return false;
                }

                receivedChunkOffsetTexture = LoadRgbaBytesIntoTexture(receivedChunkOffsetTexture, "IC_Library_ReceivedChunkOffset", chunkOffset, chunkWidth, chunkHeight);
                return receivedChunkOffsetTexture != null;
            }

            if (textureStep == 3)
            {
                receivedDcHuffmanCodesTexture = LoadRgbaBytesIntoTexture(receivedDcHuffmanCodesTexture, "IC_Library_ReceivedDcHuffmanCodes", GetDcHuffmanBytesForPlane(plane), DcFrequencyBinCount, 1);
                return receivedDcHuffmanCodesTexture != null;
            }

            if (textureStep == 4)
            {
                receivedAcHuffmanCodesTexture = LoadRgbaBytesIntoTexture(receivedAcHuffmanCodesTexture, "IC_Library_ReceivedAcHuffmanCodes", GetAcHuffmanBytesForPlane(plane), AcFrequencyBinCount, 1);
                return receivedAcHuffmanCodesTexture != null;
            }

            return false;
        }

        // Prepared Huffman 復号 upload Textureを返す
        private Texture2D GetPreparedHuffmanDecodeUploadTexture(int textureStep)
        {
            if (textureStep == 0) return receivedPayloadTexture;
            if (textureStep == 1) return receivedChunkValidTexture;
            if (textureStep == 2) return receivedChunkOffsetTexture;
            if (textureStep == 3) return receivedDcHuffmanCodesTexture;
            if (textureStep == 4) return receivedAcHuffmanCodesTexture;
            return null;
        }

        // 復号 payload upload byte列 準備完了を使用可能な状態にする
        private bool EnsureDecodePayloadUploadBytesReady(int plane, byte[] payloadBytes, int width, int height)
        {
            if (payloadBytes == null || width <= 0 || height <= 0)
            {
                return false;
            }

            int pixelCount = width * height;
            if (payloadBytes.Length <= 0 || payloadBytes.Length > pixelCount)
            {
                return false;
            }

            if (payloadBytes.Length == pixelCount)
            {
                ClearPendingDecodePayloadUploadBuild();
                return true;
            }

            if (IsDecodePayloadUploadPrepared(plane, payloadBytes, width, height, pixelCount))
            {
                return true;
            }

            if (pendingDecodePayloadUploadBuild
                && pendingDecodePayloadUploadPlane == plane
                && pendingDecodePayloadSourceBytes == payloadBytes
                && pendingDecodePayloadUploadWidth == width
                && pendingDecodePayloadUploadHeight == height
                && pendingDecodePayloadUploadPixelCount == pixelCount)
            {
                return false;
            }

            pendingDecodePayloadUploadBuild = true;
            pendingDecodePayloadUploadPlane = plane;
            pendingDecodePayloadUploadWidth = width;
            pendingDecodePayloadUploadHeight = height;
            pendingDecodePayloadUploadPixelCount = pixelCount;
            pendingDecodePayloadUploadPayloadLength = payloadBytes.Length;
            pendingDecodePayloadUploadOffset = 0;
            pendingDecodePayloadSourceBytes = payloadBytes;
            pendingDecodePayloadUploadBytes = EnsureByteArray(pendingDecodePayloadUploadBytes, pixelCount);
            // decodeはstream幅を固定するためpaddingは最終rowだけ
            // その小さい末尾を一度clearし、実payloadは複数eventへ分けてcopyする
            int tailBytes = pixelCount - payloadBytes.Length;
            if (tailBytes > 0)
            {
                System.Array.Clear(pendingDecodePayloadUploadBytes, payloadBytes.Length, tailBytes);
            }

            ScheduleGpuStageDelay(nameof(_RunDecodePayloadUploadBytesBuildStep), GetGpuDecodeStageGapStep(plane, 0));
            return false;
        }

        // 復号 payload upload Preparedかを判定する
        private bool IsDecodePayloadUploadPrepared(int plane, byte[] payloadBytes, int width, int height, int pixelCount)
        {
            return !pendingDecodePayloadUploadBuild
                && pendingDecodePayloadUploadPlane == plane
                && pendingDecodePayloadSourceBytes == payloadBytes
                && pendingDecodePayloadUploadBytes != null
                && pendingDecodePayloadUploadBytes.Length == pixelCount
                && pendingDecodePayloadUploadWidth == width
                && pendingDecodePayloadUploadHeight == height
                && pendingDecodePayloadUploadPayloadLength == payloadBytes.Length;
        }

        // 復号 payload upload byte列を返す
        private byte[] GetDecodePayloadUploadBytes(int plane, byte[] payloadBytes, int width, int height)
        {
            int pixelCount = width * height;
            if (payloadBytes != null && payloadBytes.Length == pixelCount)
            {
                return payloadBytes;
            }

            if (IsDecodePayloadUploadPrepared(plane, payloadBytes, width, height, pixelCount))
            {
                return pendingDecodePayloadUploadBytes;
            }

            return null;
        }

        // 復号 payload upload byte列 Build stepを実行する
        public void _RunDecodePayloadUploadBytesBuildStep()
        {
            if (!isRunning || huffmanDecodeFailed || !pendingDecodePayloadUploadBuild)
            {
                return;
            }

            if (pendingDecodePayloadSourceBytes == null || pendingDecodePayloadUploadBytes == null)
            {
                FailHuffmanDecode("Huffman byte[] decode skipped: payload upload buffer is missing.");
                return;
            }

            int remaining = pendingDecodePayloadUploadPayloadLength - pendingDecodePayloadUploadOffset;
            int copyCount = Mathf.Min(RuntimeByteCopyChunkBytes, Mathf.Max(remaining, 0));
            if (copyCount > 0)
            {
                CopyBytes(pendingDecodePayloadSourceBytes, pendingDecodePayloadUploadOffset, pendingDecodePayloadUploadBytes, pendingDecodePayloadUploadOffset, copyCount);
                pendingDecodePayloadUploadOffset += copyCount;
            }

            if (pendingDecodePayloadUploadOffset < pendingDecodePayloadUploadPayloadLength)
            {
                ScheduleGpuStageDelay(nameof(_RunDecodePayloadUploadBytesBuildStep), GetGpuDecodeStageGapStep(pendingDecodePayloadUploadPlane, 0));
                return;
            }

            pendingDecodePayloadUploadBuild = false;
            ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(pendingDecodePayloadUploadPlane, 0));
        }

        // chunk offset Build用復号を開始する
        private void BeginChunkOffsetBuildForDecode(int plane, byte[] chunkValidBytes, int chunkWidth, int chunkHeight)
        {
            int chunkTotal = chunkWidth * chunkHeight;
            int expectedBytes = Mathf.Max(chunkTotal * 4, 4);
            if (chunkValidBytes == null || chunkWidth <= 0 || chunkHeight <= 0 || chunkValidBytes.Length != expectedBytes)
            {
                pendingChunkOffsetBuild = false;
                FailHuffmanDecode("Huffman byte[] decode skipped: chunk offset source is invalid.");
                return;
            }

            pendingChunkOffsetBuild = true;
            pendingChunkOffsetPlane = plane;
            pendingChunkOffsetOrderIndex = 0;
            pendingChunkOffsetCumulative = 0;
            pendingChunkOffsetWidth = chunkWidth;
            pendingChunkOffsetHeight = chunkHeight;
            pendingChunkOffsetChunkValidBytes = chunkValidBytes;
            pendingChunkOffsetBytes = new byte[expectedBytes];
            // byte[]削減のためchunkOffsetはDCTHへ送らない
            // Texture upload前にchunkValidから小分けで再構築し、Android late join時のstallを避ける
            ScheduleGpuStageDelay(nameof(_RunChunkOffsetBuildForDecodeStep), GetGpuDecodeStageGapStep(plane, 0));
        }

        // chunk offset Build用復号 stepを実行する
        public void _RunChunkOffsetBuildForDecodeStep()
        {
            if (!isRunning || huffmanDecodeFailed || !pendingChunkOffsetBuild)
            {
                return;
            }

            if (pendingChunkOffsetChunkValidBytes == null || pendingChunkOffsetBytes == null)
            {
                FailHuffmanDecode("Huffman byte[] decode skipped: chunk offset build buffer is missing.");
                return;
            }

            int chunkTotal = pendingChunkOffsetWidth * pendingChunkOffsetHeight;
            int end = Mathf.Min(pendingChunkOffsetOrderIndex + RuntimeChunkOffsetBuildChunksPerStep, chunkTotal);
            for (int orderIndex = pendingChunkOffsetOrderIndex; orderIndex < end; orderIndex++)
            {
                int chunkIndex = GetMortonChunkIndex(orderIndex, pendingChunkOffsetWidth, pendingChunkOffsetHeight);
                int offset = chunkIndex * 4;
                EncodeChunkStartPixel(pendingChunkOffsetBytes, offset, pendingChunkOffsetCumulative, GetHuffmanPayloadWidth(pendingChunkOffsetPlane));
                pendingChunkOffsetCumulative += DecodeUInt24(pendingChunkOffsetChunkValidBytes, offset);
            }

            pendingChunkOffsetOrderIndex = end;
            if (pendingChunkOffsetOrderIndex < chunkTotal)
            {
                ScheduleGpuStageDelay(nameof(_RunChunkOffsetBuildForDecodeStep), GetGpuDecodeStageGapStep(pendingChunkOffsetPlane, 0));
                return;
            }

            SetChunkOffsetBytesForPlane(pendingChunkOffsetPlane, pendingChunkOffsetBytes);
            int plane = pendingChunkOffsetPlane;
            ClearPendingChunkOffsetBuild();
            ScheduleGpuStageDelay(nameof(_RunCurrentHuffmanByteDecodeStage), GetGpuDecodeStageGapStep(plane, 0));
        }

        // Coeff To RLE symbol Materialを設定する
        private void SetupCoeffToRleSymbolsMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetVector("_CoeffSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetTexture("_PrefixTex", work);
            material.SetFloat("_PrefixStep", 1f);
        }

        // Coeff To RLE symbol Build Prefix Base Materialを設定する
        private void SetupCoeffToRleSymbolsBuildPrefixBaseMaterial(Material material)
        {
            SetupCoeffToRleSymbolsMaterial(material);
            material.SetTexture("_PrefixTex", EnsureNeutralGrayTexture());
        }

        // 容量 事前計算 Coeff To RLE Materialを設定する
        private void SetupCapacityPrepassCoeffToRleMaterial(Material material, int coefficientWidth, int coefficientHeight)
        {
            material.SetVector("_CoeffSize", new Vector4(coefficientWidth, coefficientHeight, 0f, 0f));
            material.SetTexture("_PrefixTex", capacityPrepassWork);
            material.SetFloat("_PrefixStep", 1f);
        }

        // RLE AC 頻度 Materialを設定する
        private void SetupRleAcFrequencyMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetVector("_SymbolSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetVector("_FrequencySize", new Vector4(AcFrequencyBinCount, GetBlockHeight(plane), 0f, 0f));
            material.SetFloat("_BlockColumnStart", 0f);
            material.SetFloat("_BlockColumnCount", GetBlockWidth(plane));
            material.SetFloat("_AcSlotStart", 1f);
            material.SetFloat("_AcSlotCount", EncodeAcFrequencySlotCount);
            material.SetFloat("_HasPreviousFrequencyRows", 0f);
        }

        // RLE AC 頻度 行 group Materialを設定する
        private void SetupRleAcFrequencyRowGroupMaterial(
            Material material,
            Texture previousRows,
            int blockColumnStart,
            int blockColumnCount,
            int slotStart,
            int slotCount,
            bool hasPrevious)
        {
            SetupRleAcFrequencyMaterial(material);
            material.SetTexture("_PreviousFrequencyRowsTex", previousRows);
            material.SetFloat("_BlockColumnStart", blockColumnStart);
            material.SetFloat("_BlockColumnCount", blockColumnCount);
            material.SetFloat("_AcSlotStart", slotStart);
            material.SetFloat("_AcSlotCount", slotCount);
            material.SetFloat("_HasPreviousFrequencyRows", hasPrevious && previousRows != null ? 1f : 0f);
        }

        // 容量 事前計算 AC 頻度 Materialを設定する
        private void SetupCapacityPrepassAcFrequencyMaterial(Material material, int coefficientWidth, int coefficientHeight, int blockWidth, int blockHeight)
        {
            material.SetVector("_SymbolSize", new Vector4(coefficientWidth, coefficientHeight, 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(blockWidth, blockHeight, 0f, 0f));
            material.SetVector("_FrequencySize", new Vector4(AcFrequencyBinCount, blockHeight, 0f, 0f));
            material.SetFloat("_BlockColumnStart", 0f);
            material.SetFloat("_BlockColumnCount", blockWidth);
            material.SetFloat("_AcSlotStart", 1f);
            material.SetFloat("_AcSlotCount", EncodeAcFrequencySlotCount);
            material.SetFloat("_HasPreviousFrequencyRows", 0f);
        }

        // 容量 事前計算 AC 頻度 行 group Materialを設定する
        private void SetupCapacityPrepassAcFrequencyRowGroupMaterial(
            Material material,
            int coefficientWidth,
            int coefficientHeight,
            int blockWidth,
            int blockHeight,
            Texture previousRows,
            int slotStart,
            int slotCount,
            bool hasPrevious)
        {
            SetupCapacityPrepassAcFrequencyMaterial(material, coefficientWidth, coefficientHeight, blockWidth, blockHeight);
            material.SetTexture("_PreviousFrequencyRowsTex", previousRows);
            material.SetFloat("_AcSlotStart", slotStart);
            material.SetFloat("_AcSlotCount", slotCount);
            material.SetFloat("_HasPreviousFrequencyRows", hasPrevious && previousRows != null ? 1f : 0f);
        }

        // DC 差分 Materialを設定する
        private void SetupDcDeltaMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetVector("_CoeffSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
        }

        // DC scan Materialを設定する
        private void SetupDcScanMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetFloat("_ScanStep", 1f);
            material.SetFloat("_ScanBlockRowGroup", -1f);
            material.SetFloat("_ScanBlockRowsPerGroup", DecodeDcScanBlockRowsPerGroup);
        }

        // DC 頻度 Materialを設定する
        private void SetupDcFrequencyMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetVector("_FrequencySize", new Vector4(DcFrequencyBinCount, GetBlockHeight(plane), 0f, 0f));
            material.SetFloat("_FrequencyGroupStart", 0f);
            material.SetFloat("_FrequencyGroupCount", Mathf.Max(GetBlockWidth(plane), GetBlockHeight(plane)));
            material.SetFloat("_HasPreviousFrequency", 0f);
        }

        // DC 頻度 group Materialを設定する
        private void SetupDcFrequencyGroupMaterial(Material material, Texture previousFrequency, int groupStart, int groupCount, bool hasPrevious)
        {
            SetupDcFrequencyMaterial(material);
            material.SetTexture("_PreviousFrequencyTex", previousFrequency);
            material.SetFloat("_FrequencyGroupStart", groupStart);
            material.SetFloat("_FrequencyGroupCount", groupCount);
            material.SetFloat("_HasPreviousFrequency", hasPrevious && previousFrequency != null ? 1f : 0f);
        }

        // 容量 事前計算 DC 頻度 Materialを設定する
        private void SetupCapacityPrepassDcFrequencyMaterial(Material material, int blockWidth, int blockHeight)
        {
            material.SetVector("_BlockCount", new Vector4(blockWidth, blockHeight, 0f, 0f));
            material.SetVector("_FrequencySize", new Vector4(DcFrequencyBinCount, blockHeight, 0f, 0f));
            material.SetFloat("_FrequencyGroupStart", 0f);
            material.SetFloat("_FrequencyGroupCount", Mathf.Max(blockWidth, blockHeight));
            material.SetFloat("_HasPreviousFrequency", 0f);
        }

        // GPU版はfragmentごとに動的配列と二重loopで同じHuffman treeを再構築する
        // DCは16 symbolでも、初回実行時に同じshader/driver経路へ入ること自体がMobileの長時間stall要因になり得るため、
        // ACだけの例外にせずDC/ACとも頻度表を1回だけreadbackして、この共通CPU経路で構築する
        // workspaceは最大のAC 256 symbolに合わせて確保するが、DCでは先頭16 symbolだけを処理する
        // node選択順は旧shaderと同じ(weight, node id)なので、raw lengthと後続canonical codeは変わらない
        // 戻り値は-1=失敗、0=継続、1=完了
        private int RunCpuHuffmanRawLengthStep(Texture frequencyTexture, int symbolCount)
        {
            int clampedSymbolCount = Mathf.Clamp(symbolCount, 1, AcFrequencyBinCount);
            if (frequencyTexture == null || huffmanRawLengths == null)
            {
                ResetCpuHuffmanState();
                return -1;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseIdle)
            {
                cpuAcHuffmanFrequencyTexture = frequencyTexture;
                cpuHuffmanSymbolCount = clampedSymbolCount;
                cpuAcHuffmanFrequencyBytes = EnsureByteArray(cpuAcHuffmanFrequencyBytes, cpuHuffmanSymbolCount * 4);
                cpuAcHuffmanRawLengthBytes = EnsureByteArray(cpuAcHuffmanRawLengthBytes, AcFrequencyBinCount * 4);
                if (cpuAcHuffmanNodeWeights == null || cpuAcHuffmanNodeWeights.Length != CpuAcHuffmanNodeCapacity)
                {
                    cpuAcHuffmanNodeWeights = new float[CpuAcHuffmanNodeCapacity];
                    cpuAcHuffmanNodeParents = new int[CpuAcHuffmanNodeCapacity];
                    cpuAcHuffmanHeapNodes = new int[CpuAcHuffmanNodeCapacity];
                }
                if (cpuAcHuffmanLeafSymbols == null || cpuAcHuffmanLeafSymbols.Length != AcFrequencyBinCount)
                {
                    cpuAcHuffmanLeafSymbols = new int[AcFrequencyBinCount];
                }

                cpuAcHuffmanInitializeSymbol = 0;
                cpuAcHuffmanLeafCount = 0;
                cpuAcHuffmanNodeCount = 0;
                cpuAcHuffmanHeapCount = 0;
                cpuAcHuffmanMergeCount = 0;
                cpuAcHuffmanLengthLeafIndex = 0;
                cpuAcHuffmanPhase = CpuAcHuffmanPhaseRequestReadback;
                return 0;
            }

            if (cpuAcHuffmanFrequencyTexture != frequencyTexture
                || cpuHuffmanSymbolCount != clampedSymbolCount)
            {
                ResetCpuHuffmanState();
                return -1;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseRequestReadback)
            {
                // Request発行frameにはtree構築やuploadを重ねない
                huffmanReadbackRequest = VRCAsyncGPUReadback.Request(
                    cpuAcHuffmanFrequencyTexture,
                    0,
                    TextureFormat.RGBA32,
                    this);
                cpuAcHuffmanPhase = CpuAcHuffmanPhaseWaitReadback;
                return 0;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseWaitReadback)
            {
                if (!huffmanReadbackRequest.done)
                {
                    return 0;
                }
                if (huffmanReadbackRequest.hasError
                    || !huffmanReadbackRequest.TryGetData(cpuAcHuffmanFrequencyBytes, 0))
                {
                    ResetCpuHuffmanState();
                    return -1;
                }

                cpuAcHuffmanPhase = CpuAcHuffmanPhaseInitializeLeaves;
                return 0;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseInitializeLeaves)
            {
                int end = Mathf.Min(
                    cpuAcHuffmanInitializeSymbol + CpuAcHuffmanInitializeSymbolsPerStep,
                    cpuHuffmanSymbolCount);
                for (int symbol = cpuAcHuffmanInitializeSymbol; symbol < end; symbol++)
                {
                    int byteOffset = symbol * 4;
                    cpuAcHuffmanRawLengthBytes[byteOffset] = 0;
                    cpuAcHuffmanRawLengthBytes[byteOffset + 1] = 0;
                    cpuAcHuffmanRawLengthBytes[byteOffset + 2] = 0;
                    cpuAcHuffmanRawLengthBytes[byteOffset + 3] = 255;

                    int frequency = cpuAcHuffmanFrequencyBytes[byteOffset]
                        + cpuAcHuffmanFrequencyBytes[byteOffset + 1] * 256
                        + cpuAcHuffmanFrequencyBytes[byteOffset + 2] * 65536;
                    if (frequency <= 0)
                    {
                        continue;
                    }

                    int node = cpuAcHuffmanLeafCount;
                    cpuAcHuffmanNodeWeights[node] = frequency;
                    cpuAcHuffmanNodeParents[node] = -1;
                    cpuAcHuffmanLeafSymbols[node] = symbol;
                    InsertCpuAcHuffmanHeapNode(node);
                    cpuAcHuffmanLeafCount++;
                }

                cpuAcHuffmanInitializeSymbol = end;
                if (cpuAcHuffmanInitializeSymbol >= cpuHuffmanSymbolCount)
                {
                    cpuAcHuffmanNodeCount = cpuAcHuffmanLeafCount;
                    cpuAcHuffmanPhase = cpuAcHuffmanLeafCount <= 1
                        ? CpuAcHuffmanPhaseWriteLengths
                        : CpuAcHuffmanPhaseMergeNodes;
                }
                return 0;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseMergeNodes)
            {
                int mergeEnd = cpuAcHuffmanMergeCount + CpuAcHuffmanMergesPerStep;
                while (cpuAcHuffmanHeapCount > 1 && cpuAcHuffmanMergeCount < mergeEnd)
                {
                    int first = PopCpuAcHuffmanHeapNode();
                    int second = PopCpuAcHuffmanHeapNode();
                    int merged = cpuAcHuffmanNodeCount;
                    cpuAcHuffmanNodeWeights[merged] = cpuAcHuffmanNodeWeights[first] + cpuAcHuffmanNodeWeights[second];
                    cpuAcHuffmanNodeParents[first] = merged;
                    cpuAcHuffmanNodeParents[second] = merged;
                    cpuAcHuffmanNodeParents[merged] = -1;
                    cpuAcHuffmanNodeCount++;
                    cpuAcHuffmanMergeCount++;
                    InsertCpuAcHuffmanHeapNode(merged);
                }

                if (cpuAcHuffmanHeapCount <= 1)
                {
                    cpuAcHuffmanPhase = CpuAcHuffmanPhaseWriteLengths;
                }
                return 0;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseWriteLengths)
            {
                int lengthEnd = Mathf.Min(
                    cpuAcHuffmanLengthLeafIndex + CpuAcHuffmanLengthsPerStep,
                    cpuAcHuffmanLeafCount);
                for (int leaf = cpuAcHuffmanLengthLeafIndex; leaf < lengthEnd; leaf++)
                {
                    int length = 1;
                    if (cpuAcHuffmanLeafCount > 1)
                    {
                        length = 0;
                        int node = leaf;
                        while (cpuAcHuffmanNodeParents[node] >= 0 && length < 255)
                        {
                            node = cpuAcHuffmanNodeParents[node];
                            length++;
                        }
                        length = Mathf.Clamp(length, 1, 255);
                    }

                    int symbol = cpuAcHuffmanLeafSymbols[leaf];
                    cpuAcHuffmanRawLengthBytes[symbol * 4] = (byte)length;
                }

                cpuAcHuffmanLengthLeafIndex = lengthEnd;
                if (cpuAcHuffmanLengthLeafIndex >= cpuAcHuffmanLeafCount)
                {
                    cpuAcHuffmanPhase = CpuAcHuffmanPhaseLoadTexture;
                }
                return 0;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseLoadTexture)
            {
                if (cpuAcHuffmanRawLengthUploadTexture == null
                    || cpuAcHuffmanRawLengthUploadTexture.width != AcFrequencyBinCount
                    || cpuAcHuffmanRawLengthUploadTexture.height != 1)
                {
                    cpuAcHuffmanRawLengthUploadTexture = ReleaseRuntimeTexture(cpuAcHuffmanRawLengthUploadTexture);
                    cpuAcHuffmanRawLengthUploadTexture = new Texture2D(
                        AcFrequencyBinCount,
                        1,
                        TextureFormat.RGBA32,
                        false,
                        true);
                    cpuAcHuffmanRawLengthUploadTexture.name = "IC_Library_CpuAcHuffmanRawLengths";
                    cpuAcHuffmanRawLengthUploadTexture.filterMode = FilterMode.Point;
                    cpuAcHuffmanRawLengthUploadTexture.wrapMode = TextureWrapMode.Clamp;
                }

                cpuAcHuffmanRawLengthUploadTexture.LoadRawTextureData(cpuAcHuffmanRawLengthBytes);
                cpuAcHuffmanPhase = CpuAcHuffmanPhaseApplyTexture;
                return 0;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseApplyTexture)
            {
                cpuAcHuffmanRawLengthUploadTexture.Apply(false, false);
                cpuAcHuffmanPhase = CpuAcHuffmanPhaseBlitTexture;
                return 0;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseBlitTexture)
            {
                TrackedBlit(cpuAcHuffmanRawLengthUploadTexture, huffmanRawLengths);
                cpuAcHuffmanPhase = CpuAcHuffmanPhaseReleaseTexture;
                return 0;
            }

            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseReleaseTexture)
            {
                ResetCpuHuffmanState();
                return 1;
            }

            ResetCpuHuffmanState();
            return -1;
        }

        // CPU AC Huffman Node Lessかを判定する
        private bool IsCpuAcHuffmanNodeLess(int first, int second)
        {
            float firstWeight = cpuAcHuffmanNodeWeights[first];
            float secondWeight = cpuAcHuffmanNodeWeights[second];
            return firstWeight < secondWeight
                || (Mathf.Abs(firstWeight - secondWeight) < 0.5f && first < second);
        }

        // Insert CPU AC Huffman Heap Nodeを処理する
        private void InsertCpuAcHuffmanHeapNode(int node)
        {
            int index = cpuAcHuffmanHeapCount;
            cpuAcHuffmanHeapNodes[index] = node;
            cpuAcHuffmanHeapCount++;
            while (index > 0)
            {
                int parentIndex = (index - 1) / 2;
                int parentNode = cpuAcHuffmanHeapNodes[parentIndex];
                if (!IsCpuAcHuffmanNodeLess(node, parentNode))
                {
                    break;
                }

                cpuAcHuffmanHeapNodes[index] = parentNode;
                cpuAcHuffmanHeapNodes[parentIndex] = node;
                index = parentIndex;
            }
        }

        // Pop CPU AC Huffman Heap Nodeを処理する
        private int PopCpuAcHuffmanHeapNode()
        {
            int result = cpuAcHuffmanHeapNodes[0];
            cpuAcHuffmanHeapCount--;
            if (cpuAcHuffmanHeapCount <= 0)
            {
                return result;
            }

            int node = cpuAcHuffmanHeapNodes[cpuAcHuffmanHeapCount];
            cpuAcHuffmanHeapNodes[0] = node;
            int index = 0;
            while (true)
            {
                int left = index * 2 + 1;
                if (left >= cpuAcHuffmanHeapCount)
                {
                    break;
                }

                int right = left + 1;
                int smaller = left;
                if (right < cpuAcHuffmanHeapCount
                    && IsCpuAcHuffmanNodeLess(cpuAcHuffmanHeapNodes[right], cpuAcHuffmanHeapNodes[left]))
                {
                    smaller = right;
                }
                if (!IsCpuAcHuffmanNodeLess(cpuAcHuffmanHeapNodes[smaller], node))
                {
                    break;
                }

                cpuAcHuffmanHeapNodes[index] = cpuAcHuffmanHeapNodes[smaller];
                cpuAcHuffmanHeapNodes[smaller] = node;
                index = smaller;
            }

            return result;
        }

        // CPU Huffman Progress01を返す
        private float GetCpuHuffmanProgress01()
        {
            int completed = 0;
            int symbolCount = Mathf.Max(cpuHuffmanSymbolCount, 1);
            int total = 2 + symbolCount + (symbolCount - 1) + symbolCount + 4;
            if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseRequestReadback)
            {
                completed = 0;
            }
            else if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseWaitReadback)
            {
                completed = 1;
            }
            else if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseInitializeLeaves)
            {
                completed = 2 + cpuAcHuffmanInitializeSymbol;
            }
            else if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseMergeNodes)
            {
                completed = 2 + symbolCount + cpuAcHuffmanMergeCount;
            }
            else if (cpuAcHuffmanPhase == CpuAcHuffmanPhaseWriteLengths)
            {
                completed = 2 + symbolCount + (symbolCount - 1) + cpuAcHuffmanLengthLeafIndex;
            }
            else if (cpuAcHuffmanPhase >= CpuAcHuffmanPhaseLoadTexture)
            {
                completed = total - (CpuAcHuffmanPhaseReleaseTexture - cpuAcHuffmanPhase + 1);
            }

            return Mathf.Clamp01((float)completed / Mathf.Max(total, 1));
        }

        // CPU Huffman 進捗 Sub 段階を返す
        private int GetCpuHuffmanProgressSubStage(int baseSubStage, int endSubStage)
        {
            int count = Mathf.Max(endSubStage - baseSubStage, 1);
            int offset = Mathf.Clamp(Mathf.FloorToInt(GetCpuHuffmanProgress01() * count), 0, count - 1);
            return baseSubStage + offset;
        }

        // CPU Huffman 状態を初期状態へ戻す
        private void ResetCpuHuffmanState()
        {
            cpuAcHuffmanRawLengthUploadTexture = ReleaseRuntimeTexture(cpuAcHuffmanRawLengthUploadTexture);
            cpuAcHuffmanFrequencyTexture = null;
            cpuAcHuffmanPhase = CpuAcHuffmanPhaseIdle;
            cpuAcHuffmanInitializeSymbol = 0;
            cpuAcHuffmanLeafCount = 0;
            cpuAcHuffmanNodeCount = 0;
            cpuAcHuffmanHeapCount = 0;
            cpuAcHuffmanMergeCount = 0;
            cpuAcHuffmanLengthLeafIndex = 0;
            cpuHuffmanSymbolCount = 0;
        }

        // Huffman table Materialを設定する
        private void SetupHuffmanTableMaterial(Material material, int symbolCount)
        {
            material.SetFloat("_SymbolCount", symbolCount);
            material.SetVector("_TableSize", new Vector4(symbolCount, 1f, 0f, 0f));
            material.SetVector("_InputTableSize", new Vector4(symbolCount, 1f, 0f, 0f));
            material.SetFloat("_TargetSymbolStart", -1f);
            material.SetFloat("_TargetSymbolCount", symbolCount);
            material.SetTexture("_PreviousTableTex", null);
            material.SetFloat("_HasPreviousTable", 0f);
            material.SetFloat("_SingleValueMode", 0f);
            material.SetVector("_SingleValueTextureSize", new Vector4(HuffmanSingleValueTextureSize, HuffmanSingleValueTextureSize, 0f, 0f));
        }

        // Huffman table Range Materialを設定する
        private void SetupHuffmanTableRangeMaterial(Material material, Texture previousTable, int symbolStart, int symbolCount, bool hasPrevious)
        {
            material.SetTexture("_PreviousTableTex", previousTable);
            material.SetFloat("_TargetSymbolStart", symbolStart);
            material.SetFloat("_TargetSymbolCount", symbolCount);
            material.SetFloat("_HasPreviousTable", hasPrevious ? 1f : 0f);
            material.SetFloat("_SingleValueMode", 0f);
        }

        // Huffman Single Limited Histogram Materialを設定する
        private void SetupHuffmanSingleLimitedHistogramMaterial(Material material, Texture frequencyTexture, int symbolCount, int targetLengthIndex)
        {
            SetupHuffmanLimitedLengthHistogramMaterial(material, frequencyTexture, symbolCount);
            material.SetFloat("_TargetSymbolStart", targetLengthIndex);
            material.SetFloat("_TargetSymbolCount", 1f);
            material.SetFloat("_SingleValueMode", 1f);
        }

        // Huffman Single 値 Merge Materialを設定する
        private void SetupHuffmanSingleValueMergeMaterial(Material material, Texture previousTable, int symbolCount, int targetSymbol, bool hasPrevious)
        {
            SetupHuffmanTableMaterial(material, symbolCount);
            material.SetTexture("_PreviousTableTex", previousTable);
            material.SetFloat("_TargetSymbolStart", targetSymbol);
            material.SetFloat("_TargetSymbolCount", 1f);
            material.SetFloat("_HasPreviousTable", hasPrevious && previousTable != null ? 1f : 0f);
            material.SetFloat("_SingleValueMode", 0f);
        }

        // Huffman 長さ 上限 Materialを設定する
        private void SetupHuffmanLengthLimitMaterial(Material material, Texture frequencyTexture, int symbolCount)
        {
            SetupHuffmanTableMaterial(material, symbolCount);
            material.SetTexture("_FrequencyTex", frequencyTexture);
            material.SetTexture("_RawLengthHistogramTex", huffmanRawLengthHistogram);
            material.SetTexture("_LimitedLengthHistogramTex", huffmanLimitedLengthHistogram);
            material.SetVector("_InputTableSize", new Vector4(AcFrequencyBinCount, 1f, 0f, 0f));
        }

        // Huffman Raw 長さ Histogram Materialを設定する
        private void SetupHuffmanRawLengthHistogramMaterial(Material material, Texture frequencyTexture, int symbolCount)
        {
            SetupHuffmanLengthLimitMaterial(material, frequencyTexture, symbolCount);
            material.SetTexture("_RawLengthHistogramTex", EnsureNeutralGrayTexture());
        }

        // Huffman Raw 長さ Histogram Range Materialを設定する
        private void SetupHuffmanRawLengthHistogramRangeMaterial(
            Material material,
            Texture frequencyTexture,
            int symbolCount,
            Texture previousHistogram,
            int lengthStart,
            bool hasPrevious)
        {
            SetupHuffmanRawLengthHistogramMaterial(material, frequencyTexture, symbolCount);
            SetupHuffmanTableRangeMaterial(
                material,
                previousHistogram,
                lengthStart,
                HuffmanRawHistogramLengthsPerGroup,
                hasPrevious);
        }

        // Huffman Limited 長さ Histogram Materialを設定する
        private void SetupHuffmanLimitedLengthHistogramMaterial(Material material, Texture frequencyTexture, int symbolCount)
        {
            SetupHuffmanLengthLimitMaterial(material, frequencyTexture, symbolCount);
            material.SetTexture("_LimitedLengthHistogramTex", EnsureNeutralGrayTexture());
        }

        // Huffman bit 数 Materialを設定する
        private void SetupHuffmanBitCountMaterial(Material material, Texture dcDeltaTexture, Texture dcCodes, Texture acCodes)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetTexture("_DcDeltaTex", dcDeltaTexture);
            material.SetTexture("_DcHuffmanCodesTex", dcCodes);
            material.SetTexture("_AcHuffmanCodesTex", acCodes);
            material.SetVector("_SymbolSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetVector("_DcTableSize", new Vector4(DcFrequencyBinCount, 1f, 0f, 0f));
            material.SetVector("_AcTableSize", new Vector4(AcFrequencyBinCount, 1f, 0f, 0f));
            material.SetFloat("_CodeLengthsStoredInRed", 0f);
            material.SetTexture("_PreviousBitCountTex", EnsureNeutralGrayTexture());
            material.SetFloat("_BitCountSlotGroup", 0f);
            material.SetFloat("_BitCountSlotsPerGroup", HuffmanBitCountSlotsPerGroup);
        }

        // 容量 事前計算 Huffman bit 数 Materialを設定する
        private void SetupCapacityPrepassHuffmanBitCountMaterial(Material material, int coefficientWidth, int coefficientHeight, int blockWidth, int blockHeight)
        {
            material.SetTexture("_DcDeltaTex", capacityPrepassDcDelta);
            material.SetTexture("_DcHuffmanCodesTex", capacityPrepassDcHuffmanLengths);
            material.SetTexture("_AcHuffmanCodesTex", capacityPrepassAcHuffmanLengths);
            material.SetVector("_SymbolSize", new Vector4(coefficientWidth, coefficientHeight, 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(blockWidth, blockHeight, 0f, 0f));
            material.SetVector("_DcTableSize", new Vector4(DcFrequencyBinCount, 1f, 0f, 0f));
            material.SetVector("_AcTableSize", new Vector4(AcFrequencyBinCount, 1f, 0f, 0f));
            // prepassはcanonical code値を生成せず、同じtable shaderがR channelへ出したcode lengthだけを再利用する
            material.SetFloat("_CodeLengthsStoredInRed", 1f);
            material.SetTexture("_PreviousBitCountTex", EnsureNeutralGrayTexture());
            material.SetFloat("_BitCountSlotGroup", 0f);
            material.SetFloat("_BitCountSlotsPerGroup", HuffmanBitCountSlotsPerGroup);
        }

        // Huffman ブロック page 符号化 Materialを設定する
        private void SetupHuffmanBlockPageEncodeMaterial(Material material, Texture dcDeltaTexture, Texture dcCodes, Texture acCodes, Texture previousBlockPageTexture, Texture stateTexture, int slotGroup, int rowGroup)
        {
            int plane = ClampPlane(stage1PlaneMode);
            int blockWidth = GetBlockWidth(plane);
            int blockHeight = GetBlockHeight(plane);
            material.SetTexture("_DcDeltaTex", dcDeltaTexture);
            material.SetTexture("_DcHuffmanCodesTex", dcCodes);
            material.SetTexture("_AcHuffmanCodesTex", acCodes);
            material.SetTexture("_PreviousBlockPageTex", previousBlockPageTexture);
            material.SetTexture("_BlockPageStateTex", stateTexture);
            material.SetVector("_SymbolSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(blockWidth, blockHeight, 0f, 0f));
            material.SetVector("_PageSize", new Vector4(blockWidth * HuffmanBlockPageBytes, blockHeight, HuffmanBlockPageBytes, 0f));
            material.SetVector("_DcTableSize", new Vector4(DcFrequencyBinCount, 1f, 0f, 0f));
            material.SetVector("_AcTableSize", new Vector4(AcFrequencyBinCount, 1f, 0f, 0f));
            material.SetFloat("_EncodeBlockPageRowGroup", rowGroup);
            material.SetFloat("_EncodeBlockPageRowsPerGroup", EncodeBlockPageRowsPerGroup);
            material.SetFloat("_EncodeSlotGroup", slotGroup);
            material.SetFloat("_EncodeSlotsPerGroup", EncodeBlockPageSlotsPerGroup);
        }

        // Huffman ブロック 有効 byte列 Materialを設定する
        private void SetupHuffmanBlockValidBytesMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
        }

        // Huffman ブロック Overflow Materialを設定する
        private void SetupHuffmanBlockOverflowMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            int blockWidth = GetBlockWidth(plane);
            int blockHeight = GetBlockHeight(plane);
            material.SetVector("_BlockCount", new Vector4(blockWidth, blockHeight, 0f, 0f));
            material.SetVector("_OverflowMipAtlasSize", new Vector4(GetBlockMipAtlasWidth(plane), GetBlockMipAtlasHeight(plane), 0f, 0f));
        }

        // Huffman ブロック Overflow Manual Mip Materialを設定する
        private void SetupHuffmanBlockOverflowManualMipMaterial(Material material, int plane, int level)
        {
            int sourceLevel = Mathf.Max(level - 1, 0);
            int destLevel = Mathf.Max(level, 0);
            material.SetVector("_OverflowMipAtlasSize", new Vector4(GetBlockMipAtlasWidth(plane), GetBlockMipAtlasHeight(plane), 0f, 0f));
            material.SetVector("_SourceLevelOffset", new Vector4(GetBlockMipLevelOffsetX(plane, sourceLevel), 0f, 0f, 0f));
            material.SetVector("_DestLevelOffset", new Vector4(GetBlockMipLevelOffsetX(plane, destLevel), 0f, 0f, 0f));
            material.SetVector("_SourceLevelSize", new Vector4(GetBlockMipLevelWidth(plane, sourceLevel), GetBlockMipLevelHeight(plane, sourceLevel), 0f, 0f));
            material.SetVector("_DestLevelSize", new Vector4(GetBlockMipLevelWidth(plane, destLevel), GetBlockMipLevelHeight(plane, destLevel), 0f, 0f));
        }

        // Huffman chunk 有効 byte列 Materialを設定する
        private void SetupHuffmanChunkValidBytesMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetVector("_ChunkCount", new Vector4(GetChunkWidth(plane), GetChunkHeight(plane), 0f, 0f));
            material.SetFloat("_ChunkSize", ChunkSize);
            material.SetTexture("_ChunkValidTex", huffmanChunkValidBytes);
            material.SetVector("_ChunkMipAtlasSize", new Vector4(GetChunkMipAtlasWidth(plane), GetChunkMipAtlasHeight(plane), 0f, 0f));
        }

        // Huffman chunk 有効 byte列 Build Materialを設定する
        private void SetupHuffmanChunkValidBytesBuildMaterial(Material material)
        {
            SetupHuffmanChunkValidBytesMaterial(material);
            material.SetTexture("_ChunkValidTex", EnsureNeutralGrayTexture());
        }

        // 容量 事前計算 Stats Materialを設定する
        private void SetupCapacityPrepassStatsMaterial(Material material, Texture previousStats, int plane, int sampleCount, int sampleStart, int blockWidth, int blockHeight)
        {
            material.SetTexture("_PreviousStatsTex", previousStats);
            material.SetVector("_BlockCount", new Vector4(blockWidth, blockHeight, 0f, 0f));
            material.SetVector("_PrepassStatsSize", new Vector4(CapacityPrepassStatsTextureWidth, CapacityPrepassStatsTextureHeight, 0f, 0f));
            material.SetFloat("_PrepassTargetPlane", plane);
            material.SetFloat("_PrepassSampleBlockCount", sampleCount);
            material.SetFloat("_PrepassSampleBlockStart", sampleStart);
        }

        // Huffman chunk 有効 byte列 Manual Mip Materialを設定する
        private void SetupHuffmanChunkValidBytesManualMipMaterial(Material material, int plane, int level)
        {
            int sourceLevel = Mathf.Max(level - 1, 0);
            int destLevel = Mathf.Max(level, 0);
            material.SetVector("_ChunkMipAtlasSize", new Vector4(GetChunkMipAtlasWidth(plane), GetChunkMipAtlasHeight(plane), 0f, 0f));
            material.SetVector("_SourceLevelOffset", new Vector4(GetChunkMipLevelOffsetX(plane, sourceLevel), 0f, 0f, 0f));
            material.SetVector("_DestLevelOffset", new Vector4(GetChunkMipLevelOffsetX(plane, destLevel), 0f, 0f, 0f));
            material.SetVector("_SourceLevelSize", new Vector4(GetChunkMipLevelWidth(plane, sourceLevel), GetChunkMipLevelHeight(plane, sourceLevel), 0f, 0f));
            material.SetVector("_DestLevelSize", new Vector4(GetChunkMipLevelWidth(plane, destLevel), GetChunkMipLevelHeight(plane, destLevel), 0f, 0f));
            material.SetFloat("_SourceLevelMode", sourceLevel == 0 ? 1f : 0f);
        }

        // Huffman payload Gather Materialを設定する
        private void SetupHuffmanPayloadGatherMaterial(Material material, Texture previousPayloadTexture, int rowGroup)
        {
            int plane = ClampPlane(stage1PlaneMode);
            int blockWidth = GetBlockWidth(plane);
            int blockHeight = GetBlockHeight(plane);
            material.SetTexture("_MainTex", huffmanBlockPage);
            material.SetTexture("_PreviousPayloadTex", previousPayloadTexture);
            material.SetTexture("_BlockBitCountTex", huffmanBlockBits);
            material.SetTexture("_ChunkValidBytesTex", huffmanChunkValidBytes);
            material.SetTexture("_ChunkValidBytesMipTex", huffmanChunkValidBytesMip);
            material.SetVector("_BlockCount", new Vector4(blockWidth, blockHeight, 0f, 0f));
            material.SetVector("_ChunkCount", new Vector4(GetChunkWidth(plane), GetChunkHeight(plane), 0f, 0f));
            material.SetFloat("_ChunkSize", ChunkSize);
            material.SetVector("_PageSize", new Vector4(blockWidth * HuffmanBlockPageBytes, blockHeight, HuffmanBlockPageBytes, 0f));
            material.SetVector("_StreamSize", new Vector4(GetHuffmanPayloadWidth(plane), GetHuffmanPayloadHeight(plane), 0f, 0f));
            material.SetFloat("_MipCount", GetChunkMipCount(plane));
            material.SetFloat("_TotalValidBytes", GetHuffmanPayloadCapacity(plane));
            material.SetVector("_ChunkMipAtlasSize", new Vector4(GetChunkMipAtlasWidth(plane), GetChunkMipAtlasHeight(plane), 0f, 0f));
            material.SetFloat("_EncodePayloadRowGroup", rowGroup);
            material.SetFloat("_EncodePayloadRowsPerGroup", EncodePayloadRowsPerGroup);
        }

        // Huffman metadata Pack Materialを設定する
        private void SetupHuffmanMetadataPackMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetTexture("_OverflowMaskTex", huffmanBlockOverflowMask);
            material.SetTexture("_ChunkValidTex", huffmanChunkValidBytes);
            material.SetTexture("_DcCodesTex", dcHuffmanCodes);
            material.SetTexture("_AcCodesTex", acHuffmanCodes);
            material.SetVector("_MetadataSize", new Vector4(GetHuffmanMetadataWidth(), GetHuffmanMetadataHeight(plane), 0f, 0f));
            material.SetVector("_ChunkCount", new Vector4(GetChunkWidth(plane), GetChunkHeight(plane), 0f, 0f));
            material.SetVector("_DcTableSize", new Vector4(DcFrequencyBinCount, 1f, 0f, 0f));
            material.SetVector("_AcTableSize", new Vector4(AcFrequencyBinCount, 1f, 0f, 0f));
            material.SetVector("_OverflowAtlasSize", new Vector4(GetBlockMipAtlasWidth(plane), GetBlockMipAtlasHeight(plane), 0f, 0f));
            material.SetVector("_OverflowRootOffset", new Vector4(GetBlockMipLevelOffsetX(plane, GetBlockMipCount(plane) - 1), 0f, 0f, 0f));
        }

        // Huffman chunk ブロック 有効 Materialを設定する
        private void SetupHuffmanChunkBlockValidMaterial(Material material)
        {
            Texture previous = huffmanPayloadBitOffsetTemp;
            if (previous == null)
            {
                previous = receivedPayloadTexture;
            }

            int plane = ClampPlane(stage1PlaneMode);
            SetupHuffmanChunkBlockValidMaterialRange(material, previous, 0, GetChunkHeight(plane), 0, GetChunkWidth(plane), -1);
        }

        // Huffman chunk ブロック 有効 Material Rangeを設定する
        private void SetupHuffmanChunkBlockValidMaterialRange(Material material, Texture previousBitOffsetTexture, int chunkRowStart, int chunkRowCount, int chunkColumnStart, int chunkColumnCount, int decodeLocalIndex)
        {
            SetupHuffmanChunkBlockValidMaterialBase(material);
            SetupHuffmanChunkBlockValidMaterialRangeOnly(material, previousBitOffsetTexture, chunkRowStart, chunkRowCount, chunkColumnStart, chunkColumnCount, decodeLocalIndex);
        }

        // Huffman chunk ブロック 有効 Material Baseを設定する
        private void SetupHuffmanChunkBlockValidMaterialBase(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetTexture("_ChunkValidBytesTex", receivedChunkValidTexture);
            material.SetTexture("_ChunkOffsetTex", receivedChunkOffsetTexture);
            material.SetTexture("_DcHuffmanCodesTex", receivedDcHuffmanCodesTexture);
            material.SetTexture("_AcHuffmanCodesTex", receivedAcHuffmanCodesTexture);
            material.SetTexture("_AcDecodeLengthSummaryTex", huffmanAcDecodeLengthSummary);
            material.SetTexture("_AcDecodeSymbolTex", huffmanAcDecodeSymbolTable);
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetVector("_ChunkCount", new Vector4(GetChunkWidth(plane), GetChunkHeight(plane), 0f, 0f));
            material.SetFloat("_ChunkSize", ChunkSize);
            material.SetVector("_StreamSize", new Vector4(GetHuffmanPayloadWidth(plane), GetDecodePayloadTextureHeight(plane), 0f, 0f));
            material.SetVector("_DcTableSize", new Vector4(DcFrequencyBinCount, 1f, 0f, 0f));
            material.SetVector("_AcTableSize", new Vector4(AcFrequencyBinCount, 1f, 0f, 0f));
        }

        // Huffman chunk ブロック 有効 Material Range Onlyを設定する
        private void SetupHuffmanChunkBlockValidMaterialRangeOnly(Material material, Texture previousBitOffsetTexture, int chunkRowStart, int chunkRowCount, int chunkColumnStart, int chunkColumnCount, int decodeLocalIndex)
        {
            int plane = ClampPlane(stage1PlaneMode);
            Texture previous = previousBitOffsetTexture;
            if (previous == null)
            {
                previous = receivedPayloadTexture;
            }

            material.SetTexture("_PreviousBitOffsetTex", previous);
            material.SetFloat("_ChunkRowStart", Mathf.Clamp(chunkRowStart, 0, Mathf.Max(GetChunkHeight(plane) - 1, 0)));
            material.SetFloat("_ChunkRowCount", Mathf.Clamp(chunkRowCount, 1, Mathf.Max(GetChunkHeight(plane), 1)));
            material.SetFloat("_ChunkColumnStart", Mathf.Clamp(chunkColumnStart, 0, Mathf.Max(GetChunkWidth(plane) - 1, 0)));
            material.SetFloat("_ChunkColumnCount", Mathf.Clamp(chunkColumnCount, 1, Mathf.Max(GetChunkWidth(plane), 1)));
            material.SetFloat("_DecodeLocalIndex", decodeLocalIndex);
            material.SetFloat("_DecodeLocalCount", decodeLocalIndex >= 0 ? DecodeBitOffsetLocalBlocksPerGroup : DecodeBitOffsetLocalBlockCount);
        }

        // Huffman AC 復号 Lookup Materialを設定する
        private void SetupHuffmanAcDecodeLookupMaterial(Material material, Texture acHuffmanCodesTexture, Texture previousSymbolTable, int symbolStart, int symbolCount, bool hasPrevious)
        {
            material.SetTexture("_AcHuffmanCodesTex", acHuffmanCodesTexture);
            material.SetTexture("_AcDecodeLengthSummaryTex", huffmanAcDecodeLengthSummary);
            material.SetTexture("_AcDecodeSymbolTex", huffmanAcDecodeSymbolTable);
            material.SetTexture("_PreviousAcDecodeSymbolTex", previousSymbolTable);
            material.SetVector("_AcTableSize", new Vector4(AcFrequencyBinCount, 1f, 0f, 0f));
            material.SetFloat("_DecodeLengthStart", 1f);
            material.SetFloat("_DecodeLengthCount", 16f);
            material.SetFloat("_DecodeSymbolStart", Mathf.Clamp(symbolStart, 0, AcFrequencyBinCount - 1));
            material.SetFloat("_DecodeSymbolCount", Mathf.Clamp(symbolCount, 1, AcFrequencyBinCount));
            material.SetFloat("_HasPreviousAcDecodeSymbol", hasPrevious ? 1f : 0f);
        }

        // Huffman AC 復号 長さ Summary Materialを設定する
        private void SetupHuffmanAcDecodeLengthSummaryMaterial(Material material, Texture acHuffmanCodesTexture, int symbolStart, int symbolCount)
        {
            SetupHuffmanAcDecodeLookupMaterial(material, acHuffmanCodesTexture, null, symbolStart, symbolCount, false);
            material.SetTexture("_AcDecodeLengthSummaryTex", EnsureNeutralGrayTexture());
        }

        // Huffman 復号 RLE Materialを設定する
        private void SetupHuffmanDecodeRleMaterial(Material material, Texture previousRleTexture, Texture decodeStateTexture, int decodeSlotGroup, int decodeChunkRowGroup)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetTexture("_PreviousRleTex", previousRleTexture);
            material.SetTexture("_DecodeStateTex", decodeStateTexture);
            material.SetTexture("_HuffmanBitOffsetTex", GetCurrentHuffmanPayloadBitOffsetTexture());
            material.SetTexture("_ChunkOffsetTex", receivedChunkOffsetTexture);
            material.SetTexture("_DcHuffmanCodesTex", receivedDcHuffmanCodesTexture);
            material.SetTexture("_AcHuffmanCodesTex", receivedAcHuffmanCodesTexture);
            material.SetTexture("_AcDecodeLengthSummaryTex", huffmanAcDecodeLengthSummary);
            material.SetTexture("_AcDecodeSymbolTex", huffmanAcDecodeSymbolTable);
            material.SetVector("_SymbolSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetVector("_ChunkCount", new Vector4(GetChunkWidth(plane), GetChunkHeight(plane), 0f, 0f));
            material.SetFloat("_ChunkSize", ChunkSize);
            material.SetVector("_StreamSize", new Vector4(GetHuffmanPayloadWidth(plane), GetDecodePayloadTextureHeight(plane), 0f, 0f));
            material.SetVector("_DcTableSize", new Vector4(DcFrequencyBinCount, 1f, 0f, 0f));
            material.SetVector("_AcTableSize", new Vector4(AcFrequencyBinCount, 1f, 0f, 0f));
            material.SetFloat("_DecodeSlotGroup", decodeSlotGroup);
            material.SetFloat("_DecodeSlotsPerGroup", DecodeRleSlotsPerGroup);
            material.SetFloat("_DecodeChunkRowGroup", decodeChunkRowGroup);
            material.SetFloat("_DecodeChunkRowsPerGroup", DecodeRleChunkRowsPerGroup);
            int stateOutputSlot = decodeSlotGroup <= 0 ? 1 : decodeSlotGroup * DecodeRleSlotsPerGroup;
            material.SetFloat("_StateOutputSlot", stateOutputSlot);
            material.SetFloat("_StateOutputEnd", Mathf.Min((decodeSlotGroup + 1) * DecodeRleSlotsPerGroup, DecodeRleSlotCount));
        }

        // RLE DC 差分 Materialを設定する
        private void SetupRleDcDeltaMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetVector("_SymbolSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
        }

        // RLE To symbol Fixed Materialを設定する
        private void SetupRleToSymbolFixedMaterial(Material material, Texture dcValuesTexture, Texture prefixTexture, int prefixStep)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetTexture("_PrefixTex", prefixTexture);
            material.SetTexture("_DcValuesTex", dcValuesTexture);
            material.SetVector("_SymbolSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetVector("_BlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetFloat("_PrefixStep", prefixStep);
        }

        // RLE To symbol Fixed Build Prefix Base Materialを設定する
        private void SetupRleToSymbolFixedBuildPrefixBaseMaterial(Material material, Texture dcValuesTexture)
        {
            SetupRleToSymbolFixedMaterial(material, dcValuesTexture, EnsureNeutralGrayTexture(), 1);
        }

        // 復号 symbol Materialを設定する
        private void SetupDecodeSymbolsMaterial(Material material, Texture previousIdctTexture, int decodeLocalGroup, int decodeBlockRowGroup)
        {
            int plane = ClampPlane(stage1PlaneMode);
            material.SetTexture("_PreviousIdctTex", previousIdctTexture);
            material.SetTexture("_QuantTex", quantTexture);
            material.SetVector("_OutputSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetVector("_SymbolSize", new Vector4(GetPlaneWidth(plane), GetPlaneHeight(plane), 0f, 0f));
            material.SetFloat("_PlaneMode", plane);
            material.SetFloat("_DecodeLocalGroup", decodeLocalGroup);
            material.SetFloat("_DecodeLocalRowsPerGroup", DecodeIdctLocalRowsPerGroup);
            material.SetFloat("_DecodeBlockRowGroup", decodeBlockRowGroup);
            material.SetFloat("_DecodeBlockRowsPerGroup", DecodeIdctBlockRowsPerGroup);
            ApplyPlaneSelector(material);
        }

        // Should Export Huffman planeを処理する
        private bool ShouldExportHuffmanPlane(int plane)
        {
            if (plane == PlaneA)
            {
                return activeEncodeHasAlpha;
            }

            if (plane == PlaneCb || plane == PlaneCr)
            {
                return sendColor;
            }

            return plane == PlaneY;
        }

        // Should Pack 圧縮結果 segmentを処理する
        private bool ShouldPackCompressedSegment(int segment)
        {
            int plane = segment / CompressedByteSegmentPartsPerPlane;
            // plane byte[]はrun間で再利用する。今回A/Cb/Crを省く場合は該当全segment長を0にし、
            // 前画像の古いbyteが新しいDCTHへ数えられたりcopyされたりしないようにする
            return ShouldExportHuffmanPlane(plane);
        }

        // Should readback Huffman種別を処理する
        private bool ShouldReadbackHuffmanKind(int kind)
        {
            // DCTH byte[]再構築に必要なのはmetadataとpayloadだけ
            // 旧中間hash readbackはPC/Android調査には有用だったが、追加GPU readbackとstallを生むため製品経路から除外した
            return kind == HuffmanReadbackKindMetadata || kind == HuffmanReadbackKindPayload;
        }

        // Huffman readback Textureを返す
        private RenderTexture GetHuffmanReadbackTexture(int plane, int kind)
        {
            if (kind == HuffmanReadbackKindMetadata)
            {
                return GetHuffmanMetadataTexture(plane);
            }

            if (kind == HuffmanReadbackKindPayload)
            {
                return GetHuffmanPayloadTexture(plane);
            }

            return null;
        }

        // Huffman readback Texture formatを返す
        private TextureFormat GetHuffmanReadbackTextureFormat(int kind)
        {
            if (kind == HuffmanReadbackKindPayload)
            {
                return TextureFormat.R8;
            }

            return TextureFormat.RGBA32;
        }

        // readback byte 数を返す
        private int GetReadbackByteCount(Texture texture, TextureFormat format)
        {
            int width = texture != null ? Mathf.Max(texture.width, 1) : 1;
            int height = texture != null ? Mathf.Max(texture.height, 1) : 1;
            int pixelCount = width * height;
            if (format == TextureFormat.R8)
            {
                return pixelCount;
            }

            if (format == TextureFormat.RGBAFloat)
            {
                return pixelCount * 16;
            }

            return pixelCount * 4;
        }

        // plane Textureを返す
        private RenderTexture GetPlaneTexture(int plane)
        {
            if (plane == PlaneY) return planeYTexture;
            if (plane == PlaneA) return planeATexture;
            if (plane == PlaneCb) return planeCbTexture;
            return planeCrTexture;
        }

        // Huffman payload Textureを返す
        private RenderTexture GetHuffmanPayloadTexture(int plane)
        {
            if (plane == PlaneY) return yHuffmanPayloadTexture;
            if (plane == PlaneA) return aHuffmanPayloadTexture;
            if (plane == PlaneCb) return cbHuffmanPayloadTexture;
            return crHuffmanPayloadTexture;
        }

        // Huffman metadata Textureを返す
        private RenderTexture GetHuffmanMetadataTexture(int plane)
        {
            if (plane == PlaneY) return yHuffmanMetadataTexture;
            if (plane == PlaneA) return aHuffmanMetadataTexture;
            if (plane == PlaneCb) return cbHuffmanMetadataTexture;
            return crHuffmanMetadataTexture;
        }

        // 現在 Huffman payload bit offset Textureを返す
        private RenderTexture GetCurrentHuffmanPayloadBitOffsetTexture()
        {
            return decodeBitOffsetPing == 0 ? huffmanPayloadBitOffset : huffmanPayloadBitOffsetTemp;
        }

        // 次 Huffman payload bit offset Textureを返す
        private RenderTexture GetNextHuffmanPayloadBitOffsetTexture()
        {
            return decodeBitOffsetPing == 0 ? huffmanPayloadBitOffsetTemp : huffmanPayloadBitOffset;
        }

        // 現在 Huffman 復号 状態 Textureを返す
        private RenderTexture GetCurrentHuffmanDecodeStateTexture()
        {
            return decodeRleStatePing == 0 ? huffmanDecodeState : huffmanDecodeStateTemp;
        }

        // 次 Huffman 復号 状態 Textureを返す
        private RenderTexture GetNextHuffmanDecodeStateTexture()
        {
            return decodeRleStatePing == 0 ? huffmanDecodeStateTemp : huffmanDecodeState;
        }

        // 現在 Huffman ブロック page 状態 Textureを返す
        private RenderTexture GetCurrentHuffmanBlockPageStateTexture()
        {
            return encodeBlockPageStatePing == 0 ? huffmanBlockPageState : huffmanBlockPageStateTemp;
        }

        // 次 Huffman ブロック page 状態 Textureを返す
        private RenderTexture GetNextHuffmanBlockPageStateTexture()
        {
            return encodeBlockPageStatePing == 0 ? huffmanBlockPageStateTemp : huffmanBlockPageState;
        }

        // chunk 有効 Textureを返す
        private RenderTexture GetChunkValidTexture(int plane)
        {
            if (plane == PlaneY) return yChunkValidTexture;
            if (plane == PlaneA) return aChunkValidTexture;
            if (plane == PlaneCb) return cbChunkValidTexture;
            return crChunkValidTexture;
        }

        // DC Huffman code Textureを返す
        private RenderTexture GetDcHuffmanCodesTexture(int plane)
        {
            if (plane == PlaneY) return yDcHuffmanCodesTexture;
            if (plane == PlaneA) return aDcHuffmanCodesTexture;
            if (plane == PlaneCb) return cbDcHuffmanCodesTexture;
            return crDcHuffmanCodesTexture;
        }

        // AC Huffman code Textureを返す
        private RenderTexture GetAcHuffmanCodesTexture(int plane)
        {
            if (plane == PlaneY) return yAcHuffmanCodesTexture;
            if (plane == PlaneA) return aAcHuffmanCodesTexture;
            if (plane == PlaneCb) return cbAcHuffmanCodesTexture;
            return crAcHuffmanCodesTexture;
        }

        // payload byte列用planeを設定する
        private void SetPayloadBytesForPlane(int plane, byte[] bytes)
        {
            if (plane == PlaneY) yPayloadBytes = bytes;
            else if (plane == PlaneA) aPayloadBytes = bytes;
            else if (plane == PlaneCb) cbPayloadBytes = bytes;
            else crPayloadBytes = bytes;
        }

        // chunk 有効 byte列用planeを設定する
        private void SetChunkValidBytesForPlane(int plane, byte[] bytes)
        {
            if (plane == PlaneY) yChunkValidBytes = bytes;
            else if (plane == PlaneA) aChunkValidBytes = bytes;
            else if (plane == PlaneCb) cbChunkValidBytes = bytes;
            else crChunkValidBytes = bytes;
        }

        // chunk offset byte列用planeを設定する
        private void SetChunkOffsetBytesForPlane(int plane, byte[] bytes)
        {
            if (plane == PlaneY) yChunkOffsetBytes = bytes;
            else if (plane == PlaneA) aChunkOffsetBytes = bytes;
            else if (plane == PlaneCb) cbChunkOffsetBytes = bytes;
            else crChunkOffsetBytes = bytes;
        }

        // DC Huffman byte列用planeを設定する
        private void SetDcHuffmanBytesForPlane(int plane, byte[] bytes)
        {
            if (plane == PlaneY) yDcHuffmanBytes = bytes;
            else if (plane == PlaneA) aDcHuffmanBytes = bytes;
            else if (plane == PlaneCb) cbDcHuffmanBytes = bytes;
            else crDcHuffmanBytes = bytes;
        }

        // AC Huffman byte列用planeを設定する
        private void SetAcHuffmanBytesForPlane(int plane, byte[] bytes)
        {
            if (plane == PlaneY) yAcHuffmanBytes = bytes;
            else if (plane == PlaneA) aAcHuffmanBytes = bytes;
            else if (plane == PlaneCb) cbAcHuffmanBytes = bytes;
            else crAcHuffmanBytes = bytes;
        }

        // chunk 有効 byte列用planeを返す
        private byte[] GetChunkValidBytesForPlane(int plane)
        {
            if (plane == PlaneY) return yChunkValidBytes;
            if (plane == PlaneA) return aChunkValidBytes;
            if (plane == PlaneCb) return cbChunkValidBytes;
            return crChunkValidBytes;
        }

        // payload byte列用planeを返す
        private byte[] GetPayloadBytesForPlane(int plane)
        {
            if (plane == PlaneY) return yPayloadBytes;
            if (plane == PlaneA) return aPayloadBytes;
            if (plane == PlaneCb) return cbPayloadBytes;
            return crPayloadBytes;
        }

        // chunk offset byte列用planeを返す
        private byte[] GetChunkOffsetBytesForPlane(int plane)
        {
            if (plane == PlaneY) return yChunkOffsetBytes;
            if (plane == PlaneA) return aChunkOffsetBytes;
            if (plane == PlaneCb) return cbChunkOffsetBytes;
            return crChunkOffsetBytes;
        }

        // DC Huffman byte列用planeを返す
        private byte[] GetDcHuffmanBytesForPlane(int plane)
        {
            if (plane == PlaneY) return yDcHuffmanBytes;
            if (plane == PlaneA) return aDcHuffmanBytes;
            if (plane == PlaneCb) return cbDcHuffmanBytes;
            return crDcHuffmanBytes;
        }

        // AC Huffman byte列用planeを返す
        private byte[] GetAcHuffmanBytesForPlane(int plane)
        {
            if (plane == PlaneY) return yAcHuffmanBytes;
            if (plane == PlaneA) return aAcHuffmanBytes;
            if (plane == PlaneCb) return cbAcHuffmanBytes;
            return crAcHuffmanBytes;
        }

        // 圧縮結果 Image IDを返す
        private int GetCompressedImageId()
        {
            return ReadCompressedImageHeaderInt(CompressedByteOffsetImageId);
        }

        // 圧縮結果 Image 幅を返す
        private int GetCompressedImageWidth()
        {
            return ReadCompressedImageHeaderInt(CompressedByteOffsetWidth);
        }

        // 圧縮結果 Image 高さを返す
        private int GetCompressedImageHeight()
        {
            return ReadCompressedImageHeaderInt(CompressedByteOffsetHeight);
        }

        // 圧縮結果 Image header Intを読み取る
        private int ReadCompressedImageHeaderInt(int offset)
        {
            return ReadCompressedImageHeaderIntFromBytes(compressedBytes, offset);
        }

        // 圧縮結果 Image header Int From byte列を読み取る
        private int ReadCompressedImageHeaderIntFromBytes(byte[] source, int offset)
        {
            if (source == null || source.Length < CompressedByteHeaderBytes)
            {
                return 0;
            }

            if (ReadInt(source, CompressedByteOffsetMagic) != CompressedByteMagic)
            {
                return 0;
            }

            return ReadInt(source, offset);
        }

        // 圧縮結果 Image用IDを保持しているか判定する
        private bool HasCompressedImageForId(int imageId)
        {
            return compressedBytesReady
                && compressedBytes != null
                && compressedBytes.Length >= CompressedByteHeaderBytes
                && GetCompressedImageId() == imageId;
        }

        // 圧縮結果segmentに対応するbyte列を返す
        private byte[] GetCompressedSegmentBytes(int segment)
        {
            int plane = segment / CompressedByteSegmentPartsPerPlane;
            int part = segment - plane * CompressedByteSegmentPartsPerPlane;
            if (part == 0) return GetPayloadBytesForPlane(plane);
            if (part == 1) return GetChunkValidBytesForPlane(plane);
            if (part == 2) return GetDcHuffmanBytesForPlane(plane);
            return GetAcHuffmanBytesForPlane(plane);
        }

        // 圧縮結果 segment byte列を設定する
        private void SetCompressedSegmentBytes(int segment, byte[] bytes)
        {
            int plane = segment / CompressedByteSegmentPartsPerPlane;
            int part = segment - plane * CompressedByteSegmentPartsPerPlane;
            if (part == 0)
            {
                SetPayloadBytesForPlane(plane, bytes);
            }
            else if (part == 1)
            {
                SetChunkValidBytesForPlane(plane, bytes);
                // 前回のoffsetを再利用せず、現在のchunk有効長から再構築させる
                SetChunkOffsetBytesForPlane(plane, GetEmptyBytes());
            }
            else if (part == 2)
            {
                SetDcHuffmanBytesForPlane(plane, bytes);
            }
            else
            {
                SetAcHuffmanBytesForPlane(plane, bytes);
            }
        }

        // Huffman payload 長さ用planeを返す
        private int GetHuffmanPayloadLengthForPlane(int plane)
        {
            return Mathf.Clamp(GetHuffmanPayloadTotalBytesForPlane(plane), 0, GetHuffmanPayloadCapacity(plane));
        }

        // Huffman payload Total byte列用planeを返す
        private int GetHuffmanPayloadTotalBytesForPlane(int plane)
        {
            byte[] chunkValid = GetChunkValidBytesForPlane(plane);
            if (chunkValid == null)
            {
                return 0;
            }

            int total = 0;
            for (int offset = 0; offset + 3 < chunkValid.Length; offset += 4)
            {
                total += DecodeUInt24(chunkValid, offset);
            }

            return total;
        }

        // Huffman payload 幅を返す
        private int GetHuffmanPayloadWidth(int plane)
        {
            return Mathf.Max(GetPlaneWidth(plane), 1);
        }

        // Huffman payload 高さを返す
        private int GetHuffmanPayloadHeight(int plane)
        {
            int width = GetHuffmanPayloadWidth(plane);
            int capacity = GetHuffmanPayloadCapacity(plane);
            return Mathf.Max((capacity + width - 1) / width, 1);
        }

        // 復号 payload Texture 高さを返す
        private int GetDecodePayloadTextureHeight(int plane)
        {
            // encode RTはblock-page最大容量で確保するが、decodeには実DCTH payloadを格納できるrow数だけあればよい
            // stream幅を固定してchunk offsetを維持しつつ、Android/Questで重いpadding済みR8 uploadを避ける
            int width = GetHuffmanPayloadWidth(plane);
            byte[] payload = GetPayloadBytesForPlane(plane);
            int payloadLength = payload != null ? payload.Length : 0;
            int clampedLength = Mathf.Clamp(payloadLength, 1, GetHuffmanPayloadCapacity(plane));
            return Mathf.Max((clampedLength + width - 1) / width, 1);
        }

        // Huffman payload 容量を返す
        private int GetHuffmanPayloadCapacity(int plane)
        {
            return Mathf.Max(GetBlockWidth(plane) * GetBlockHeight(plane) * HuffmanBlockPageBytes, 1);
        }

        // Huffman metadata 幅を返す
        private int GetHuffmanMetadataWidth()
        {
            return AcFrequencyBinCount;
        }

        // Huffman metadata chunk 行を返す
        private int GetHuffmanMetadataChunkRows(int plane)
        {
            int chunkPixels = Mathf.Max(GetChunkWidth(plane) * GetChunkHeight(plane), 1);
            int width = GetHuffmanMetadataWidth();
            return Mathf.Max((chunkPixels + width - 1) / width, 1);
        }

        // Huffman metadata 高さを返す
        private int GetHuffmanMetadataHeight(int plane)
        {
            return 2 + GetHuffmanMetadataChunkRows(plane);
        }

        // Huffman metadata chunk offsetを返す
        private int GetHuffmanMetadataChunkOffset()
        {
            return GetHuffmanMetadataWidth() * 2 * 4;
        }

        // Huffman metadata chunk 有効 byte列を返す
        private int GetHuffmanMetadataChunkValidBytes(int plane)
        {
            return Mathf.Max(GetChunkWidth(plane) * GetChunkHeight(plane) * 4, 4);
        }

        // 圧縮結果 plane 実行中かを判定する
        private bool IsCompressedPlaneActive(int plane)
        {
            if (plane == PlaneA)
            {
                return hasAlpha;
            }

            if (plane == PlaneCb || plane == PlaneCr)
            {
                return sendColor;
            }

            return plane == PlaneY;
        }

        // 有効 圧縮結果 segment 長さかを判定する
        private bool IsValidCompressedSegmentLength(int segment, int length)
        {
            if (segment < 0 || segment >= CompressedByteSegmentCount || length < 0)
            {
                return false;
            }

            int plane = segment / CompressedByteSegmentPartsPerPlane;
            int part = segment - plane * CompressedByteSegmentPartsPerPlane;
            if (!IsCompressedPlaneActive(plane))
            {
                return length == 0;
            }

            if (part == 0)
            {
                return length > 0 && length <= GetHuffmanPayloadCapacity(plane);
            }

            if (part == 1)
            {
                return length == GetHuffmanMetadataChunkValidBytes(plane);
            }

            if (part == 2)
            {
                return length == DcFrequencyBinCount * 4;
            }

            return length == AcFrequencyBinCount * 4;
        }

        // Restored 圧縮結果 Segmentsを検証する
        private bool ValidateRestoredCompressedSegments()
        {
            for (int plane = PlaneY; plane <= PlaneCr; plane++)
            {
                if (!IsCompressedPlaneActive(plane))
                {
                    continue;
                }

                byte[] dcBytes = GetDcHuffmanBytesForPlane(plane);
                byte[] acBytes = GetAcHuffmanBytesForPlane(plane);
                if (CountHuffmanCodeLengths(dcBytes, 0, DcFrequencyBinCount, 1, 16) <= 0
                    || CountHuffmanCodeLengths(acBytes, 0, AcFrequencyBinCount, 1, 16) <= 0
                    || GetMaxHuffmanCodeLength(dcBytes, 0, DcFrequencyBinCount) > 16
                    || GetMaxHuffmanCodeLength(acBytes, 0, AcFrequencyBinCount) > 16)
                {
                    return false;
                }
            }

            return true;
        }


        // byte列を複製する
        private void CopyBytes(byte[] source, int sourceOffset, byte[] destination, int destinationOffset, int count)
        {
            if (source == null || destination == null || count <= 0)
            {
                return;
            }

            System.Array.Copy(source, sourceOffset, destination, destinationOffset, count);
        }

        // byte Rangeを複製する
        private byte[] CopyByteRange(byte[] source, int offset, int count)
        {
            if (source == null || count <= 0)
            {
                return GetEmptyBytes();
            }

            byte[] bytes = new byte[count];
            System.Array.Copy(source, offset, bytes, 0, count);

            return bytes;
        }

        // Empty byte列を返す
        private byte[] GetEmptyBytes()
        {
            if (emptyBytes == null)
            {
                emptyBytes = new byte[0];
            }

            return emptyBytes;
        }

        // byte Arrayを使用可能な状態にする
        private byte[] EnsureByteArray(byte[] bytes, int length)
        {
            if (bytes != null && bytes.Length == length)
            {
                return bytes;
            }

            return new byte[length];
        }

        // Red byte列 Into Textureを読み込む
        private Texture2D LoadRedBytesIntoTexture(Texture2D current, string textureName, byte[] bytes, int width, int height)
        {
            if (bytes == null || width <= 0 || height <= 0)
            {
                return null;
            }

            int pixelCount = width * height;
            if (bytes.Length != pixelCount)
            {
                return null;
            }

            Texture2D texture = current;
            if (texture == null || texture.width != width || texture.height != height || texture.format != TextureFormat.R8)
            {
                if (texture != null)
                {
                    // 解像度変更時は古いTexture2Dを明示破棄し、VRAMに残さない
                    Destroy(texture);
                }

                texture = new Texture2D(width, height, TextureFormat.R8, false, true);
                texture.name = textureName;
                texture.filterMode = FilterMode.Point;
                texture.wrapMode = TextureWrapMode.Clamp;
            }

            // 呼び出し側はTextureと同じ長さのdataを事前準備する
            // decode payload経路ではRuntimeByteCopyChunkBytes単位で作るため、短いbufferを補う大きなmain-thread copyを防げる
            texture.LoadRawTextureData(bytes);
            return texture;
        }

        // RGBA byte列 Into Textureを読み込む
        private Texture2D LoadRgbaBytesIntoTexture(Texture2D current, string textureName, byte[] bytes, int width, int height)
        {
            if (bytes == null || width <= 0 || height <= 0)
            {
                return null;
            }

            int pixelCount = width * height;
            if (bytes.Length != pixelCount * 4)
            {
                return null;
            }

            Texture2D texture = current;
            if (texture == null || texture.width != width || texture.height != height || texture.format != TextureFormat.RGBA32)
            {
                if (texture != null)
                {
                    // 解像度変更時は古いTexture2Dを明示破棄し、VRAMに残さない
                    Destroy(texture);
                }

                texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                texture.name = textureName;
                texture.filterMode = FilterMode.Point;
                texture.wrapMode = TextureWrapMode.Clamp;
            }

            texture.LoadRawTextureData(bytes);
            return texture;
        }

        // Morton chunk indexを返す
        private int GetMortonChunkIndex(int orderIndex, int width, int height)
        {
            int safeWidth = Mathf.Max(width, 1);
            int safeHeight = Mathf.Max(height, 1);
            int remaining = Mathf.Clamp(orderIndex, 0, safeWidth * safeHeight - 1);
            int side = 1;
            int maxSize = Mathf.Max(safeWidth, safeHeight);
            while (side < maxSize)
            {
                side <<= 1;
            }

            int originX = 0;
            int originY = 0;
            // payload gather shaderと同じ子順 (左上、右上、左下、右下) で
            // power-of-two外の領域を飛ばす。単純clampでは3x2等で重複chunkが生じる
            while (side > 1)
            {
                int half = side >> 1;
                int selectedChild = 3;
                for (int child = 0; child < 4; child++)
                {
                    int childX = originX + ((child & 1) != 0 ? half : 0);
                    int childY = originY + ((child & 2) != 0 ? half : 0);
                    int childCount = GetClippedChunkRegionCount(childX, childY, half, safeWidth, safeHeight);
                    if (remaining < childCount)
                    {
                        selectedChild = child;
                        break;
                    }

                    remaining -= childCount;
                }

                if ((selectedChild & 1) != 0)
                {
                    originX += half;
                }
                if ((selectedChild & 2) != 0)
                {
                    originY += half;
                }
                side = half;
            }

            return originY * safeWidth + originX;
        }

        // Clipped chunk Region 数を返す
        private int GetClippedChunkRegionCount(int x, int y, int side, int width, int height)
        {
            int regionWidth = Mathf.Max(Mathf.Min(x + side, width) - x, 0);
            int regionHeight = Mathf.Max(Mathf.Min(y + side, height) - y, 0);
            return regionWidth * regionHeight;
        }

        // U Int24を復号する
        private int DecodeUInt24(byte[] bytes, int offset)
        {
            if (bytes == null || offset < 0 || offset + 2 >= bytes.Length)
            {
                return 0;
            }

            return bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
        }

        // U Int24を符号化する
        private void EncodeUInt24(byte[] bytes, int offset, int value)
        {
            if (bytes == null || offset < 0 || offset + 3 >= bytes.Length)
            {
                return;
            }

            int clamped = Mathf.Clamp(value, 0, 16777215);
            bytes[offset] = (byte)(clamped & 255);
            bytes[offset + 1] = (byte)((clamped >> 8) & 255);
            bytes[offset + 2] = (byte)((clamped >> 16) & 255);
            bytes[offset + 3] = 255;
        }

        // chunk Start Pixelを符号化する
        private void EncodeChunkStartPixel(byte[] bytes, int offset, int byteOffset, int payloadWidth)
        {
            if (bytes == null || offset < 0 || offset + 3 >= bytes.Length)
            {
                return;
            }

            int width = Mathf.Max(payloadWidth, 1);
            int safeOffset = Mathf.Max(byteOffset, 0);
            int x = safeOffset % width;
            int y = safeOffset / width;
            x = Mathf.Clamp(x, 0, 65535);
            y = Mathf.Clamp(y, 0, 65535);
            bytes[offset] = (byte)(x & 255);
            bytes[offset + 1] = (byte)((x >> 8) & 255);
            bytes[offset + 2] = (byte)(y & 255);
            bytes[offset + 3] = (byte)((y >> 8) & 255);
        }

        // Huffman byte列を初期化する
        private void ClearHuffmanBytes()
        {
            // Androidのreceive/decodeではResetが繰り返される。共通の変更しない空byte[]を再利用してGC負荷を避け、
            // 空でないpayload配列だけは通常どおり確保/copyする
            byte[] empty = GetEmptyBytes();
            yPayloadBytes = empty;
            aPayloadBytes = empty;
            cbPayloadBytes = empty;
            crPayloadBytes = empty;
            yChunkValidBytes = empty;
            aChunkValidBytes = empty;
            cbChunkValidBytes = empty;
            crChunkValidBytes = empty;
            yChunkOffsetBytes = empty;
            aChunkOffsetBytes = empty;
            cbChunkOffsetBytes = empty;
            crChunkOffsetBytes = empty;
            yDcHuffmanBytes = empty;
            aDcHuffmanBytes = empty;
            cbDcHuffmanBytes = empty;
            crDcHuffmanBytes = empty;
            yAcHuffmanBytes = empty;
            aAcHuffmanBytes = empty;
            cbAcHuffmanBytes = empty;
            crAcHuffmanBytes = empty;
        }

        // Dynamic Gamma 符号化 Textureを使用可能な状態にする
        private void EnsureDynamicGammaEncodeTexture()
        {
            if (generatedGammaEncodeTexture == null
                || generatedGammaEncodeTexture.width != GammaEncodeLutWidth
                || generatedGammaEncodeTexture.height != GammaEncodeLutHeight
                || generatedGammaEncodeTexture.format != TextureFormat.R8)
            {
                if (generatedGammaEncodeTexture != null)
                {
                    Destroy(generatedGammaEncodeTexture);
                }

                if (gammaEncodeLutBytes == null)
                {
                    generatedGammaEncodeTexture = null;
                    return;
                }

                byte[] bytes = gammaEncodeLutBytes.bytes;
                if (bytes == null || bytes.Length != GammaEncodeLutSize)
                {
                    generatedGammaEncodeTexture = null;
                    return;
                }

                // 65536要素を横一列にすると端末の最大Texture幅を超えるため、256x256のR8へ事前生成値を配置する
                // 実行時にGamma式を再計算すると初回Udon負荷とplatform差が戻るため、TextAssetのbyte列をそのままuploadする
                generatedGammaEncodeTexture = new Texture2D(
                    GammaEncodeLutWidth,
                    GammaEncodeLutHeight,
                    TextureFormat.R8,
                    false,
                    true);
                generatedGammaEncodeTexture.name = "IC_Library_GammaEncodeLut";
                generatedGammaEncodeTexture.filterMode = FilterMode.Point;
                generatedGammaEncodeTexture.wrapMode = TextureWrapMode.Clamp;
                generatedGammaEncodeTexture.LoadRawTextureData(bytes);
                generatedGammaEncodeTexture.Apply(false, true);
                bytes = null;
            }
        }

        // Dynamic 量子化 Reciprocal Textureを使用可能な状態にする
        private void EnsureDynamicQuantReciprocalTexture()
        {
            bool needsUpload = false;
            if (generatedQuantReciprocalTexture == null
                || generatedQuantReciprocalTexture.width != 256
                || generatedQuantReciprocalTexture.height != 1)
            {
                // これは色ではなく数値tableなので、linear RGBA32 + point samplingを意図的に使う
                // sRGB変換やfilteringを入れるとplatform依存の量子化差が再発する
                generatedQuantReciprocalTexture = new Texture2D(256, 1, TextureFormat.RGBA32, false, true);
                generatedQuantReciprocalTexture.name = "IC_Library_QuantReciprocalLut";
                needsUpload = true;
            }

            generatedQuantReciprocalTexture.filterMode = FilterMode.Point;
            generatedQuantReciprocalTexture.wrapMode = TextureWrapMode.Clamp;
            if (needsUpload)
            {
                byte[] packedReciprocal = GetPrecomputedQuantReciprocalLutBytes();
                Color32[] pixels = new Color32[256];
                for (int i = 0; i < pixels.Length; i++)
                {
                    int offset = i * 3;
                    pixels[i] = new Color32(
                        packedReciprocal[offset],
                        packedReciprocal[offset + 1],
                        packedReciprocal[offset + 2],
                        255);
                }

                generatedQuantReciprocalTexture.SetPixels32(pixels);
                generatedQuantReciprocalTexture.Apply(false, false);
            }
            quantReciprocalTexture = generatedQuantReciprocalTexture;
        }

        // Precomputed 量子化 Reciprocal LUT byte列を返す
        private byte[] GetPrecomputedQuantReciprocalLutBytes()
        {
            // round(2^20 / max(q, 1))を事前計算し、RGB little-endian byteとしてpackしたtable
            // code内固定値にすることでruntime float除算やsRGB Texture生成経路によるplatform差を避ける
            return new byte[]
            {
                0, 0, 16, 0, 0, 16, 0, 0, 8, 85, 85, 5, 0, 0, 4, 51, 51, 3, 171, 170, 2, 37, 73, 2,
                0, 0, 2, 28, 199, 1, 154, 153, 1, 93, 116, 1, 85, 85, 1, 20, 59, 1, 146, 36, 1, 17, 17, 1,
                0, 0, 1, 241, 240, 0, 142, 227, 0, 148, 215, 0, 205, 204, 0, 12, 195, 0, 47, 186, 0, 22, 178, 0,
                171, 170, 0, 215, 163, 0, 138, 157, 0, 180, 151, 0, 73, 146, 0, 62, 141, 0, 137, 136, 0, 33, 132, 0,
                0, 128, 0, 31, 124, 0, 120, 120, 0, 7, 117, 0, 199, 113, 0, 180, 110, 0, 202, 107, 0, 7, 105, 0,
                102, 102, 0, 231, 99, 0, 134, 97, 0, 65, 95, 0, 23, 93, 0, 6, 91, 0, 11, 89, 0, 38, 87, 0,
                85, 85, 0, 152, 83, 0, 236, 81, 0, 80, 80, 0, 197, 78, 0, 72, 77, 0, 218, 75, 0, 121, 74, 0,
                37, 73, 0, 220, 71, 0, 159, 70, 0, 108, 69, 0, 68, 68, 0, 38, 67, 0, 17, 66, 0, 4, 65, 0,
                0, 64, 0, 4, 63, 0, 16, 62, 0, 34, 61, 0, 60, 60, 0, 93, 59, 0, 132, 58, 0, 177, 57, 0,
                228, 56, 0, 28, 56, 0, 90, 55, 0, 157, 54, 0, 229, 53, 0, 50, 53, 0, 131, 52, 0, 217, 51, 0,
                51, 51, 0, 145, 50, 0, 244, 49, 0, 89, 49, 0, 195, 48, 0, 48, 48, 0, 161, 47, 0, 21, 47, 0,
                140, 46, 0, 6, 46, 0, 131, 45, 0, 3, 45, 0, 134, 44, 0, 11, 44, 0, 147, 43, 0, 30, 43, 0,
                171, 42, 0, 58, 42, 0, 204, 41, 0, 96, 41, 0, 246, 40, 0, 142, 40, 0, 40, 40, 0, 196, 39, 0,
                98, 39, 0, 2, 39, 0, 164, 38, 0, 72, 38, 0, 237, 37, 0, 148, 37, 0, 61, 37, 0, 231, 36, 0,
                146, 36, 0, 63, 36, 0, 238, 35, 0, 158, 35, 0, 79, 35, 0, 2, 35, 0, 182, 34, 0, 108, 34, 0,
                34, 34, 0, 218, 33, 0, 147, 33, 0, 77, 33, 0, 8, 33, 0, 197, 32, 0, 130, 32, 0, 65, 32, 0,
                0, 32, 0, 192, 31, 0, 130, 31, 0, 68, 31, 0, 8, 31, 0, 204, 30, 0, 145, 30, 0, 87, 30, 0,
                30, 30, 0, 230, 29, 0, 174, 29, 0, 120, 29, 0, 66, 29, 0, 13, 29, 0, 216, 28, 0, 165, 28, 0,
                114, 28, 0, 64, 28, 0, 14, 28, 0, 221, 27, 0, 173, 27, 0, 125, 27, 0, 79, 27, 0, 32, 27, 0,
                243, 26, 0, 197, 26, 0, 153, 26, 0, 109, 26, 0, 66, 26, 0, 23, 26, 0, 237, 25, 0, 195, 25, 0,
                154, 25, 0, 113, 25, 0, 73, 25, 0, 33, 25, 0, 250, 24, 0, 211, 24, 0, 173, 24, 0, 135, 24, 0,
                98, 24, 0, 61, 24, 0, 24, 24, 0, 244, 23, 0, 208, 23, 0, 173, 23, 0, 138, 23, 0, 104, 23, 0,
                70, 23, 0, 36, 23, 0, 3, 23, 0, 226, 22, 0, 193, 22, 0, 161, 22, 0, 129, 22, 0, 98, 22, 0,
                67, 22, 0, 36, 22, 0, 6, 22, 0, 231, 21, 0, 202, 21, 0, 172, 21, 0, 143, 21, 0, 114, 21, 0,
                85, 21, 0, 57, 21, 0, 29, 21, 0, 1, 21, 0, 230, 20, 0, 203, 20, 0, 176, 20, 0, 149, 20, 0,
                123, 20, 0, 97, 20, 0, 71, 20, 0, 45, 20, 0, 20, 20, 0, 251, 19, 0, 226, 19, 0, 202, 19, 0,
                177, 19, 0, 153, 19, 0, 129, 19, 0, 106, 19, 0, 82, 19, 0, 59, 19, 0, 36, 19, 0, 13, 19, 0,
                247, 18, 0, 224, 18, 0, 202, 18, 0, 180, 18, 0, 158, 18, 0, 137, 18, 0, 115, 18, 0, 94, 18, 0,
                73, 18, 0, 52, 18, 0, 32, 18, 0, 11, 18, 0, 247, 17, 0, 227, 17, 0, 207, 17, 0, 187, 17, 0,
                168, 17, 0, 148, 17, 0, 129, 17, 0, 110, 17, 0, 91, 17, 0, 72, 17, 0, 54, 17, 0, 35, 17, 0,
                17, 17, 0, 255, 16, 0, 237, 16, 0, 219, 16, 0, 201, 16, 0, 184, 16, 0, 167, 16, 0, 149, 16, 0,
                132, 16, 0, 115, 16, 0, 98, 16, 0, 82, 16, 0, 65, 16, 0, 49, 16, 0, 32, 16, 0, 16, 16, 0
            };
        }

        // Runtime 量子化 Textureを使用可能な状態にする
        private Texture EnsureRuntimeQuantTexture(int qualityValue, int presetValue)
        {
            int q = Mathf.Clamp(qualityValue, MinQuality, MaxQuality);
            int p = Mathf.Clamp(presetValue, 0, MaxQuantPreset);
            if (runtimeQuantTexture == null)
            {
                runtimeQuantTexture = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
                runtimeQuantTexture.name = "IC_Library_QuantTexture";
                runtimeQuantTexture.filterMode = FilterMode.Point;
                runtimeQuantTexture.wrapMode = TextureWrapMode.Clamp;
            }
            else if (runtimeQuantQuality == q && runtimeQuantPreset == p)
            {
                return runtimeQuantTexture;
            }

            if (runtimeQuantPixels == null || runtimeQuantPixels.Length != 64)
            {
                runtimeQuantPixels = new Color32[64];
            }

            Color32[] pixels = runtimeQuantPixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(
                    (byte)GetScaledQuantValue(i, p, 0, q),
                    (byte)GetScaledQuantValue(i, p, 1, q),
                    (byte)GetScaledQuantValue(i, p, 2, q),
                    255);
            }

            runtimeQuantTexture.SetPixels32(pixels);
            runtimeQuantTexture.Apply(false, false);
            runtimeQuantQuality = q;
            runtimeQuantPreset = p;
            return runtimeQuantTexture;
        }

        // 符号化 Materialを設定する
        private void SetupEncodeMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            int width = GetPlaneWidth(plane);
            int height = GetPlaneHeight(plane);
            int sourceWidth = GetPlaneSourceWidth(plane);
            int sourceHeight = GetPlaneSourceHeight(plane);

            material.SetTexture("_QuantTex", quantTexture);
            material.SetTexture("_QuantReciprocalTex", quantReciprocalTexture);
            material.SetTexture("_GammaEncodeTex", generatedGammaEncodeTexture);
            material.SetFloat("_GammaEncodeSize", GammaEncodeLutSize);
            material.SetFloat("_GammaEncodeWidth", GammaEncodeLutWidth);
            material.SetFloat("_GammaEncodeHeight", GammaEncodeLutHeight);
            material.SetFloat("_InputIsRawStorage", activeEncodeSourceIsRawStorage ? 1f : 0f);
            material.SetFloat("_SourceTextureSrgb", activeSourceSrgb ? 1f : 0f);
            material.SetFloat("_EncodeSrgb", activeEncodeSrgb ? 1f : 0f);
            material.SetVector("_SourceSize", new Vector4(sourceWidth, sourceHeight, 0f, 0f));
            material.SetVector("_CoeffSize", new Vector4(width, height, 0f, 0f));
            material.SetFloat("_PlaneMode", plane);
            ApplyPlaneSelector(material);
        }

        // 容量 事前計算 符号化 Materialを設定する
        private void SetupCapacityPrepassEncodeMaterial(Material material, int plane, int sampleCount)
        {
            int oldPlane = stage1PlaneMode;
            stage1PlaneMode = plane;
            SetupEncodeMaterial(material);
            int sampleBlockWidth = GetCapacityPrepassSampleBlockWidth(sampleCount);
            int sampleBlockHeight = GetCapacityPrepassSampleBlockHeight(sampleCount);
            material.SetTexture("_SampleBlockMapTex", capacityPrepassBlockMapTexture);
            material.SetVector("_SampleMapSize", new Vector4(CapacityPrepassSampleBlockLimit, 1f, 0f, 0f));
            material.SetVector("_SampleBlockCount", new Vector4(sampleBlockWidth, sampleBlockHeight, sampleCount, 0f));
            material.SetVector("_SourceBlockCount", new Vector4(GetBlockWidth(plane), GetBlockHeight(plane), 0f, 0f));
            material.SetVector("_CoeffSize", new Vector4(sampleBlockWidth * DctBlockSize, sampleBlockHeight * DctBlockSize, 0f, 0f));
            stage1PlaneMode = oldPlane;
        }

        // Horizontal Accumulation Materialを設定する
        private void SetupHorizontalAccumulationMaterial(Material material, Texture previous, int stage)
        {
            material.SetTexture("_HorizontalPreviousTex", previous);
            material.SetFloat("_HorizontalSampleOffset", Mathf.Clamp(stage, 0, 3) * 2f);
            material.SetFloat("_HasHorizontalPrevious", previous != null ? 1f : 0f);
        }

        // 色 Downsample Materialを設定する
        private void SetupColorDownsampleMaterial(Material material, int sourceWidth, int sourceHeight)
        {
            material.SetVector("_SourceSize", new Vector4(Mathf.Max(sourceWidth, 1), Mathf.Max(sourceHeight, 1), 0f, 0f));
        }

        // 復号 Materialを設定する
        private void SetupDecodeMaterial(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            int width = GetPlaneWidth(plane);
            int height = GetPlaneHeight(plane);
            material.SetTexture("_QuantTex", quantTexture);
            material.SetVector("_OutputSize", new Vector4(width, height, 0f, 0f));
            material.SetVector("_CoeffSize", new Vector4(width, height, 0f, 0f));
            material.SetFloat("_PlaneMode", plane);
            ApplyPlaneSelector(material);
        }

        // Packed plane Materialを設定する
        private void SetupPackedPlaneMaterial(Material material, int plane)
        {
            int clampedPlane = ClampPlane(plane);
            material.SetVector("_OutputSize", new Vector4(GetPlaneWidth(PlaneY), GetPlaneHeight(PlaneY), 0f, 0f));
            material.SetVector("_PackedPlaneSourceSize", new Vector4(GetPlaneSourceWidth(clampedPlane), GetPlaneSourceHeight(clampedPlane), 0f, 0f));
            material.SetVector("_PackedPlaneTexSize", new Vector4(GetPlaneWidth(clampedPlane), GetPlaneHeight(clampedPlane), 0f, 0f));
            material.SetFloat("_PackedPlaneHalfSize", halfSizeCbCr && (clampedPlane == PlaneCb || clampedPlane == PlaneCr) ? 1f : 0f);
        }

        // Packed Compose Materialを設定する
        private void SetupPackedComposeMaterial(Material material, int outputWidth, int outputHeight)
        {
            material.SetVector("_OutputSize", new Vector4(outputWidth, outputHeight, 0f, 0f));
            material.SetFloat("_AlphaMode", alphaMode);
            material.SetFloat("_EncodeSrgb", activeEncodeSrgb ? 1f : 0f);
            material.SetFloat("_OutputSrgb", activeSourceSrgb ? 1f : 0f);
        }

        // plane Selectorを適用する
        private void ApplyPlaneSelector(Material material)
        {
            int plane = ClampPlane(stage1PlaneMode);
            Vector4 planeWeights = new Vector4(0.299f, 0.587f, 0.114f, 0f);
            float planeOffset = 0f;
            Vector4 quantWeights = new Vector4(1f, 0f, 0f, 0f);

            if (plane == PlaneA)
            {
                planeWeights = new Vector4(0f, 0f, 0f, 1f);
                quantWeights = new Vector4(0f, 1f, 0f, 0f);
            }
            else if (plane == PlaneCb)
            {
                planeWeights = new Vector4(-0.168736f, -0.331264f, 0.5f, 0f);
                planeOffset = 0.5f;
                quantWeights = new Vector4(0f, 0f, 1f, 0f);
            }
            else if (plane == PlaneCr)
            {
                planeWeights = new Vector4(0.5f, -0.418688f, -0.081312f, 0f);
                planeOffset = 0.5f;
                quantWeights = new Vector4(0f, 0f, 1f, 0f);
            }

            material.SetVector("_PlaneWeights", planeWeights);
            material.SetFloat("_PlaneOffset", planeOffset);
            material.SetVector("_QuantWeights", quantWeights);
        }

        // plane 幅を返す
        private int GetPlaneWidth(int plane)
        {
            if (halfSizeCbCr && (plane == PlaneCb || plane == PlaneCr))
            {
                return AlignToBlock(GetHalfSampleSize(GetInputWidth()));
            }

            return AlignToBlock(GetInputWidth());
        }

        // plane 高さを返す
        private int GetPlaneHeight(int plane)
        {
            if (halfSizeCbCr && (plane == PlaneCb || plane == PlaneCr))
            {
                return AlignToBlock(GetHalfSampleSize(GetInputHeight()));
            }

            return AlignToBlock(GetInputHeight());
        }

        // plane 入力 幅を返す
        private int GetPlaneSourceWidth(int plane)
        {
            if (halfSizeCbCr && (plane == PlaneCb || plane == PlaneCr))
            {
                return GetHalfSampleSize(GetInputWidth());
            }

            return GetInputWidth();
        }

        // plane 入力 高さを返す
        private int GetPlaneSourceHeight(int plane)
        {
            if (halfSizeCbCr && (plane == PlaneCb || plane == PlaneCr))
            {
                return GetHalfSampleSize(GetInputHeight());
            }

            return GetInputHeight();
        }

        // 色 plane 幅を返す
        private int GetColorPlaneWidth()
        {
            return GetPlaneWidth(PlaneCb);
        }

        // 色 plane 高さを返す
        private int GetColorPlaneHeight()
        {
            return GetPlaneHeight(PlaneCb);
        }

        // ブロック 幅を返す
        private int GetBlockWidth(int plane)
        {
            return Mathf.Max(GetPlaneWidth(plane) / DctBlockSize, 1);
        }

        // ブロック 高さを返す
        private int GetBlockHeight(int plane)
        {
            return Mathf.Max(GetPlaneHeight(plane) / DctBlockSize, 1);
        }

        // chunk 幅を返す
        private int GetChunkWidth(int plane)
        {
            return Mathf.Max(GetBlockWidth(plane) / ChunkSize, 1);
        }

        // chunk 高さを返す
        private int GetChunkHeight(int plane)
        {
            return Mathf.Max(GetBlockHeight(plane) / ChunkSize, 1);
        }

        // chunk Mip 数を返す
        private int GetChunkMipCount(int plane)
        {
            int count = 1;
            int size = Mathf.Max(GetChunkWidth(plane), GetChunkHeight(plane));
            while (size > 1)
            {
                size = (size + 1) / 2;
                count++;
            }

            return count;
        }

        // ブロック Mip 数を返す
        private int GetBlockMipCount(int plane)
        {
            int count = 1;
            int size = Mathf.Max(GetBlockWidth(plane), GetBlockHeight(plane));
            while (size > 1)
            {
                size = (size + 1) / 2;
                count++;
            }

            return count;
        }

        // ブロック Mip Level 幅を返す
        private int GetBlockMipLevelWidth(int plane, int level)
        {
            int divisor = 1 << Mathf.Clamp(level, 0, 15);
            return Mathf.Max((GetBlockWidth(plane) + divisor - 1) / divisor, 1);
        }

        // ブロック Mip Level 高さを返す
        private int GetBlockMipLevelHeight(int plane, int level)
        {
            int divisor = 1 << Mathf.Clamp(level, 0, 15);
            return Mathf.Max((GetBlockHeight(plane) + divisor - 1) / divisor, 1);
        }

        // ブロック Mip Level offset Xを返す
        private int GetBlockMipLevelOffsetX(int plane, int level)
        {
            int offset = 0;
            int safeLevel = Mathf.Clamp(level, 0, GetBlockMipCount(plane) - 1);
            for (int index = 0; index < safeLevel; index++)
            {
                offset += GetBlockMipLevelWidth(plane, index);
            }

            return offset;
        }

        // ブロック Mip Atlas 幅を返す
        private int GetBlockMipAtlasWidth(int plane)
        {
            int width = 0;
            int mipCount = GetBlockMipCount(plane);
            for (int level = 0; level < mipCount; level++)
            {
                width += GetBlockMipLevelWidth(plane, level);
            }

            return Mathf.Max(width, 1);
        }

        // ブロック Mip Atlas 高さを返す
        private int GetBlockMipAtlasHeight(int plane)
        {
            return Mathf.Max(GetBlockHeight(plane), 1);
        }

        // chunk Mip Level 幅を返す
        private int GetChunkMipLevelWidth(int plane, int level)
        {
            int divisor = 1 << Mathf.Clamp(level, 0, 15);
            return Mathf.Max((GetChunkWidth(plane) + divisor - 1) / divisor, 1);
        }

        // chunk Mip Level 高さを返す
        private int GetChunkMipLevelHeight(int plane, int level)
        {
            int divisor = 1 << Mathf.Clamp(level, 0, 15);
            return Mathf.Max((GetChunkHeight(plane) + divisor - 1) / divisor, 1);
        }

        // chunk Mip Level offset Xを返す
        private int GetChunkMipLevelOffsetX(int plane, int level)
        {
            int offset = 0;
            int safeLevel = Mathf.Clamp(level, 0, GetChunkMipCount(plane) - 1);
            for (int index = 0; index < safeLevel; index++)
            {
                offset += GetChunkMipLevelWidth(plane, index);
            }

            return offset;
        }

        // chunk Mip Atlas 幅を返す
        private int GetChunkMipAtlasWidth(int plane)
        {
            int width = 0;
            int mipCount = GetChunkMipCount(plane);
            for (int level = 0; level < mipCount; level++)
            {
                width += GetChunkMipLevelWidth(plane, level);
            }

            return Mathf.Max(width, 1);
        }

        // chunk Mip Atlas 高さを返す
        private int GetChunkMipAtlasHeight(int plane)
        {
            return Mathf.Max(GetChunkHeight(plane), 1);
        }

        // Input 幅を返す
        private int GetInputWidth()
        {
            int compressedWidth = GetCompressedImageWidth();
            if (compressedWidth > 0)
            {
                return compressedWidth;
            }

            Texture sourceTexture = GetCompressInputTexture();
            return sourceTexture != null ? Mathf.Max(sourceTexture.width, 1) : DefaultTextureSize;
        }

        // Input 高さを返す
        private int GetInputHeight()
        {
            int compressedHeight = GetCompressedImageHeight();
            if (compressedHeight > 0)
            {
                return compressedHeight;
            }

            Texture sourceTexture = GetCompressInputTexture();
            return sourceTexture != null ? Mathf.Max(sourceTexture.height, 1) : DefaultTextureSize;
        }

        // Compress Input Textureを返す
        private Texture GetCompressInputTexture()
        {
            return activeEncodeSourceTexture != null ? activeEncodeSourceTexture : sourceTexture;
        }

        // Requested 出力 幅を返す
        private int GetRequestedOutputWidth()
        {
            return GetInputWidth();
        }

        // Requested 出力 高さを返す
        private int GetRequestedOutputHeight()
        {
            return GetInputHeight();
        }

        // Half Sample サイズを返す
        private int GetHalfSampleSize(int value)
        {
            return Mathf.Max((value + 1) / 2, 1);
        }

        // Align To ブロックを処理する
        private int AlignToBlock(int value)
        {
            int clamped = Mathf.Max(value, 1);
            int block = DctBlockSize * ChunkSize;
            return ((clamped + block - 1) / block) * block;
        }

        // 画像寸法がcodecの許容範囲内かを判定する
        private bool IsValidCodecDimensions(int width, int height)
        {
            return width > 0
                && height > 0
                && width <= MaxCodecTextureSize
                && height <= MaxCodecTextureSize
                && (long)width * (long)height <= MaxCodecPixelCount;
        }

        // Clamp planeを処理する
        private int ClampPlane(int value)
        {
            if (value <= PlaneY)
            {
                return PlaneY;
            }

            if (value == PlaneA)
            {
                return PlaneA;
            }

            if (value == PlaneCb)
            {
                return PlaneCb;
            }

            return PlaneCr;
        }

        // Scaled 量子化 値を返す
        private int GetScaledQuantValue(int index, int preset, int plane, int qualityValue)
        {
            int baseValue = GetBaseQuantValue(index, preset, plane);
            int q = Mathf.Clamp(qualityValue, MinQuality, MaxQuality);
            int scale = GetPrecomputedQuantScale(q);
            // JPEG quality scale式:
            //   scale = quality < 50 ? floor(5000 / quality) : 200 - quality * 2
            //   quant = clamp(floor((baseQuant * scale + 50) / 100), 1, 255)
            // 先頭式の除算は下記tableへ事前計算し、PC/Android/iOSで異なるquant Textureを生成しない
            int value = (baseValue * scale + 50) / 100;
            return Mathf.Clamp(value, 1, 255);
        }

        // Precomputed 量子化 Scaleを返す
        private int GetPrecomputedQuantScale(int qualityValue)
        {
            if (precomputedQuantScaleByQuality == null)
            {
                precomputedQuantScaleByQuality = new int[]
                {
                    0, 5000, 2500, 1667, 1250, 1000, 833, 714, 625, 556, 500, 455, 417, 385, 357, 333, 312, 294, 278, 263,
                    250, 238, 227, 217, 208, 200, 192, 185, 179, 172, 167, 161, 156, 152, 147, 143, 139, 135, 132, 128,
                    125, 122, 119, 116, 114, 111, 109, 106, 104, 102, 100, 98, 96, 94, 92, 90, 88, 86, 84, 82,
                    80, 78, 76, 74, 72, 70, 68, 66, 64, 62, 60, 58, 56, 54, 52, 50, 48, 46, 44, 42,
                    40, 38, 36, 34, 32, 30, 28, 26, 24, 22, 20, 18, 16, 14, 12, 10, 8, 6, 4, 2,
                    0
                };
            }

            return precomputedQuantScaleByQuality[Mathf.Clamp(qualityValue, MinQuality, MaxQuality)];
        }

        // Base 量子化 値を返す
        private int GetBaseQuantValue(int index, int preset, int plane)
        {
            int clampedIndex = Mathf.Clamp(index, 0, 63);
            int p = Mathf.Clamp(preset, 0, MaxQuantPreset);
            if (plane == 1)
            {
                return GetBaseAQuantValue(clampedIndex, p);
            }

            if (plane == 2)
            {
                return GetBaseCbCrQuantValue(clampedIndex, p);
            }

            return GetBaseYQuantValue(clampedIndex, p);
        }

        // Base 量子化 Tablesを使用可能な状態にする
        private void EnsureBaseQuantTables()
        {
            if (baseYQuantTables != null)
            {
                return;
            }

            // tableはpreset 0..5の順で各64要素。cacheしてMobileでquant Textureを再構築する際の
            // 小さいint[]大量allocationを避け、encoded byte自体は変えない
            baseYQuantTables = new int[]
            {
                16, 11, 10, 16, 24, 40, 51, 61,
                12, 12, 14, 19, 26, 58, 60, 55,
                14, 13, 16, 24, 40, 57, 69, 56,
                14, 17, 22, 29, 51, 87, 80, 62,
                18, 22, 37, 56, 68, 109, 103, 77,
                24, 35, 55, 64, 81, 104, 113, 92,
                49, 64, 78, 87, 103, 121, 120, 101,
                72, 92, 95, 98, 112, 100, 103, 99,

                14, 10, 10, 14, 22, 42, 56, 70,
                10, 11, 13, 18, 28, 62, 68, 66,
                12, 12, 15, 24, 44, 66, 78, 72,
                14, 16, 22, 32, 58, 94, 94, 78,
                18, 24, 40, 62, 78, 122, 122, 96,
                26, 38, 62, 76, 98, 126, 138, 116,
                56, 76, 96, 108, 126, 150, 148, 126,
                84, 112, 118, 122, 140, 126, 130, 126,

                10, 8, 8, 10, 14, 18, 24, 32,
                8, 8, 9, 11, 14, 22, 28, 34,
                8, 9, 10, 13, 18, 26, 34, 40,
                10, 11, 13, 18, 24, 34, 44, 52,
                14, 14, 18, 24, 34, 46, 58, 68,
                18, 22, 26, 34, 46, 62, 76, 86,
                24, 28, 34, 44, 58, 76, 92, 104,
                32, 34, 40, 52, 68, 86, 104, 112,

                9, 7, 7, 9, 12, 16, 21, 28,
                7, 7, 8, 10, 12, 19, 25, 30,
                7, 8, 9, 11, 16, 23, 30, 36,
                9, 10, 11, 16, 21, 30, 39, 46,
                12, 12, 16, 21, 30, 40, 51, 60,
                16, 19, 23, 30, 40, 55, 67, 76,
                21, 25, 30, 39, 51, 67, 81, 92,
                28, 30, 36, 46, 60, 76, 92, 100,

                50, 25, 25, 50, 75, 125, 150, 175,
                25, 25, 50, 50, 75, 175, 175, 175,
                50, 50, 50, 75, 125, 175, 200, 175,
                50, 50, 75, 75, 150, 250, 250, 175,
                50, 75, 100, 175, 200, 325, 300, 225,
                75, 100, 175, 200, 250, 300, 350, 275,
                150, 200, 225, 250, 300, 375, 350, 300,
                225, 275, 275, 300, 325, 300, 300, 300,

                6, 5, 5, 6, 8, 10, 14, 18,
                5, 5, 5, 7, 8, 12, 16, 20,
                5, 5, 6, 7, 10, 15, 20, 23,
                6, 7, 7, 10, 14, 20, 25, 30,
                8, 8, 10, 14, 20, 26, 33, 39,
                10, 12, 15, 20, 26, 36, 44, 49,
                14, 16, 20, 25, 33, 44, 53, 60,
                18, 20, 23, 30, 39, 49, 60, 65
            };

            baseAQuantTables = new int[]
            {
                12, 10, 10, 12, 18, 28, 36, 44,
                10, 10, 12, 15, 20, 42, 44, 40,
                12, 11, 13, 18, 28, 40, 48, 40,
                12, 14, 17, 22, 36, 60, 56, 44,
                15, 17, 26, 40, 48, 70, 68, 52,
                18, 25, 38, 44, 56, 68, 74, 60,
                34, 44, 54, 60, 68, 80, 78, 66,
                48, 60, 62, 64, 74, 66, 68, 66,

                12, 10, 10, 12, 18, 30, 40, 50,
                10, 10, 12, 16, 22, 48, 54, 52,
                12, 11, 14, 20, 32, 48, 58, 52,
                12, 15, 18, 24, 42, 70, 68, 56,
                16, 18, 30, 48, 58, 86, 84, 68,
                20, 28, 44, 56, 70, 88, 96, 80,
                40, 56, 70, 80, 88, 104, 102, 88,
                60, 80, 84, 88, 100, 90, 92, 90,

                8, 7, 7, 8, 11, 14, 18, 24,
                7, 7, 8, 9, 11, 16, 22, 28,
                7, 8, 8, 10, 14, 20, 28, 34,
                8, 9, 10, 14, 18, 28, 38, 46,
                11, 11, 14, 18, 28, 40, 52, 62,
                14, 16, 20, 28, 40, 56, 70, 82,
                18, 22, 28, 38, 52, 70, 88, 100,
                24, 28, 34, 46, 62, 82, 100, 112,

                7, 6, 6, 7, 10, 13, 16, 22,
                6, 6, 7, 8, 10, 15, 20, 25,
                6, 7, 7, 9, 13, 18, 25, 30,
                7, 8, 9, 13, 16, 25, 34, 41,
                10, 10, 13, 16, 25, 36, 47, 56,
                13, 15, 18, 25, 36, 50, 63, 74,
                16, 20, 25, 34, 47, 63, 79, 90,
                22, 25, 30, 41, 56, 74, 90, 100,

                50, 25, 25, 50, 75, 125, 150, 175,
                25, 25, 50, 50, 75, 175, 175, 175,
                50, 50, 50, 75, 125, 175, 200, 175,
                50, 50, 75, 75, 150, 250, 250, 175,
                50, 75, 100, 175, 200, 325, 300, 225,
                75, 100, 175, 200, 250, 300, 350, 275,
                150, 200, 225, 250, 300, 375, 350, 300,
                225, 275, 275, 300, 325, 300, 300, 300,

                7, 6, 6, 7, 10, 13, 16, 22,
                6, 6, 7, 8, 10, 15, 20, 25,
                6, 7, 7, 9, 13, 18, 25, 30,
                7, 8, 9, 13, 16, 25, 34, 41,
                10, 10, 13, 16, 25, 36, 47, 56,
                13, 15, 18, 25, 36, 50, 63, 74,
                16, 20, 25, 34, 47, 63, 79, 90,
                22, 25, 30, 41, 56, 74, 90, 100
            };

            baseCbCrQuantTables = new int[]
            {
                17, 18, 24, 47, 99, 99, 99, 99,
                18, 21, 26, 66, 99, 99, 99, 99,
                24, 26, 56, 99, 99, 99, 99, 99,
                47, 66, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,
                99, 99, 99, 99, 99, 99, 99, 99,

                17, 18, 26, 54, 112, 128, 140, 148,
                18, 22, 30, 76, 128, 140, 148, 156,
                26, 30, 66, 128, 140, 148, 156, 164,
                54, 76, 128, 140, 148, 156, 164, 172,
                112, 128, 140, 148, 156, 164, 172, 180,
                128, 140, 148, 156, 164, 172, 180, 188,
                140, 148, 156, 164, 172, 180, 188, 196,
                148, 156, 164, 172, 180, 188, 196, 204,

                12, 12, 14, 18, 26, 34, 44, 54,
                12, 13, 15, 22, 30, 40, 52, 62,
                14, 15, 20, 30, 42, 54, 66, 78,
                18, 22, 30, 42, 56, 70, 84, 96,
                26, 30, 42, 56, 72, 88, 104, 116,
                34, 40, 54, 70, 88, 108, 124, 136,
                44, 52, 66, 84, 104, 124, 144, 156,
                54, 62, 78, 96, 116, 136, 156, 168,

                14, 14, 17, 28, 52, 66, 78, 88,
                14, 16, 19, 36, 66, 78, 88, 96,
                17, 19, 30, 58, 78, 88, 96, 104,
                28, 36, 58, 78, 88, 96, 104, 112,
                52, 66, 78, 88, 98, 108, 118, 128,
                66, 78, 88, 96, 108, 120, 132, 144,
                78, 88, 96, 104, 118, 132, 148, 160,
                88, 96, 104, 112, 128, 144, 160, 172,

                50, 50, 75, 150, 300, 300, 300, 300,
                50, 75, 75, 200, 300, 300, 300, 300,
                75, 75, 175, 300, 300, 300, 300, 300,
                150, 200, 300, 300, 300, 300, 300, 300,
                300, 300, 300, 300, 300, 300, 300, 300,
                300, 300, 300, 300, 300, 300, 300, 300,
                300, 300, 300, 300, 300, 300, 300, 300,
                300, 300, 300, 300, 300, 300, 300, 300,

                11, 11, 14, 22, 42, 53, 62, 70,
                11, 13, 15, 29, 53, 62, 70, 77,
                14, 15, 24, 46, 62, 70, 77, 83,
                22, 29, 46, 62, 70, 77, 83, 90,
                42, 53, 62, 70, 78, 86, 94, 102,
                53, 62, 70, 77, 86, 96, 106, 115,
                62, 70, 77, 83, 94, 106, 118, 128,
                70, 77, 83, 90, 102, 115, 128, 138
            };
        }

        // Base Y 量子化 値を返す
        private int GetBaseYQuantValue(int index, int preset)
        {
            EnsureBaseQuantTables();
            return baseYQuantTables[Mathf.Clamp(preset, 0, MaxQuantPreset) * 64 + index];
        }

        // Base A 量子化 値を返す
        private int GetBaseAQuantValue(int index, int preset)
        {
            EnsureBaseQuantTables();
            return baseAQuantTables[Mathf.Clamp(preset, 0, MaxQuantPreset) * 64 + index];
        }

        // Base Cb Cr 量子化 値を返す
        private int GetBaseCbCrQuantValue(int index, int preset)
        {
            EnsureBaseQuantTables();
            return baseCbCrQuantTables[Mathf.Clamp(preset, 0, MaxQuantPreset) * 64 + index];
        }

        // 経過 Msを処理する
        private float ElapsedMs(float startedAt)
        {
            if (!EnableTimingDiagnostics || startedAt <= 0f)
            {
                return 0f;
            }

            return Mathf.Max((Time.realtimeSinceStartup - startedAt) * 1000f, 0f);
        }

        // Timing Startを返す
        private float GetTimingStart()
        {
            return EnableTimingDiagnostics ? Time.realtimeSinceStartup : 0f;
        }

        // Update Timing Summaryを処理する
        private void UpdateTimingSummary(string label, int bytes)
        {
            if (!EnableTimingDiagnostics)
            {
                latestTimingSummary = "";
                return;
            }

            int totalMs = ReadTimingMillis(TimingOffsetRunTotalMs);
            int encodeGapMs = SumTimingMillis(TimingOffsetGpuStageGapSteps, GpuEncodeStageGapStepCount);
            int decodeGapMs = SumTimingMillis(TimingOffsetGpuDecodeDisplayGapSteps, GpuDecodeDisplayGapStepCount);
            latestTimingSummary = "Timing " + label
                + ": total=" + totalMs.ToString() + "ms"
                + " bytes=" + bytes.ToString()
                + " dct=" + FormatTimingMs(lastStage1EncodeMs)
                + " huffEnc=" + FormatTimingMs(lastHuffmanEncodeMs)
                + " rbWait=" + ReadTimingMillis(TimingOffsetReadbackTotalMs).ToString()
                + " rbStore=" + ReadTimingMillis(TimingOffsetReadbackStoreMs).ToString()
                + " pack=" + ReadTimingMillis(TimingOffsetPackMs).ToString()
                + " restore=" + ReadTimingMillis(TimingOffsetRestoreMs).ToString()
                + " upload=" + ReadTimingMillis(TimingOffsetDecodeUploadMs).ToString()
                + " compose=" + ReadTimingMillis(TimingOffsetComposeMs).ToString()
                + " out=" + ReadTimingMillis(TimingOffsetCompleteOutputMs).ToString()
                + " gapE/D=" + encodeGapMs.ToString() + "/" + decodeGapMs.ToString()
                + " fps(avg/worst)=" + FormatTimingFps(GetAverageTimingFps()) + "/" + FormatTimingFps(GetWorstTimingFps())
                + BuildEncodeTimingFpsSummary()
                + BuildDecodeTimingFpsSummary()
                + " split " + BuildTimingSplitSummary();
        }

        // Average Timing FPSを返す
        private float GetAverageTimingFps()
        {
            if (timingFrameCount <= 0 || timingFrameSeconds <= 0f)
            {
                return 0f;
            }

            return timingFrameCount / timingFrameSeconds;
        }

        // Worst Timing FPSを返す
        private float GetWorstTimingFps()
        {
            if (timingWorstFrameSeconds <= 0f)
            {
                return 0f;
            }

            return 1f / timingWorstFrameSeconds;
        }

        // format Timing FPSを処理する
        private string FormatTimingFps(float value)
        {
            if (value <= 0f)
            {
                return "0";
            }

            return Mathf.RoundToInt(value).ToString();
        }

        // 復号 Timing FPS Summaryを構築する
        private string BuildDecodeTimingFpsSummary()
        {
            if (!HasTimingPhaseFrames(TimingFpsPhaseDecodeUpload, TimingFpsPhaseDecodeOutput))
            {
                return "";
            }

            return " fpsD(up/bit/rle/scan/fix/idct/cmp/out)="
                + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseDecodeUpload)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseDecodeUpload))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseDecodeBitOffset)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseDecodeBitOffset))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseDecodeRle)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseDecodeRle))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseDecodeScan)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseDecodeScan))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseDecodeFix)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseDecodeFix))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseDecodeIdct)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseDecodeIdct))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseDecodeCompose)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseDecodeCompose))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseDecodeOutput)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseDecodeOutput));
        }

        // 符号化 Timing FPS Summaryを構築する
        private string BuildEncodeTimingFpsSummary()
        {
            if (!HasTimingPhaseFrames(TimingFpsPhaseEncodeDct, TimingFpsPhaseEncodeReadback))
            {
                return "";
            }

            return " fpsE(dct/rle/ac/dc/tbl/bit/ovf/blk/pay/rb)="
                + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeDct)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeDct))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeRle)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeRle))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeAcFreq)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeAcFreq))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeDcFreq)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeDcFreq))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeTable)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeTable))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeBitCount)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeBitCount))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeOverflow)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeOverflow))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeBlock)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeBlock))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodePayload)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodePayload))
                + "," + FormatTimingFps(GetAverageTimingPhaseFps(TimingFpsPhaseEncodeReadback)) + "/" + FormatTimingFps(GetWorstTimingPhaseFps(TimingFpsPhaseEncodeReadback));
        }

        // Timing phase Framesを保持しているか判定する
        private bool HasTimingPhaseFrames(int firstPhase, int lastPhase)
        {
            EnsureTimingPhaseFpsArrays();
            int first = Mathf.Clamp(firstPhase, 0, TimingFpsPhaseCount - 1);
            int last = Mathf.Clamp(lastPhase, 0, TimingFpsPhaseCount - 1);
            for (int phase = first; phase <= last; phase++)
            {
                if (timingPhaseFrameCounts[phase] > 0)
                {
                    return true;
                }
            }

            return false;
        }

        // Timing phase FPS Arraysを使用可能な状態にする
        private void EnsureTimingPhaseFpsArrays()
        {
            if (timingPhaseFrameSeconds == null || timingPhaseFrameSeconds.Length != TimingFpsPhaseCount)
            {
                timingPhaseFrameSeconds = new float[TimingFpsPhaseCount];
            }

            if (timingPhaseWorstFrameSeconds == null || timingPhaseWorstFrameSeconds.Length != TimingFpsPhaseCount)
            {
                timingPhaseWorstFrameSeconds = new float[TimingFpsPhaseCount];
            }

            if (timingPhaseFrameCounts == null || timingPhaseFrameCounts.Length != TimingFpsPhaseCount)
            {
                timingPhaseFrameCounts = new int[TimingFpsPhaseCount];
            }
        }

        // Record Timing phase Frameを処理する
        private void RecordTimingPhaseFrame(int phase, float frameSeconds)
        {
            if (phase < 0 || phase >= TimingFpsPhaseCount || frameSeconds <= 0f)
            {
                return;
            }

            EnsureTimingPhaseFpsArrays();
            timingPhaseFrameSeconds[phase] += frameSeconds;
            timingPhaseFrameCounts[phase]++;
            if (frameSeconds > timingPhaseWorstFrameSeconds[phase])
            {
                timingPhaseWorstFrameSeconds[phase] = frameSeconds;
            }
        }

        // Average Timing phase FPSを返す
        private float GetAverageTimingPhaseFps(int phase)
        {
            EnsureTimingPhaseFpsArrays();
            if (phase < 0 || phase >= TimingFpsPhaseCount || timingPhaseFrameCounts[phase] <= 0 || timingPhaseFrameSeconds[phase] <= 0f)
            {
                return 0f;
            }

            return timingPhaseFrameCounts[phase] / timingPhaseFrameSeconds[phase];
        }

        // Worst Timing phase FPSを返す
        private float GetWorstTimingPhaseFps(int phase)
        {
            EnsureTimingPhaseFpsArrays();
            if (phase < 0 || phase >= TimingFpsPhaseCount || timingPhaseWorstFrameSeconds[phase] <= 0f)
            {
                return 0f;
            }

            return 1f / timingPhaseWorstFrameSeconds[phase];
        }

        // Timing Split Summaryを構築する
        private string BuildTimingSplitSummary()
        {
            return "encBP=" + GetEncodeBlockPagePassCountForTiming().ToString()
                + " encPay=" + GetEncodePayloadPassCountForTiming().ToString()
                + " decBit=" + GetDecodeBitOffsetPassCountForTiming().ToString()
                + " decRle=" + GetDecodeRlePassCountForTiming().ToString()
                + " decScan=" + GetDecodeDcScanPassCountForTiming().ToString()
                + " decSym=" + GetDecodeSymbolPassCountForTiming().ToString()
                + " decIdct=" + GetDecodeIdctPassCountForTiming().ToString()
                + " groups(bp-slot/pay/bit/rle/scan/idct)=" + EncodeBlockPageSlotsPerGroup.ToString()
                + "/" + EncodePayloadRowsPerGroup.ToString()
                + "/" + DecodeBitOffsetLocalBlocksPerGroup.ToString()
                + "x" + DecodeBitOffsetChunkRowsPerGroup.ToString()
                + "/" + DecodeRleSlotsPerGroup.ToString()
                + "x" + DecodeRleChunkRowsPerGroup.ToString()
                + "/" + DecodeDcScanBlockRowsPerGroup.ToString()
                + "/" + DecodeIdctLocalRowsPerGroup.ToString()
                + "x" + DecodeIdctBlockRowsPerGroup.ToString();
        }

        // Sum Timing Millisを処理する
        private int SumTimingMillis(int baseOffset, int count)
        {
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                total = Mathf.Clamp(total + ReadTimingMillis(baseOffset + i * 4), 0, 2147483647);
            }

            return total;
        }

        // 符号化 ブロック page Pass 数用Timingを返す
        private int GetEncodeBlockPagePassCountForTiming()
        {
            int total = 0;
            for (int plane = 0; plane < 4; plane++)
            {
                if (ShouldMeasurePlaneForTiming(plane))
                {
                    total += GetEncodeBlockPageStepCount();
                }
            }

            return total;
        }

        // 符号化 payload Pass 数用Timingを返す
        private int GetEncodePayloadPassCountForTiming()
        {
            int total = 0;
            for (int plane = 0; plane < 4; plane++)
            {
                if (ShouldMeasurePlaneForTiming(plane))
                {
                    total += GetEncodePayloadRowGroupCount(plane);
                }
            }

            return total;
        }

        // 復号 bit offset Pass 数用Timingを返す
        private int GetDecodeBitOffsetPassCountForTiming()
        {
            int total = 0;
            for (int plane = 0; plane < 4; plane++)
            {
                if (ShouldMeasurePlaneForTiming(plane))
                {
                    total += DecodeBitOffsetLocalGroupCount * GetDecodeBitOffsetChunkRowGroupCount(plane);
                }
            }

            return total;
        }

        // 復号 RLE Pass 数用Timingを返す
        private int GetDecodeRlePassCountForTiming()
        {
            int total = 0;
            for (int plane = 0; plane < 4; plane++)
            {
                if (ShouldMeasurePlaneForTiming(plane))
                {
                    int rowGroupCount = GetDecodeRleChunkRowGroupCount(plane);
                    total += rowGroupCount
                        + Mathf.Max(DecodeRleSlotGroupCount - 1, 0) * rowGroupCount * 2
                        + rowGroupCount;
                }
            }

            return total;
        }

        // 復号 symbol Pass 数用Timingを返す
        private int GetDecodeSymbolPassCountForTiming()
        {
            int total = 0;
            for (int plane = 0; plane < 4; plane++)
            {
                if (ShouldMeasurePlaneForTiming(plane))
                {
                    total += DecodeRleSlotGroupCount * GetDecodeRleChunkRowGroupCount(plane);
                }
            }

            return total;
        }

        // 復号 DC scan Pass 数用Timingを返す
        private int GetDecodeDcScanPassCountForTiming()
        {
            int total = 0;
            for (int plane = 0; plane < 4; plane++)
            {
                if (ShouldMeasurePlaneForTiming(plane))
                {
                    int blockTotal = GetBlockWidth(plane) * GetBlockHeight(plane);
                    int scanPassCount = GetDcScanPassCount(blockTotal);
                    int rowGroupCount = GetDecodeDcScanBlockRowGroupCount(plane);
                    total += 2 + scanPassCount * (rowGroupCount + 1);
                }
            }

            return total;
        }

        // 復号 IDCT Pass 数用Timingを返す
        private int GetDecodeIdctPassCountForTiming()
        {
            int total = 0;
            for (int plane = 0; plane < 4; plane++)
            {
                if (ShouldMeasurePlaneForTiming(plane))
                {
                    total += DecodeIdctLocalGroupCount * GetDecodeIdctBlockRowGroupCount(plane) * 2;
                }
            }

            return total;
        }

        // Should Measure plane用Timingを処理する
        private bool ShouldMeasurePlaneForTiming(int plane)
        {
            if (plane == PlaneY) return true;
            if (plane == PlaneA) return hasAlpha;
            if (plane == PlaneCb || plane == PlaneCr) return sendColor;
            return false;
        }

        // Now Millisを返す
        private int GetNowMillis()
        {
            return Mathf.Clamp(Mathf.RoundToInt(Time.realtimeSinceStartup * 1000f), 0, 2147483647);
        }

        // 経過 Run Millisを返す
        private int GetElapsedRunMillis()
        {
            int startedAt = ReadTimingMillis(TimingOffsetRunStartMs);
            if (startedAt <= 0)
            {
                return 0;
            }

            return Mathf.Max(GetNowMillis() - startedAt, 0);
        }

        // Timing byte列を使用可能な状態にする
        private byte[] EnsureTimingBytes()
        {
            timingBytes = EnsureByteArray(timingBytes, TimingBytes);
            return timingBytes;
        }

        // Timing byte列を初期状態へ戻す
        private void ResetTimingBytes()
        {
            timingFrameSeconds = 0f;
            timingWorstFrameSeconds = 0f;
            timingFrameCount = 0;
            timingCurrentFpsPhase = TimingFpsPhaseNone;
            timingPhaseFrameSeconds = new float[TimingFpsPhaseCount];
            timingPhaseWorstFrameSeconds = new float[TimingFpsPhaseCount];
            timingPhaseFrameCounts = new int[TimingFpsPhaseCount];
            if (!EnableTimingDiagnostics)
            {
                timingBytes = null;
                return;
            }

            timingBytes = new byte[TimingBytes];
        }

        // Timing Millisを読み取る
        private int ReadTimingMillis(int offset)
        {
            if (!EnableTimingDiagnostics)
            {
                return 0;
            }

            byte[] bytes = EnsureTimingBytes();
            if (offset < 0 || offset + 4 > bytes.Length)
            {
                return 0;
            }

            return ReadInt(bytes, offset);
        }

        // Timing Millisを書き込む
        private void WriteTimingMillis(int offset, int value)
        {
            if (!EnableTimingDiagnostics)
            {
                return;
            }

            byte[] bytes = EnsureTimingBytes();
            if (offset >= 0 && offset + 4 <= bytes.Length)
            {
                WriteInt(bytes, offset, Mathf.Max(value, 0));
            }
        }

        // Add Timing Millisを処理する
        private void AddTimingMillis(int offset, float value)
        {
            if (!EnableTimingDiagnostics)
            {
                return;
            }

            AddTimingMillis(offset, Mathf.RoundToInt(Mathf.Max(value, 0f)));
        }

        // Add Timing Millisを処理する
        private void AddTimingMillis(int offset, int value)
        {
            if (!EnableTimingDiagnostics)
            {
                return;
            }

            int current = ReadTimingMillis(offset);
            int next = Mathf.Clamp(current + Mathf.Max(value, 0), 0, 2147483647);
            WriteTimingMillis(offset, next);
        }

        // Timing readback step offsetを返す
        private int GetTimingReadbackStepOffset(int baseOffset, int step)
        {
            return baseOffset + Mathf.Clamp(step, 0, HuffmanReadbackStepCount - 1) * 4;
        }

        // Timing readback step Millisを書き込む
        private void WriteTimingReadbackStepMillis(int baseOffset, int step, int value)
        {
            WriteTimingMillis(GetTimingReadbackStepOffset(baseOffset, step), value);
        }

        // GPU 符号化 段階 Gap stepを返す
        private int GetGpuEncodeStageGapStep(int plane, int kind)
        {
            return Mathf.Clamp(plane, 0, 3) * GpuEncodeStageGapKindsPerPlane + Mathf.Clamp(kind, 0, GpuEncodeStageGapKindsPerPlane - 1);
        }

        // Timing GPU 段階 Gap offsetを返す
        private int GetTimingGpuStageGapOffset(int step)
        {
            return TimingOffsetGpuStageGapSteps + Mathf.Clamp(step, 0, GpuEncodeStageGapStepCount - 1) * 4;
        }

        // GPU 復号 表示 Gap stepを返す
        private int GetGpuDecodeDisplayGapStep(int kind)
        {
            return GpuEncodeStageGapStepCount + Mathf.Clamp(kind, 0, GpuDecodeDisplayGapStepCount - 1);
        }

        // GPU 復号 段階 Gap stepを返す
        private int GetGpuDecodeStageGapStep(int plane, int kind)
        {
            return GetGpuDecodeDisplayGapStep(Mathf.Clamp(plane, 0, 3) * GpuDecodeStageGapKindsPerPlane + Mathf.Clamp(kind, 0, GpuDecodeStageGapKindsPerPlane - 1));
        }

        // GPU 復号 Compose Gap stepを返す
        private int GetGpuDecodeComposeGapStep()
        {
            return GetGpuDecodeDisplayGapStep(GpuDecodeStageGapKindsPerPlane * 4);
        }

        // GPU 復号 完了 Gap stepを返す
        private int GetGpuDecodeCompleteGapStep()
        {
            return GetGpuDecodeDisplayGapStep(GpuDecodeStageGapKindsPerPlane * 4 + 1);
        }

        // Timing GPU 復号 表示 Gap offsetを返す
        private int GetTimingGpuDecodeDisplayGapOffset(int step)
        {
            return TimingOffsetGpuDecodeDisplayGapSteps + Mathf.Clamp(step - GpuEncodeStageGapStepCount, 0, GpuDecodeDisplayGapStepCount - 1) * 4;
        }

        // Timing GPU 段階 Gap Millisを読み取る
        private int ReadTimingGpuStageGapMillis(int step)
        {
            if (step >= GpuEncodeStageGapStepCount)
            {
                return ReadTimingMillis(GetTimingGpuDecodeDisplayGapOffset(step));
            }

            return ReadTimingMillis(GetTimingGpuStageGapOffset(step));
        }

        // Timing GPU 段階 Gap Millisを書き込む
        private void WriteTimingGpuStageGapMillis(int step, int value)
        {
            int next = Mathf.Clamp(ReadTimingGpuStageGapMillis(step) + Mathf.Max(value, 0), 0, 2147483647);
            if (step >= GpuEncodeStageGapStepCount)
            {
                WriteTimingMillis(GetTimingGpuDecodeDisplayGapOffset(step), next);
                return;
            }

            WriteTimingMillis(GetTimingGpuStageGapOffset(step), next);
        }

        // format Timing Msを処理する
        private string FormatTimingMs(float value)
        {
            return Mathf.RoundToInt(Mathf.Max(value, 0f)).ToString();
        }

        // plane名を返す
        private string GetPlaneName(int plane)
        {
            if (plane == PlaneY) return "Y";
            if (plane == PlaneA) return "A";
            if (plane == PlaneCb) return "Cb";
            if (plane == PlaneCr) return "Cr";
            return plane.ToString();
        }

        // Huffman readback種別名を返す
        private string GetHuffmanReadbackKindName(int kind)
        {
            if (kind == HuffmanReadbackKindMetadata) return "metadata";
            if (kind == HuffmanReadbackKindWork) return "work";
            if (kind == HuffmanReadbackKindCoefficients) return "coeff";
            if (kind == HuffmanReadbackKindRleSymbols) return "rle";
            if (kind == HuffmanReadbackKindAcFrequency) return "acFreq";
            if (kind == HuffmanReadbackKindDcDelta) return "dcDelta";
            if (kind == HuffmanReadbackKindDcScan) return "dcScan";
            if (kind == HuffmanReadbackKindDcFrequency) return "dcFreq";
            if (kind == HuffmanReadbackKindDcCodes) return "dcCodeRT";
            if (kind == HuffmanReadbackKindAcCodes) return "acCodeRT";
            if (kind == HuffmanReadbackKindBlockBits) return "blockBits";
            if (kind == HuffmanReadbackKindBlockValid) return "blockValid";
            if (kind == HuffmanReadbackKindChunkValid) return "chunkValidRT";
            if (kind == HuffmanReadbackKindPayload) return "payload";
            return kind.ToString();
        }

        // GPU 診断 Arraysを使用可能な状態にする
        private void EnsureGpuDiagnosticArrays()
        {
            if (gpuDiagnosticBlitFrames == null || gpuDiagnosticBlitFrames.Length != GpuDiagnosticBlitCapacity)
            {
                gpuDiagnosticBlitStageSequences = new int[GpuDiagnosticBlitCapacity];
                gpuDiagnosticBlitFrames = new int[GpuDiagnosticBlitCapacity];
                gpuDiagnosticBlitFrameSeconds = new float[GpuDiagnosticBlitCapacity];
                gpuDiagnosticBlitSources = new string[GpuDiagnosticBlitCapacity];
                gpuDiagnosticBlitDestinations = new string[GpuDiagnosticBlitCapacity];
                gpuDiagnosticBlitMaterials = new string[GpuDiagnosticBlitCapacity];
                gpuDiagnosticBlitPasses = new int[GpuDiagnosticBlitCapacity];
            }

            if (gpuDiagnosticStageFrames == null || gpuDiagnosticStageFrames.Length != GpuDiagnosticStageCapacity)
            {
                gpuDiagnosticStageEvents = new string[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageCodes = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageEncodePlanes = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageEncodeSubStages = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageDecodePlanes = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageDecodeStages = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageFences = new bool[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageFrames = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageElapsedMillis = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageFrameCounts = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageFrameSeconds = new float[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageWorstFrameSeconds = new float[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageBlitStarts = new int[GpuDiagnosticStageCapacity];
                gpuDiagnosticStageBlitCounts = new int[GpuDiagnosticStageCapacity];
            }
        }

        // GPU Diagnosticsを初期状態へ戻す
        private void ResetGpuDiagnostics(string runLabel)
        {
            if (!enableGpuDiagnosticCapture)
            {
                return;
            }

            EnsureGpuDiagnosticArrays();
            gpuDiagnosticActive = true;
            gpuDiagnosticRunLabel = runLabel;
            gpuDiagnosticBlitCount = 0;
            gpuDiagnosticStageCount = 0;
            gpuDiagnosticBlitOverflow = false;
            gpuDiagnosticStageOverflow = false;
            gpuDiagnosticStagePending = false;
            gpuDiagnosticPendingStageSequence = -1;
            gpuDiagnosticPendingStageEvent = "";
            gpuDiagnosticPendingStageCode = -1;
            gpuDiagnosticPendingStageFrameCount = 0;
            gpuDiagnosticPendingStageFrameSeconds = 0f;
            gpuDiagnosticPendingStageWorstFrameSeconds = 0f;
        }

        // Record GPU 診断 Frameを処理する
        private void RecordGpuDiagnosticFrame(float frameSeconds)
        {
            if (!gpuDiagnosticActive || !gpuDiagnosticStagePending || frameSeconds <= 0f)
            {
                return;
            }

            gpuDiagnosticPendingStageFrameSeconds += frameSeconds;
            gpuDiagnosticPendingStageFrameCount++;
            if (frameSeconds > gpuDiagnosticPendingStageWorstFrameSeconds)
            {
                gpuDiagnosticPendingStageWorstFrameSeconds = frameSeconds;
            }
        }

        // GPU 診断 段階を開始する
        private void BeginGpuDiagnosticStage(string eventName, int stageCode, bool requiresFence)
        {
            if (!gpuDiagnosticActive)
            {
                return;
            }
            if (gpuDiagnosticStageCount >= GpuDiagnosticStageCapacity)
            {
                gpuDiagnosticStageOverflow = true;
                gpuDiagnosticStagePending = false;
                gpuDiagnosticPendingStageSequence = -1;
                return;
            }

            gpuDiagnosticStagePending = true;
            gpuDiagnosticPendingStageSequence = gpuDiagnosticStageCount;
            gpuDiagnosticPendingStageEvent = eventName;
            gpuDiagnosticPendingStageCode = stageCode;
            gpuDiagnosticPendingStageFence = requiresFence;
            gpuDiagnosticPendingStageStartedFrame = Time.frameCount;
            gpuDiagnosticPendingStageStartedAt = Time.realtimeSinceStartup;
            gpuDiagnosticPendingStageBlitStart = gpuDiagnosticBlitCount;
            gpuDiagnosticPendingStageFrameCount = 0;
            gpuDiagnosticPendingStageFrameSeconds = 0f;
            gpuDiagnosticPendingStageWorstFrameSeconds = 0f;
        }

        // GPU 診断 段階を完了状態にする
        private void CompleteGpuDiagnosticStage()
        {
            if (!gpuDiagnosticActive || !gpuDiagnosticStagePending)
            {
                return;
            }
            if (gpuDiagnosticStageCount >= GpuDiagnosticStageCapacity)
            {
                gpuDiagnosticStageOverflow = true;
                gpuDiagnosticStagePending = false;
                gpuDiagnosticPendingStageSequence = -1;
                return;
            }

            int index = gpuDiagnosticStageCount;
            gpuDiagnosticStageEvents[index] = gpuDiagnosticPendingStageEvent;
            gpuDiagnosticStageCodes[index] = gpuDiagnosticPendingStageCode;
            gpuDiagnosticStageEncodePlanes[index] = stage1PlaneMode;
            gpuDiagnosticStageEncodeSubStages[index] = encodeSubStage;
            gpuDiagnosticStageDecodePlanes[index] = decodePlane;
            gpuDiagnosticStageDecodeStages[index] = decodeStage;
            gpuDiagnosticStageFences[index] = gpuDiagnosticPendingStageFence;
            gpuDiagnosticStageFrames[index] = Mathf.Max(Time.frameCount - gpuDiagnosticPendingStageStartedFrame, 0);
            gpuDiagnosticStageElapsedMillis[index] = Mathf.Max(
                Mathf.RoundToInt((Time.realtimeSinceStartup - gpuDiagnosticPendingStageStartedAt) * 1000f),
                0);
            // SendCustomEventで同一frame内に完了したstageはUpdateを挟まないため、従来はfps=0になっていた
            // Blit記録と同じTime.deltaTimeを補完し、次回ログでbatch境界も実際のframe FPSとして比較できるようにする
            int stageFrameCount = gpuDiagnosticPendingStageFrameCount;
            float stageFrameSeconds = gpuDiagnosticPendingStageFrameSeconds;
            float stageWorstFrameSeconds = gpuDiagnosticPendingStageWorstFrameSeconds;
            if (stageFrameCount <= 0 && Time.deltaTime > 0f)
            {
                stageFrameCount = 1;
                stageFrameSeconds = Time.deltaTime;
                stageWorstFrameSeconds = Time.deltaTime;
            }
            gpuDiagnosticStageFrameCounts[index] = stageFrameCount;
            gpuDiagnosticStageFrameSeconds[index] = stageFrameSeconds;
            gpuDiagnosticStageWorstFrameSeconds[index] = stageWorstFrameSeconds;
            gpuDiagnosticStageBlitStarts[index] = gpuDiagnosticPendingStageBlitStart;
            gpuDiagnosticStageBlitCounts[index] = Mathf.Max(gpuDiagnosticBlitCount - gpuDiagnosticPendingStageBlitStart, 0);
            gpuDiagnosticStageCount++;
            gpuDiagnosticStagePending = false;
            gpuDiagnosticPendingStageSequence = -1;
        }

        // Record GPU 診断 Blitを処理する
        private void RecordGpuDiagnosticBlit(Texture source, RenderTexture destination, Material material, int pass)
        {
            if (!gpuDiagnosticActive)
            {
                return;
            }
            if (gpuDiagnosticBlitCount >= GpuDiagnosticBlitCapacity)
            {
                gpuDiagnosticBlitOverflow = true;
                return;
            }

            int index = gpuDiagnosticBlitCount;
            gpuDiagnosticBlitStageSequences[index] = gpuDiagnosticPendingStageSequence;
            gpuDiagnosticBlitFrames[index] = Time.frameCount;
            gpuDiagnosticBlitFrameSeconds[index] = Time.deltaTime;
            gpuDiagnosticBlitSources[index] = source != null ? source.name : "null";
            gpuDiagnosticBlitDestinations[index] = destination != null ? destination.name : "null";
            gpuDiagnosticBlitMaterials[index] = material != null ? material.name : "copy";
            gpuDiagnosticBlitPasses[index] = pass;
            gpuDiagnosticBlitCount++;
        }

        // GPU 診断 FPSを返す
        private int GetGpuDiagnosticFps(float frameSeconds, int frameCount)
        {
            if (frameSeconds <= 0f || frameCount <= 0)
            {
                return 0;
            }
            return Mathf.RoundToInt(frameCount / frameSeconds);
        }

        // GPU Diagnosticsをlogへ出力する
        private void DumpGpuDiagnostics(string result)
        {
            if (!gpuDiagnosticActive)
            {
                return;
            }

            CompleteGpuDiagnosticStage();
            gpuDiagnosticActive = false;
            string chunk = "[DCTH_GPU_DIAG_BEGIN] run=" + gpuDiagnosticRunLabel
                + " result=" + result
                + " stages=" + gpuDiagnosticStageCount.ToString()
                + " blits=" + gpuDiagnosticBlitCount.ToString()
                + " stageOverflow=" + (gpuDiagnosticStageOverflow ? "1" : "0")
                + " blitOverflow=" + (gpuDiagnosticBlitOverflow ? "1" : "0")
                + " timeoutDisabled=" + (disableOperationTimeoutForGpuDiagnostics ? "1" : "0") + "\n";

            for (int i = 0; i < gpuDiagnosticStageCount; i++)
            {
                int averageFps = GetGpuDiagnosticFps(
                    gpuDiagnosticStageFrameSeconds[i],
                    gpuDiagnosticStageFrameCounts[i]);
                int worstFps = gpuDiagnosticStageWorstFrameSeconds[i] > 0f
                    ? Mathf.RoundToInt(1f / gpuDiagnosticStageWorstFrameSeconds[i])
                    : 0;
                string line = "S,seq=" + i.ToString()
                    + ",event=" + gpuDiagnosticStageEvents[i]
                    + ",code=" + gpuDiagnosticStageCodes[i].ToString()
                    + ",encPlane=" + gpuDiagnosticStageEncodePlanes[i].ToString()
                    + ",encSub=" + gpuDiagnosticStageEncodeSubStages[i].ToString()
                    + ",decPlane=" + gpuDiagnosticStageDecodePlanes[i].ToString()
                    + ",decStage=" + gpuDiagnosticStageDecodeStages[i].ToString()
                    + ",fence=" + (gpuDiagnosticStageFences[i] ? "1" : "0")
                    + ",frames=" + gpuDiagnosticStageFrames[i].ToString()
                    + ",elapsedMs=" + gpuDiagnosticStageElapsedMillis[i].ToString()
                    + ",fpsAvg=" + averageFps.ToString()
                    + ",fpsWorst=" + worstFps.ToString()
                    + ",blitStart=" + gpuDiagnosticStageBlitStarts[i].ToString()
                    + ",blitCount=" + gpuDiagnosticStageBlitCounts[i].ToString() + "\n";
                if (chunk.Length + line.Length > GpuDiagnosticLogChunkCharacters)
                {
                    Debug.Log(chunk);
                    chunk = "[DCTH_GPU_DIAG_STAGE_CONT]\n";
                }
                chunk += line;
            }

            for (int i = 0; i < gpuDiagnosticBlitCount; i++)
            {
                int submitFps = gpuDiagnosticBlitFrameSeconds[i] > 0f
                    ? Mathf.RoundToInt(1f / gpuDiagnosticBlitFrameSeconds[i])
                    : 0;
                string line = "B,idx=" + i.ToString()
                    + ",stage=" + gpuDiagnosticBlitStageSequences[i].ToString()
                    + ",frame=" + gpuDiagnosticBlitFrames[i].ToString()
                    + ",submitFps=" + submitFps.ToString()
                    + ",src=" + gpuDiagnosticBlitSources[i]
                    + ",dst=" + gpuDiagnosticBlitDestinations[i]
                    + ",mat=" + gpuDiagnosticBlitMaterials[i]
                    + ",pass=" + gpuDiagnosticBlitPasses[i].ToString() + "\n";
                if (chunk.Length + line.Length > GpuDiagnosticLogChunkCharacters)
                {
                    Debug.Log(chunk);
                    chunk = "[DCTH_GPU_DIAG_BLIT_CONT]\n";
                }
                chunk += line;
            }

            chunk += "[DCTH_GPU_DIAG_END]\n";
            Debug.Log(chunk);
        }

        // log Timingを処理する
        private void LogTiming(string message)
        {
            // 製品buildでは意図的にno-op。encode/decode制御を変えず一時timing計測を戻せるよう呼出先だけ残し、
            // ここからVRChat logは出さない
        }

        // Solid Textureを使用可能な状態にする
        private Texture EnsureSolidTexture(bool white)
        {
            if (generatedOpaqueWhiteTexture == null || generatedOpaqueWhiteTexture.width != 1 || generatedOpaqueWhiteTexture.height != 1)
            {
                generatedOpaqueWhiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                generatedOpaqueWhiteTexture.name = "IC_Library_OpaqueWhite";
                generatedOpaqueWhiteTexture.filterMode = FilterMode.Point;
                generatedOpaqueWhiteTexture.wrapMode = TextureWrapMode.Clamp;
                generatedOpaqueWhiteTexture.SetPixel(0, 0, Color.white);
                generatedOpaqueWhiteTexture.Apply(false, false);
            }

            return generatedOpaqueWhiteTexture;
        }

        // Neutral Gray Textureを使用可能な状態にする
        private Texture EnsureNeutralGrayTexture()
        {
            if (generatedNeutralGrayTexture == null || generatedNeutralGrayTexture.width != 1 || generatedNeutralGrayTexture.height != 1)
            {
                generatedNeutralGrayTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                generatedNeutralGrayTexture.name = "IC_Library_NeutralGray";
                generatedNeutralGrayTexture.filterMode = FilterMode.Point;
                generatedNeutralGrayTexture.wrapMode = TextureWrapMode.Clamp;
                generatedNeutralGrayTexture.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
                generatedNeutralGrayTexture.Apply(false, false);
            }

            return generatedNeutralGrayTexture;
        }

        // Transparent Textureを使用可能な状態にする
        private Texture EnsureTransparentTexture()
        {
            if (generatedTransparentTexture == null || generatedTransparentTexture.width != 1 || generatedTransparentTexture.height != 1)
            {
                generatedTransparentTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                generatedTransparentTexture.name = "IC_Library_Transparent";
                generatedTransparentTexture.filterMode = FilterMode.Point;
                generatedTransparentTexture.wrapMode = TextureWrapMode.Clamp;
                generatedTransparentTexture.SetPixel(0, 0, Color.clear);
                generatedTransparentTexture.Apply(false, false);
            }

            return generatedTransparentTexture;
        }

        // Run 状態を初期状態へ戻す
        private void ResetRunState()
        {
            CancelGpuStageDelay();
            CancelWarmup();
            ResetCpuHuffmanState();
            sourceNormalizationReadbackRequestQueued = false;
            sourceNormalizationReadbackPending = false;
            sourceNormalizationReadbackTexture = null;
            ClearPendingSourceNormalizationBytes();
            activeEncodeHasAlpha = false;
            huffmanReadbackPending = false;
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            retryCompressionOnFailure = false;
            gpuStage = 0;
            encodeSubStage = 0;
            decodePlane = 0;
            decodeStage = 0;
            decodeLocalIndex = 0;
            decodeBitOffsetPing = 0;
            decodeUploadStep = 0;
            decodeUploadElapsedMs = 0f;
            ClearPendingByteCopyState();
            if (EnableTimingDiagnostics)
            {
                pendingGpuStageGapStep = -1;
            }
            outputReady = false;
            completedImageId = 0;
            compressedBytesReady = false;
            compressedBytesFailed = false;
            huffmanEncodeComplete = false;
            huffmanEncodeFailed = false;
            huffmanDecodeComplete = false;
            huffmanDecodeFailed = false;
            compressedBytes = GetEmptyBytes();
            latestTimingSummary = "";
            ClearHuffmanBytes();
            ClearOutputRenderTextures();
        }

        // 復号 Run 状態を初期状態へ戻す
        private void ResetDecodeRunState()
        {
            CancelGpuStageDelay();
            CancelWarmup();
            ResetCpuHuffmanState();
            sourceNormalizationReadbackRequestQueued = false;
            sourceNormalizationReadbackPending = false;
            sourceNormalizationReadbackTexture = null;
            ClearPendingSourceNormalizationBytes();
            activeEncodeHasAlpha = false;
            huffmanReadbackPending = false;
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            retryCompressionOnFailure = false;
            gpuStage = 0;
            encodeSubStage = 0;
            decodePlane = 0;
            decodeStage = 0;
            decodeLocalIndex = 0;
            decodeBitOffsetPing = 0;
            decodeUploadStep = 0;
            decodeUploadElapsedMs = 0f;
            ClearPendingByteCopyState();
            if (EnableTimingDiagnostics)
            {
                pendingGpuStageGapStep = -1;
            }
            outputReady = false;
            completedImageId = 0;
            huffmanDecodeComplete = false;
            huffmanDecodeFailed = false;
            latestTimingSummary = "";
            ClearHuffmanBytes();
        }

        // 自動 再試行 状態を初期状態へ戻す
        private void ResetAutoRetryState()
        {
            capacityPrepassReadbackRequestQueued = false;
            capacityPrepassReadbackPending = false;
            capacityPrepassReadbackDrainFramesRemaining = 0;
            capacityPrepassCompletedForRequest = false;
            capacityPrepassColorDownsampleReady = false;
            capacityPrepassPreparePlane = 0;
            capacityPrepassPrepareSubStage = 0;
            capacityPrepassPrepareBlockMapReady = false;
            capacityPrepassPreparePreviousBlockMapReady = false;
            capacityPrepassCandidatePlane = 0;
            capacityPrepassCandidateSubStage = 0;
            capacityPrepassCandidateQuality = 0;
            capacityPrepassCandidateSetupStep = 0;
            capacityPrepassCandidateAttempt = 0;
            capacityPrepassStartedMs = 0;
            capacityFullCompressionStartedMs = 0;
            capacityTotalStartedMs = 0;
            capacityPrepassRan = false;
            capacityPrepassRequestedQuality = 0;
            capacityPrepassSelectedQuality = 0;
            capacityPrepassEstimatedBytes = 0;
            capacityPrepassActualBytes = 0;
            capacityPrepassSampleBlockCount = 0;
            capacityPrepassYSampleBlockCount = 0;
            capacityPrepassASampleBlockCount = 0;
            capacityPrepassCbSampleBlockCount = 0;
            capacityPrepassCrSampleBlockCount = 0;
            capacityPrepassFullCompressionRetryCount = 0;
            capacityPrepassFallbackReason = "";
            capacityPrepassDurationMs = 0f;
            capacityFullCompressionDurationMs = 0f;
            capacityTotalDurationMs = 0f;
            ReleaseCapacityPrepassResources();
            qualityWasExplicitlyChangedByRetry = false;
            qualityWasExplicitlyLoweredByRetry = false;
            qualityRequestedBeforeAutoRetry = quality;
            qualityBeforeAutoRetry = quality;
            qualityAfterAutoRetry = quality;
            qualityAutoRetryCount = 0;
            lastQualityRetryRequiredBytes = 0;
            lastQualityRetryTargetBytes = 0;
            lastQualityRetryPlane = -1;
            lastCompressionFailureReason = "";
            lastCompressionRetryReason = "";
            ResetQualityRetryPredictionInput();
        }

        // 画質 再試行 Prediction Inputを初期状態へ戻す
        private void ResetQualityRetryPredictionInput()
        {
            qualityRetryReason = QualityRetryReasonNone;
            qualityRetryRequiredBytes = 0;
            qualityRetryTargetBytes = 0;
            qualityRetryPlane = -1;
        }

        // 画質 再試行 Prediction Inputを設定する
        private void SetQualityRetryPredictionInput(int reason, int requiredBytes, int targetBytes, int plane)
        {
            qualityRetryReason = reason;
            qualityRetryRequiredBytes = Mathf.Max(requiredBytes, 0);
            qualityRetryTargetBytes = Mathf.Max(targetBytes, 0);
            qualityRetryPlane = plane;
        }

        // Total byte Prediction Targetを返す
        private int GetTotalBytePredictionTarget(int maximumBytes)
        {
            // longで乗算し、大きな上限値でもint overflowによってtargetが負数にならないようにする
            long target = (long)Mathf.Max(maximumBytes, 1) * (long)TotalBytePredictionTargetPercent / 100L;
            if (target < 1L)
            {
                return 1;
            }
            if (target > 2147483647L)
            {
                return 2147483647;
            }

            return (int)target;
        }

        // 出力 描画 Textureを初期化する
        private void ClearOutputRenderTextures()
        {
            Texture transparent = EnsureTransparentTexture();
            RenderTexture postprocessedOutput = postprocessedOutputRenderTexture;
            if (postprocessedOutput != null)
            {
                TrackedBlit(transparent, postprocessedOutput);
            }

        }

        // 出力 byte Flagsを返す
        private int GetOutputByteFlags()
        {
            int flags = 0;
            if (activeEncodeHasAlpha)
            {
                flags |= FlagHasAlpha;
            }

            if (sendColor)
            {
                flags |= FlagHasColor;
            }

            if (halfSizeCbCr)
            {
                flags |= FlagHalfSizeCbCr;
            }

            if (skipOutputBlockCompression)
            {
                flags |= FlagSkipOutputBlockCompression;
            }

            if (outputBlockCompressionSrgb)
            {
                flags |= FlagSrgbOutputBlockCompression;
            }

            if (!activeEncodeSrgb)
            {
                flags |= FlagLinearDctEncoding;
            }

            if (!activeSourceSrgb)
            {
                flags |= FlagLinearOutputTexture;
            }

            return flags;
        }

        // 出力 alpha modeを返す
        private int GetOutputAlphaMode()
        {
            return activeEncodeHasAlpha ? 1 : 0;
        }

        // Runtime 状態 byte列を使用可能な状態にする
        private byte[] EnsureRuntimeStateBytes()
        {
            runtimeStateBytes = EnsureByteArray(runtimeStateBytes, RuntimeStateBytes);
            if (runtimeStateBytes[RuntimeStateInitialized] == 0)
            {
                WriteInt(runtimeStateBytes, RuntimeStateQuantQuality, -1);
                WriteInt(runtimeStateBytes, RuntimeStateQuantPreset, -1);
                runtimeStateBytes[RuntimeStateInitialized] = 1;
            }

            return runtimeStateBytes;
        }

        // Runtime 状態 Boolを読み取る
        private bool ReadRuntimeStateBool(int offset)
        {
            byte[] bytes = EnsureRuntimeStateBytes();
            return offset >= 0 && offset < bytes.Length && bytes[offset] != 0;
        }

        // Runtime 状態 Boolを書き込む
        private void WriteRuntimeStateBool(int offset, bool value)
        {
            byte[] bytes = EnsureRuntimeStateBytes();
            if (offset >= 0 && offset < bytes.Length)
            {
                bytes[offset] = value ? (byte)1 : (byte)0;
            }
        }

        // Runtime 状態 Intを読み取る
        private int ReadRuntimeStateInt(int offset)
        {
            byte[] bytes = EnsureRuntimeStateBytes();
            if (offset < 0 || offset + 4 > bytes.Length)
            {
                return 0;
            }

            return ReadInt(bytes, offset);
        }

        // Runtime 状態 Intを書き込む
        private void WriteRuntimeStateInt(int offset, int value)
        {
            byte[] bytes = EnsureRuntimeStateBytes();
            if (offset >= 0 && offset + 4 <= bytes.Length)
            {
                WriteInt(bytes, offset, value);
            }
        }

        // Runtime 状態 Millisを読み取る
        private float ReadRuntimeStateMillis(int offset)
        {
            return ReadRuntimeStateInt(offset) / 1000f;
        }

        // Runtime 状態 Millisを書き込む
        private void WriteRuntimeStateMillis(int offset, float value)
        {
            float clamped = Mathf.Clamp(value, 0f, 2147483f);
            WriteRuntimeStateInt(offset, Mathf.RoundToInt(clamped * 1000f));
        }

        // Status byte列を使用可能な状態にする
        private byte[] EnsureStatusBytes()
        {
            statusBytes = EnsureByteArray(statusBytes, StatusBytes);
            int length = ReadInt(statusBytes, StatusByteOffsetLength);
            if (length <= 0)
            {
                WriteStatusString("Ready.");
            }

            return statusBytes;
        }

        // Status Stringを読み取る
        private string ReadStatusString()
        {
            byte[] bytes = EnsureStatusBytes();
            int length = ReadInt(bytes, StatusByteOffsetLength);
            int capacity = StatusBytes - StatusByteOffsetData;
            if (length < 0)
            {
                length = 0;
            }
            else if (length > capacity)
            {
                length = capacity;
            }

            string value = "";
            for (int i = 0; i < length; i++)
            {
                value += (char)bytes[StatusByteOffsetData + i];
            }

            return value;
        }

        // Status Stringを書き込む
        private void WriteStatusString(string value)
        {
            statusBytes = EnsureByteArray(statusBytes, StatusBytes);
            byte[] bytes = statusBytes;
            string source = value != null ? value : "";
            int capacity = StatusBytes - StatusByteOffsetData;
            int previousLength = Mathf.Clamp(ReadInt(bytes, StatusByteOffsetLength), 0, capacity);
            int length = Mathf.Min(source.Length, capacity);
            WriteInt(bytes, StatusByteOffsetLength, length);
            for (int i = 0; i < length; i++)
            {
                int code = source[i];
                if (code < 0 || code > 255)
                {
                    code = 63;
                }

                bytes[StatusByteOffsetData + i] = (byte)code;
            }

            // status文字列は小さいがencode/decode中に頻繁に更新される
            // 固定buffer全体を書き直さず、前statusから残る末尾だけをclearする
            for (int i = length; i < previousLength; i++)
            {
                bytes[StatusByteOffsetData + i] = 0;
            }
        }

        // Intを書き込む
        private void WriteInt(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)(value & 255);
            bytes[offset + 1] = (byte)((value >> 8) & 255);
            bytes[offset + 2] = (byte)((value >> 16) & 255);
            bytes[offset + 3] = (byte)((value >> 24) & 255);
        }

        // Intを読み取る
        private int ReadInt(byte[] bytes, int offset)
        {
            return bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
        }

        // Huffman符号化を失敗状態にして後処理へ進める
        private void FailHuffmanEncode(string message)
        {
            CapturePendingGpuReadbacksForRelease();
            CancelGpuStageDelay();
            ResetCpuHuffmanState();
            huffmanReadbackPending = false;
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            huffmanEncodeComplete = false;
            huffmanEncodeFailed = true;
            outputReady = false;
            isRunning = false;
            HandleCompressionFailure(message, false);
        }

        // 実行中処理をtimeout失敗として終了する
        private void FailOperationTimeout()
        {
            bool expansion = currentOperationIsExpansion;
            operationLastProgressAtRealtime = 0f;
            CapturePendingGpuReadbacksForRelease();
            CancelGpuStageDelay();
            ClearPendingSourceNormalizationBytes();
            ClearPendingByteCopyState();
            ClearPendingPayloadStore();
            ClearPendingChunkOffsetBuild();
            ClearPendingDecodePayloadUploadBuild();
            if (expansion)
            {
                FailHuffmanDecode("DCTH expansion timed out.");
            }
            else
            {
                FailCompressedBytes("DCTH compression timed out.");
            }
        }

        // Huffman 符号化 With 画質 再試行を失敗状態にする
        private void FailHuffmanEncodeWithQualityRetry(string message)
        {
            CancelGpuStageDelay();
            ResetCpuHuffmanState();
            huffmanReadbackPending = false;
            huffmanReadbackDrainFramesRemaining = 0;
            huffmanReadbackRequestQueued = false;
            huffmanEncodeComplete = false;
            huffmanEncodeFailed = true;
            outputReady = false;
            isRunning = false;
            HandleCompressionFailure(message, true);
        }

        // 圧縮byte列の生成を失敗状態にして後処理へ進める
        private void FailCompressedBytes(string message)
        {
            CapturePendingGpuReadbacksForRelease();
            CancelGpuStageDelay();
            compressedBytesReady = false;
            compressedBytesFailed = true;
            outputReady = false;
            isRunning = false;
            HandleCompressionFailure(message, false);
        }

        // 圧縮結果 byte列 With 画質 再試行を失敗状態にする
        private void FailCompressedBytesWithQualityRetry(string message)
        {
            CancelGpuStageDelay();
            compressedBytesReady = false;
            compressedBytesFailed = true;
            outputReady = false;
            isRunning = false;
            HandleCompressionFailure(message, true);
        }

        // Huffman復号を失敗状態にして後処理へ進める
        private void FailHuffmanDecode(string message)
        {
            CapturePendingGpuReadbacksForRelease();
            CancelGpuStageDelay();
            huffmanDecodeComplete = false;
            huffmanDecodeFailed = true;
            outputReady = false;
            isRunning = false;
            HandleCompressionFailure(message, false);
        }

        // 圧縮失敗時の再試行判定、リソース解放、失敗通知を行う
        private void HandleCompressionFailure(string message, bool canRetryWithLowerQuality)
        {
            inputCopyPending = false;
            lastCompressionFailureReason = message;
            if (retryCompressionOnFailure && canRetryWithLowerQuality && TryPrepareLowerQualityRetry(message))
            {
                ScheduleGpuStageDelay(nameof(_RetryCompressionAfterQualityChange));
                return;
            }

            SetStatus("Failed: " + message);
            DumpGpuDiagnostics(activeRequestIsExpansion ? "expansion-failed" : "compression-failed");
            // 異常終了時のGPU処理とreadback完了を待って関連RTを一括解放する
            ReleaseFailedGpuResourcesWhenReadbacksComplete();
            NotifyCompressionFailed();
        }

        // Prepare Lower 画質 再試行を試行する
        private bool TryPrepareLowerQualityRetry(string message)
        {
            if (!autoRetryLowerQuality)
            {
                return false;
            }

            if (quality <= MinQuality)
            {
                return false;
            }

            if (qualityAutoRetryCount >= maxQualityRetryCount)
            {
                return false;
            }

            int before = quality;
            int after = PredictLowerQualityFromByteTarget(before);
            if (after >= before)
            {
                int step = Mathf.Clamp(retryQualityStep, 1, MaxQuality - MinQuality);
                after = Mathf.Max(MinQuality, before - step);
            }
            if (after >= before)
            {
                return false;
            }

            lastQualityRetryRequiredBytes = qualityRetryRequiredBytes;
            lastQualityRetryTargetBytes = qualityRetryTargetBytes;
            lastQualityRetryPlane = qualityRetryPlane;
            quality = after;
            qualityWasExplicitlyChangedByRetry = true;
            qualityWasExplicitlyLoweredByRetry = true;
            qualityBeforeAutoRetry = before;
            qualityAfterAutoRetry = after;
            qualityAutoRetryCount++;
            if (activeCompressionUsesCapacityLimit)
            {
                capacityPrepassFullCompressionRetryCount++;
            }
            lastCompressionRetryReason = message;
            SetStatus("Failed: " + message
                + " Retrying with predicted lower quality " + before.ToString() + " -> " + after.ToString()
                + " (required/target=" + lastQualityRetryRequiredBytes.ToString() + "/" + lastQualityRetryTargetBytes.ToString() + ").");
            ResetQualityRetryPredictionInput();
            NotifyCompressionRetrying();
            return true;
        }

        // Predict Lower 画質 From byte Targetを処理する
        private int PredictLowerQualityFromByteTarget(int currentQuality)
        {
            if (qualityRetryReason == QualityRetryReasonNone
                || qualityRetryRequiredBytes <= qualityRetryTargetBytes
                || qualityRetryTargetBytes <= 0)
            {
                return currentQuality;
            }

            // block上限は失敗したplane自身の量子化表を使う。総byte上限は複数planeの合計なので、
            // 全planeで共通するquality変化の代表としてY表を使い、結果は次の実圧縮で必ず再検証する
            int plane = qualityRetryReason == QualityRetryReasonBlockCapacity
                ? ClampPlane(qualityRetryPlane)
                : PlaneY;
            int currentStrength = GetQuantStrengthMetric(currentQuality, quantPreset, plane);
            if (currentStrength <= 0)
            {
                return currentQuality;
            }

            // required/targetの比率を量子化強度へ掛ける。longで計算し、4MBを超える容量でも
            // int乗算のoverflowによってPC/Mobileの予測Qualityが分岐しないようにする
            int safetyPercent = qualityRetryReason == QualityRetryReasonBlockCapacity
                ? BlockQualityPredictionSafetyPercent
                : capacityPrepassCompletedForRequest
                    ? TotalQualityRetrySafetyPercent
                    : TotalQualityPredictionSafetyPercent;
            long numerator = (long)currentStrength
                * (long)qualityRetryRequiredBytes
                * (long)safetyPercent;
            long denominator = (long)qualityRetryTargetBytes * 100L;
            long predictedStrengthLong = (numerator + denominator - 1L) / denominator;
            int predictedStrength = predictedStrengthLong > 2147483647L
                ? 2147483647
                : (int)predictedStrengthLong;
            predictedStrength = Mathf.Max(predictedStrength, currentStrength + 1);

            return FindHighestQualityForQuantStrength(currentQuality - 1, quantPreset, plane, predictedStrength);
        }

        // Find Highest 画質用量子化 Strengthを処理する
        private int FindHighestQualityForQuantStrength(int maximumQuality, int preset, int plane, int targetStrength)
        {
            int low = MinQuality;
            int high = Mathf.Clamp(maximumQuality, MinQuality, MaxQuality);
            int best = MinQuality;

            // qualityが下がるほど量子化値の合計は単調に増えるため、1刻みの探索ではなく二分探索できる
            // 最大7回の比較で1..100を探索し、各比較も固定64要素なので端末間で処理量が安定する
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int strength = GetQuantStrengthMetric(middle, preset, plane);
                if (strength >= targetStrength)
                {
                    best = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return best;
        }

        // 量子化 Strength Metricを返す
        private int GetQuantStrengthMetric(int qualityValue, int preset, int plane)
        {
            int strength = 0;
            // Q100はJPEG scaleが0でも実際の量子化値は全要素1へclampされる
            // scale値ではなく実際にGPUへ渡す64要素の合計を使い、Q100だけ予測が弱くなるのを防ぐ
            for (int index = 0; index < 64; index++)
            {
                strength += GetScaledQuantValue(index, preset, plane, qualityValue);
            }

            return strength;
        }

        // 圧縮 再試行を通知する
        private void NotifyCompressionRetrying()
        {
            if (activeRequestReceiver != null && !activeRequestIsExpansion)
            {
                activeRequestReceiver.SendCustomEvent(CompressionRetryingEventName);
                return;
            }
            SendConfiguredEvent(outputReadyReceiver, outputRetryingEventName);
        }

        // 圧縮 失敗を通知する
        private void NotifyCompressionFailed()
        {
            if (failedGpuResourceReleasePending) { failureNotificationPending = true; return; }
            failureNotificationPending = false;
            bool expansion = activeRequestHandleId != InvalidHandleId
                ? activeRequestIsExpansion
                : currentOperationIsExpansion || huffmanDecodeFailed;
            UdonBehaviour requestReceiver = activeRequestReceiver;
            if (activeRequestHandleId != InvalidHandleId)
            {
                failedRequestHandleId = activeRequestHandleId;
                failedRequestWasExpansion = expansion;
                activeRequestHandleId = InvalidHandleId;
            }
            activeRequestReceiver = null;
            compressionPending = false;
            if (expansion)
            {
                expandFailed = true;
                expandComplete = false;
                if (requestReceiver != null)
                {
                    requestReceiver.SendCustomEvent(ExpansionFailedEventName);
                    return;
                }
                SendConfiguredEvent(outputReadyReceiver, outputFailedEventName);
                return;
            }

            compressionFailed = true;
            compressionComplete = false;
            if (requestReceiver != null)
            {
                requestReceiver.SendCustomEvent(CompressionFailedEventName);
                return;
            }
            SendConfiguredEvent(compressedBytesReadyReceiver, compressedBytesFailedEventName);
        }

        // 圧縮結果 byte列 準備完了を通知する
        private void NotifyCompressedBytesReady()
        {
            ReleaseOwnedInput();
            if (activeRequestHandleId != InvalidHandleId && !activeRequestIsExpansion)
            {
                completedRequestHandleId = activeRequestHandleId;
                completedRequestWasExpansion = false;
                activeRequestHandleId = InvalidHandleId;
            }
            compressionProgress01 = 1f;
            compressionProgressStage = ProgressStageComplete;
            if (activeRequestReceiver != null && !activeRequestIsExpansion)
            {
                UdonBehaviour receiver = activeRequestReceiver;
                activeRequestReceiver = null;
                receiver.SendCustomEvent(CompressionSucceededEventName);
                return;
            }
            SendConfiguredEvent(compressedBytesReadyReceiver, compressedBytesReadyEventName);
        }

        // 展開 準備完了を通知する
        private void NotifyExpansionReady()
        {
            if (activeRequestHandleId != InvalidHandleId && activeRequestIsExpansion)
            {
                completedRequestHandleId = activeRequestHandleId;
                completedRequestWasExpansion = true;
                activeRequestHandleId = InvalidHandleId;
            }
            if (activeRequestReceiver != null && activeRequestIsExpansion)
            {
                UdonBehaviour receiver = activeRequestReceiver;
                activeRequestReceiver = null;
                receiver.SendCustomEvent(ExpansionSucceededEventName);
                return;
            }
            SendConfiguredEvent(outputReadyReceiver, outputReadyEventName);
        }

        // request handle IDを生成する
        private int GenerateRequestHandleId()
        {
            nextRequestHandleId++;
            if (nextRequestHandleId <= 0)
            {
                nextRequestHandleId = 1;
            }
            return nextRequestHandleId;
        }

        // warmup handle IDを生成する
        private int GenerateWarmupHandleId()
        {
            nextWarmupHandleId++;
            if (nextWarmupHandleId <= 0)
            {
                nextWarmupHandleId = 1;
            }
            return nextWarmupHandleId;
        }

        // Configured イベントを通知する
        private void SendConfiguredEvent(UdonBehaviour receiver, string eventName)
        {
            if (receiver == null || eventName == null || eventName.Length == 0)
            {
                return;
            }

            receiver.SendCustomEvent(eventName);
        }

        // ここへ来る時点でGPU使用は終了している。所有している入力だけを破棄する
        private void ReleaseOwnedInput()
        {
            Texture input = ownedInputTexture;
            ownedInputTexture = null;
            if (sourceTexture == input || sourceTexture == borrowedCopySource) sourceTexture = null;
            borrowedCopySource = null;
            if (input == null) return;
            Destroy(input);
        }

        private void FlushTerminalNotifications()
        {
            if (failedGpuResourceReleasePending) return;
            if (failureNotificationPending) NotifyCompressionFailed();
            if (cancellationNotificationPending)
            {
                cancellationNotificationPending = false;
                UdonBehaviour receiver = cancellationReceiver;
                cancellationReceiver = null;
                if (receiver != null) receiver.SendCustomEvent(cancelledOperationIsExpansion ? "_HandleDcthExpandCancelled" : "_HandleDcthCompressCancelled");
            }
            if (disposalRequested)
            {
                _ClearOutputTexture();
                normalizedSourceTexture = ReleaseRuntimeTexture(normalizedSourceTexture);
                generatedGammaEncodeTexture = ReleaseRuntimeTexture(generatedGammaEncodeTexture);
                generatedQuantReciprocalTexture = ReleaseRuntimeTexture(generatedQuantReciprocalTexture);
                runtimeQuantTexture = ReleaseRuntimeTexture(runtimeQuantTexture);
                generatedOpaqueWhiteTexture = ReleaseRuntimeTexture(generatedOpaqueWhiteTexture);
                generatedNeutralGrayTexture = ReleaseRuntimeTexture(generatedNeutralGrayTexture);
                generatedTransparentTexture = ReleaseRuntimeTexture(generatedTransparentTexture);
                ReleaseOwnedMaterials();
            }
        }

        // 使用終了時はDestroyの前に呼び、IsDisposedを待つ。待機中もcomponentは有効に保つ
        public void _Dispose()
        {
            if (disposalRequested) return;
            disposalRequested = true;
            _StopCompression();
            _ClearOutputTexture();
            _ClearSourceBytes();
            _ClearCompressedBytes();
            FlushTerminalNotifications();
        }

        // Renderer.materialsで複製したものだけを破棄し、元assetは変更しない
        private void ReleaseOwnedMaterials()
        {
            if (ownedRuntimeMaterials != null)
            {
                for (int i = 0; i < ownedRuntimeMaterials.Length; i++) Destroy(ownedRuntimeMaterials[i]);
                ownedRuntimeMaterials = null;
            }
            disposed = true;
        }

        // 通常の破棄は_Dispose完了後に行う。恒久Textureと複製Materialも残さない
        private void OnDestroy()
        {
            // Takeで渡した画像は参照から外れているため、ここでは未取得の結果だけを破棄する
            if (expansionResultTexture != null && expansionResultTexture != postprocessedOutputRenderTexture)
                Destroy(expansionResultTexture);
            expansionResultTexture = null;
            outputReady = false;
            ReleaseIntermediateRenderTextures();
            ReleaseOwnedInput();
            normalizedSourceTexture = ReleaseRuntimeTexture(normalizedSourceTexture);
            generatedGammaEncodeTexture = ReleaseRuntimeTexture(generatedGammaEncodeTexture);
            generatedQuantReciprocalTexture = ReleaseRuntimeTexture(generatedQuantReciprocalTexture);
            runtimeQuantTexture = ReleaseRuntimeTexture(runtimeQuantTexture);
            generatedOpaqueWhiteTexture = ReleaseRuntimeTexture(generatedOpaqueWhiteTexture);
            generatedNeutralGrayTexture = ReleaseRuntimeTexture(generatedNeutralGrayTexture);
            generatedTransparentTexture = ReleaseRuntimeTexture(generatedTransparentTexture);
            ReleaseOwnedMaterials();
        }

        private void SetStatus(string value)
        {
            status = value;
        }
    }
}
