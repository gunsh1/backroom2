using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Backrooms.Audio;
using Backrooms.Generation;

namespace Backrooms.EditorTools
{
    /// <summary>
    /// 音を外へ出して測れるようにする。
    ///
    /// 音声スレッドで走っているものは、走っている最中に中を見られない。
    /// 同じクラスをここでオフラインに叩き、インパルス応答と合成した素材を
    /// WAV に書き出す。「RT60 を 0.5 秒に設定した」ではなく
    /// 「実際に 0.5 秒で 60dB 落ちた」を tools/analyse-audio.py が確かめる。
    ///
    /// 間取りの中から狭い所・普通の所・開けた所を自分で探して、
    /// 3 つとも測る。動的反響が効いているなら 3 つは違う応答になる。
    /// </summary>
    public static class AudioProbe
    {
        const int Fs = 48000;
        const int Seed = 1996;
        static readonly string Out = Path.Combine(
            Path.GetDirectoryName(Application.dataPath) ?? ".", "..", "artifacts", "audio");

        [MenuItem("Backrooms/4. Audio Check")]
        public static void Run()
        {
            var dir = Path.GetFullPath(Out);
            Directory.CreateDirectory(dir);
            var log = new StringBuilder();
            log.AppendLine("=== 音 ===");
            log.AppendLine("出力先: " + dir);

            // ---- 素材を書き出す ------------------------------------------
            SoundBank.Build(Seed);
            log.AppendLine(SoundBank.Report ?? "(合成に失敗)");
            DumpClip(dir, "src_ballast.wav", SoundBank.Ballast, log);
            DumpClip(dir, "src_ballast_fail.wav", SoundBank.BallastFail, log);
            DumpClip(dir, "src_hvac.wav", SoundBank.Hvac, log);
            DumpClip(dir, "src_roomtone.wav", SoundBank.RoomTone, log);
            DumpClip(dir, "src_step.wav", SoundBank.Footsteps[0], log);
            DumpClip(dir, "src_click.wav", SoundBank.Clicks[0], log);

            // ---- 測る場所を間取りから探す --------------------------------
            var spots = FindSpots(log);

            log.AppendLine();
            log.AppendLine("場所                    床面積   容積   吸音   平均α   RT60  RT60hf  室定数  Wet  最近壁 最遠壁");
            foreach (var s in spots)
            {
                var e = s.Est;
                log.AppendLine(string.Format(
                    "{0,-20} {1,7:F0} {2,7:F0} {3,6:F0} {4,7:F3} {5,6:F3} {6,6:F3} {7,7:F0} {8,5:F2} {9,6:F1} {10,6:F1}",
                    s.Name, e.FloorArea, e.Volume, e.Absorption, e.MeanAlpha,
                    e.Rt60, e.Rt60Hf, e.RoomConstant, e.Wet, e.NearWall, e.FarWall));
            }

            // ---- インパルス応答 ------------------------------------------
            log.AppendLine();
            foreach (var s in spots)
            {
                var taps = RenderIr(s, 3.0f, false);
                Wav.Write(Path.Combine(dir, "ir_" + s.Key + ".wav"), taps.ir, 2, Fs);
                log.AppendLine(string.Format(
                    "{0,-20} 初期反射 {1} 本  最初 {2:F1} ms  最後 {3:F1} ms",
                    s.Name, taps.n, taps.first * 1000f, taps.last * 1000f));
                var sb = new StringBuilder("   タップ(ms): ");
                for (int i = 0; i < taps.n; i++) sb.AppendFormat("{0:F1} ", taps.sec[i] * 1000f);
                log.AppendLine(sb.ToString());
            }

            // ---- 通した音 (テープあり) ------------------------------------
            foreach (var s in spots)
            {
                var demo = RenderDemo(s);
                Wav.Write(Path.Combine(dir, "demo_" + s.Key + ".wav"), demo, 2, Fs);
            }
            // ---- テープの応答を測る素材 -----------------------------------
            // 足音のスペクトルで測ると素材自体の傾きと区別が付かない。
            // 平坦な雑音を通した前後を並べて置く。
            {
                int len = Fs * 3;
                var flat = new float[len * 2];
                uint rs = 12345;
                for (int i = 0; i < len; i++)
                {
                    rs ^= rs << 13; rs ^= rs >> 17; rs ^= rs << 5;
                    float v = ((rs & 0xFFFFFF) / 8388607.5f - 1f) * 0.25f;
                    flat[i * 2] = v; flat[i * 2 + 1] = v;
                }
                Wav.Write(Path.Combine(dir, "tape_dry.wav"), (float[])flat.Clone(), 2, Fs);
                var t = new Tape(Fs) { Amount = 1f };
                t.Process(flat, 2);
                Wav.Write(Path.Combine(dir, "tape_wet.wav"), flat, 2, Fs);
                log.AppendLine();
                log.AppendLine("tape_dry / tape_wet は平坦な雑音の前後。テープの帯域を測る用。");
            }

            log.AppendLine("ir_*.wav は残響のみ (直接音なし)。RT60 を測る用。");
            log.AppendLine("demo_*.wav は足音を部屋に通してテープまで掛けたもの。");

            File.WriteAllText(Path.Combine(dir, "..", "audio_report.txt"), log.ToString(), Encoding.UTF8);
            Debug.Log(log.ToString());
        }

