using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace HDAssets.ImageCompress.Dcth
{
    /// <summary>
    /// DCTHの圧縮・展開ライブラリオブジェクトの参照を保持する
    /// DCTHを使用するUdonにはこのオブジェクトを指定する
    /// 圧縮と展開の処理は参照先の各ライブラリオブジェクトが実行する
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ICLibraryDcth : UdonSharpBehaviour
    {
        [Header("Library Objects")]
        // DCTH圧縮処理を実行するライブラリオブジェクト
        public DcthCodecLibrary compression;
        // DCTH展開処理を実行するライブラリオブジェクト
        public DcthCodecLibrary expansion;
    }
}
