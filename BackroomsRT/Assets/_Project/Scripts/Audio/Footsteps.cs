using UnityEngine;
using Backrooms.Player;

namespace Backrooms.Audio
{
    /// <summary>
    /// 足音。
    ///
    /// 撃ち方で気を付けていること。
    ///   * 左右を交互に撃つ。コントローラが踏んだ足を持っている。
    ///     同じ音が両足から出ると、歩いているというより何かを叩いている音になる。
    ///   * 靴は 1 足に決める。再生のたびに音程を振ると、1 歩ごとに
    ///     体格の違う人間が歩いていることになる。組は起動時に 1 つ選ぶだけ。
    ///   * 間隔をゆらす。歩行の位相からそのまま撃つと完全な等間隔になり、
    ///     機械が歩いているように聞こえる。人の歩幅の周期は 3〜5% ばらつく。
    /// </summary>
    [RequireComponent(typeof(FirstPersonController))]
    [DisallowMultipleComponent]
    public class Footsteps : MonoBehaviour
    {
        [Tooltip("カーペット上の足音は耳の位置で 48dBA 前後。基準からの相対で決めている")]
        public float Volume = 0.11f;
        public int Sources = 4;
        [Tooltip("踏む間隔のばらつき。歩幅の周期に対する割合")]
        public float TimingJitter = 0.04f;

        FirstPersonController _fps;
        AudioSource[] _pool;
        int _next;
        int _pair;

        public int Played { get; private set; }

        void Awake() { _fps = GetComponent<FirstPersonController>(); }

        void Start()
        {
            SoundBank.Build();
            // 靴は 1 足。歩いている途中で替わらない。
            _pair = Random.Range(0, SoundBank.StepPairs);
            _pool = new AudioSource[Sources];
            for (int i = 0; i < Sources; i++)
            {
                var go = new GameObject("Step_" + i);
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 0.1f, 0f);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0.45f;    // 自分の足なので完全な 3D にはしない
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = 1.2f;
                s.maxDistance = 14f;
                s.dopplerLevel = 0f;
                _pool[i] = s;
            }
        }

        void Update()
        {
            if (_fps == null || _pool == null) return;
            if (!_fps.SteppedThisFrame) return;

            int k = _pair * 2 + (_fps.LastStepWasLeft ? 0 : 1);
            var s = _pool[_next];
            _next = (_next + 1) % _pool.Length;

            s.clip = SoundBank.Footsteps[k];
            s.pitch = 1f;
            float speed = Mathf.Clamp01(_fps.SpeedNormalized);
            // 踏み込みの強さ。忍び足と全力で 3 倍ほど違う。
            float v = Volume * Mathf.Lerp(0.45f, 1.25f, speed);
            if (_fps.IsCrouching) v *= 0.40f;
            s.volume = v;

            // 等間隔を崩す。歩幅の周期に対して数 % だけ遅らせる。
            float jitter = Random.value * TimingJitter * _fps.StrideSeconds;
            s.PlayScheduled(AudioSettings.dspTime + jitter);
            Played++;
        }
    }
}
