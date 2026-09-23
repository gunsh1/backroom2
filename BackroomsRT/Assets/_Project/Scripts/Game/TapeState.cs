using System;
using UnityEngine;

namespace Backrooms.Game
{
    /// <summary>
    /// テープの残り。1 回のプレイの長さを決める唯一の時計。
    ///
    /// もとは懐中電灯が 18 分の電池を持っていて、そこに「ここが体験の
    /// 長さを決める」とコメントが付いていた。ただし電池は点けている間
    /// しか減らないので、消しておけば永久に切れない。時計が 2 つあるのも
    /// 事故のもとなので、時間の持ち主をここ 1 つにして、ライトは
    /// その残量を読むだけにする。
    ///
    /// 残量は嘘をつかない。バーに出す値と実際に残っている時間は同じ。
    /// 「あと少しに見えて実は長い」は、短いバーより悪い。
    /// </summary>
    [DisallowMultipleComponent]
    public class TapeState : MonoBehaviour
    {
        [Tooltip("1 本の長さ (秒)。15 分")]
        public float TotalSeconds = 15f * 60f;

        [Tooltip("何本目か。死ぬたびに 1 本進む")]
        public int TapeNumber = 1;

        [Tooltip("1 本目の日付。以後 1 本につき 1 日進む")]
        public string FirstDate = "1996-08-19";

        /// <summary>残り (秒)。</summary>
        public float Remaining { get; private set; }
        /// <summary>経過 (秒)。タイムコードはこちらから作る。</summary>
        public float Elapsed { get { return Mathf.Max(0f, TotalSeconds - Remaining); } }
        /// <summary>1 = 新品、0 = 尽きた。</summary>
        public float Remaining01
        {
            get { return TotalSeconds <= 0f ? 0f : Mathf.Clamp01(Remaining / TotalSeconds); }
        }
        public bool RunOut { get { return Remaining <= 0f; } }

        /// <summary>進んでいるか。ポーズ中と、終わったあとは止める。</summary>
        [NonSerialized] public bool Running = true;

        /// <summary>尽きた瞬間に一度だけ。</summary>
        public event Action OnRunOut;

        bool _fired;

        void Awake() { Remaining = TotalSeconds; }

        void Update()
        {
            PushOsd();
            if (!Running || _fired) return;
            Remaining -= Time.deltaTime;
            if (Remaining <= 0f)
            {
                Remaining = 0f;
                _fired = true;
                Running = false;
                if (OnRunOut != null) OnRunOut();
            }
        }

        /// <summary>ビューファインダへ値を渡す。</summary>
        void PushOsd()
        {
            int h, m, s, f;
            Timecode(out h, out m, out s, out f);
            Osd.Hours = h; Osd.Minutes = m; Osd.Seconds = s;
            Osd.Tape01 = Remaining01;
            // 1Hz、点いている時間のほうが長い。実機の REC 表示と同じ。
            Osd.Rec = Running && (Elapsed - Mathf.Floor(Elapsed)) < 0.6f;
        }

        /// <summary>新しいテープを入れる。</summary>
        public void Load(int tapeNumber)
        {
            TapeNumber = Mathf.Max(1, tapeNumber);
            Remaining = TotalSeconds;
            _fired = false;
            Running = true;
        }

        // ------------------------------------------------------------ 表示

        /// <summary>撮っている時刻。テープの頭からの経過。</summary>
        public void Timecode(out int h, out int m, out int s, out int f)
        {
            // 29.97 ではなくドロップフレーム無しの 30 で数える。
            // 民生機のカウンタは実時間との厳密な一致を保証しない。
            float t = Elapsed;
            h = (int)(t / 3600f);
            m = (int)(t / 60f) % 60;
            s = (int)t % 60;
            f = (int)((t - Mathf.Floor(t)) * 30f) % 30;
        }

        /// <summary>このテープを撮った日。1 本につき 1 日進む。</summary>
        public DateTime Date
        {
            get
            {
                DateTime d;
                if (!DateTime.TryParse(FirstDate, out d)) d = new DateTime(1996, 8, 19);
                return d.AddDays(TapeNumber - 1);
            }
        }
    }
}
