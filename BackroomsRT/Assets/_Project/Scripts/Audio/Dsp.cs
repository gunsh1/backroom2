using System;

namespace Backrooms.Audio
{
    /// <summary>
    /// 音の処理。UnityEngine には依存しない。
    ///
    /// 依存させないのには理由がある。音声スレッドで走らせる処理は、
    /// 走っている最中に中を見られない。同じクラスをエディタから
    /// オフラインで叩いてインパルス応答を書き出せるようにしておけば、
    /// 「RT60 を 0.5 秒と指定した」ではなく「実際に 0.5 秒で減衰した」
    /// を測って確かめられる。tools/analyse-audio.py がそれをやる。
    /// </summary>
    public sealed class Delay
    {
        readonly float[] _b;
        int _w;

        public Delay(int maxSamples) { _b = new float[Math.Max(4, maxSamples)]; }
        public int Capacity => _b.Length;
        public void Clear() { Array.Clear(_b, 0, _b.Length); _w = 0; }

        public void Write(float x) { _b[_w] = x; if (++_w >= _b.Length) _w = 0; }

        /// <summary>d サンプル前。Write の直後に Tap(1) を呼ぶと書いた値が返る。</summary>
        public float Tap(int d)
        {
            if (d < 1) d = 1;
            else if (d >= _b.Length) d = _b.Length - 1;
            int i = _w - d;
            if (i < 0) i += _b.Length;
            return _b[i];
        }

        public float TapFrac(float d)
        {
            int i0 = (int)d;
            float f = d - i0;
            return Tap(i0) * (1f - f) + Tap(i0 + 1) * f;
        }
    }

    /// <summary>直流利得 1 の 1 次ローパス。y += a(x - y)</summary>
    public sealed class OnePole
    {
        float _a = 1f, _y;
        public void Clear() { _y = 0f; }
        public float A { get { return _a; } set { _a = value < 0f ? 0f : (value > 1f ? 1f : value); } }
        public float Process(float x) { _y += _a * (x - _y); return _y; }

        /// <summary>
        /// 角周波数 w での振幅が m になる減衰係数 d を求める。
        /// y = (1-d)x + d*y_prev の形で、|H(w)| = (1-d)/sqrt(1-2d cos w + d^2)。
        /// これを解くと d^2(1-m^2) - 2d(1 - m^2 cos w) + (1-m^2) = 0。
        /// </summary>
        public static float DampingFor(float m, float w)
        {
            if (m >= 0.999f) return 0f;
            if (m < 1e-4f) m = 1e-4f;
            double mm = m * m;
            double a = 1.0 - mm;
            double b = 1.0 - mm * Math.Cos(w);
            double disc = b * b - a * a;
            if (disc < 0.0) return 0f;
            double d = (b - Math.Sqrt(disc)) / a;
            if (d < 0.0) d = 0.0;
            if (d > 0.98) d = 0.98;
            return (float)(1.0 - d);      // OnePole.A = 1 - d
        }
    }

    /// <summary>Schroeder のオールパス。位相だけを散らして密度を上げる。</summary>
    public sealed class Allpass
    {
        readonly Delay _d;
        readonly int _len;
        readonly float _g;

        public Allpass(int len, float g) { _len = len; _d = new Delay(len + 4); _g = g; }
        public void Clear() { _d.Clear(); }

        public float Process(float x)
        {
            float dl = _d.Tap(_len);
            float v = x + _g * dl;
            _d.Write(v);
            return dl - _g * v;
        }
    }

    /// <summary>2 次のフィルタ。RBJ の式。</summary>
    public sealed class Biquad
    {
        float _b0 = 1f, _b1, _b2, _a1, _a2;
        float _x1, _x2, _y1, _y2;

        public void Clear() { _x1 = _x2 = _y1 = _y2 = 0f; }

        public void LowPass(float fs, float fc, float q)
        {
            double w = 2.0 * Math.PI * fc / fs, c = Math.Cos(w), s = Math.Sin(w);
            double al = s / (2.0 * q);
            double a0 = 1.0 + al;
            _b0 = (float)((1.0 - c) * 0.5 / a0);
            _b1 = (float)((1.0 - c) / a0);
            _b2 = _b0;
            _a1 = (float)(-2.0 * c / a0);
            _a2 = (float)((1.0 - al) / a0);
        }

        public void HighPass(float fs, float fc, float q)
        {
            double w = 2.0 * Math.PI * fc / fs, c = Math.Cos(w), s = Math.Sin(w);
            double al = s / (2.0 * q);
            double a0 = 1.0 + al;
            _b0 = (float)((1.0 + c) * 0.5 / a0);
            _b1 = (float)(-(1.0 + c) / a0);
            _b2 = _b0;
            _a1 = (float)(-2.0 * c / a0);
            _a2 = (float)((1.0 - al) / a0);
        }

