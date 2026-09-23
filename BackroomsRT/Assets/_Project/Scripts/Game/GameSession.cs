using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Backrooms.Player;

namespace Backrooms.Game
{
    public enum Phase
    {
        /// <summary>撮っている。</summary>
        Playing,
        /// <summary>止めている。</summary>
        Paused,
        /// <summary>捕まった。</summary>
        Dying,
        /// <summary>テープが尽きた。</summary>
        RunOut,
        /// <summary>出た。</summary>
        Escaped,
        /// <summary>次のテープへ。</summary>
        NewTape,
    }

    /// <summary>
    /// 1 回のプレイの持ち主。
    ///
    /// このプロジェクトには今まで「始まり」も「終わり」も無かった。
    /// 目的も失敗も、やり直しも、止める手段も無い。ここがその全部を持つ。
    ///
    /// やり直しはシーンの読み直し 1 行で済ませる。状態を手で戻す経路を
    /// 作ると、戻し忘れが必ずどこかに残る。しかも読み込みに掛かる
    /// 2-4 秒（ChunkedLevel0 が 81 チャンクを同期で建てるぶん）が、
    /// ちょうど「新しいテープ」の札の裏に欲しい間になる。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class GameSession : MonoBehaviour
    {
        public static GameSession Instance { get; private set; }

        [Tooltip("捕まってから暗転までの猶予 (秒)")]
        public float DyingSeconds = 1.2f;
        [Tooltip("テープが尽きてから暗転までの崩れ (秒)")]
        public float RunOutSeconds = 3.0f;
        [Tooltip("「新しいテープ」の札を出す時間 (秒)")]
        public float CardSeconds = 3.5f;

        /// <summary>札のあとに実際にシーンを読み直すか。自動検査では切る。</summary>
        [System.NonSerialized] public bool AutoRestart = true;

        public Phase Current { get; private set; } = Phase.Playing;
        public SaveData Save { get; private set; }
        public TapeState Tape { get; private set; }

        /// <summary>札に出す文字。UI が読む。</summary>
        public string CardTitle { get; private set; } = "";
        public string CardBody { get; private set; } = "";

        FirstPersonController _fps;
        bool _quitting;

        void Awake()
        {
            Instance = this;
            Save = SaveData.Load();
            Tape = GetComponent<TapeState>();
            if (Tape == null) Tape = gameObject.AddComponent<TapeState>();
            Tape.Load(Save.tapeNumber);
            Tape.OnRunOut += OnRunOut;
        }

        void Start()
        {
            _fps = FindFirstObjectByType<FirstPersonController>();
            SetPlaying();
            Debug.Log($"[session] テープ {Tape.TapeNumber} 本目 / {Tape.Date:yyyy-MM-dd} / "
                      + $"殺された {Save.kills} 回 / 保存 {SaveData.Where}");
        }

        void Update()
        {
            if (!Application.isFocused) return;
            // ポーズは Esc で開き、Esc で閉じる。開いた手段で必ず閉じられること。
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (Current == Phase.Playing) SetPaused(true);
                else if (Current == Phase.Paused) SetPaused(false);
            }
        }

        // ------------------------------------------------------------ 状態

        void SetPlaying()
        {
            Current = Phase.Playing;
            Time.timeScale = 1f;
            Tape.Running = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (_fps != null) _fps.enabled = true;
        }

        public void SetPaused(bool on)
        {
            if (on && Current != Phase.Playing) return;
            if (!on && Current != Phase.Paused) return;

            Current = on ? Phase.Paused : Phase.Playing;
            Time.timeScale = on ? 0f : 1f;
            Tape.Running = !on;
            // カーソルを外したものが、必ず戻す。クリックに戻させない。
            Cursor.lockState = on ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = on;
            if (on) Save.Save();
        }

        /// <summary>捕まった。</summary>
        public void Kill()
        {
            if (Current != Phase.Playing && Current != Phase.Paused) return;
            Save.kills++;
            End(Phase.Dying, DyingSeconds, "TAPE END",
                $"テープ {Tape.TapeNumber} 本目はここで終わっている");
        }

        void OnRunOut()
        {
            if (Current != Phase.Playing && Current != Phase.Paused) return;
            Save.runOuts++;
            End(Phase.RunOut, RunOutSeconds, "TAPE END", "テープが尽きた");
        }

        /// <summary>出口に着いた。</summary>
        public void Escape()
        {
            if (Current != Phase.Playing && Current != Phase.Paused) return;
            Save.escaped = true;
            Save.totalSeconds += Tape.Elapsed;
            Save.Save();
            Current = Phase.Escaped;
            Time.timeScale = 1f;
            Tape.Running = false;
            Freeze();
            CardTitle = "OUT";
            CardBody = $"テープ {Tape.TapeNumber} 本 / 殺された {Save.kills} 回";
            Debug.Log($"[session] 脱出 / テープ {Tape.TapeNumber} / 殺された {Save.kills} 回");
        }

        void End(Phase phase, float hold, string title, string body)
        {
            Current = phase;
            Time.timeScale = 1f;
            Tape.Running = false;
            Save.totalSeconds += Tape.Elapsed;
            Freeze();
            CardTitle = title;
            CardBody = body;
            StartCoroutine(ToNewTape(hold));
        }

        void Freeze()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (_fps != null) _fps.enabled = false;
        }

        IEnumerator ToNewTape(float hold)
        {
            yield return new WaitForSecondsRealtime(hold);
            Current = Phase.NewTape;
            Save.tapeNumber++;
            Save.Save();
            CardTitle = $"TAPE {Save.tapeNumber:00}";
            CardBody = Tape.Date.AddDays(1)
                .ToString("MMM dd yyyy", System.Globalization.CultureInfo.InvariantCulture)
                .ToUpperInvariant();
            yield return new WaitForSecondsRealtime(CardSeconds);
            if (AutoRestart) Restart();
        }

        /// <summary>やり直す。シーンを読み直すので状態が漏れない。</summary>
        public void Restart()
        {
            Save.Save();
            Time.timeScale = 1f;
            var s = SceneManager.GetActiveScene();
            SceneManager.LoadScene(s.buildIndex >= 0 ? s.buildIndex : 0);
        }

        public void Quit()
        {
            Save.Save();
            Time.timeScale = 1f;
            _quitting = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void OnApplicationQuit()
        {
            if (_quitting || Save == null) return;
            Save.totalSeconds += Tape != null ? Tape.Elapsed : 0f;
            Save.Save();
        }
    }
}
