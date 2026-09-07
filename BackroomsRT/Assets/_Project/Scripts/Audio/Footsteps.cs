using UnityEngine;
using Backrooms.Player;

namespace Backrooms.Audio
{
    /// <summary>
    /// 足音。FirstPersonController が歩幅の折り返しで立てる旗を拾う。
    ///
    /// カーペットなので硬い音はしない。立ち上がりが鈍い低い「ドス」と、
    /// 毛足を擦る高い成分だけ。無響ではないので、聞こえている残響は
    /// ListenerDsp が部屋の形から付けている。
    /// </summary>
    [RequireComponent(typeof(FirstPersonController))]
    [DisallowMultipleComponent]
    public class Footsteps : MonoBehaviour
    {
        public float Volume = 0.34f;
        public int Sources = 3;

        FirstPersonController _fps;
        AudioSource[] _pool;
        int _next;
        int _last = -1;

        public int Played { get; private set; }

        void Awake() { _fps = GetComponent<FirstPersonController>(); }

        void Start()
        {
            SoundBank.Build();
            _pool = new AudioSource[Sources];
            for (int i = 0; i < Sources; i++)
            {
                var go = new GameObject("Step_" + i);
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 0.1f, 0f);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0.55f;    // 自分の足なので完全な 3D にはしない
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = 1f;
                s.maxDistance = 12f;
                s.dopplerLevel = 0f;
                _pool[i] = s;
            }
        }

        void Update()
        {
            if (_fps == null || _pool == null) return;
            if (!_fps.SteppedThisFrame) return;

            // 直前と同じ音を続けない。同じ足音が 2 回続くとすぐ嘘に聞こえる。
            int k = Random.Range(0, SoundBank.Footsteps.Length);
            if (k == _last) k = (k + 1) % SoundBank.Footsteps.Length;
            _last = k;

            var s = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            s.pitch = 0.92f + Random.value * 0.16f;
            float v = Volume * (0.75f + 0.35f * _fps.SpeedNormalized);
            s.PlayOneShot(SoundBank.Footsteps[k], v);
            Played++;
        }
    }
}
