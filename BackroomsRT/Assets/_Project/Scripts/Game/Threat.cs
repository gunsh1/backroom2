using System.Collections.Generic;
using UnityEngine;
using Backrooms.Audio;
using Backrooms.Generation;
using Backrooms.Player;

namespace Backrooms.Game
{
    public enum ThreatMode { Roam, Alert, Hunt, Lose }

    /// <summary>
    /// 徘徊するもの。姿はまだ無い。
    ///
    /// 速さがこの設計の全て。追跡中の 1.85 m/s は歩き (1.40) より速く
    /// 走り (3.60) より遅い。歩いて逃げることはできず、走れば逃げられる。
    /// 息が上がると走りは 2.2 m/s まで落ちるが、それでもまだ上なので、
    /// 「疲れていても走り続ければ逃げ切れる」が保たれる。ここを下回らせると
    /// 「走って逃げ切る」という約束そのものが壊れる。
    ///
    /// 「音が消えたら安全」はハードフェードにする。環境音へ溶かすと
    /// 「静か」と「居ない」の区別が付かず、やはり約束が壊れる。
    ///
    /// 徘徊先はプレイヤーの方へ寄せる。172.8m 四方に 1 体では、
    /// 純粋なランダムウォークだとまず出会わない。
    /// </summary>
    [DisallowMultipleComponent]
    public class Threat : MonoBehaviour
    {
        [Header("速さ")]
        [Tooltip("徘徊中")]
        public float RoamSpeed = 1.05f;
        [Tooltip("追跡中。歩き 1.40 より速く、走り 3.60 より遅いこと")]
        public float HuntSpeed = 1.85f;

        [Header("距離")]
        [Tooltip("これより近いと気付く")]
        public float NoticeRange = 28f;
        [Tooltip("これより遠い状態が続くと見失う")]
        public float LoseRange = 45f;
        [Tooltip("見失うまでに要る秒数")]
        public float LoseSeconds = 6f;
        [Tooltip("気付いてから追い始めるまで")]
        public float AlertSeconds = 3f;
        [Tooltip("これ以上追い続けない。勝てない追跡を作らない")]
        public float MaxHuntSeconds = 90f;
        [Tooltip("この距離で捕まる")]
        public float KillRange = 1.6f;

        [Header("置き方")]
        [Tooltip("開始時にプレイヤーからこれ以上離す")]
        public float StartAway = 80f;
        [Tooltip("徘徊先の距離")]
        public Vector2 RoamPick = new Vector2(40f, 120f);
        [Tooltip("徘徊先をプレイヤー側へ寄せる割合")]
        [Range(0f, 1f)] public float RoamBias = 0.60f;

        [Header("音")]
        public AudioClip Voice;
        [Tooltip("素材の再生速度。低くして「部屋の音が落ちたもの」にする")]
        public float VoicePitch = 0.25f;
        public float RoamVolume = 0.02f;
        public float HuntVolume = 0.38f;
        [Tooltip("見失ってから無音になるまで。溶かさず落とす")]
        public float SilenceSeconds = 4f;

        public ThreatMode Mode { get; private set; } = ThreatMode.Roam;
        public float DistanceToPlayer { get; private set; }

        int _seed = 1996;
        Transform _player;
        FirstPersonController _fps;
        Flashlight _torch;
        Shout _shout;
        AudioSource _src;
        ReverbSend _send;

        readonly List<Vector3> _path = new List<Vector3>();
        int _leg;
        float _modeT, _farT, _huntT, _replan;
        int _shoutSeen = -1;
        float _vol;

