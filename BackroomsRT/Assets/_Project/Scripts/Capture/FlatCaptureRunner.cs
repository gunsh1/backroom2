using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Backrooms.Audio;
using Backrooms.Generation;
using Backrooms.Player;

namespace Backrooms.Capture
{
    /// <summary>
    /// フラット画面で決められた視点のスチルと fps を採取し、エディタごと終了する。
    /// VHS 合成が無くなったので、カメラの出力をそのまま 1920x1080 で書き出す。
    /// </summary>
    public class FlatCaptureRunner : MonoBehaviour
    {
        [System.Serializable]
        public struct Shot
        {
            public string Name;
            public Vector3 Pos;
            public float Yaw, Pitch;
            public bool Light;
            public Shot(string n, Vector3 p, float yaw, float pitch, bool light = false)
            { Name = n; Pos = p; Yaw = yaw; Pitch = pitch; Light = light; }
        }

        public string OutputDir = "../artifacts/shots";
        public int Width = 1920;
        public int Height = 1080;
        public int WarmupFrames = 16;
        // ヒストグラム自動露出は 64 フレームでは収束しきらず、実行ごとに
        // 全体の明るさが 0.02 ほど振れていた。参照と数値で比べる以上、
        // 撮り直して同じ数字が出ないと測定にならない。
        public int SettleFrames = 220;
        public int FpsFrames = 180;
        public bool QuitWhenDone = true;

        readonly List<string> _log = new List<string>();

        /// <summary>
        /// シーンに CaptureRoute があればそちらを使う。間取りが乱数で決まる
        /// ようになったので、直書きの座標は生成器が無い場合の保険でしかない。
        /// </summary>
        static readonly Shot[] Fallback =
        {
            new Shot("01_hero",     new Vector3( 1.60f, 0f,  1.60f),  30f,   2f),
            new Shot("02_corner",   new Vector3( 5.00f, 0f,  2.20f), 245f,  22f),
            new Shot("03_ceiling",  new Vector3( 4.40f, 0f,  4.40f),  20f, -26f),
            new Shot("04_doorway",  new Vector3( 6.00f, 0f,  6.40f),   4f,   1f),
            new Shot("05_backroom", new Vector3( 5.20f, 0f, 13.60f), 190f,   2f),
            // 面取りと材質が最も出る、壁と幅木と床の取り合いに寄ったカット
            new Shot("06_detail",   new Vector3( 1.30f, 0f,  4.20f), 265f,  34f),
            // ライトを点けて RT シャドウと光の落ち方を見る
            new Shot("07_torch",    new Vector3( 5.60f, 0f, 12.40f), 200f,   0f, true),
            // 人の触った物: 開口脇のスイッチと床の見切り
            new Shot("08_switch",   new Vector3( 6.90f, 0f,  7.60f),  10f,  -4f),
            // 西壁の配線モールとサーモスタット
            new Shot("09_conduit",  new Vector3( 2.20f, 0f,  6.20f), 228f,  14f),
        };

        IEnumerator Start()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Directory.CreateDirectory(OutputDir);

            var fps = FindFirstObjectByType<FirstPersonController>();
            var cam = Camera.main;
            var torch = FindFirstObjectByType<Flashlight>();
            var culler = FindFirstObjectByType<LightCuller>();
            var route = FindFirstObjectByType<CaptureRoute>();
            _route = route;
            var world = FindFirstObjectByType<ChunkedLevel0>();
            var dsp = FindFirstObjectByType<ListenerDsp>();
            var shots = (route != null && route.Shots != null && route.Shots.Length > 0)
                        ? route.Shots : Fallback;
            if (fps == null || cam == null)
            {
                _log.Add("FATAL: FirstPersonController / Camera.main が見つからない");
                Finish();
                yield break;
            }

            float eyeH = route != null ? route.EyeHeight : 1.68f;
            // 記録媒体の縦横比はシーンが決める。VHS は 4:3 しかない。
            if (route != null && route.Width > 0 && route.Height > 0)
            {
                Width = route.Width; Height = route.Height;
            }

