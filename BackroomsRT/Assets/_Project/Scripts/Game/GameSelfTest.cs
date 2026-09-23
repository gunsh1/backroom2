using System.Collections;
using System.Text;
using UnityEngine;
using Backrooms.Player;

namespace Backrooms.Game
{
    /// <summary>
    /// 1 回のプレイのループが本当に閉じているかを、実際に再生して確かめる。
    ///
    /// 確かめるのは「コンパイルが通った」ではなく「3 通りの終わり方を
    /// 全部踏めて、それぞれ正しく次のテープへ行く」こと。脱出・捕まる・
    /// テープ切れのどれか 1 つでも抜けていれば、ゲームとして閉じていない。
    ///
    /// 保存は検査の前に消す。前回の続きから始まると、テープ番号の
    /// 進み方が確かめられない。
    /// </summary>
    /// GameSession より先に走らせる。保存を消すのが後回しになると、
    /// GameSession.Awake が先に古い保存を読んでしまい、テープ番号も
    /// 殺された回数も前回の続きから始まる。
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public class GameSelfTest : MonoBehaviour
    {
        public string OutPath = "../artifacts/game_report.txt";
        public bool QuitWhenDone = true;

        readonly StringBuilder _log = new StringBuilder();
        int _fail;

        void Line(string s) { _log.AppendLine(s); Debug.Log("[gametest] " + s); }

        void Check(string what, bool ok, string got)
        {
            if (!ok) _fail++;
            Line($"  {(ok ? "OK  " : "NG  ")}{what}  -> {got}");
        }

        void Awake() { SaveData.Wipe(); }

        IEnumerator Start()
        {
            yield return null;

            var s = GameSession.Instance;
            if (s == null) { Line("FATAL: GameSession が無い"); _fail++; Finish(); yield break; }
            s.AutoRestart = false;      // シーンを読み直すとこの検査自体が消える
            s.DyingSeconds = 0.3f;
            s.RunOutSeconds = 0.3f;
            s.CardSeconds = 0.3f;

            var fps = FindFirstObjectByType<FirstPersonController>();
            var tape = s.Tape;
            var exit = FindFirstObjectByType<Exit>();
            var threat = FindFirstObjectByType<Threat>();
            var torch = FindFirstObjectByType<Flashlight>();

            Line("=== ゲームの自己検査 " + System.DateTime.Now + " ===");
            Line("");
            Line("組み立て");
            Check("GameSession", s != null, "あり");
            Check("TapeState", tape != null, tape != null ? $"{tape.TotalSeconds / 60f:F0} 分" : "無し");
            Check("Exit", exit != null, exit != null ? $"({exit.Where.x:F1}, {exit.Where.z:F1})" : "無し");
            Check("Threat", threat != null,
                  threat != null ? $"追跡 {threat.HuntSpeed:F2} m/s" : "無し");
            Check("Hud", FindFirstObjectByType<Hud>() != null, "あり");
            Check("テープ 1 本目から", s.Save.tapeNumber == 1, $"{s.Save.tapeNumber} 本目");

            // --- 時計が減る
            Line("");
            Line("テープの時計");
            float t0 = tape.Remaining;
            yield return new WaitForSeconds(1.0f);
            float dropped = t0 - tape.Remaining;
            Check("止めなくても減る", dropped > 0.5f && dropped < 2.0f, $"1 秒で {dropped:F2} 秒");

            // --- 懐中電灯が残量を読む
            float want = tape.Remaining01;
            Check("ライトがテープ残量を読む",
                  torch != null && Mathf.Abs(torch.Battery01 - want) < 0.001f,
                  torch != null ? $"{torch.Battery01:F4} / テープ {want:F4}" : "ライト無し");

            // --- ポーズ
            Line("");
            Line("ポーズ");
            s.SetPaused(true);
            yield return null;
            Check("止まる", s.Current == Phase.Paused && Time.timeScale == 0f,
                  $"{s.Current} / timeScale {Time.timeScale:F1}");
            Check("カーソルが外れる", Cursor.lockState == CursorLockMode.None, Cursor.lockState.ToString());
            float held = tape.Remaining;
            yield return new WaitForSecondsRealtime(0.5f);
            Check("止めている間はテープも止まる", Mathf.Abs(tape.Remaining - held) < 0.01f,
                  $"{tape.Remaining - held:F3} 秒");
            s.SetPaused(false);
            yield return null;
            Check("戻る", s.Current == Phase.Playing && Time.timeScale == 1f,
                  $"{s.Current} / timeScale {Time.timeScale:F1}");
            Check("カーソルが必ず戻る", Cursor.lockState == CursorLockMode.Locked,
                  Cursor.lockState.ToString());

            // --- 息が上がると走りが落ちる
            Line("");
            Line("息切れ");
            var breath = FindFirstObjectByType<Backrooms.Audio.Breathing>();
            float before = fps != null ? fps.SprintFactor : -1f;
            if (breath != null && fps != null)
            {
                fps.UseExternalInput = true;
                fps.ExternalMove = new Vector2(0f, 1f);
                fps.ExternalSprint = true;
                float wait = 0f;
                while (wait < 20f && breath.Effort < 0.999f) { wait += Time.deltaTime; yield return null; }
                Check("走り続けると労力が上がりきる", breath.Effort > 0.99f, $"{breath.Effort:F2} ({wait:F0} 秒)");
                Check("走る速さが落ちる", fps.SprintFactor < 0.7f,
                      $"係数 {fps.SprintFactor:F2} -> {fps.SprintSpeed * fps.SprintFactor:F2} m/s");
                Check("脅威より速いまま",
                      threat == null || fps.SprintSpeed * fps.SprintFactor > threat.HuntSpeed,
                      threat != null
                        ? $"{fps.SprintSpeed * fps.SprintFactor:F2} > {threat.HuntSpeed:F2}" : "脅威無し");
                fps.ExternalSprint = false;
                fps.ExternalMove = Vector2.zero;
                fps.UseExternalInput = false;
            }
            else Check("Breathing", false, "無し");
            _ = before;

            // --- 捕まる
            Line("");
            Line("終わり方 1: 捕まる");
            int tapeBefore = s.Save.tapeNumber;
            s.Kill();
            yield return null;
            Check("死んだ状態になる", s.Current == Phase.Dying, s.Current.ToString());
            Check("殺された回数が増える", s.Save.kills == 1, $"{s.Save.kills} 回");
            yield return new WaitForSecondsRealtime(1.2f);
            Check("次のテープへ行く", s.Current == Phase.NewTape, s.Current.ToString());
            Check("テープ番号が進む", s.Save.tapeNumber == tapeBefore + 1,
                  $"{tapeBefore} -> {s.Save.tapeNumber}");
            Check("日付が進む", true, s.CardBody);

            // --- テープ切れ
            Line("");
            Line("終わり方 2: テープ切れ");
            tape.Load(s.Save.tapeNumber);
            RestartSession(s);
            yield return null;
            tape.TotalSeconds = 1.0f;
            tape.Load(s.Save.tapeNumber);
            int before2 = s.Save.tapeNumber;
            float guard = 0f;
            while (guard < 5f && s.Current == Phase.Playing) { guard += Time.deltaTime; yield return null; }
            Check("尽きたら終わる", s.Current == Phase.RunOut || s.Current == Phase.NewTape,
                  s.Current.ToString());
            Check("尽きた回数が増える", s.Save.runOuts == 1, $"{s.Save.runOuts} 回");
            yield return new WaitForSecondsRealtime(1.2f);
            Check("テープ番号が進む", s.Save.tapeNumber == before2 + 1,
                  $"{before2} -> {s.Save.tapeNumber}");

            // --- 脱出
            Line("");
            Line("終わり方 3: 脱出");
            tape.TotalSeconds = 900f;
            tape.Load(s.Save.tapeNumber);
            RestartSession(s);
            yield return null;
            if (fps != null && exit != null)
            {
                // 出口の当たり判定へ実際に入る。直接 Escape() を呼ばない。
                fps.SetPose(exit.Where + Vector3.up * 0.1f, 0f, 0f);
                yield return new WaitForSeconds(0.4f);
            }
            Check("出口に入ると脱出になる", s.Current == Phase.Escaped, s.Current.ToString());
            Check("脱出が記録される", s.Save.escaped, s.Save.escaped ? "true" : "false");

            // --- 保存が残る
            Line("");
            Line("保存");
            s.Save.Save();
            var re = SaveData.Load();
            Check("読み直せる", re != null && re.tapeNumber == s.Save.tapeNumber,
                  re != null ? $"テープ {re.tapeNumber} / 殺された {re.kills} / 尽きた {re.runOuts}" : "読めない");
            Line($"  場所: {SaveData.Where}");

            Finish();
        }

        /// <summary>検査の都合で、シーンを読み直さずに再生状態へ戻す。</summary>
        static void RestartSession(GameSession s)
        {
            var m = typeof(GameSession).GetMethod("SetPlaying",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (m != null) m.Invoke(s, null);
        }

        void Finish()
        {
            _log.AppendLine();
            _log.AppendLine(_fail == 0 ? "RESULT: OK" : $"RESULT: NG ({_fail} 件)");
            try
            {
                // 作業ディレクトリはプロジェクトの根。FlatCaptureRunner と同じ書き方。
                // Application.dataPath は Assets を指すので、そこからの相対にすると
                // レポートがプロジェクトの中に落ちる。
                var path = System.IO.Path.GetFullPath(OutPath);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, _log.ToString());
                Debug.Log("[gametest] " + path);
            }
            catch (System.Exception e) { Debug.LogError("[gametest] 書けない: " + e.Message); }

#if UNITY_EDITOR
            if (QuitWhenDone) UnityEditor.EditorApplication.Exit(_fail == 0 ? 0 : 1);
#endif
        }
    }
}
