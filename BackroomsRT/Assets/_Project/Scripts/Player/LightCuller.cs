using System.Collections.Generic;
using UnityEngine;

namespace Backrooms.Player
{
    /// <summary>
    /// 近い順に決まった数のエリアライトだけを有効にする。
    ///
    /// M2 でフロアが 1 部屋から 24x21.6m に広がり、器具が 90 台になった。
    /// HDRP のエリアライトは 1 台ずつが重く、全部を点けたままだと
    /// レイトレの予算が全て照明に食われる。
    ///
    /// 消しても暗くならないのは、器具のレンズが自発光だから。
    /// RTGI は自発光面をそのまま拾うので、遠くの器具は「光る面」として
    /// 残り、間接光も届く。切れるのは直接光の計算だけ。
    /// </summary>
    [DisallowMultipleComponent]
    public class LightCuller : MonoBehaviour
    {
        [Tooltip("同時に有効にする最大数")]
        public int MaxActive = 28;

        [Tooltip("この距離より遠い器具は直接光を計算しない (m)")]
        public float Radius = 17f;

        [Tooltip("この距離だけ動いたら即座に組み替える (m)")]
        public float MoveEpsilon = 0.75f;

        /// <summary>
        /// 選ぶ基準の点を視線方向へこれだけ前に出す。
        ///
        /// 単に「カメラから近い順」で選ぶと、背後の器具まで有効になる一方で
        /// 見えている奥が全部落ちる。参照画像の中景の壁が実測で参照の半分の
        /// 明るさ (0.271 対 0.548) になっていたのはこれが原因だった。
        /// 自発光と RTGI だけでは直接光の代わりにならない。
        /// </summary>
        public float LookAhead = 0f;

        public float Interval = 0.5f;

        Light[] _lights;
        float[] _dist;
        int[] _order;
        Transform _target;
        Vector3 _lastAt = new Vector3(1e9f, 1e9f, 1e9f);
        float _next;

        bool _dirty;
        float _nextRebuild;

        void Awake() { Collect(); }

        void Collect()
        {
            _lights = GetComponentsInChildren<Light>(true);
            _dist = new float[_lights.Length];
            _order = new int[_lights.Length];
            for (int i = 0; i < _order.Length; i++) _order[i] = i;
            _lastAt = new Vector3(1e9f, 1e9f, 1e9f);
        }

        /// <summary>
        /// 器具が増減したときに呼ぶ。無限生成はチャンクを作るたびに
        /// ライトが増えるので、毎回集め直すと GetComponentsInChildren だけで
        /// フレームが埋まる。要求は溜めておいて 0.25 秒に 1 度だけ実際に集める。
        /// </summary>
        public void Rebuild() { _dirty = true; }

        void LateUpdate()
        {
            if (_target == null)
            {
                var cam = Camera.main;
                if (cam == null) return;
                _target = cam.transform;
            }
            if (_dirty && Time.unscaledTime >= _nextRebuild)
            {
                _dirty = false;
                _nextRebuild = Time.unscaledTime + 0.25f;
                Collect();
            }
            var p = _target.position + _target.forward * LookAhead;
            bool moved = (p - _lastAt).sqrMagnitude > MoveEpsilon * MoveEpsilon;
            if (!moved && Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            _lastAt = p;
            Refresh(p);
        }

        /// <summary>
        /// キャプチャは視点を瞬間移動させるので、外から明示的に呼べるようにしておく。
        /// タイマー任せにすると、切り替わる前のフレームが撮れる。
        /// </summary>
        public void Refresh(Vector3 at)
        {
            if (_lights == null || _lights.Length == 0) return;
            for (int i = 0; i < _lights.Length; i++)
            {
                var l = _lights[i];
                _dist[i] = l == null ? float.MaxValue
                                     : (l.transform.position - at).sqrMagnitude;
                _order[i] = i;
            }
            // 部分選択で十分だが、90 台程度なら素直に並べ替えた方が速い
            System.Array.Sort(_order, (a, b) => _dist[a].CompareTo(_dist[b]));

            float r2 = Radius * Radius;
            int onCount = 0;
            for (int k = 0; k < _order.Length; k++)
            {
                var l = _lights[_order[k]];
                if (l == null) continue;
                bool on = k < MaxActive && _dist[_order[k]] <= r2;
                if (on) onCount++;
                if (l.enabled != on) l.enabled = on;
            }
            ActiveCount = onCount;
            // 半径の中に上限より多くの器具があると、近い順に選び直すたびに
            // 点く器具が入れ替わって明かりが泳ぐ。起きていたら記録に残す。
            Saturated = onCount >= MaxActive;
        }

        public void RefreshNow()
        {
            if (_target == null && Camera.main != null) _target = Camera.main.transform;
            if (_target != null)
            {
                _lastAt = _target.position + _target.forward * LookAhead;
                Refresh(_lastAt);
            }
        }

        public int Count => _lights == null ? 0 : _lights.Length;

        /// <summary>実際に点いている数。</summary>
        public int ActiveCount { get; private set; }

        /// <summary>上限に張り付いているか。真なら歩くたびに器具が入れ替わる。</summary>
        public bool Saturated { get; private set; }
    }
}