        public void Peak(float fs, float fc, float q, float gainDb)
        {
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w = 2.0 * Math.PI * fc / fs, c = Math.Cos(w), s = Math.Sin(w);
            double al = s / (2.0 * q);
            double a0 = 1.0 + al / A;
            _b0 = (float)((1.0 + al * A) / a0);
            _b1 = (float)(-2.0 * c / a0);
            _b2 = (float)((1.0 - al * A) / a0);
            _a1 = _b1;
            _a2 = (float)((1.0 - al / A) / a0);
        }

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }

    /// <summary>
    /// 帰還遅延網 (FDN) の残響。
    ///
    /// 8 本の遅延線を Hadamard 行列で混ぜ合わせる。行列がエネルギーを
    /// 保つので、減衰は各線の帰還利得だけで決まる。
    ///   g_i = 10^(-3 L_i / (fs T))
    /// これは「T 秒で -60dB」をそのまま書いたもの。指定した RT60 が
    /// そのまま出るので、測って合っていなければ実装が間違っている。
    ///
    /// 高域は別に落とす。鉱物繊維の吸音天井は 4kHz で吸音率 0.8 を超え、
    /// 低域の 3 倍近く吸う。高域だけ短く減衰しないと部屋の材料が嘘になる。
    /// </summary>
    public sealed class Fdn
    {
        public const int N = 8;

        // 互いに素に近い長さ。24〜54ms。短すぎると金属的に響き、
        // 長すぎると立ち上がりが粗くなる。
        static readonly int[] BaseLen = { 1237, 1381, 1607, 1789, 1993, 2179, 2381, 2557 };

        readonly Delay[] _d = new Delay[N];
        readonly int[] _len = new int[N];
        readonly OnePole[] _damp = new OnePole[N];
        readonly float[] _g = new float[N];
        readonly float[] _s = new float[N];
        readonly Allpass[] _diff;
        readonly float _fs;

        public Fdn(float sampleRate)
        {
            _fs = sampleRate;
            float k = sampleRate / 48000f;
            for (int i = 0; i < N; i++)
            {
                _len[i] = Math.Max(64, (int)(BaseLen[i] * k));
                _d[i] = new Delay(_len[i] + 8);
                _damp[i] = new OnePole();
            }
            // 入力の拡散。これが無いと最初の数十 ms が「点」に聞こえる。
            _diff = new Allpass[]
            {
                new Allpass((int)(142 * k), 0.62f), new Allpass((int)(107 * k), 0.62f),
                new Allpass((int)(379 * k), 0.55f), new Allpass((int)(277 * k), 0.55f),
            };
            SetDecay(0.5f, 0.55f);
        }

        public void Clear()
        {
            for (int i = 0; i < N; i++) { _d[i].Clear(); _damp[i].Clear(); _s[i] = 0f; }
            for (int i = 0; i < _diff.Length; i++) _diff[i].Clear();
        }

        /// <summary>rt60 秒。hfRatio は 4kHz の残響時間の比 (0.3 なら高域は 3 割の長さ)。</summary>
        public void SetDecay(float rt60, float hfRatio)
        {
            if (rt60 < 0.05f) rt60 = 0.05f;
            if (hfRatio < 0.05f) hfRatio = 0.05f;
            if (hfRatio > 1f) hfRatio = 1f;
            float w = 2f * (float)Math.PI * 4000f / _fs;    // 高域の基準は 4kHz
            for (int i = 0; i < N; i++)
            {
                double e = -3.0 * _len[i] / (_fs * rt60);
                _g[i] = (float)Math.Pow(10.0, e);
                // 高域だけ余分に落とす分。1 巡あたりの追加損失。
                double m = Math.Pow(10.0, e * (1.0 / hfRatio - 1.0));
                _damp[i].A = OnePole.DampingFor((float)m, w);
            }
        }

        public void Process(float x, out float l, out float r)
        {
            float v = x;
            for (int i = 0; i < _diff.Length; i++) v = _diff[i].Process(v);

            for (int i = 0; i < N; i++)
                _s[i] = _damp[i].Process(_d[i].Tap(_len[i]) * _g[i]);

            Hadamard8(_s);

            for (int i = 0; i < N; i++) _d[i].Write(v + _s[i]);

            l = (_s[0] + _s[2] - _s[4] - _s[6]) * 0.5f;
            r = (_s[1] - _s[3] + _s[5] - _s[7]) * 0.5f;
        }

