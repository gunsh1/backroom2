using UnityEngine;
using Backrooms.Player;

namespace Backrooms.Audio
{
    /// <summary>
    /// 撮っている人の呼吸。止まっていても歩いていても鳴っている。
    ///
    /// ファウンドフッテージで「誰かがカメラを持っている」ことを担保して
    /// いるのは画ではなく息のほうで、無音だとカメラだけが独りでに歩いて
    /// いることになる。手持ち揺れを止めると三脚に見えるのと同じ理屈。
    ///
    /// 走ると速くなる。ただし走り出した瞬間ではない。呼吸が上がるのは
    /// 運動を始めて 15-40 秒あたりからで、戻るのはさらに遅い。この
    /// 「上がるより収まるほうが遅い」非対称が、息が上がった感じを作る。
    ///
    /// 速さは AudioSource.pitch では変えない。あれは再生速度と音程を
    /// 同時に動かすので、息が上がると気道の共鳴まで上がって別人の息に
    /// なる。実際に息が上がっても共鳴の高さは変わらないので、
    /// Ola (overlap-add) で速さだけを動かす。
    ///
    /// 素材は 2 つ持つ。平常 (Normal) と、異常時 (Heavy)。切り替えは
    /// 混ぜ合わせで、両方とも常に回している。止めて掛け直すと戻るときに
    /// 息継ぎが飛んで、そこで演出だと分かる。
    /// </summary>
    [RequireComponent(typeof(FirstPersonController))]
    [DisallowMultipleComponent]
    public class Breathing : MonoBehaviour
    {
        [Header("素材")]
        public AudioClip Normal;
        public AudioClip Heavy;

        [Header("平常")]
        [Tooltip("平常時の音量。Mix に導出がある")]
        public float Volume = Mix.Breath;
        [Tooltip("走って息が上がりきったときの再生速度。「若干」なので 1.2 倍まで")]
        public float MaxRate = 1.20f;
        [Tooltip("息が上がりきったときの音量の倍率")]
        public float LoudGain = 1.8f;
        [Tooltip("走り続けて息が上がりきるまで (秒)")]
        public float RiseSeconds = 14f;
        [Tooltip("やめてから収まるまで (秒)。上がるより遅くすること")]
        public float FallSeconds = 35f;

        [Header("異常時")]
        [Tooltip("Heavy に完全に入れ替わったときの追加の速さ")]
        public float PanicRate = 0.15f;
        [Tooltip("Heavy に完全に入れ替わったときの音量の倍率")]
        public float PanicGain = 2.6f;
        [Tooltip("動悸が残っている間の追加の速さ。素材は Normal のまま")]
        public float AfterRate = 0.12f;
        [Tooltip("動悸が残っている間の音量の倍率")]
        public float AfterGain = 1.35f;

        /// <summary>0 = 平常、1 = Heavy に完全に入れ替わる。PanicEvent が書く。</summary>
        [System.NonSerialized] public float Panic;
        /// <summary>動悸が残っている度合い。素材は Normal のまま速さだけ上げる。</summary>
        [System.NonSerialized] public float After;

        /// <summary>0 = 平常、1 = 走って上がりきった。</summary>
        public float Effort { get; private set; }
        /// <summary>今の再生速度。1.0 が等速。</summary>
        public float Rate { get; private set; }

        FirstPersonController _fps;
        AudioSource _src;
        AudioClip _stream;
        readonly Ola _olaN = new Ola();
        readonly Ola _olaH = new Ola();

        // 音声スレッドへ渡す値。代入しか起きないので錠は要らない。
        volatile float _rate = 1f, _mix = 0f, _gain = 1f;

        void Awake() { _fps = GetComponent<FirstPersonController>(); }

