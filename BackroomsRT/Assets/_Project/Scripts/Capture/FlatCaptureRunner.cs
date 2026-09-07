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
            Finish();
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
                    wTotal += fdt; wFrames++;
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
                _log.Add($"walk frames: avg={wFrames / Mathf.Max(wTotal, 1e-4f):F1} fps  "
                         + $"worst={wWorst * 1000f:F1} ms  20ms超え {hitch}/{wFrames}  "
                         + $"チャンク {(chMin == int.MaxValue ? 0 : chMin)}..{chMax}  "
                         + $"同時ライト最大 {ltMax}  上限張り付き {satFrames}/{wFrames}");
        }

        /// <summary>
        /// 蓄積済みの RT を PNG に書き出す。CaptureRoute にレンズと記録系の
        /// 値が入っていればそれを掛ける。掛ける前の生の画も _raw.png として
        /// 残す。媒体の値は生の画から測り直すので、これが無いと較正できない。
        /// </summary>
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
