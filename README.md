# DCTH for VRC

バージョン: v1.0.0

VRChat Worldの実行時に、TextureをDCT・量子化・RLE・Huffman符号化した独自byte列へ圧縮し、そのbyte列からTextureを復元するライブラリです。

作者: hy
ライセンス: CC0-1.0

## 動作要件

- Unity 2022.3.22f1
- VRChat Worlds SDK 3.10.4（このプロジェクトの開発環境）
- UdonSharp
- 圧縮・展開には、使用Shader（最大target 3.5）、ARGBHalf / ARGBFloat / ARGB32 / RFloat / R8 RenderTexture、GPU readbackを使用できる実行環境が必要です。

## 概要

DCTHはJPEGと同じ考え方の8x8 DCT、量子化、zigzag、RLE、Huffman符号化を使用しますが、JPEG file形式ではありません。

出力はVRChat Runtime向けの独自`DCTH` byte列です。

## フォルダ構成

- `Runtime/Prefabs`: Material参照を設定済みの`ICLibraryDcth.prefab`
- `Runtime/Scripts`: Runtime codec実装と量子化table
- `Runtime/Shaders`: DCT、RLE、Huffman encode・decode、最終合成用Shader
- `Runtime/Materials`: 各Shaderを使用するRuntime Material
- `Runtime/Textures`: 色変換用の`IC_DCTHGammaEncodeLut.bytes`

## 簡単な利用ガイド

1. `Runtime/Prefabs/ICLibraryDcth.prefab`をSceneに配置し、利用側のUdonSharpスクリプトから`ICLibraryDcth`を参照します。

   圧縮要求はその`compression`へ送ります。
2. 初回の圧縮前に`RequestCompressionWarmup(eventReceiver)`を呼び、`_HandleDcthCompressWarmupComplete`の通知を待ちます。
3. `CanAcceptRequest`がtrueであることを確認し、入力Textureを渡します。

   CameraのRenderTextureなど共有する画像には`RequestCompressionCopy(...)`、処理後に破棄してよい専用Textureには`RequestCompressionOwned(...)`を使います。
4. 戻り値のhandleを保存します。

   `InvalidHandleId`なら受付失敗です。

   Copy入力は`IsInputCopyComplete(handle)`がtrueになるまで変更・解放せず、Owned入力は受付成功後に触らないでください。
5. `eventReceiver`で`_HandleDcthCompressComplete`を受けたら、`TakeCompressionResult(handle)`で圧縮byte列を取得します。

   失敗は`_HandleDcthCompressFailed`で受け取ります。

   `eventReceiver`には通知を処理するUdonBehaviourを指定してください。

要求時には入力の色空間、Quality（1〜100）、容量上限を指定します。

`maximumCompressedBytes`が正なら容量制限付き、0以下なら画質指定で圧縮します。

結果には展開に必要な画像寸法・設定も含まれます。

## 実装ファイル

### C#

| ファイル | 役割 |
| --- | --- |
| `ICLibraryDcth.cs` | 圧縮・展開を実行する`DcthCodecLibrary`の参照を保持します |
| `DcthCodecLibrary.cs` | 入力検証、容量予測、GPU pass進行、Huffman table構築、readback、payload組立、展開、resource解放を管理します |
| `DcthEncodingTableProfile.cs` | QualityとpresetからY/A/Cb/Cr用の量子化tableを生成し、table hashを計算します |

### 圧縮と容量予測に使用するShader

| ファイル | 役割 |
| --- | --- |
| `DcthEncodeCommon.cginc` | 色変換、DCT、量子化table参照などencode共通処理です |
| `DcthHalfRound.cginc` | half精度値を係数へ変換する共通丸め処理です |
| `DcthColorDownsampleBlit.shader` | half-size Cb/Cr用の事前filterと縮小を行います |
| `DcthEncodeBlit.shader` | 各プレーンへ水平DCTを行います |
| `DcthVerticalQuantizeBlit.shader` | 垂直DCTと量子化を行います |
| `DcthCoeffToRleSymbolsBlit.shader` | 量子化係数をzigzag順のRLE symbolへ変換します |
| `DcthDcDeltaBlit.shader` | DC値をblock間の差分へ変換します |
| `DcthDcFrequencyBlit.shader` | DC categoryの出現頻度を集計します |
| `DcthRleAcFrequencyBlit.shader` | AC RLE symbolの出現頻度を集計します |
| `DcthHuffmanTableBlit.shader` | code length制限とcanonical Huffman code生成を行います |
| `DcthHuffmanBitCountBlit.shader` | blockごとのHuffman必要bit数を計算します |
| `DcthHuffmanBlockPageEncodeBlit.shader` | symbolを固定64-byte block pageへ符号化します |
| `DcthHuffmanBlockValidBytesBlit.shader` | 各blockが実際に使用したbyte数を求めます |
| `DcthHuffmanBlockOverflowBlit.shader` | block page上限超過を縮約して判定します |
| `DcthHuffmanChunkValidBytesBlit.shader` | block長をchunk単位の有効byte数へ集約します |
| `DcthHuffmanPayloadGatherBlit.shader` | block pageから有効byteだけを連続payloadへ集めます |
| `DcthHuffmanMetadataPackBlit.shader` | Huffman tableとchunk長をreadback用Textureへ格納します |
| `DcthCapacityPrepassEncodeBlit.shader` | 容量予測sampleへ水平DCTを行います |
| `DcthCapacityPrepassVerticalBlit.shader` | 容量予測sampleへ垂直DCTを行います |
| `DcthCapacityPrepassPostBlit.shader` | 容量予測用の量子化、DC差分、統計値生成を行います |