        void Start()
        {
            int fs = AudioSettings.outputSampleRate;
            if (fs <= 0) fs = 48000;
            if (Normal != null) _olaN.SetSource(Resample(Normal, fs));
            if (Heavy != null) _olaH.SetSource(Resample(Heavy, fs));
            if (!_olaN.Ready && !_olaH.Ready) return;

            var go = new GameObject("Breath");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0.05f, 0.04f);
            _src = go.AddComponent<AudioSource>();
            _src.loop = true;
            _src.playOnAwake = false;
            // 自分の息。完全な 2D にすると頭の中で鳴るので、わずかに定位させる。
            _src.spatialBlend = 0.10f;
            _src.minDistance = 2f;
            _src.maxDistance = 20f;
            _src.dopplerLevel = 0f;
            _src.volume = 1f;              // 量は Read の中で掛ける
            // 自前で流し込む。Ola の出力をそのまま渡すので、再生速度を
            // 変えても音程は動かない。
            _stream = AudioClip.Create("BreathStream", fs * 4, 1, fs, true, Read);
            _src.clip = _stream;
            _src.Play();
        }

        /// <summary>
        /// 素材を出力の標本化周波数に合わせる。Ola は粒の中を 1 標本ずつ
        /// 読むので、ここが食い違うとそのぶん音程がずれる。
        /// </summary>
        static float[] Resample(AudioClip c, int fs)
        {
            var raw = new float[c.samples * c.channels];
            c.GetData(raw, 0);
            var mono = new float[c.samples];
            if (c.channels == 1) System.Array.Copy(raw, mono, c.samples);
            else
                for (int i = 0; i < c.samples; i++)
                {
                    float s = 0f;
                    for (int ch = 0; ch < c.channels; ch++) s += raw[i * c.channels + ch];
                    mono[i] = s / c.channels;
                }
            if (c.frequency == fs) return mono;

            int n = Mathf.Max(4, Mathf.RoundToInt(mono.Length * (float)fs / c.frequency));
            var outp = new float[n];
            double step = (double)c.samples / n;
            for (int i = 0; i < n; i++)
            {
                double p = i * step;
                int i0 = (int)p;
                float f = (float)(p - i0);
                float a = mono[i0 % mono.Length], b = mono[(i0 + 1) % mono.Length];
                outp[i] = a + (b - a) * f;
            }
            return outp;
        }

        /// <summary>止める／再開する。負荷の切り分け用。</summary>
        public void SetStreaming(bool on)
        {
            if (_src == null) return;
            if (on) { if (!_src.isPlaying) _src.Play(); }
            else if (_src.isPlaying) _src.Stop();
        }

        void Update()
        {
            if (_src == null) return;
            float d = Time.deltaTime;
            // 直線で上げ下げする。指数で寄せると 0 に着かず、いつまでも
            // わずかに上がったままで「おちついた」にならない。
            bool hard = _fps != null && _fps.IsSprinting;
            Effort = Mathf.Clamp01(Effort + (hard ? d / Mathf.Max(0.1f, RiseSeconds)
                                                  : -d / Mathf.Max(0.1f, FallSeconds)));
            float p = Mathf.Clamp01(Panic), af = Mathf.Clamp01(After);
            // 動悸ぶんは異常時と重ねない。異常時のほうが常に上。
            float afterOnly = Mathf.Max(0f, af - p);

            Rate = Mathf.Lerp(1f, MaxRate, Effort) + p * PanicRate + afterOnly * AfterRate;
            _rate = Rate;
            _mix = p;
            _gain = Volume * Mathf.Lerp(1f, LoudGain, Effort)
                           * Mathf.Lerp(1f, PanicGain, p)
                           * Mathf.Lerp(1f, AfterGain, afterOnly);
        }

        // ------------------------------------------------------ 音声スレッド

        void Read(float[] data)
        {
            float r = _rate, m = _mix, g = _gain;
            bool hn = _olaN.Ready, hh = _olaH.Ready;
            for (int i = 0; i < data.Length; i++)
            {
                // 混ぜていない側も回し続ける。止めると戻したときに
                // 息継ぎの途中から始まって、そこで作り物だと分かる。
                float a = hn ? _olaN.Next(r) : 0f;
                float b = hh ? _olaH.Next(r) : 0f;
                data[i] = (a * (1f - m) + b * m) * g;
            }
        }
    }
}
