using UnityEditor;
using UnityEngine;

namespace Backrooms.EditorTools
{
    /// <summary>
    /// _Project/Audio の素材の読み込み方を決める。
    ///
    /// 既定のままだと、ある程度長い素材は「圧縮したままメモリに置く」に
    /// なる。すると最初に鳴らした瞬間、本編スレッドで復号が走る。
    /// 歩行中の最悪フレームが 26.8 -> 39.6ms に伸びていたのがこれで、
    /// 声を鳴らした瞬間に画がつっかえていた。全部で 3MB も無いので、
    /// 展開した状態で持っておけば鳴らす瞬間には何もしなくて済む。
    /// </summary>
    public class AudioImportRules : AssetPostprocessor
    {
        void OnPreprocessAudio()
        {
            var p = assetPath.Replace('\\', '/');
            if (!p.Contains("/_Project/Audio/")) return;

            var im = (AudioImporter)assetImporter;
            im.forceToMono = true;          // 素材は全て単一の音源。定位は実行時に付ける
            im.loadInBackground = false;

            var s = im.defaultSampleSettings;
            s.loadType = AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.PCM;
            s.preloadAudioData = true;
            im.defaultSampleSettings = s;
        }
    }
}