        void Start()
        {
            var world = FindFirstObjectByType<ChunkedLevel0>();
            if (world != null) _seed = world.Seed;
            _fps = FindFirstObjectByType<FirstPersonController>();
            _player = _fps != null ? _fps.transform : null;
            _torch = FindFirstObjectByType<Flashlight>();
            _shout = FindFirstObjectByType<Shout>();

            var listener = FindFirstObjectByType<ListenerDsp>();
            _src = gameObject.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.loop = true;
            _src.spatialBlend = 1f;
            _src.rolloffMode = AudioRolloffMode.Logarithmic;
            _src.minDistance = 6f;
            _src.maxDistance = 90f;
            _src.dopplerLevel = 0f;
            _src.pitch = VoicePitch;
            _src.volume = 0f;
            if (Voice != null) { _src.clip = Voice; _src.Play(); }

            // 壁の向こうからも残響で届く。DistantVoice と同じ経路。
            // 「それが建物の中に居る」ことは、直接音ではなく響きが伝える。
            var w = new GameObject("Threat_Send");
            w.transform.SetParent(transform, false);
            var wet = w.AddComponent<AudioSource>();
            wet.playOnAwake = false;
            wet.loop = true;
            wet.spatialBlend = 0f;
            wet.dopplerLevel = 0f;
            wet.pitch = VoicePitch;
            wet.volume = 1f;
            if (Voice != null) { wet.clip = Voice; wet.Play(); }
            _send = w.AddComponent<ReverbSend>();
            _send.Target = listener;
            _send.Gain = 0f;

            Place();
        }

        /// <summary>テープごとに違う場所から始める。世界は同じ、テープは違う。</summary>
        public void Place()
        {
            if (_player == null) return;
            int tape = GameSession.Instance != null ? GameSession.Instance.Tape.TapeNumber : 1;
            var p = _player.position;
            for (int k = 0; k < 40; k++)
            {
                uint h = Level0Layout.Hash(tape * 131 + k, _seed, 9173);
                float a = (h & 0xFFFF) / 65535f * Mathf.PI * 2f;
                float d = StartAway + ((h >> 16) & 0xFFFF) / 65535f * 60f;
                var c = new Vector3(p.x + Mathf.Cos(a) * d, 0f, p.z + Mathf.Sin(a) * d);
                if (!Level0Layout.Clear(c.x, c.z, _seed, 0.70f)) continue;
                transform.position = c;
                Repath(PickRoam());
                return;
            }
            transform.position = Level0Layout.FindOpen(p + Vector3.right * StartAway, _seed);
        }

        void Update()
        {
            if (_player == null) return;
            var session = GameSession.Instance;
            if (session != null && session.Current != Phase.Playing) { Quieten(); return; }

            float dt = Time.deltaTime;
            var me = transform.position;
            var you = _player.position;
            DistanceToPlayer = Vector3.Distance(new Vector3(me.x, 0f, me.z),
                                                new Vector3(you.x, 0f, you.z));

            Think(dt);
            Move(dt, Mode == ThreatMode.Hunt ? HuntSpeed : RoamSpeed);
            Sound(dt);

            if (Mode == ThreatMode.Hunt && DistanceToPlayer <= KillRange && session != null)
                session.Kill();
        }

        void Think(float dt)
        {
            _modeT += dt;
            bool noisy = _fps != null && _fps.IsSprinting;
            if (_torch != null && _torch.On) noisy = true;
            if (_shout != null)
            {
                if (_shoutSeen < 0) _shoutSeen = _shout.Played;
                else if (_shout.Played > _shoutSeen) { _shoutSeen = _shout.Played; noisy = true; }
            }

            switch (Mode)
            {
                case ThreatMode.Roam:
                    if (DistanceToPlayer < NoticeRange && (noisy || CanSee())) Switch(ThreatMode.Alert);
                    else if (_path.Count == 0 || _leg >= _path.Count) Repath(PickRoam());
                    break;

                case ThreatMode.Alert:
                    if (DistanceToPlayer > NoticeRange * 1.4f) Switch(ThreatMode.Roam);
                    else if (_modeT >= AlertSeconds) { Switch(ThreatMode.Hunt); _huntT = 0f; }
                    break;

                case ThreatMode.Hunt:
                    _huntT += dt;
                    _farT = DistanceToPlayer > LoseRange ? _farT + dt : 0f;
                    if (_farT >= LoseSeconds || _huntT >= MaxHuntSeconds) Switch(ThreatMode.Lose);
                    else
                    {
                        _replan -= dt;
                        if (_replan <= 0f) { _replan = 1.0f; Repath(_player.position); }
                    }
                    break;

                case ThreatMode.Lose:
                    if (_modeT >= SilenceSeconds) Switch(ThreatMode.Roam);
                    break;
            }
        }

