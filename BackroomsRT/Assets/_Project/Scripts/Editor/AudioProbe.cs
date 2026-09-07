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
            {
                var rc = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(
                    "Assets/_Project/Audio/Step_Carpet.wav");
                log.AppendLine(rc != null
                    ? string.Format("  足音は実録音 Step_Carpet.wav ({0:F3}s)", rc.length)
                    : "  足音は合成 (実録音が見つからない)");
            }
            DumpClip(dir, "src_stepL.wav", SoundBank.Footsteps[SoundBank.StepIndex(0, false, 0)], log);
            DumpClip(dir, "src_stepL2.wav", SoundBank.Footsteps[SoundBank.StepIndex(0, false, 1)], log);
            DumpClip(dir, "src_stepR.wav", SoundBank.Footsteps[SoundBank.StepIndex(0, true, 0)], log);
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

            // ---- 声 -------------------------------------------------------
            // 呼び声 (自分) と遠くの声を、実行時と同じ構造で部屋に通す。
            // 遠い声は「直接音がほとんど無く、残響だけが返ってくる」ことが
            // 全てなので、直接音と残響の比を数字で出しておく。
            {
                var s = spots.Count > 1 ? spots[1] : spots[0];
                var male = LoadVoices("male_");
                var female = LoadVoices("female_");
                log.AppendLine();
                log.AppendLine($"声の素材: male {male.Count} 本 / female {female.Count} 本  "
                               + $"(部屋 = {s.Name})");
                log.AppendLine("種類          素材                     直接音   残響   残響/直接  出力peak 出力rms");
                if (male.Count > 0)
                    VoiceRow(dir, s, "shout", "呼び声(自分)", male[0],
                             Mix.Shout, Mix.ShoutSend, 0f, log);
                if (female.Count > 0)
                    VoiceRow(dir, s, "voice_far", "遠くの声", female[0],
                             Mix.VoiceFarDirect * 0.25f, Mix.VoiceFarSend, 430f, log);

                var breath = AssetDatabase.LoadAssetAtPath<AudioClip>(
                    "Assets/_Project/Audio/Breath_Normal.wav");
                if (breath != null) BreathDemo(dir, s, breath, log);
                else log.AppendLine("呼吸: Breath_Normal.wav が無い");
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

        /// <summary>
        /// 足音を部屋に通して、最後にテープを掛ける。耳で聞く用。
        /// 並べ方は Footsteps.cs と同じ規則にする。左右交互、変種を巡回、
        /// 間隔と強さをばらつかせる。片方の音を 10 回並べても判断できない。
        /// </summary>
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

            int len = Fs * 7;
            var dry = new float[len];
            var rnd = new System.Random(4242);
            var rec = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(
                "Assets/_Project/Audio/Step_Carpet.wav");
            float[] recData = rec != null ? ClipData(rec) : null;
            float stride = 1f / 1.9f;              // 歩幅の周期
            float t = 0.35f;
            for (int k = 0; k < 12; k++)
            {
                bool right = (k & 1) == 1;
                var clip = recData ?? ClipData(SoundBank.Footsteps[
                    SoundBank.StepIndex(0, right, k % SoundBank.StepVariants)]);
                float jitter = (float)rnd.NextDouble() * 0.04f * stride;
                float lvl = 0.55f * (right ? 0.90f : 1.0f)
                            * (0.84f + (float)rnd.NextDouble() * 0.32f);
                int at = Mathf.RoundToInt((t + jitter) * Fs);
                for (int i = 0; i < clip.Length && at + i < len; i++) dry[at + i] += clip[i] * lvl;
                t += stride;
            }
            var hum = ClipData(SoundBank.Ballast);
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
            var tp = new Tape(Fs); tp.Amount = 0.7f;
            tp.Process(buf, 2);
            return buf;
        }

        // ------------------------------------------------------------ 声

        static List<AudioClip> LoadVoices(string prefix)
        {
            var list = new List<AudioClip>();
            const string dir = "Assets/_Project/Audio/Voices";
            if (!UnityEditor.AssetDatabase.IsValidFolder(dir)) return list;
            var paths = new List<string>();
            foreach (var g in UnityEditor.AssetDatabase.FindAssets("t:AudioClip", new[] { dir }))
                paths.Add(UnityEditor.AssetDatabase.GUIDToAssetPath(g));
            paths.Sort(StringComparer.Ordinal);
            foreach (var p in paths)
            {
                if (!Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal)) continue;
                var c = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(p);
                if (c != null) list.Add(c);
            }
            return list;
        }

        static void VoiceRow(string dir, Spot s, string key, string label, AudioClip clip,
                             float direct, float send, float lpHz, StringBuilder log)
        {
            float dPk, wPk, oPk, oRms;
            var buf = RenderVoice(s, ClipData(clip), direct, send, lpHz,
                                  out dPk, out wPk, out oPk, out oRms);
            Wav.Write(Path.Combine(dir, "demo_" + key + ".wav"), buf, 2, Fs);
            float ratio = dPk > 1e-6f ? 20f * Mathf.Log10(wPk / dPk) : 99f;
            log.AppendLine(string.Format(
                "{0,-12} {1,-24} {2,7:F4} {3,7:F4} {4,8:F1}dB {5,8:F3} {6,8:F4}",
                label, Path.GetFileNameWithoutExtension(
                    UnityEditor.AssetDatabase.GetAssetPath(clip)), dPk, wPk, ratio, oPk, oRms));
        }

        /// <summary>
        /// 呼吸を 30 秒ぶん通す。平常 -> 走って上がる -> 収まる。
        /// 実行時と同じ規則 (直線で上げ下げ、上がるより戻りが遅い) で
        /// 再生速度を動かす。耳で確かめられる形にしておかないと、
        /// 「速くなった」かどうかは数字では分からない。
        /// </summary>
        static void BreathDemo(string dir, Spot s, AudioClip clip, StringBuilder log)
        {
            const float Rise = 14f, Fall = 35f, MaxPitch = 1.20f, Loud = 1.8f;
            const float RunFrom = 8f, RunTo = 24f;
            var data = ClipData(clip);
            int len = Fs * 30;
            var dry = new float[len];

            double idx = 0;
            float pkPitch = 1f, endPitch = 1f;
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)Fs;
                float effort = t < RunFrom ? 0f
                             : t < RunTo ? Mathf.Clamp01((t - RunFrom) / Rise)
                             : Mathf.Clamp01((RunTo - RunFrom) / Rise - (t - RunTo) / Fall);
                float pitch = Mathf.Lerp(1f, MaxPitch, effort);
                if (pitch > pkPitch) pkPitch = pitch;
                endPitch = pitch;

                int i0 = (int)idx;
                float f = (float)(idx - i0);
                float a = data[i0 % data.Length], b = data[(i0 + 1) % data.Length];
                dry[i] = (a * (1f - f) + b * f) * Mix.Breath * Mathf.Lerp(1f, Loud, effort);
                idx += pitch;
                if (idx >= data.Length) idx -= data.Length;
            }
            // 床を混ぜる前の呼吸だけも出しておく。混ぜたあとでは、息が
            // 上がったときに音量が 1.8 倍になる分へ重心が引きずられて、
            // 「再生速度が上がった」ことを測って取り出せない。
            Wav.Write(Path.Combine(dir, "demo_breath_dry.wav"), (float[])dry.Clone(), 1, Fs);

            // 部屋の床。呼吸だけ聞いても大きさの見当が付かない。
            var hum = ClipData(SoundBank.Ballast);
            var tone = ClipData(SoundBank.RoomTone);
            var hvac = ClipData(SoundBank.Hvac);
            float bRms = 0f;
            for (int i = 0; i < len; i++) bRms += dry[i] * dry[i];
            bRms = Mathf.Sqrt(bRms / len);
            float bedRms = 0f;
            for (int i = 0; i < len; i++)
            {
                float bed = hum[i % hum.Length] * Mix.Ballast + tone[i % tone.Length] * Mix.Tone
                          + hvac[i % hvac.Length] * Mix.Hvac;
                bedRms += bed * bed;
                dry[i] += bed;
            }
            bedRms = Mathf.Sqrt(bedRms / len);

            var buf = RunRoom(s, dry, null);
            Wav.Write(Path.Combine(dir, "demo_breath.wav"), buf, 2, Fs);
            log.AppendLine(string.Format(
                "呼吸: {0:F1}s ループ / 走り {1:F0}s で速度 {2:F2} 倍 / 30s 時点で {3:F2} 倍  "
                + "呼吸 rms {4:F4} / 部屋の床 rms {5:F4} = {6:+0.0;-0.0}dB",
                clip.length, Rise, pkPitch, endPitch, bRms, bedRms,
                20f * Mathf.Log10(bRms / Mathf.Max(bedRms, 1e-9f))));
        }

        /// <summary>乾いた音の並びを部屋に通してテープまで掛ける。送りは任意。</summary>
        static float[] RunRoom(Spot s, float[] dry, float[] send)
        {
            var fdn = new Fdn(Fs);
            var early = new Early(Fs);
            fdn.SetDecay(s.Est.Rt60, s.Est.HfRatio);
            var sec = new float[Early.MaxTaps];
            var gain = new float[Early.MaxTaps];
            var pan = new float[Early.MaxTaps];
            int n = RoomAcoustics.BuildTaps(s.Est, Vector3.forward, sec, gain, pan, Level0Layout.EyeH);
            early.SetTaps(sec, gain, pan, n);

            int len = dry.Length;
            var buf = new float[len * 2];
            float w = s.Est.Wet;
            for (int i = 0; i < len; i++)
            {
                float x = dry[i];
                float rev = send != null ? x + send[i] : x;
                float el, er, rl, rr;
                early.Process(rev, out el, out er);
                fdn.Process(rev, out rl, out rr);
                buf[i * 2] = x + (el + rl) * w;
                buf[i * 2 + 1] = x + (er + rr) * w;
            }
            var tp = new Tape(Fs) { Amount = 0.7f };
            tp.Process(buf, 2);
            return buf;
        }

        /// <summary>
        /// 声を部屋に通す。直接音と「残響にだけ送る分」を別々に持たせるのは
        /// 実行時 (ReverbSend + ListenerDsp) と同じ構造。遠さは音量ではなく
        /// この 2 つの比で決まるので、比を測れる形で組む。
        /// </summary>
        static float[] RenderVoice(Spot s, float[] clip, float direct, float send, float lpHz,
                                   out float dPeak, out float wPeak,
                                   out float outPeak, out float outRms)
        {
            var fdn = new Fdn(Fs);
            var early = new Early(Fs);
            fdn.SetDecay(s.Est.Rt60, s.Est.HfRatio);
            var sec = new float[Early.MaxTaps];
            var gain = new float[Early.MaxTaps];
            var pan = new float[Early.MaxTaps];
            int n = RoomAcoustics.BuildTaps(s.Est, Vector3.forward, sec, gain, pan, Level0Layout.EyeH);
            early.SetTaps(sec, gain, pan, n);

            int len = Fs * 8;
            var dry = new float[len];
            var snd = new float[len];
            var lp = new Biquad();
            if (lpHz > 0f) lp.LowPass(Fs, lpHz, 0.7f);
            int at = Fs / 2;
            dPeak = 0f;
            for (int i = 0; i < clip.Length && at + i < len; i++)
            {
                float v = clip[i];
                float d = (lpHz > 0f ? lp.Process(v) : v) * direct;
                dry[at + i] += d;
                snd[at + i] += v * send;
                float a = Math.Abs(d); if (a > dPeak) dPeak = a;
            }
            // 部屋の床。これが無いと AGC が声だけを見て動き、実機と挙動が変わる。
            var hum = ClipData(SoundBank.Ballast);
            var tone = ClipData(SoundBank.RoomTone);
            for (int i = 0; i < len; i++)
                dry[i] += hum[i % hum.Length] * Mix.Ballast + tone[i % tone.Length] * Mix.Tone;

            var buf = new float[len * 2];
            float w = s.Est.Wet;
            wPeak = 0f;
            for (int i = 0; i < len; i++)
            {
                float x = dry[i];
                float rev = x + snd[i];
                float el, er, rl, rr;
                early.Process(rev, out el, out er);
                fdn.Process(rev, out rl, out rr);
                float wl = (el + rl) * w, wr = (er + rr) * w;
                buf[i * 2] = x + wl;
                buf[i * 2 + 1] = x + wr;
                float a = Math.Abs(wl); if (a > wPeak) wPeak = a;
            }
            var tp = new Tape(Fs) { Amount = 0.7f };
            tp.Process(buf, 2);
            outPeak = 0f; outRms = 0f;
            for (int i = 0; i < buf.Length; i++)
            {
                float a = Math.Abs(buf[i]); if (a > outPeak) outPeak = a;
                outRms += buf[i] * buf[i];
            }
            outRms = Mathf.Sqrt(outRms / buf.Length);
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
