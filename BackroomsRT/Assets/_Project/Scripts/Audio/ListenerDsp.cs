using UnityEngine;
using Backrooms.Generation;

namespace Backrooms.Audio
{
    /// <summary>
    /// 聞き手に付ける最終段。初期反射 → 残響 → テープ。
    ///
    /// 部屋の測り直しは主スレッドで、音の処理は音声スレッドで走る。
    /// 受け渡しは 1 つの錠だけ。音声スレッドで錠を取るのは本来避けたいが、
    /// ここで守るのは数値の代入だけで、待たされる相手がいない。
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    [DisallowMultipleComponent]
    public class ListenerDsp : MonoBehaviour
    {
        [Header("部屋の測定")]
        public int Seed = 1996;
        public float EyeHeight = 1.345f;
        [Tooltip("これだけ動いたら測り直す (m)")]
        public float MoveEpsilon = 0.60f;
        [Tooltip("これだけ向きが変われば測り直す (度)")]
        public float TurnEpsilon = 22f;
        public float MaxInterval = 0.35f;

        [Header("量")]
        [Range(0f, 1f)] public float ReverbAmount = 1f;
        [Range(0f, 1f)] public float TapeAmount = 0.7f;
        public bool Enabled = true;

        /// <summary>直前に測った部屋。ログと検証用。</summary>
        public RoomAcoustics.Estimate Room { get; private set; }
        public int Probes { get; private set; }
        public int Blocks { get; private set; }
        public float TapeGain { get; private set; }

        Fdn _fdn;
        Early _early;
        Tape _tape;
        int _fs;

        readonly object _gate = new object();
        bool _pendingDecay, _pendingTaps;
        float _rt60 = 0.5f, _hfRatio = 0.6f, _wet = 0.33f;
        readonly float[] _sec = new float[Early.MaxTaps];
        readonly float[] _gain = new float[Early.MaxTaps];
        readonly float[] _pan = new float[Early.MaxTaps];
        int _nTaps;
        float _wetLive = 0.33f;

        Vector3 _lastAt = new Vector3(1e9f, 1e9f, 1e9f);
        Vector3 _lastFwd = Vector3.forward;
        float _nextProbe;

        // 録音 (検証用)
        float[] _rec;
        int _recPos;
        volatile bool _recording;

        void Awake()
        {
            _fs = AudioSettings.outputSampleRate;
            if (_fs <= 0) _fs = 48000;
            _fdn = new Fdn(_fs);
            _early = new Early(_fs);
            _tape = new Tape(_fs);
            _fdn.SetDecay(_rt60, _hfRatio);
        }

        void Update()
        {
            if (!Enabled) return;
            var p = transform.position;
            var f = transform.forward;
            bool moved = (p - _lastAt).sqrMagnitude > MoveEpsilon * MoveEpsilon;
            bool turned = Vector3.Angle(f, _lastFwd) > TurnEpsilon;
            if (!moved && !turned && Time.unscaledTime < _nextProbe) return;
            _nextProbe = Time.unscaledTime + MaxInterval;
            _lastAt = p; _lastFwd = f;
            ProbeNow(p, f);
        }

        /// <summary>キャプチャは瞬間移動するので、外から明示的に呼べるようにしておく。</summary>
        public void ProbeNow(Vector3 p, Vector3 fwd)
        {
            var e = RoomAcoustics.Probe(p, Seed);
            Room = e;
            Probes++;

            var sec = new float[Early.MaxTaps];
            var gain = new float[Early.MaxTaps];
            var pan = new float[Early.MaxTaps];
            int n = RoomAcoustics.BuildTaps(e, fwd, sec, gain, pan, EyeHeight);

            lock (_gate)
            {
                _rt60 = e.Rt60; _hfRatio = e.HfRatio; _wet = e.Wet * ReverbAmount;
                for (int i = 0; i < n; i++) { _sec[i] = sec[i]; _gain[i] = gain[i]; _pan[i] = pan[i]; }
                _nTaps = n;
                _pendingDecay = true; _pendingTaps = true;
            }
        }

        // ------------------------------------------------------------ 録音

        public void StartRecord(float seconds, int channels = 2)
        {
            _rec = new float[Mathf.CeilToInt(seconds * _fs) * channels];
            _recPos = 0;
            _recording = true;
        }

        public bool RecordDone { get { return _rec != null && _recPos >= _rec.Length; } }

        public void StopRecord(string path, int channels = 2)
        {
            _recording = false;
            if (_rec == null) return;
            var cut = _rec;
            if (_recPos < _rec.Length)
            {
                cut = new float[_recPos];
                System.Array.Copy(_rec, cut, _recPos);
            }
            Wav.Write(path, cut, channels, _fs);
            _rec = null;
        }

        public int SampleRate { get { return _fs; } }

        // ------------------------------------------------------ 音声スレッド

        void OnAudioFilterRead(float[] data, int channels)
        {
            Blocks++;
            if (!Enabled || _fdn == null) return;

            float wetTarget;
            lock (_gate)
            {
                if (_pendingDecay) { _fdn.SetDecay(_rt60, _hfRatio); _pendingDecay = false; }
                if (_pendingTaps) { _early.SetTaps(_sec, _gain, _pan, _nTaps); _pendingTaps = false; }
                wetTarget = _wet;
            }

            int n = data.Length / channels;
            // ブロック内で滑らかに寄せる。段で切り替えるとザラつきが乗る。
            float step = n > 0 ? (wetTarget - _wetLive) / n : 0f;

            for (int i = 0; i < n; i++)
            {
                int o = i * channels;
                float l = data[o];
                float r = channels > 1 ? data[o + 1] : l;
                float mono = (l + r) * 0.5f;

                float el, er2;
                _early.Process(mono, out el, out er2);
                float rl, rr;
                _fdn.Process(mono, out rl, out rr);

                _wetLive += step;
                float w = _wetLive;
                data[o] = l + (el + rl) * w;
                if (channels > 1) data[o + 1] = r + (er2 + rr) * w;
            }
            _wetLive = wetTarget;

            _tape.Amount = TapeAmount;
            _tape.Process(data, channels);
            TapeGain = _tape.CurrentGain;

            if (_recording && _rec != null)
            {
                int take = Mathf.Min(data.Length, _rec.Length - _recPos);
                if (take > 0) { System.Array.Copy(data, 0, _rec, _recPos, take); _recPos += take; }
                if (_recPos >= _rec.Length) _recording = false;
            }
        }
    }
}
