using UnityEngine;

namespace Backrooms.Capture
{
    /// <summary>
    /// 生成器が選んだ視点をシーンに載せておく入れ物。
    ///
    /// 間取りが乱数で決まるようになった以上、キャプチャ位置をスクリプトに
    /// 直書きすると、生成のたびに壁の中を撮ることになる。位置は必ず
    /// 生成器の側（どこに開口があり、どこが暗いかを知っている側）が決める。
    /// </summary>
    public class CaptureRoute : MonoBehaviour
    {
        public FlatCaptureRunner.Shot[] Shots;
        /// <summary>ライト間引きを撮影位置で強制更新するときに使う目の高さ。</summary>
        public float EyeHeight = 1.68f;
        /// <summary>
        /// 書き出しの直前に掛ける「レンズと記録系」。参照画像から実測した値で、
        /// 演出ではない。詳しくは CaptureMedium を参照。
        /// </summary>
        public float LensK1 = 0f;
        public float LensOverscan = 1f;
        public float BlurSigma = 0f;
        public float Glare = 0f;
        public float GlareSigma = 0f;
        public Color MediumGain = Color.white;

        public Vector3 FpsProbe;      // fps 計測に使う立ち位置
        public float FpsYaw;

        /// <summary>
        /// 歩行テストの経路。スポーンから暗黒区画まで、隣り合うベイの中心を
        /// 繋いだもの。隣接するベイの中心どうしを結ぶ線は必ず境界の中央
        /// —— 開口があるならその中央 —— を通るので、経路探索なしで歩ける。
        /// </summary>
        public Vector3[] Waypoints;
    }
}