### 展開に使用するShader

| ファイル | 役割 |
| --- | --- |
| `DcthHuffmanChunkBlockValidBlit.shader` | chunk情報からblockごとのbit位置を再構築します |
| `DcthHuffmanDecodeRleBlit.shader` | canonical tableを作り、payloadからDCとACのRLE streamを復号します |
| `DcthRleDcDeltaBlit.shader` | 復号したRLE streamからDC差分を取り出します |
| `DcthDcScanBlit.shader` | DC差分のprefix sumで各blockのDC値を復元します |
| `DcthRleToSymbolFixedBlit.shader` | 可変長RLEをblockごとの64係数slotへ展開します |
| `DcthDecodeSymbolsBlit.shader` | 逆量子化とIDCTで各プレーンの画素値を復元します |
| `DcthComposeRgbaBlit.shader` | 復元したY/A/Cb/Crを一時Textureへ格納し、最終RGBAへ合成します |

`Runtime/Materials`の各Materialは同名のShaderを参照します。

`DcthCodecLibrary`が各Blitの直前に必要なTexture、画像寸法、plane、Quality、tableを設定します。

## 圧縮フロー

1. `RequestCompressionOwned(...)`または`RequestCompressionCopy(...)`で圧縮を要求します。
2. `DcthCodecLibrary.cs`が入力Texture、画像寸法、Material参照、容量上限を検証します。
3. 容量制限付きの場合は最大128個の8x8 blockを決定的に選び、容量予測ShaderでDCT、量子化、RLE、Huffman bit数を推定します。

   DCT結果を再利用しながらQualityを二分探索します。
4. 本圧縮ではY、必要に応じてA、Cb、Crの順に処理します。half-size Cb/Crでは先に`DcthColorDownsampleBlit.shader`を使用します。
5. 水平DCT、垂直DCTと量子化、zigzag RLE、DC差分、DC/AC頻度集計を順に実行します。
6. 頻度tableをreadbackし、`DcthCodecLibrary.cs`の小分けCPU処理でHuffman treeのraw code lengthを構築します。

   Shaderで長さを16 bit以下へ制限し、canonical codeを生成します。
7. 各blockの必要bit数を求め、64-byte block pageへ符号化します。上限超過を検査し、block長をchunk長へ集約します。
8. block pageから連続payloadを生成し、payload、chunk有効長、DC table、AC tableをGPU readbackします。
9. 128-byte headerと、各plane 4 segmentを`compressedBytes`へ格納します。

   segmentはpayload、chunk有効長、DC Huffman table、AC Huffman tableの順です。
10. GPU処理とreadbackの完了後、処理中のRenderTextureを解放し、完了または失敗イベントを送ります。

## 展開フロー

1. `RequestExpansion(...)`、または`sourceBytes`を設定して`_LoadSourceBytesToTexture()`を呼びます。
2. 128-byte headerからmagic、version、header長、画像寸法、flag、segment数、payload全体長を読み取り、不正な値をRenderTexture確保前に拒否します。
3. 各planeのpayload、chunk有効長、DC table、AC tableを長さ上限と一致条件で検証し、小分けに取り出します。
4. payloadに含めていないchunk offsetをchunk有効長から再構築し、decode用Textureへuploadします。
5. `DcthHuffmanDecodeRleBlit.shader`でHuffman payloadを段階的に復号します。
6. DC差分のscan、RLEから64係数slotへの展開、逆量子化、IDCTを行います。
7. 復元した各planeを`DcthComposeRgbaBlit.shader`でまとめ、Y/A/Cb/Crから最終RGBA RenderTextureを生成します。
8. GPU完了確認後に中間RenderTextureを解放し、`outputTexture`だけを保持して完了イベントを送ります。

   失敗またはキャンセル時はGPU完了確認後に全て解放します。