        /// <summary>8 点の高速 Walsh-Hadamard 変換。1/sqrt(8) で正規化。</summary>
        static void Hadamard8(float[] a)
        {
            float t;
            for (int s = 1; s < 8; s <<= 1)
                for (int i = 0; i < 8; i += s << 1)
                    for (int j = i; j < i + s; j++)
                    {
                        t = a[j]; a[j] = t + a[j + s]; a[j + s] = t - a[j + s];
                    }
            const float k = 0.35355339f;    // 1/sqrt(8)
            for (int i = 0; i < 8; i++) a[i] *= k;
        }
    }

    /// <summary>
    /// 初期反射。壁までの実距離から直接タップを置く。
    ///
    /// 部屋の広さが変わっても RT60 はほとんど動かない (天井高で決まる)。
    /// 空間が変わったと分かるのはここ。壁が 1.8m なら往復 10.5ms、
    /// 30m なら 175ms。この時刻の分布そのものが部屋の形の情報になる。
    ///
    /// タップの入れ替えは 50ms かけて古い組と新しい組を混ぜる。
    /// 直接差し替えると遅延が飛んでプツッと鳴る。
    /// </summary>
    public sealed class Early
    {
        public const int MaxTaps = 10;

        readonly Delay _d;
        readonly float[] _delA = new float[MaxTaps], _gainA = new float[MaxTaps];
        readonly float[] _delB = new float[MaxTaps], _gainB = new float[MaxTaps];
        readonly float[] _panA = new float[MaxTaps], _panB = new float[MaxTaps];
        int _nA, _nB;
        float _mix = 1f;            // 0 = A、1 = B
        float _step;
        readonly float _fs;

        public Early(float sampleRate, float maxSeconds = 0.35f)
        {
            _fs = sampleRate;
            _d = new Delay((int)(sampleRate * maxSeconds) + 8);
            _step = 1f / (0.050f * sampleRate);
        }

        public void Clear() { _d.Clear(); }

        /// <summary>秒と利得と定位 (-1..1) で組を差し替える。</summary>
        public void SetTaps(float[] sec, float[] gain, float[] pan, int n)
        {
            if (n > MaxTaps) n = MaxTaps;
            // いま鳴っている組を A に落としてから、新しい組を B に入れる。
            // 遷移の途中でもう一度呼ばれたら、そこは諦めて B に揃える。
            // 部屋の測り直しは 4 回/秒なので、遷移中に来ることはまずない。
            Array.Copy(_delB, _delA, MaxTaps);
            Array.Copy(_gainB, _gainA, MaxTaps);
            Array.Copy(_panB, _panA, MaxTaps);
            _nA = _nB;
            for (int i = 0; i < n; i++)
            {
                float s = sec[i] * _fs;
                if (s < 1f) s = 1f;
                if (s > _d.Capacity - 4) s = _d.Capacity - 4;
                _delB[i] = s; _gainB[i] = gain[i]; _panB[i] = pan[i];
            }
            _nB = n;
            _mix = 0f;
        }

        public void Process(float x, out float l, out float r)
        {
            _d.Write(x);
            float al = 0f, ar = 0f, bl = 0f, br = 0f;
            for (int i = 0; i < _nA; i++)
            {
                float v = _d.TapFrac(_delA[i]) * _gainA[i];
                al += v * (1f - _panA[i]) * 0.5f;
                ar += v * (1f + _panA[i]) * 0.5f;
            }
            for (int i = 0; i < _nB; i++)
            {
                float v = _d.TapFrac(_delB[i]) * _gainB[i];
                bl += v * (1f - _panB[i]) * 0.5f;
                br += v * (1f + _panB[i]) * 0.5f;
            }
            float m = _mix;
            l = al * (1f - m) + bl * m;
            r = ar * (1f - m) + br * m;
            if (_mix < 1f) { _mix += _step; if (_mix > 1f) _mix = 1f; }
        }
    }

    /// <summary>
    /// 1996 年の家庭用カムコーダで録った音にする。
    ///
    /// 演出ではなく、実機が持っている制約を並べたもの。
    ///   * リニア音声トラックの帯域は 100Hz〜8kHz 程度しかない
    ///   * テープ走行のむらでピッチがわずかに揺れる (ワウ・フラッタ)
    ///   * 内蔵マイクの AGC が静かな場面で利得を上げ、ヒスが浮いてくる
    /// 最後のものが一番効く。無音のはずの廊下で「サー」が膨らんでくるのは、
    /// マイクが自分でそうしているから。
    /// </summary>
    public sealed class Tape
    {
        readonly Biquad _hpL = new Biquad(), _hpR = new Biquad();
        readonly Biquad _lpL1 = new Biquad(), _lpR1 = new Biquad();
        readonly Biquad _lpL2 = new Biquad(), _lpR2 = new Biquad();
        readonly Delay _wowL, _wowR;
        readonly float _fs;
        float _ph1, _ph2;
        float _env;
        float _agc = 1f;
        uint _rng = 0x9E3779B1;

