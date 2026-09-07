using UnityEngine;
using Backrooms.Generation;

namespace Backrooms.Audio
{
    /// <summary>
    /// 遠くの誰かの声。歩いているうちに、たまに聞こえる。
    ///
    /// 大事なのは「遠くに居る」ことがそのまま聞こえること。近くで鳴らして
    /// 音量だけ絞ると、小声で囁かれているようにしか聞こえない。遠さは
    /// 音量ではなく、直接音と残響の比で決まる:
    ///
    ///   直接音は距離に反比例して落ち、壁が挟まればほとんど消える。
    ///   残響音場は部屋のどこでもほぼ同じ大きさで、距離では減らない。
    ///
    /// だから遠い声ほど「反響しか聞こえない」。ここではその 2 つを別々の
    /// 経路で作る。直接音は実際の位置に置いた 3D 音源、残響は ReverbSend で
    /// 聞き手の部屋へ直接送る。壁の向こうを選べるなら選ぶ。姿の見えない
    /// 場所から聞こえたほうが遠く感じるし、探しに行く動機にもなる。
    ///
    /// 出る頻度は指数分布。等間隔だと 2 回目で「仕掛け」だと分かる。
    /// </summary>
    [DisallowMultipleComponent]
    public class DistantVoice : MonoBehaviour
    {
        public int Seed = 1996;

        /// <summary>女声。SceneBuilder が Audio/Voices の female_* を入れる。</summary>
        public AudioClip[] Clips;

        [Tooltip("平均してこの秒数に 1 回鳴る")]
        public float MeanInterval = 120f;
        [Tooltip("開始直後は鳴らさない")]
        public float FirstDelay = 40f;
        [Tooltip("どれだけ短くてもこの秒数は空ける")]
        public float MinGap = 45f;
        [Tooltip("聞き手からの距離 (m)")]
        public Vector2 Distance = new Vector2(18f, 34f);

        [Tooltip("直接音。壁を挟めばさらに落ちる")]
        public float DirectVolume = Mix.VoiceFarDirect;
        [Tooltip("部屋へ送る量。遠い声はこちらが主になる")]
        public float Send = Mix.VoiceFarSend;
        [Tooltip("壁越しに届く直接音の上限周波数")]
        public float OccludedCutoff = 430f;
        [Tooltip("見通せる場合。25m 先の声は空気の吸収で高域が落ちている")]
        public float OpenCutoff = 3200f;

        public int Played { get; private set; }
        public Vector3 LastAt { get; private set; }
        public float LastDistance { get; private set; }
        public bool LastOccluded { get; private set; }
        public float NextIn { get { return Mathf.Max(0f, _next - Time.unscaledTime); } }

        AudioSource _direct, _wet;
        ReverbSend _send;
        AudioLowPassFilter _lp;
        Transform _ear;
        float _next;
        int _last = -1;

        void Start()
        {
            var listener = FindFirstObjectByType<ListenerDsp>();

            var d = new GameObject("Voice_Direct");
            d.transform.SetParent(transform, false);
            _direct = d.AddComponent<AudioSource>();
            _direct.playOnAwake = false;
            _direct.spatialBlend = 1f;
            _direct.rolloffMode = AudioRolloffMode.Logarithmic;
            _direct.maxDistance = 90f;
            _direct.dopplerLevel = 0f;
            _lp = d.AddComponent<AudioLowPassFilter>();

            var w = new GameObject("Voice_Send");
            w.transform.SetParent(transform, false);
            _wet = w.AddComponent<AudioSource>();
            _wet.playOnAwake = false;
            _wet.spatialBlend = 0f;
            _wet.volume = 1f;
            _wet.dopplerLevel = 0f;
            _send = w.AddComponent<ReverbSend>();
            _send.Target = listener;
            _send.Gain = Send;

            _next = Time.unscaledTime + FirstDelay;
        }

        void Update()
        {
            if (_ear == null)
            {
                var l = FindFirstObjectByType<AudioListener>();
                if (l == null) return;
                _ear = l.transform;
            }
            if (Time.unscaledTime < _next) return;
            Fire();
        }