## 色空間とVRAM

### 圧縮する色空間（`encodeSrgb`）

圧縮側ライブラリの`encodeSrgb`は、画像をsRGBの色空間で圧縮するかを指定します。

写真やイラストは`true`を基本とします。

sRGBで圧縮することで、暗い部分を含む色の階調を再現しやすくするための設定です。

圧縮要求の引数ではなく、要求前に圧縮側ライブラリへ設定します。

Linearのマスク画像などでは`false`を使うことも考えられますが、この用途の画質は未確認です。本ライブラリは非可逆の画像圧縮を想定しています。

DCTHでは色空間の情報を圧縮byte列に記録し、展開時に圧縮時と同じ設定を復元します。

ASTC／BC7と異なり、`RequestExpansion(inputBytes, eventReceiver)`へ`encodedSrgb`を別途渡す必要はありません。

展開後のTextureの色空間は、圧縮時の入力設定に従います。

### 入力画像の色空間（`inputSrgb`）

`inputSrgb`は、圧縮へ渡すTextureのsRGB設定を指定します。

画像をImportしたTextureではInspectorの「sRGB (Color Texture)」、RenderTextureではそのTextureの`sRGB`設定に合わせます。

| UnityプロジェクトのColor Space | 入力TextureのsRGB設定 | `inputSrgb` |
| --- | --- | --- |
| Linear | 有効 | `true` |
| Linear | 無効 | `false` |
| Gamma | 有効 | `true` |
| Gamma | 無効 | `false` |

プロジェクト設定との組み合わせによる色変換は、ライブラリ内部で判定します。

LinearプロジェクトではUnityがsRGB Textureの読み取り時にlinearへ変換し、Gammaプロジェクトではその自動変換がないため、シェーダーが入力設定を参照します。

呼び出し側で「Linearプロジェクトだから`inputSrgb = false`」と置き換えないでください。

例えば、sRGBが無効のRenderTextureから写真を圧縮する場合は、`inputSrgb = false`、圧縮側の`encodeSrgb = true`にします。

### byte列の容量を抑えたいときに使う

DCTHは、画像を保存・転送するためのbyte列を小さくしたい場合に使います。

展開すると、表示や画像加工に使える非圧縮のRenderTextureへ戻ります。

`RequestExpansion(...)`の完了通知後に`TakeExpansionResult(handle)`で取得し、使用後は呼び側でRelease／Destroyしてください。

圧縮byte列が小さくなっても、展開後のTextureのVRAMが同じ割合で小さくなるわけではありません。

DCTHのbyte列をそのままMaterialに設定して表示することはできません。

## DCTH byte列

headerは128 bytesで、little-endian int32として次の情報を持ちます。

- magicとversion
- header byte数、image ID、画像の幅と高さ
- alpha、color、half-size Cb/Cr、色空間などのflag
- Quality、量子化preset、alpha mode
- segment数、payload全体のbyte数、各segmentのbyte数

有効なplaneごとに次の4 segmentを格納します。

1. 連続Huffman payload
2. chunkごとの有効byte数
3. DC canonical Huffman table
4. AC canonical Huffman table

chunk offsetはchunk有効長から一意に再構築できるため、payloadには格納しません。

## 容量制限prepass

容量制限APIでは、本圧縮前に画像全体から最大128個の8x8 blockを決定的に選択します。

- sample数は解像度に比例して増加しません。
- block配置はPCとMobileで同じ規則を使用します。
- DCT結果をQuality候補間で再利用します。
- `halfSizeCbCr`、`sendColor`、alpha省略を含む実際のplane構成を反映します。
- prepass後の本圧縮は1回実行します。
- 予測外れまたは64-byte block上限超過時だけ実測値を使って再試行します。
- 最終的な容量は`compressedBytes.Length`で判定します。

画質指定APIではprepassを実行せず、指定Qualityと64-byte block上限処理を使用します。

## Runtime API

| 処理 | API・値 | 補足 |
| --- | --- | --- |
| 圧縮要求 | `RequestCompressionOwned(inputTexture, inputSrgb, qualityValue, maximumCompressedBytes, eventReceiver)` | handleを返し、次frameから処理します |
| 圧縮結果 | `compressedBytes` | 完全なDCTH payloadです |
| 圧縮状態 | `compressionPending`、`compressionComplete`、`compressionFailed`、`status` | handle APIでも状態を確認できます |
| 展開要求 | `RequestExpansion(inputBytes, eventReceiver)` | 完全なDCTH payloadを受け取ります |
| 展開開始 | `sourceBytes`、`_LoadSourceBytesToTexture()` | headerと全segmentを検証します |
| 展開結果 | `outputTexture` | decode後のRenderTextureです |
| codec設定 | `quality`、`quantPreset`、`hasAlpha`、`sendColor`、`halfSizeCbCr`、`maxImageBytes` | 設定によってpayload容量が変化します |
| clear | `_ClearCompressedBytes()`、`_ClearSourceBytes()`、`_ClearOutputTexture()` | 保持結果を明示的に破棄します |

