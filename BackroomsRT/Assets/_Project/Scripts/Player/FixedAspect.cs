using UnityEngine;

namespace Backrooms.Player
{
    /// <summary>
    /// カメラの縦横比を固定する。
    ///
    /// 記録媒体が 4:3 なら、画面が何であろうと描くのは 4:3 の絵でなければ
    /// ならない。ウィンドウの比率をそのまま使うと、横長の画面では横に
    /// 広い絵になり、VHS 側で 4:3 の窓に詰め直したときに縦長に潰れる。
    ///
    /// Camera.aspect は描画先を差し替えるたびに戻るので毎フレーム入れ直す。
    /// 撮影用の RT が最初から 4:3 なら、同じ値を入れるだけで何も起きない。
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public class FixedAspect : MonoBehaviour
    {
        public float Aspect = 4f / 3f;
        Camera _cam;

        void Awake() { _cam = GetComponent<Camera>(); }

        void LateUpdate()
        {
            if (_cam != null && Aspect > 0f) _cam.aspect = Aspect;
        }
    }
}