            // 測っている間はキーを見ない。GUI モードの Unity が前面を取るので、
            // その間に打たれた E をそのまま拾ってしまい、fps を測っている
            // 最中にイベントが走って別物の数字が出る (実測 20 回拾った)。
            var shoutRef = FindFirstObjectByType<Shout>();
            if (shoutRef != null) shoutRef.AcceptKey = false;
            // 抽選も止める。fps を測っている最中に当たると数字が別物になる。
            // イベントは PanicTest が明示的に起こす。
            var panicRef = FindFirstObjectByType<Player.PanicEvent>();
            if (panicRef != null) panicRef.AutoRoll = false;

            // 参照画像にタイムコードが写ると、見た目の前後比較が使えなくなる。
            Backrooms.Game.Osd.Enabled = false;

            _log.Add("=== capture ===");
            _log.Add("graphicsDeviceType : " + SystemInfo.graphicsDeviceType);
            _log.Add("graphicsDeviceName : " + SystemInfo.graphicsDeviceName);
            _log.Add("supportsRayTracing : " + SystemInfo.supportsRayTracing);
            _log.Add("output             : " + Width + "x" + Height);
            _log.Add("lightmaps          : " + LightmapSettings.lightmaps.Length);
            _log.Add("shots              : " + shots.Length
                     + (route != null ? " (from CaptureRoute)" : " (fallback)"));
            for (int i = 0; i < WarmupFrames; i++) yield return null;

            // 数えるのは暖機のあと。ChunkedLevel0.Start より先にここへ来るので、
            // 前に置くと必ず 0 が記録される。
            if (world != null)
                _log.Add("chunks             : " + world.LoadedChunks
                         + "  (" + ChunkedLevel0.ChunkSize.ToString("F1") + " m each)");
            if (dsp != null)
            {
                var e = dsp.Room;
                _log.Add("audio              : " + dsp.SampleRate + " Hz  "
                         + "ブロック " + dsp.Blocks + " 回"
                         + (dsp.Blocks > 0 ? "" : "  << 音声スレッドが回っていない"));
                _log.Add("room               : 床 " + e.FloorArea.ToString("F0") + " m2  "
                         + "RT60 " + e.Rt60.ToString("F3") + " s  hf比 " + e.HfRatio.ToString("F2")
                         + "  室定数 " + e.RoomConstant.ToString("F0")
                         + "  wet " + e.Wet.ToString("F2"));
            }
            else _log.Add("audio              : ListenerDsp が無い");

            if (culler != null)
            {
                culler.RefreshNow();
                _log.Add("area lights        : " + culler.Count + " 個  うち点灯 "
                         + culler.ActiveCount + "  上限 " + culler.MaxActive
                         + (culler.Saturated ? "  << 上限に張り付き (歩くと入れ替わる)" : ""));
            }

            // 出力用 RT はキャプチャ中ずっとカメラの描画先にしておく。
            // 待機後に cam.Render() を一発呼ぶ方式だと TAA とレイトレのデノイザに
            // 履歴が乗らず、実際の画面より粗い絵が撮れてしまう。
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                       RenderTextureReadWrite.sRGB) { name = "CaptureTarget" };
            rt.Create();
            cam.targetTexture = rt;

            foreach (var s in shots)
            {
                fps.SetPose(s.Pos, s.Yaw, s.Pitch);
                fps.enabled = false;
                fps.SetPose(s.Pos, s.Yaw, s.Pitch);
                if (torch != null) torch.ForceState(s.Light);
                // 瞬間移動なので、間引きをタイマー任せにすると
                // 切り替わる前のフレームが撮れる。
                if (culler != null)
                {
                    var fwd = Quaternion.Euler(s.Pitch, s.Yaw, 0f) * Vector3.forward;
                    culler.Refresh(s.Pos + Vector3.up * eyeH + fwd * culler.LookAhead);
                }

                // 無限生成では瞬間移動の直後に周りが存在しない。
                // 1 フレームに 1 チャンクずつしか作らないので、
                // 増えなくなるまで待たないと「空中に浮いた絵」が撮れる。
                if (world != null)
                {
                    int last = -1, still = 0;
                    for (int i = 0; i < 400 && still < 20; i++)
                    {
                        yield return new WaitForEndOfFrame();
                        if (world.LoadedChunks == last) still++; else { still = 0; last = world.LoadedChunks; }
                    }
                }

                // ここで毎フレーム rt に描かれ、履歴が積み上がる
                for (int i = 0; i < SettleFrames; i++) yield return new WaitForEndOfFrame();

                ReadOut(rt, Path.Combine(OutputDir, s.Name + ".png"), route);
                _log.Add($"shot: {s.Name}  pos={s.Pos} yaw={s.Yaw} pitch={s.Pitch}"
                         + (s.Light ? "  torch=ON" : ""));
                fps.enabled = true;
            }