        // ------------------------------------------------------------ 場所

        class Spot
        {
            public string Name, Key;
            public Vector3 P;
            public RoomAcoustics.Estimate Est;
        }

        static List<Spot> FindSpots(StringBuilder log)
        {
            // 開いている点を広く拾って、床面積で並べる
            var cands = new List<(Vector3 p, RoomAcoustics.Estimate e)>();
            for (int i = -14; i <= 14; i += 2)
                for (int j = -14; j <= 14; j += 2)
                {
                    var p = new Vector3(i * Level0Layout.Bay + 0.9f, 0f, j * Level0Layout.Bay + 0.9f);
                    if (!Level0Layout.Clear(p.x, p.z, Seed, 0.75f)) continue;
                    cands.Add((p, RoomAcoustics.Probe(p, Seed)));
                }
            cands.Sort((a, b) => a.e.FloorArea.CompareTo(b.e.FloorArea));
            log.AppendLine(string.Format("候補 {0} 点  床面積 {1:F0} 〜 {2:F0} m2",
                cands.Count,
                cands.Count > 0 ? cands[0].e.FloorArea : 0f,
                cands.Count > 0 ? cands[cands.Count - 1].e.FloorArea : 0f));

            var outp = new List<Spot>();
            if (cands.Count == 0) return outp;
            void Add(string name, string key, int idx)
            {
                var c = cands[Mathf.Clamp(idx, 0, cands.Count - 1)];
                outp.Add(new Spot { Name = name, Key = key, P = c.p, Est = c.e });
            }
            Add("狭い所", "tight", 0);
            Add("普通の部屋", "room", cands.Count / 2);
            Add("開けた所", "open", cands.Count - 1);
            return outp;
        }

        // ------------------------------------------------------------ 描画

        struct IrResult
        {
            public float[] ir;
            public float[] sec;
            public int n;
            public float first, last;
        }