        /// <summary>
        /// 次の 1 回を今すぐ鳴らす。キャプチャとイベントからも叩ける。
        /// gain はイベント時に前に出すための倍率。
        /// </summary>
        public bool Fire(float gain = 1f)
        {
            Schedule();
            if (Clips == null || Clips.Length == 0 || _direct == null || _ear == null) return false;

            int i = Clips.Length == 1 ? 0
                  : (_last + 1 + Random.Range(0, Clips.Length - 1)) % Clips.Length;
            _last = i;
            var clip = Clips[i];
            if (clip == null) return false;

            var ear = _ear.position;
            Vector3 p;
            if (!Pick(ear, out p))
            {
                // 空いた場所が見つからなかった。次で拾い直す。
                _next = Time.unscaledTime + 6f;
                return false;
            }
            float dist = Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(ear.x, 0f, ear.z));
            if (dist < 1f) return false;
            bool blocked = Blocked(p, ear);

            // 口の高さに置く。床から鳴ると人の声に聞こえない。
            _direct.transform.position = new Vector3(p.x, 1.55f, p.z);
            // 減衰は minDistance を今の距離に合わせて相殺し、量は Volume で決める。
            // こうしておくと定位だけが残り、鳴っている間に近づけば大きくなる。
            _direct.minDistance = dist;
            _direct.clip = clip;
            _direct.volume = DirectVolume * (blocked ? 0.25f : 1f) * gain;
            _lp.cutoffFrequency = blocked ? OccludedCutoff : OpenCutoff;

            double at = AudioSettings.dspTime + 0.03;
            _direct.PlayScheduled(at);
            _wet.clip = clip;
            _wet.PlayScheduled(at);
            if (_send != null) _send.Gain = Send * gain;

            LastAt = p; LastDistance = dist; LastOccluded = blocked;
            Played++;
            // 長い素材があるので、鳴っている間に次が来ないようにする。
            if (_next < Time.unscaledTime + clip.length + 2f)
                _next = Time.unscaledTime + clip.length + 2f;
            return true;
        }

        void Schedule()
        {
            // 指数分布。等間隔だと 2 回目で仕掛けだと分かる。
            float u = Mathf.Max(1e-4f, Random.value);
            float mean = Mathf.Max(MinGap + 1f, MeanInterval);
            _next = Time.unscaledTime + MinGap + (-Mathf.Log(u)) * (mean - MinGap);
        }

        /// <summary>
        /// 鳴らす場所。壁の向こうを選べるなら選ぶ。
        ///
        /// FindOpen は空きが見つかるまで渦巻き状に探すので、外れを引くと
        /// 1 回で数千回 Solid を呼ぶ。それを 1 フレームに 20 回投げていて、
        /// 歩行中の最悪フレームが 28.0 -> 38.9ms に伸びた。候補はその場で
        /// 空いているかを見るだけにして、駄目なら次の候補へ行く。
        /// </summary>
        bool Pick(Vector3 ear, out Vector3 best)
        {
            best = Vector3.zero;
            float bestScore = -1f;
            float mid = (Distance.x + Distance.y) * 0.5f;
            for (int k = 0; k < 10; k++)
            {
                float a = Random.value * Mathf.PI * 2f;
                float d = Random.Range(Distance.x, Distance.y);
                float x = ear.x + Mathf.Cos(a) * d, z = ear.z + Mathf.Sin(a) * d;
                if (!Level0Layout.Clear(x, z, Seed, 0.70f)) continue;
                var p = new Vector3(x, 0f, z);
                float score = (Blocked(p, ear) ? 2f : 0f)
                            + 1f - Mathf.Abs(d - mid) / Mathf.Max(1f, Distance.y);
                if (score > bestScore) { bestScore = score; best = p; }
            }
            return bestScore >= 0f;
        }

        /// <summary>間に壁があるか。FixtureVoices と同じ見方。</summary>
        bool Blocked(Vector3 a, Vector3 b)
        {
            float len = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
            int steps = Mathf.CeilToInt(len / 0.6f);
            for (int i = 1; i < steps; i++)
            {
                float t = i / (float)steps;
                if (Level0Layout.Solid(Mathf.Lerp(b.x, a.x, t), Mathf.Lerp(b.z, a.z, t), Seed))
                    return true;
            }
            return false;
        }
    }
}