        void Switch(ThreatMode m)
        {
            Mode = m; _modeT = 0f; _farT = 0f;
            if (m == ThreatMode.Roam || m == ThreatMode.Lose) Repath(PickRoam());
        }

        Vector3 PickRoam()
        {
            var me = transform.position;
            var you = _player != null ? _player.position : me;
            for (int k = 0; k < 24; k++)
            {
                // 6 割はプレイヤーの側へ。寄せないと出会わない。
                bool toward = Random.value < RoamBias;
                float a = toward
                    ? Mathf.Atan2(you.z - me.z, you.x - me.x) + Random.Range(-0.9f, 0.9f)
                    : Random.value * Mathf.PI * 2f;
                float d = Random.Range(RoamPick.x, RoamPick.y);
                float x = me.x + Mathf.Cos(a) * d, z = me.z + Mathf.Sin(a) * d;
                if (Level0Layout.Clear(x, z, _seed, 0.70f)) return new Vector3(x, 0f, z);
            }
            return me;
        }

        void Repath(Vector3 to)
        {
            _path.Clear(); _leg = 0;
            var from = transform.position;
            // Path は節点を使い切ると黙って空を返す。空なら直線で寄る。
            // M-A の探知半径は 28m なので、経路が長くなる場面は少ない。
            var p = Level0Layout.Path(from, to, _seed, 0.55f);
            if (p != null && p.Count > 0) _path.AddRange(p);
            else _path.Add(to);
        }

        void Move(float dt, float speed)
        {
            if (_path.Count == 0) return;
            if (_leg >= _path.Count) { _leg = _path.Count - 1; }
            var me = transform.position;
            var target = _path[_leg];
            target.y = me.y;
            var d = target - me;
            float len = d.magnitude;
            if (len < 0.35f) { _leg++; return; }
            transform.position = me + d / len * speed * dt;
        }

        void Sound(float dt)
        {
            float want;
            switch (Mode)
            {
                case ThreatMode.Hunt: want = HuntVolume; break;
                case ThreatMode.Alert: want = Mathf.Lerp(RoamVolume, HuntVolume, 0.45f); break;
                case ThreatMode.Lose: want = 0f; break;
                default: want = RoamVolume; break;
            }
            // 上がるのは速く、落ちるのはハードに。溶かさない。
            float rate = want > _vol ? 3.0f : 1f / Mathf.Max(0.1f, SilenceSeconds);
            _vol = Mathf.MoveTowards(_vol, want, rate * dt);
            if (_src != null) _src.volume = _vol;
            if (_send != null) _send.Gain = _vol * 0.8f;
        }

        void Quieten()
        {
            _vol = Mathf.MoveTowards(_vol, 0f, Time.unscaledDeltaTime * 2f);
            if (_src != null) _src.volume = _vol;
            if (_send != null) _send.Gain = _vol * 0.8f;
        }

        /// <summary>間に壁が無いか。DistantVoice と同じ見方。</summary>
        bool CanSee()
        {
            var a = transform.position;
            var b = _player.position;
            float len = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
            int steps = Mathf.CeilToInt(len / 0.6f);
            for (int i = 1; i < steps; i++)
            {
                float t = i / (float)steps;
                if (Level0Layout.Solid(Mathf.Lerp(b.x, a.x, t), Mathf.Lerp(b.z, a.z, t), _seed))
                    return false;
            }
            return true;
        }
    }
}