        public float Amount = 1f;          // 全体の掛かり具合
        public float Hiss = 0.0018f;       // ヒスの振幅
        public float WowPercent = 0.15f;   // ピッチ揺れ (%)
        public float AgcTarget = 0.14f;    // AGC が狙う実効値
        public float AgcMaxGain = 6f;

        public Tape(float sampleRate)
        {
            _fs = sampleRate;
            _hpL.HighPass(sampleRate, 95f, 0.7f); _hpR.HighPass(sampleRate, 95f, 0.7f);
            _lpL1.LowPass(sampleRate, 8000f, 0.72f); _lpR1.LowPass(sampleRate, 8000f, 0.72f);
            _lpL2.LowPass(sampleRate, 8000f, 1.60f); _lpR2.LowPass(sampleRate, 8000f, 1.60f);
            int n = (int)(sampleRate * 0.006f) + 8;
            _wowL = new Delay(n); _wowR = new Delay(n);
        }

        public void Clear()
        {
            _hpL.Clear(); _hpR.Clear(); _lpL1.Clear(); _lpR1.Clear(); _lpL2.Clear(); _lpR2.Clear();
            _wowL.Clear(); _wowR.Clear(); _env = 0f; _agc = 1f;
        }

        float Noise()
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            return (_rng & 0xFFFFFF) / 8388607.5f - 1f;
        }

        public void Process(float[] data, int channels)
        {
            if (Amount <= 0f) return;
            float k = Amount;
            int n = data.Length / channels;
            // ピッチ揺れ。1 周期あたりの遅延の振れ幅は (揺れ率)/(2 pi f)。
            float w1 = 2f * (float)Math.PI * 4.1f / _fs, w2 = 2f * (float)Math.PI * 0.63f / _fs;
            float sw1 = WowPercent * 0.01f / (2f * (float)Math.PI * 4.1f) * _fs;
            float sw2 = WowPercent * 0.01f / (2f * (float)Math.PI * 0.63f) * _fs * 0.45f;
            float nominal = _fs * 0.003f;

            // AGC の追従。上げるのは遅く (2.5 秒)、下げるのは速く (0.12 秒)。
            // 実機もそうで、大きな音が入ると一瞬で絞られてゆっくり戻る。
            float upA = 1f - (float)Math.Exp(-1.0 / (_fs * 2.5));
            float dnA = 1f - (float)Math.Exp(-1.0 / (_fs * 0.12));
            float envA = 1f - (float)Math.Exp(-1.0 / (_fs * 0.05));

            for (int i = 0; i < n; i++)
            {
                int o = i * channels;
                float l = data[o];
                float r = channels > 1 ? data[o + 1] : l;

                float a = Math.Abs(l) + Math.Abs(r);
                _env += envA * (a * 0.5f - _env);
                float want = _env > 1e-5f ? AgcTarget / _env : AgcMaxGain;
                if (want > AgcMaxGain) want = AgcMaxGain;
                if (want < 0.25f) want = 0.25f;
                _agc += (want < _agc ? dnA : upA) * (want - _agc);
                l *= _agc; r *= _agc;

                _ph1 += w1; if (_ph1 > 6.2831853f) _ph1 -= 6.2831853f;
                _ph2 += w2; if (_ph2 > 6.2831853f) _ph2 -= 6.2831853f;
                float d = nominal + (float)Math.Sin(_ph1) * sw1 + (float)Math.Sin(_ph2) * sw2;
                _wowL.Write(l); _wowR.Write(r);
                l = _wowL.TapFrac(d); r = _wowR.TapFrac(d);

                l = _lpL2.Process(_lpL1.Process(_hpL.Process(l)));
                r = _lpR2.Process(_lpR1.Process(_hpR.Process(r)));

                // ヒスは再生側で乗るので帯域制限のあと。AGC の利得ぶんだけ持ち上がる。
                float h = Hiss * (0.5f + 0.5f * _agc);
                l += Noise() * h; r += Noise() * h;

                data[o] = data[o] * (1f - k) + l * k;
                if (channels > 1) data[o + 1] = data[o + 1] * (1f - k) + r * k;
                for (int c = 2; c < channels; c++) data[o + c] = 0f;
            }
        }

        public float CurrentGain { get { return _agc; } }
    }
}
