using UnityEngine;

namespace Backrooms.Audio
{
    /// <summary>
    /// E で呼ぶ。自分の声。
    ///
    /// 自分の口から出る音なので、距離減衰も定位もほとんど無い。返って
    /// くるのは部屋だけで、しかも返ってくるのは自分の声しかない。
    /// それが分かるように、残響への送りを直接音とは別に持たせている。
    /// 全体に一律の wet を掛けるだけでは、叫んでも部屋が答えない。
    ///
    /// 素材が 2 つしかないので、直前と同じものは選ばない。
    /// </summary>
    [DisallowMultipleComponent]
    public class Shout : MonoBehaviour
    {
        public KeyCode Key = KeyCode.E;

        /// <summary>男声。SceneBuilder が Audio/Voices の male_* を入れる。</summary>
        public AudioClip[] Clips;

        [Tooltip("自分の声。Mix に導出がある")]
        public float Volume = Mix.Shout;
        [Tooltip("部屋へ送る量。直接音と独立に決める")]
        public float Send = Mix.ShoutSend;
        [Tooltip("鳴り終わってから次に叫べるまで (秒)")]
        public float Cooldown = 0.9f;

        public int Played { get; private set; }
        /// <summary>そのうちキーで撃たれた回数。誰も押していないのに増えたら入力側を疑う。</summary>
        public int ByKey { get; private set; }
        public bool Ready { get { return Time.unscaledTime >= _free; } }
        public AudioClip LastClip { get; private set; }

        AudioSource _direct, _wet;
        float _free;
        int _last = -1;

        void Start()
        {
            var listener = FindFirstObjectByType<ListenerDsp>();

            var d = new GameObject("Shout_Direct");
            d.transform.SetParent(transform, false);
            d.transform.localPosition = new Vector3(0f, 0.08f, 0.05f);
            _direct = d.AddComponent<AudioSource>();
            _direct.playOnAwake = false;
            // 自分の口。完全な 2D にすると頭の中で鳴るので、わずかに定位させる。
            _direct.spatialBlend = 0.15f;
            _direct.minDistance = 2f;
            _direct.maxDistance = 30f;
            _direct.dopplerLevel = 0f;

            var w = new GameObject("Shout_Send");
            w.transform.SetParent(transform, false);
            _wet = w.AddComponent<AudioSource>();
            _wet.playOnAwake = false;
            _wet.spatialBlend = 0f;      // 送りに距離減衰を掛けない
            _wet.volume = 1f;            // 量は ReverbSend.Gain で決める
            _wet.dopplerLevel = 0f;
            var rs = w.AddComponent<ReverbSend>();
            rs.Target = listener;
            rs.Gain = Send;
        }

        void Update()
        {
            // 窓が前面に無いときの入力は見ない。エディタから再生している間、
            // 焦点の外で押された（あるいは残っていた）キーを拾って、誰も
            // 押していないのに叫ぶことがある。
            if (!Application.isFocused) return;
            if (Input.GetKeyDown(Key) && Fire()) ByKey++;
        }

        /// <summary>キャプチャからも叩けるようにしておく。鳴らせたら true。</summary>
        public bool Fire()
        {
            if (Clips == null || Clips.Length == 0 || _direct == null) return false;
            if (Time.unscaledTime < _free) return false;

            int i = Clips.Length == 1 ? 0
                  : (_last + 1 + Random.Range(0, Clips.Length - 1)) % Clips.Length;
            _last = i;
            var clip = Clips[i];
            if (clip == null) return false;
            LastClip = clip;

            // 直接音と送りは同じ時刻に始める。ずれると自分の声が二人になる。
            double at = AudioSettings.dspTime + 0.03;
            _direct.clip = clip; _direct.volume = Volume; _direct.PlayScheduled(at);
            _wet.clip = clip; _wet.PlayScheduled(at);

            _free = Time.unscaledTime + clip.length + Cooldown;
            Played++;
            return true;
        }
    }
}