        static IrResult RenderIr(Spot s, float seconds, bool tape)
        {
            var fdn = new Fdn(Fs);
            var early = new Early(Fs);
            fdn.SetDecay(s.Est.Rt60, s.Est.HfRatio);

            var sec = new float[Early.MaxTaps];
            var gain = new float[Early.MaxTaps];
            var pan = new float[Early.MaxTaps];
            int n = RoomAcoustics.BuildTaps(s.Est, Vector3.forward, sec, gain, pan, Level0Layout.EyeH);
            early.SetTaps(sec, gain, pan, n);
            // 差し替えの混ぜが終わるまで無音を通す
            for (int i = 0; i < Fs / 10; i++) { float a, b; early.Process(0f, out a, out b); }

            int len = Mathf.RoundToInt(seconds * Fs);
            var buf = new float[len * 2];
            float w = s.Est.Wet;
            for (int i = 0; i < len; i++)
            {
                float x = i == 0 ? 1f : 0f;
                float el, er, rl, rr;
                early.Process(x, out el, out er);
                fdn.Process(x, out rl, out rr);
                // 測定用なので直接音は入れない。RT60 は残響の傾きで、
                // 直接音を混ぜると最初の -5dB がそこで決まってしまい、
                // 傾きが指定した値からずれる。
                buf[i * 2] = (el + rl) * w;
                buf[i * 2 + 1] = (er + rr) * w;
            }
            if (tape) new Tape(Fs).Process(buf, 2);

            float first = float.MaxValue, last = 0f;
            for (int i = 0; i < n; i++) { if (sec[i] < first) first = sec[i]; if (sec[i] > last) last = sec[i]; }
            return new IrResult { ir = buf, sec = sec, n = n, first = first, last = last };
        }

        /// <summary>足音を部屋に通して、最後にテープを掛ける。耳で聞く用。</summary>
        static float[] RenderDemo(Spot s)
        {
            var fdn = new Fdn(Fs);
            var early = new Early(Fs);
            fdn.SetDecay(s.Est.Rt60, s.Est.HfRatio);
            var sec = new float[Early.MaxTaps];
            var gain = new float[Early.MaxTaps];
            var pan = new float[Early.MaxTaps];
            int n = RoomAcoustics.BuildTaps(s.Est, Vector3.forward, sec, gain, pan, Level0Layout.EyeH);
            early.SetTaps(sec, gain, pan, n);
            for (int i = 0; i < Fs / 10; i++) { float a, b; early.Process(0f, out a, out b); }

            // 4 歩ぶん + 蛍光灯のうなりを薄く
            int len = Fs * 4;
            var dry = new float[len];
            var step = ClipData(SoundBank.Footsteps[0]);
            var hum = ClipData(SoundBank.Ballast);
            for (int k = 0; k < 4; k++)
            {
                int at = Mathf.RoundToInt((0.35f + k * 0.62f) * Fs);
                for (int i = 0; i < step.Length && at + i < len; i++) dry[at + i] += step[i] * 0.55f;
            }
            for (int i = 0; i < len; i++) dry[i] += hum[i % hum.Length] * 0.05f;

            var buf = new float[len * 2];
            float w = s.Est.Wet;
            for (int i = 0; i < len; i++)
            {
                float x = dry[i];
                float el, er, rl, rr;
                early.Process(x, out el, out er);
                fdn.Process(x, out rl, out rr);
                buf[i * 2] = x + (el + rl) * w;
                buf[i * 2 + 1] = x + (er + rr) * w;
            }
            var t = new Tape(Fs); t.Amount = 0.7f;
            t.Process(buf, 2);
            return buf;
        }

        static float[] ClipData(AudioClip c)
        {
            var d = new float[c.samples * c.channels];
            c.GetData(d, 0);
            if (c.channels == 1) return d;
            var m = new float[c.samples];
            for (int i = 0; i < c.samples; i++) m[i] = d[i * c.channels];
            return m;
        }

        static void DumpClip(string dir, string name, AudioClip c, StringBuilder log)
        {
            if (c == null) { log.AppendLine("  " + name + ": null"); return; }
            var d = ClipData(c);
            Wav.Write(Path.Combine(dir, name), d, 1, c.frequency);
            float pk = 0f, rms = 0f;
            for (int i = 0; i < d.Length; i++) { float a = Math.Abs(d[i]); if (a > pk) pk = a; rms += d[i] * d[i]; }
            rms = Mathf.Sqrt(rms / Math.Max(1, d.Length));
            log.AppendLine(string.Format("  {0,-24} {1,6:F2}s  peak {2:F3}  rms {3:F4}",
                                         name, d.Length / (float)c.frequency, pk, rms));
        }
    }
}
