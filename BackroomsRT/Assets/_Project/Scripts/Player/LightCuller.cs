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

        public float Interval = 0.5f;

        Light[] _lights;
        float[] _dist;
        int[] _order;
        Transform _target;
        Vector3 _lastAt = new Vector3(1e9f, 1e9f, 1e9f);
        float _next;

        void Awake()
        {
            _lights = GetComponentsInChildren<Light>(true);
            _dist = new float[_lights.Length];
            _order = new int[_lights.Length];
            for (int i = 0; i < _order.Length; i++) _order[i] = i;
        }

        void LateUpdate()
        {
            if (_target == null)
            {
                var cam = Camera.main;
                if (cam == null) return;
                _target = cam.transform;
            }
            var p = _target.position;
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
            for (int k = 0; k < _order.Length; k++)
            {
                var l = _lights[_order[k]];
                if (l == null) continue;
                bool on = k < MaxActive && _dist[_order[k]] <= r2;
                if (l.enabled != on) l.enabled = on;
            }
        }

        public void RefreshNow()
        {
            if (_target == null && Camera.main != null) _target = Camera.main.transform;
            if (_target != null) { _lastAt = _target.position; Refresh(_lastAt); }
        }

        public int Count => _lights == null ? 0 : _lights.Length;
    }
}