            cam.targetTexture = null;
            rt.Release();
            if (torch != null) torch.ForceState(false);

            // --- fps 計測（照明が多く見通しの利く主室で）---
            var probe = route != null ? route.FpsProbe : new Vector3(3.0f, 0f, 3.0f);
            float probeYaw = route != null ? route.FpsYaw : 40f;
            fps.SetPose(probe, probeYaw, 0f);
            fps.enabled = false;
            if (culler != null) culler.Refresh(probe + Vector3.up * 1.68f);
            for (int i = 0; i < 30; i++) yield return null;

            float total = 0f, worst = 0f;
            for (int i = 0; i < FpsFrames; i++)
            {
                yield return null;
                total += Time.unscaledDeltaTime;
                worst = Mathf.Max(worst, Time.unscaledDeltaTime);
            }
            float avg = FpsFrames / Mathf.Max(total, 1e-5f);
            _log.Add($"fps: avg={avg:F1}  worst frame={worst * 1000f:F1} ms  ({FpsFrames} frames)"
                     + $"   [{Width}x{Height}]");

            // 歩きながら実際の出力を録る。合成した素材が本当に鳴り、
            // 残響とテープを通って出ているかは、これを測らないと分からない。
            // 声は歩き出してから撃つ。呼び声と遠くの声が実際に鳴って、
            // 残響を通って出ているかは録って測らないと分からない。
            if (dsp != null) { dsp.StartRecord(12f); StartCoroutine(VoiceCues()); }
            yield return WalkTest(fps, route, culler, world, dsp);
            if (dsp != null)
            {
                try
                {
                    dsp.StopRecord("../artifacts/audio/live.wav");
                    _log.Add("audio live         : ../artifacts/audio/live.wav");
                }
                catch (System.Exception e) { _log.Add("audio live 失敗: " + e.Message); }
                var steps = FindFirstObjectByType<Footsteps>();
                var voices = FindFirstObjectByType<FixtureVoices>();
                _log.Add("audio sources      : 足音 " + (steps != null ? steps.Played : -1) + " 回  "
                         + "器具 " + (voices != null ? voices.Assigned : -1) + " 台 "
                         + "(壁越し " + (voices != null ? voices.Occluded : -1) + ")  "
                         + "AGC " + (dsp != null ? dsp.TapeGain.ToString("F2") : "-"));
                var sh = FindFirstObjectByType<Shout>();
                var dv = FindFirstObjectByType<DistantVoice>();
                _log.Add("audio voices       : 呼び声 " + (sh != null ? sh.Played : -1) + " 回"
                         + " (うちキー " + (sh != null ? sh.ByKey : -1) + ")  "
                         + "遠くの声 " + (dv != null ? dv.Played : -1) + " 回"
                         + (dv != null && dv.Played > 0
                            ? $"  最後は {dv.LastDistance:F1}m "
                              + (dv.LastOccluded ? "壁の向こう" : "見通せる")
                            : ""));
            }
            yield return BreathTest(fps);
            yield return BreathCostTest();
            yield return PanicTest(dsp);
            Finish();
        }

        /// <summary>
        /// 呼吸の流し込みを止めた前後で fps を比べる。
        ///
        /// Ola を入れてから fps が 89.5 -> 74.2 に落ちた。音声スレッドの
        /// 処理が本編の速さに効くはずはないので、どこで食っているかを
        /// 憶測ではなく A/B で切り分ける。
        /// </summary>
        IEnumerator BreathCostTest()
        {
            var br = FindFirstObjectByType<Breathing>();
            if (br == null) yield break;

            float on = 0f, off = 0f;
            const int N = 150;
            br.SetStreaming(true);
            for (int i = 0; i < 20; i++) yield return null;
            for (int i = 0; i < N; i++) { yield return null; on += Time.unscaledDeltaTime; }
            br.SetStreaming(false);
            for (int i = 0; i < 20; i++) yield return null;
            for (int i = 0; i < N; i++) { yield return null; off += Time.unscaledDeltaTime; }
            br.SetStreaming(true);

            _log.Add(string.Format(
                "breath cost: 流し込みあり {0:F1} fps / 止めると {1:F1} fps  (差 {2:F2} ms/frame)",
                N / Mathf.Max(on, 1e-4f), N / Mathf.Max(off, 1e-4f),
                (on - off) / N * 1000f));

            // カメラを揺らすと、レイトレのデノイザと TAA が前のフレームを
            // 使い回せなくなる。イベント中に重くなるならここが出どころ。
            var shake = Camera.main != null ? Camera.main.GetComponent<Player.CameraShake>() : null;
            if (shake == null) yield break;
            float still = 0f, moving = 0f;
            shake.Amount = 0f;
            for (int i = 0; i < 20; i++) yield return null;
            for (int i = 0; i < N; i++) { yield return null; still += Time.unscaledDeltaTime; }
            shake.Amount = 0.8f;
            for (int i = 0; i < 20; i++) yield return null;
            for (int i = 0; i < N; i++) { yield return null; moving += Time.unscaledDeltaTime; }
            shake.Amount = 0f;
            _log.Add(string.Format(
                "shake cost: 揺れ無し {0:F1} fps / 揺れ 0.8 {1:F1} fps  (差 {2:F2} ms/frame)",
                N / Mathf.Max(still, 1e-4f), N / Mathf.Max(moving, 1e-4f),
                (moving - still) / N * 1000f));
        }

        /// <summary>
        /// イベントを 1 回起こして、各所に届いているかを実際に測る。
        /// 「カメラを揺らし、VHS を荒らし、走りを速くし、息を替える」は
        /// どれも別の仕組みなので、1 つでも配線が抜けていても絵は出る。
        /// 収まったあとに基準値へ戻ることまで見る。
        /// </summary>
        IEnumerator PanicTest(ListenerDsp dsp)
        {
            var pe = FindFirstObjectByType<Player.PanicEvent>();
            if (pe == null) { _log.Add("panic: PanicEvent が無い"); yield break; }
            var shake = Camera.main != null ? Camera.main.GetComponent<Player.CameraShake>() : null;
            var br = FindFirstObjectByType<Breathing>();
            var fps = FindFirstObjectByType<FirstPersonController>();
            var vol = FindFirstObjectByType<UnityEngine.Rendering.Volume>();
            Post.VhsPostProcess vhs = null;
            if (vol != null && vol.profile != null) vol.profile.TryGet(out vhs);

            float sprint0 = fps != null ? fps.SprintSpeed : 0f;
            float grain0 = vhs != null ? vhs.grain.value : 0f;
            float track0 = vhs != null ? vhs.tracking.value : 0f;

            // 揺れが実際に何度まで振れたかを見る。振れ幅は Amount x RotDegrees で
            // 決まるが、Perlin なので毎フレーム最大まで行くわけではない。
            var camT = Camera.main != null ? Camera.main.transform : null;
            Quaternion rot0 = camT != null ? camT.localRotation : Quaternion.identity;
            float maxRot = 0f, maxPos = 0f, maxGain = 0f, minGain = 99f;
            Vector3 pos0 = camT != null ? camT.localPosition : Vector3.zero;

            // 揺れている最中の絵を撮る。「道がぎりぎり読める」かどうかは
            // 数字ではなく絵でしか判定できない。同じ場所・同じ向きで
            // 発生前と並べる。
            var probe = _route != null ? _route.FpsProbe : new Vector3(3.0f, 0f, 3.0f);
            float probeYaw = _route != null ? _route.FpsYaw : 40f;
            if (fps != null) { fps.SetPose(probe, probeYaw, 0f); fps.enabled = false; }
            var prt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                        RenderTextureReadWrite.sRGB) { name = "PanicTarget" };
            prt.Create();
            if (Camera.main != null) Camera.main.targetTexture = prt;
            for (int i = 0; i < 60; i++) yield return new WaitForEndOfFrame();
            ReadOut(prt, System.IO.Path.Combine(OutputDir, "P0_before.png"), _route);

            if (dsp != null) dsp.StartRecord(20f);
            pe.Fire();
            float[] shotAt = { 0.8f, 2.5f, 6.0f, 12.0f };
            string[] shotName = { "P1_t08", "P2_t25", "P3_t60", "P4_t120" };
            int shotNext = 0;

            var rows = new System.Text.StringBuilder();
            float t0 = Time.unscaledTime, next = 0f;
            while (Time.unscaledTime - t0 < 22f)
            {
                float t = Time.unscaledTime - t0;
                if (camT != null)
                {
                    maxRot = Mathf.Max(maxRot, Quaternion.Angle(rot0, camT.localRotation));
                    maxPos = Mathf.Max(maxPos, Vector3.Distance(pos0, camT.localPosition));
                }
                if (dsp != null && t > 0.05f)
                {
                    maxGain = Mathf.Max(maxGain, dsp.TapeGain);
                    minGain = Mathf.Min(minGain, dsp.TapeGain);
                }
                if (shotNext < shotAt.Length && t >= shotAt[shotNext])
                {
                    ReadOut(prt, System.IO.Path.Combine(OutputDir, shotName[shotNext] + ".png"), _route);
                    shotNext++;
                }
                if (t >= next)
                {
                    rows.Append(string.Format("{0:F0}s p{1:F2} 揺{2:F2} 粒{3:F2} 追{4:F2} 走{5:F1} 息{6:F2} 利{7:F2}  ",
                        t, pe.Panic, shake != null ? shake.Amount : -1f,
                        vhs != null ? vhs.grain.value : -1f,
                        vhs != null ? vhs.tracking.value : -1f,
                        fps != null ? fps.SprintSpeed : -1f,
                        br != null ? br.Rate : -1f,
                        dsp != null ? dsp.TapeGain : -1f));
                    next += 3f;
                }
                yield return null;
            }
            if (dsp != null)
            {
                try
                {
                    dsp.StopRecord("../artifacts/audio/panic.wav");
                    _log.Add("panic audio        : ../artifacts/audio/panic.wav");
                }
                catch (System.Exception e) { _log.Add("panic audio 失敗: " + e.Message); }
            }

            if (Camera.main != null) Camera.main.targetTexture = null;
            prt.Release();
            if (fps != null) fps.enabled = true;

            _log.Add($"panic: 発生 {pe.Fired} 回 / 次の確率 {pe.Chance * 100f:F2}%");
            _log.Add(string.Format(
                "  カメラ: 最大 {0:F1} 度 / {1:F3} m  (画角 {2:F0} 度) / AGC {3:F2}-{4:F2}",
                maxRot, maxPos, Camera.main != null ? Camera.main.fieldOfView : -1f,
                minGain, maxGain));
            _log.Add("  " + rows.ToString().TrimEnd());
            _log.Add(string.Format(
                "  収束後: 揺れ {0:F2} (動悸ぶん) / 走り {1:F1} (基準 {2:F1}) / "
                + "粒 {3:F2} (基準 {4:F2}) / 追従 {5:F2} (基準 {6:F2}) / 動悸 {7:F2}",
                shake != null ? shake.Amount : -1f,
                fps != null ? fps.SprintSpeed : -1f, sprint0,
                vhs != null ? vhs.grain.value : -1f, grain0,
                vhs != null ? vhs.tracking.value : -1f, track0,
                pe.After));
        }

        /// <summary>
        /// 走って息が上がり、やめて収まるところまでを実際に動かして測る。
        /// 「走ると速くなる」は数字にしないと直ったかどうか分からない。
        /// </summary>
        IEnumerator BreathTest(FirstPersonController fps)
        {
            var br = FindFirstObjectByType<Breathing>();
            if (br == null || fps == null) yield break;

            var rows = new System.Text.StringBuilder();
            fps.enabled = true;
            fps.UseExternalInput = true;
            fps.ExternalMove = new Vector2(0f, 1f);

            float t0 = Time.unscaledTime, next = 0f;
            const float Run = 16f, Rest = 20f;
            while (Time.unscaledTime - t0 < Run + Rest)
            {
                float t = Time.unscaledTime - t0;
                fps.ExternalSprint = t < Run;
                if (t >= next)
                {
                    rows.Append($"{t:F0}s {br.Rate:F3}  ");
                    next += 4f;
                }
                yield return null;
            }
            fps.ExternalSprint = false;
            fps.ExternalMove = Vector2.zero;
            fps.UseExternalInput = false;
            fps.enabled = false;

            _log.Add($"breath: 走り {Run:F0}s -> 休み {Rest:F0}s の再生速度");
            _log.Add("  " + rows.ToString().TrimEnd());
            _log.Add($"  上がりきり {br.MaxRate:F2} 倍 (走り {br.RiseSeconds:F0}s) / "
                     + $"戻り {br.FallSeconds:F0}s / 終了時 {br.Effort:F2}");
        }

        /// <summary>
        /// 録音の窓の中で声を撃つ。E キーは押せないので直接叩く。
        /// 遠くの声は平均 2 分に 1 回なので、待っていては録れない。
        /// </summary>
        IEnumerator VoiceCues()
        {
            var sh = FindFirstObjectByType<Shout>();
            var dv = FindFirstObjectByType<DistantVoice>();
            yield return new WaitForSecondsRealtime(1.2f);
            if (sh != null) sh.Fire();
            yield return new WaitForSecondsRealtime(3.5f);
            if (dv != null) dv.Fire();
        }

        /// <summary>
        /// 生成した間取りを実際に歩いて通れるか確かめる。
        /// ベイのグラフ上の連結を確認しただけでは歩けることの証明にならない。
        /// コライダ・開口の有効幅・敷居の段差のどれかが引っ掛かれば止まる。
        /// </summary>
        IEnumerator WalkTest(FirstPersonController fps, CaptureRoute route, LightCuller culler,
                             ChunkedLevel0 world, ListenerDsp dspRef)
        {
            if (route == null || route.Waypoints == null || route.Waypoints.Length < 2)
            {
                _log.Add("walk: no route");
                yield break;
            }
            var wp = route.Waypoints;
            float eyeH = route.EyeHeight;
            fps.SetPose(wp[0], route.FpsYaw, 0f);
            fps.enabled = true;
            fps.UseExternalInput = true;
            fps.ExternalMove = new Vector2(0f, 1f);

            int reached = 0;
            float t0 = Time.unscaledTime, stuckAt = -1f;
            float travelled = 0f;
            // 「チャンクの更新が見える」かどうかは、止まって測った fps では
            // 分からない。歩いている間だけを測る。
            float wWorst = 0f, wTotal = 0f;
            // 平均だけだと、外の要因で 1 回止まっただけでも数字が別物になる。
            // 実行のたびに 57.8-75.6 fps と振れていたので中央値も採る。
            var wAll = new List<float>(8192);
            int wFrames = 0, hitch = 0, chMin = int.MaxValue, chMax = 0;
            int ltMax = 0, satFrames = 0;
            float agcLo = float.MaxValue, agcHi = 0f;

            for (int k = 1; k < wp.Length; k++)
            {
                float deadline = Time.unscaledTime + 20f;
                float bestD = float.MaxValue, lastGain = Time.unscaledTime;
                while (Time.unscaledTime < deadline)
                {
                    var p = fps.transform.position;
                    var to = wp[k] - p; to.y = 0f;
                    float dist = to.magnitude;
                    if (dist < 0.55f) break;
                    if (dist < bestD - 0.05f) { bestD = dist; lastGain = Time.unscaledTime; }

                    fps.SetYaw(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
                    // 角に引っ掛かったら横に逃がす。まっすぐ押し続けても抜けない。
                    bool stuck = Time.unscaledTime - lastGain > 1.2f;
                    fps.ExternalMove = stuck
                        ? new Vector2(Mathf.Sin(Time.unscaledTime * 3.1f) > 0f ? 1f : -1f, 0.5f)
                        : new Vector2(0f, 1f);

                    if (culler != null && k % 2 == 0) culler.Refresh(p + Vector3.up * eyeH);
                    yield return null;
                    travelled += Vector3.Distance(new Vector3(p.x, 0f, p.z),
                                                  new Vector3(fps.transform.position.x, 0f, fps.transform.position.z));

                    float fdt = Time.unscaledDeltaTime;
                    wTotal += fdt; wFrames++; wAll.Add(fdt);
                    if (fdt > wWorst) wWorst = fdt;
                    if (fdt > 0.020f) hitch++;              // 50fps を割った frame
                    if (world != null)
                    {
                        chMin = Mathf.Min(chMin, world.LoadedChunks);
                        chMax = Mathf.Max(chMax, world.LoadedChunks);
                    }
                    if (culler != null)
                    {
                        ltMax = Mathf.Max(ltMax, culler.ActiveCount);
                        if (culler.Saturated) satFrames++;
                    }
                    if (dspRef != null)
                    {
                        float g = dspRef.TapeGain;
                        if (g < agcLo) agcLo = g;
                        if (g > agcHi) agcHi = g;
                    }
                }
                fps.ExternalMove = new Vector2(0f, 1f);
                var d = wp[k] - fps.transform.position; d.y = 0f;
                if (d.magnitude < 0.55f) reached++;
                else { stuckAt = k; break; }
            }
            fps.ExternalMove = Vector2.zero;
            fps.UseExternalInput = false;
            fps.enabled = false;

            float dt = Time.unscaledTime - t0;
            _log.Add($"walk: reached {reached}/{wp.Length - 1} waypoints in {dt:F1} s, "
                     + $"travelled {travelled:F1} m"
                     + (stuckAt >= 0 ? $"  STUCK before waypoint {stuckAt} at {fps.transform.position}" : "  OK"));
            if (dspRef != null && agcHi > 0f)
                _log.Add(string.Format("walk agc: {0:F2} 〜 {1:F2} 倍  (上限 {2:F1})",
                                       agcLo, agcHi, Mix.AgcMaxGain));
            if (wFrames > 0)
            {
                wAll.Sort();
                float med = wAll[wAll.Count / 2];
                float p95 = wAll[Mathf.Min(wAll.Count - 1, (int)(wAll.Count * 0.95f))];
                _log.Add($"walk frames: 中央値 {1f / med:F1} fps ({med * 1000f:F1} ms)  "
                         + $"95% {p95 * 1000f:F1} ms  avg={wFrames / Mathf.Max(wTotal, 1e-4f):F1} fps  "
                         + $"worst={wWorst * 1000f:F1} ms  20ms超え {hitch}/{wFrames}  "
                         + $"チャンク {(chMin == int.MaxValue ? 0 : chMin)}..{chMax}  "
                         + $"同時ライト最大 {ltMax}  上限張り付き {satFrames}/{wFrames}");
            }
        }

        /// <summary>
        /// 蓄積済みの RT を PNG に書き出す。CaptureRoute にレンズと記録系の
        /// 値が入っていればそれを掛ける。掛ける前の生の画も _raw.png として
        /// 残す。媒体の値は生の画から測り直すので、これが無いと較正できない。
        /// </summary>
        CaptureRoute _route;

        void ReadOut(RenderTexture rt, string path, CaptureRoute route)
        {
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;

            bool medium = route != null && (route.LensK1 != 0f || route.BlurSigma > 0f
                                            || route.Glare > 0f
                                            || route.MediumGain != Color.white);
            if (medium)
            {
                File.WriteAllBytes(path.Replace(".png", "_raw.png"), tex.EncodeToPNG());
                CaptureMedium.Apply(tex, route.LensK1, route.LensOverscan,
                                    route.BlurSigma, route.Glare, route.GlareSigma,
                                    route.MediumGain);
            }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
        }

        void Finish()
        {
            var sb = new StringBuilder();
            foreach (var l in _log) sb.AppendLine(l);
            try { File.WriteAllText("../artifacts/capture_report.txt", sb.ToString()); } catch { }
            Debug.Log(sb.ToString());
#if UNITY_EDITOR
            if (QuitWhenDone) UnityEditor.EditorApplication.Exit(0);
#endif
        }
    }
}