## 展開時の容量上限

展開側の`maxImageBytes`は、受け付けるDCTH byte列全体の上限です。

Prefabの初期値は4 MiB（4,194,304 bytes）です。

圧縮側と展開側は別のライブラリオブジェクトなので、圧縮側の上限を変更しても展開側には反映されません。

4 MiBを超える画像を扱う場合は、`ICLibraryDcth.expansion.maxImageBytes`も必要な上限へ設定してから`RequestExpansion(...)`を呼んでください。

上限を超えるbyte列は受付時に`InvalidHandleId`を返します。

圧縮時に容量制限を使わなかった場合も、展開側の上限は適用されます。

## 入出力の所有権と終了処理

外部アプリからの新規利用は、handleを返す要求APIを使用してください。圧縮と展開はそれぞれのライブラリオブジェクトへ要求します。

| API | 所有権と利用方法 |
| --- | --- |
| `RequestCompressionOwned(...)` | 有効なhandleが返った時点で入力の所有権が移ります。以後、呼び側は入力を変更・再利用・Release・Destroyしません。成功、失敗、キャンセルのいずれでもライブラリがGPU完了後に破棄します |
| `RequestCompressionCopy(...)` | 引数はOwnedと同じです。共有asset・Cameraが再利用するRTなどに使います。元画像の所有権は呼び側に残り、内部コピーだけをライブラリが破棄します |
| `IsInputCopyComplete(handle)` | trueになれば元画像を変更・解放できます。コピーは非同期のため、呼び出し直後には変更・解放しないでください。コピー前の失敗・キャンセルでは終端状態または終了通知を待ちます |
| `TakeCompressionResult(handle)` | 成功したbyte[]を取り出し、ライブラリの保持参照を外します |
| `TakeExpansionResult(handle)` | 成功したRenderTextureの所有権を呼び側へ渡します。取得後は次の要求やライブラリ終了で破棄されません。使用後のRelease/Destroyは呼び側が行います |
| `CanAcceptRequest` | trueのときだけ新しい要求を開始します。falseの間は呼び側のキューに残します |
| `CancelCompression(handle)` / `CancelExpansion(handle)` | キャンセルを受け付けます。GPU待機中は`TaskStateCancelling`、後処理完了後に`TaskStateCancelled`となります |
| `_Dispose()` / `IsDisposed` | ライブラリの使用を終了します。新しい要求を拒否し、未完了GPU処理を待ち、内部RT・未取得結果・複製Materialを解放します。`IsDisposed`を待ってライブラリのGameObjectをDestroyしてください |

- 受付に失敗して`InvalidHandleId`が返った場合、Ownedでも入力の所有権は移りません。
- Ownedへ渡せるのは、呼び側が破棄権限を持つ専用の実行時Textureです。

  Project asset、組み込みTexture、temporary RT poolから借りたRTは渡さず、Copyを使ってください。
- CopyはARGB32の内部RTへ転写します。HDRや8bitを超える入力精度を保持するコピーではありません。転写時点の画像を取り込むため、取り込み完了まで元画像を固定します。
- 展開入力のbyte[]は処理終了まで書き換えないでください。参照の保持はライブラリが行います。
- 成功・失敗・キャンセルの通知は、その処理のGPU利用が終了した後に届きます。キャンセル受理だけを解放完了として扱わないでください。
- 終了待機中はライブラリのcomponentとGameObjectを有効なまま保ちます。直接Destroy/無効化すると、Udonの後処理イベントを継続できません。
- 未取得の結果はライブラリが所有し、次の要求またはDisposeで破棄できます。継続利用する結果は必ずTakeで受け取ってください。

コピー完了イベントは`_HandleDcthInputCopied`、キャンセル完了は`_HandleDcthCompressCancelled` / `_HandleDcthExpandCancelled`です。

## 導入方法

このフォルダを、VRChat Worlds SDKとUdonSharpを導入済みのUnityプロジェクト内の`Assets/HDAssets/ImageCompress/Dcth/`へ配置してください。  
各assetと対応する`.meta`は必ずセットで保持してください。  
導入後はUnityのimportとUdonSharpのコンパイルが完了するまで待ってから、PrefabをSceneへ配置してください。  
`Assets/SerializedUdonPrograms`は導入先で生成されるため、このリポジトリには含めません。

## リポジトリ

`https://github.com/hd4tails/ic-dct-huffman-for-vrc.git`
