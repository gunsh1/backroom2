using UnityEngine;

namespace Backrooms.Player
{
    /// <summary>
    /// カメラを揺らす。手持ちの歩行揺れ (FirstPersonController の Head) とは
    /// 別で、こちらは「持っている人が動揺している」ぶん。
    ///
    /// Head の子であるカメラの局所変換だけを触るので、歩行の揺れとは
    /// 足し合わさる。Perlin を使うのは、乱数だと 1 フレームごとに飛んで
    /// 手ぶれではなく画の破綻に見えるから。軸ごとに違う位置から引くこと。
    /// 同じ数列を使い回すと 3 軸が揃って動いて、揺れではなく平行移動になる。
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraShake : MonoBehaviour
    {
        /// <summary>0 で静止、1 で最大。外から書く。</summary>
        [System.NonSerialized] public float Amount;

        // 最大時は「どこへ歩けるかがぎりぎり読める」ところまで振る。
        // 回転で +-9 度 (ロールは +-13.5 度)、画角 47 度に対して 1/5 が
        // 毎フレーム動く。これ以上振ると道が読めず、ただ操作不能になる。
        [Tooltip("最大時の振れ幅 (m)")]
        public float PosMeters = 0.120f;
        [Tooltip("最大時の振れ幅 (度)")]
        public float RotDegrees = 9.0f;
        [Tooltip("揺れの速さ (Hz 相当)")]
        public float Frequency = 6.5f;

        Vector3 _basePos;
        Quaternion _baseRot;
        bool _got, _moved;

        void LateUpdate()
        {
            if (!_got)
            {
                _basePos = transform.localPosition;
                _baseRot = transform.localRotation;
                _got = true;
            }
            float a = Mathf.Clamp01(Amount);
            if (a <= 0.0005f)
            {
                // 揺れていないときは Transform に触らない。毎フレーム
                // 同じ値を書き戻すだけでも、描画側は「動いた」ものとして扱う。
                if (_moved)
                {
                    transform.localPosition = _basePos;
                    transform.localRotation = _baseRot;
                    _moved = false;
                }
                return;
            }
            _moved = true;

            float t = Time.unscaledTime * Frequency;
            // 大きく揺れるほど速くもなる。振れ幅だけ増やすと、
            // ゆっくり大きく漂うだけで「動揺」に見えない。
            float t2 = t * (1f + a * 0.8f);
            float nx = Mathf.PerlinNoise(t2, 0.13f) - 0.5f;
            float ny = Mathf.PerlinNoise(t2, 4.71f) - 0.5f;
            float nz = Mathf.PerlinNoise(t2, 9.27f) - 0.5f;
            float rx = Mathf.PerlinNoise(t2 * 0.87f, 14.3f) - 0.5f;
            float ry = Mathf.PerlinNoise(t2 * 0.87f, 21.7f) - 0.5f;
            float rz = Mathf.PerlinNoise(t2 * 0.61f, 27.1f) - 0.5f;

            transform.localPosition = _basePos
                + new Vector3(nx, ny, nz) * (PosMeters * 2f * a);
            transform.localRotation = _baseRot
                * Quaternion.Euler(rx * RotDegrees * 2f * a,
                                   ry * RotDegrees * 2f * a,
                                   rz * RotDegrees * 3f * a);
        }
    }
}
