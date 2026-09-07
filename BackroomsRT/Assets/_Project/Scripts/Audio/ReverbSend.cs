using UnityEngine;

namespace Backrooms.Audio
{
    /// <summary>
    /// この音源を「部屋の残響にだけ」送る。素の音としては出さない。
    ///
    /// 遠くの人の声で聞こえているのは、直接届いた音ではなく部屋を回って
    /// きた音のほうが大きい。ところが 3D 音源に付く距離減衰は直接音の
    /// 減衰なので、それを残響への送りにも掛けてしまうと、遠ざかるほど
    /// 残響まで一緒に小さくなって消える。実際の残響音場は部屋のどこでも
    /// ほぼ同じ大きさで、距離ではほとんど減らない (減るのは直接音だけで、
    /// だから遠い音ほど「反響しか聞こえない」)。
    ///
    /// なので送りは減衰の掛からない 2D の音源を別に立てて、その出力を
    /// そのまま聞き手の残響へ渡す。直接音は 3D の音源が別に持つ。
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public class ReverbSend : MonoBehaviour
    {
        public ListenerDsp Target;
        public float Gain = 1f;

        void OnAudioFilterRead(float[] data, int channels)
        {
            var t = Target;
            if (t != null) t.PushSend(data, channels, Gain);
            // 直接音としては出さない。消し忘れると同じ声が二重に鳴る。
            System.Array.Clear(data, 0, data.Length);
        }
    }
}
