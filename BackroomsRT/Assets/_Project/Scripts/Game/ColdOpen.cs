using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;
using Backrooms.Audio;
using Backrooms.Player;
using Backrooms.Post;

namespace Backrooms.Game
{
    public enum OpenStep
    {
        /// <summary>実写が回っている。</summary>
        Plate,
        /// <summary>抜ける。</summary>
        Slip,
        /// <summary>落ちている。</summary>
        Fall,
        /// <summary>倒れている。</summary>
        Down,
        /// <summary>起き上がる。</summary>
        Rise,
        /// <summary>終わった。</summary>
        Done,
    }

    /// <summary>
    /// 最初の最初。普通の世界を撮っていたテープが、そのまま落ちる。
    ///
    /// 実写を「別の画面」として出さず、シーンと同じテープの信号として
    /// 差し替える (IntroFeed)。帯域制限もドロップアウトもタイムコードも
    /// 同じように乗るので、1 本のテープが続いているように見える。
    /// ここで切り替わったことが分かってしまうと、落ちた先が
    /// 「別の作り物」に見える。
    ///
    /// 落下は CharacterController を通さない。Move() を呼ばなければ
    /// 何にもぶつからないので、床をすり抜ける事故が起きない。
    /// noclip で確かめた通りの仕組みで、当たり判定は生かしたまま。
    ///
    /// 落ちる高さは「目の高さ」で持つ。既定の 3.0m は天井 (3.157m) の
    /// すぐ下で、画に映らない天井裏を落ちる時間が生まれない。もっと
    /// 長く落としたければ上げてよく、そのときは体が天井を抜けて落ちる。
    ///
    /// 流すのは 1 本目だけ。入り方は一度きりで、20 回以上死ぬ設計に
    /// 毎回の前振りを付けると、20 回目には邪魔しかしない。
    /// </summary>
    [DisallowMultipleComponent]
    public class ColdOpen : MonoBehaviour
    {
        [Header("差し込む実写")]
        [Tooltip("StreamingAssets の中のファイル名。無ければ真っ暗で始める")]
        public string ClipFile = "intro.mp4";
        [Tooltip("実写を映す長さ (秒)。素材が短ければそちらに合わせる")]
        public float PlateSeconds = 5f;
        [Tooltip("実写の音量。テープの AGC が均すので控えめでよい")]
        public float PlateVolume = 0.60f;
        [Tooltip("実写にも最初から少しテープの荒れを乗せる (ダビング 1 世代ぶん)")]
        [Range(0f, 1f)] public float PlateHarm = 0.12f;

        [Header("落ちる")]
        [Tooltip("画が壊れてから落ち始めるまで (秒)")]
        public float SlipSeconds = 0.16f;
        [Tooltip("落ちている時間 (秒)")]
        public float FallSeconds = 0.80f;
        [Tooltip("落ち始めの目の高さ (m)。天井は 3.157m")]
        public float FallHeight = 3.00f;
        [Tooltip("倒れたときの目の高さ (m)")]
        public float LandEye = 0.26f;
        [Tooltip("着地の揺り戻しが収まるまで (秒)")]
        public float LandSeconds = 0.30f;

        [Header("倒れている")]
        [Tooltip("横倒しの角度 (度)")]
        public float LieRoll = -74f;
        [Tooltip("倒れたまま見ている向き (度)")]
        public float LiePitch = -6f;
        [Tooltip("この秒数だけ何も押さなければ、押し方を出す")]
        public float PromptAfter = 3.0f;
        [Tooltip("起き上がるのに掛ける時間 (秒)")]
        public float RiseSeconds = 1.00f;
        [Tooltip("押し方の文言。テープの一部ではないので Canvas に出る")]
        public string PromptText = "PRESS ANY KEY";

        [Header("音")]
        public AudioClip Impact;
        public float ImpactVolume = Mix.Impact;
        [Tooltip("着地の瞬間に AGC が絞れる下限。1 = 絞らない。爆音を決めるのはここ")]
        public float ImpactAgcFloor = 1.8f;

        [Header("流す条件")]
        [Tooltip("1 本目のテープでだけ流す")]
        public bool OnlyFirstTape = true;

        /// <summary>
        /// そのテープで流すか。GameSession が実際にこれで決めるので、
        /// 検査はここを突けば production と同じ判断を確かめられる。
        /// </summary>
        public bool WouldPlay(int tapeNumber)
        {
            return !OnlyFirstTape || tapeNumber <= 1;
        }

        public OpenStep Step { get; private set; } = OpenStep.Plate;
        public bool Running { get { return Step != OpenStep.Done; } }
        /// <summary>実写の素材が実際に読めたか。読めなければ真っ暗で始める。</summary>
        public bool HasClip { get; private set; }

        GameSession _session;
        FirstPersonController _fps;
        Transform _head;
        CameraShake _shake;
        Breathing _breath;
        ListenerDsp _dsp;
        Hud _hud;
        VhsPostProcess _vhs;
        VideoPlayer _vp;
        AudioSource _plateAudio, _thud;
        RenderTexture _rt;
        Texture2D _probe;

        float _yaw0, _yawEnd;
        float _harm, _shakeAmt, _panic, _agc;
        float _baseFloor = 0.25f;
        float _bTape, _bTrack, _bDrop, _bGrain, _bHead, _bChroma, _bDesat;
        bool _haveVhs;
        bool _stand, _skip;

        // 落ち終わった姿勢。着地の揺り戻しはここから続ける。
        float _fallEye, _fallRoll, _fallPitch;

        void Start()
        {
            _session = GameSession.Instance;
            if (_session == null || _session.Current != Phase.Intro)
            {
                Step = OpenStep.Done;
                enabled = false;
                return;
            }

            _fps = FindFirstObjectByType<FirstPersonController>();
            if (_fps == null) { Finish(); return; }
            _head = _fps.Head;
            _yaw0 = _fps.transform.eulerAngles.y;
            _yawEnd = _yaw0;

            _breath = FindFirstObjectByType<Breathing>();
            _dsp = FindFirstObjectByType<ListenerDsp>();
            if (_dsp != null) _baseFloor = _dsp.AgcMinGain;
            _hud = FindFirstObjectByType<Hud>();

            var cam = Camera.main;
            if (cam != null)
            {
                _shake = cam.GetComponent<CameraShake>();
                if (_shake == null) _shake = cam.gameObject.AddComponent<CameraShake>();
            }

            var vol = FindFirstObjectByType<Volume>();
            if (vol != null && vol.profile != null && vol.profile.TryGet(out _vhs))
            {
                _bTape = _vhs.tape.value; _bTrack = _vhs.tracking.value;
                _bDrop = _vhs.dropout.value; _bGrain = _vhs.grain.value;
                _bHead = _vhs.headSwitch.value; _bChroma = _vhs.chromaBand.value;
                _bDesat = _vhs.desaturate.value;
                _haveVhs = true;
            }

            var go = new GameObject("Impact");
            go.transform.SetParent(transform, false);
            _thud = go.AddComponent<AudioSource>();
            _thud.playOnAwake = false;
            _thud.spatialBlend = 0f;     // 自分の体の音。どこから、ではない
            _thud.dopplerLevel = 0f;

            StartCoroutine(Run());
        }

        // PanicEvent が Update で同じ値 (揺れ・息・AGC・テープの荒れ) を書く。
        // 取り合いにならないよう、こちらは必ず後に書く。
        void LateUpdate() { Apply(); }

        // ------------------------------------------------------------ 本体

        IEnumerator Run()
        {
            // ---- 実写 ------------------------------------------------
            Step = OpenStep.Plate;
            yield return OpenClip();

            float show = PlateSeconds;
            if (HasClip && _vp.length > 0.05) show = Mathf.Min(show, (float)_vp.length - 0.05f);
            if (!HasClip) show = Mathf.Min(show, 1.6f);    // 真っ暗を長く見せても仕方がない

            IntroFeed.Amount = 1f;
            _harm = PlateHarm;
            _shakeAmt = 0f;
            PoseEye(_fps.StandHeight + _fps.EyeOffset, 0f, 0f);

            float t = 0f;
            while (t < show && !_skip) { t += Time.deltaTime; yield return null; }

            // ---- 抜ける ----------------------------------------------
            // 画が壊れるのと、実写が消えるのを同時に起こす。先に画が
            // 壊れると「何かの演出が始まった」に見えてしまう。
            Step = OpenStep.Slip;
            t = 0f;
            while (t < SlipSeconds && !_skip)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / Mathf.Max(0.01f, SlipSeconds));
                _harm = Mathf.Lerp(PlateHarm, 1f, u);
                IntroFeed.Amount = 1f - u;
                _shakeAmt = u * 0.7f;
                yield return null;
            }
            IntroFeed.Amount = 0f;
            StopClip();

            // ---- 落ちる ----------------------------------------------
            Step = OpenStep.Fall;
            float stand = _fps.StandHeight + _fps.EyeOffset;
            t = 0f;
            while (t < FallSeconds && !_skip)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / Mathf.Max(0.01f, FallSeconds));
                // 等加速度。落ち始めは静かで、床に着く直前がいちばん速い。
                _fallEye = Mathf.Lerp(FallHeight, LandEye, u * u);
                _fallRoll = LieRoll * 1.18f * (u * u * u);
                _fallPitch = Mathf.Lerp(0f, -46f, Mathf.SmoothStep(0f, 1f, u));
                _yawEnd = _yaw0 + u * u * 26f;
                _fps.transform.rotation = Quaternion.Euler(0f, _yawEnd, 0f);
                PoseEye(_fallEye, _fallRoll, _fallPitch);

                _harm = Mathf.Lerp(1f, 0.35f, u);
                _shakeAmt = 0.55f;
                yield return null;
            }

            // ---- 着く ------------------------------------------------
            Land();

            Step = OpenStep.Down;
            t = 0f;
            while (t < LandSeconds && !_skip)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / Mathf.Max(0.01f, LandSeconds));
                float s = 1f - Mathf.Exp(-u * 6f);
                float w = Mathf.Exp(-u * 5f) * Mathf.Sin(u * 26f) * (1f - u);
                PoseEye(Mathf.Lerp(_fallEye, LandEye, s) + w * 0.05f,
                        Mathf.Lerp(_fallRoll, LieRoll, s) + w * 6f,
                        Mathf.Lerp(_fallPitch, LiePitch, s) + w * 5f);
                _harm = Mathf.Lerp(1f, 0.25f, u);
                _shakeAmt = Mathf.Lerp(1f, 0.16f, u);
                _agc = 1f - u;
                yield return null;
            }

            // ---- 倒れたまま ------------------------------------------
            // 自動では立たない。最初の入力を自分の意思にする。
            _stand = false;
            t = 0f;
            bool prompted = false;
            while (!_stand && !_skip)
            {
                t += Time.deltaTime;
                if (Input.anyKeyDown) _stand = true;
                if (!prompted && t >= PromptAfter)
                {
                    prompted = true;
                    if (_hud != null) _hud.Note = PromptText;
                }
                // 息は上がったまま。呼吸だけで「まだ動けない」を伝える。
                _panic = Mathf.Lerp(0.85f, 0.45f, Mathf.Clamp01(t / 8f));
                _harm = Mathf.Lerp(0.25f, 0.05f, Mathf.Clamp01(t / 3f));
                _shakeAmt = 0.16f;
                _agc = 0f;
                // 手の中でカメラがわずかに動く。完全な静止は「止まった画」に見える。
                float d = Mathf.Sin(t * 1.7f) * 0.6f + Mathf.Sin(t * 0.9f) * 0.4f;
                PoseEye(LandEye, LieRoll + d * 1.1f, LiePitch + d * 0.7f);
                yield return null;
            }
            if (_hud != null) _hud.Note = null;

            // ---- 起き上がる ------------------------------------------
            Step = OpenStep.Rise;
            t = 0f;
            while (t < RiseSeconds && !_skip)
            {
                t += Time.deltaTime;
                float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / Mathf.Max(0.01f, RiseSeconds)));
                PoseEye(Mathf.Lerp(LandEye, stand, u),
                        Mathf.Lerp(LieRoll, 0f, u),
                        Mathf.Lerp(LiePitch, 0f, u));
                _panic = Mathf.Lerp(0.45f, 0.2f, u);
                _shakeAmt = Mathf.Lerp(0.16f, 0.04f, u);
                yield return null;
            }

            Finish();
        }

        /// <summary>着地。音と揺れと AGC を同時に開ける。</summary>
        void Land()
        {
            _fallEye = LandEye;
            if (_thud != null && Impact != null)
            {
                _thud.clip = Impact;
                _thud.volume = ImpactVolume;
                _thud.Play();
            }
            // AGC の下限を開けないと、0.12 秒で叩き落とされて衝撃にならない。
            _agc = 1f;
            _panic = 1f;
            _harm = 1f;
            _shakeAmt = 1f;
        }

        /// <summary>
        /// 目の高さで姿勢を書く。目が立った高さより上なら体ごと浮かせる。
        /// FallHeight を上げると、体が本当に天井を抜けて落ちてくる。
        /// </summary>
        void PoseEye(float eye, float roll, float pitch)
        {
            if (_fps == null || _head == null) return;
            float stand = _fps.StandHeight + _fps.EyeOffset;
            float feet = Mathf.Max(0f, eye - stand);
            var p = _fps.transform.position;
            _fps.transform.position = new Vector3(p.x, feet, p.z);
            _head.localPosition = new Vector3(0f, eye - feet, 0f);
            _head.localRotation = Quaternion.Euler(pitch, 0f, roll);
        }

        void Finish()
        {
            Step = OpenStep.Done;
            IntroFeed.Clear();
            StopClip();
            if (_hud != null) _hud.Note = null;

            // 触ったものを全部戻す。持ち逃げすると、以後ずっとテープが荒れたまま。
            _harm = 0f; _shakeAmt = 0f; _panic = 0f; _agc = 0f;
            Apply();
            if (_dsp != null) _dsp.AgcMinGain = _baseFloor;

            if (_fps != null)
            {
                var p = _fps.transform.position;
                _fps.SetPose(new Vector3(p.x, 0f, p.z), _yawEnd, 0f);
            }
            if (_session != null) _session.EndIntro();
            enabled = false;
        }

        void Apply()
        {
            if (_shake != null) _shake.Amount = _shakeAmt;
            if (_breath != null) _breath.Panic = _panic;
            if (_dsp != null && _agc > 0f)
                _dsp.AgcMinGain = Mathf.Lerp(_baseFloor, ImpactAgcFloor, _agc);

            if (!_haveVhs || _vhs == null) return;
            float p = _harm;
            _vhs.tape.value = _bTape + p * 2.6f;
            _vhs.tracking.value = _bTrack + p * 2.6f;
            _vhs.dropout.value = _bDrop + p * 2.2f;
            _vhs.grain.value = _bGrain + p * 1.0f;
            _vhs.headSwitch.value = _bHead + p * 1.3f;
            // 荒れると真っ先に死ぬのは色。PanicEvent と同じ書き方。
            _vhs.chromaBand.value = Mathf.Lerp(_bChroma, 10f, p);
            _vhs.desaturate.value = Mathf.Lerp(_bDesat, 0.70f, p);
        }

        // ------------------------------------------------------------ 実写

        IEnumerator OpenClip()
        {
            HasClip = false;
            var path = Path.Combine(Application.streamingAssetsPath, ClipFile);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[coldopen] 実写が無いので真っ暗で始める: {path}");
                IntroFeed.Tex = Texture2D.blackTexture;
                IntroFeed.Fit = new Vector4(1f, 1f, 0f, 0f);
                yield break;
            }

            _plateAudio = gameObject.AddComponent<AudioSource>();
            _plateAudio.playOnAwake = false;
            _plateAudio.spatialBlend = 0f;
            _plateAudio.volume = PlateVolume;

            _vp = gameObject.AddComponent<VideoPlayer>();
            _vp.source = VideoSource.Url;
            _vp.url = path;
            _vp.playOnAwake = false;
            _vp.isLooping = false;
            _vp.skipOnDrop = true;
            _vp.renderMode = VideoRenderMode.RenderTexture;
            _vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _vp.controlledAudioTrackCount = 1;
            _vp.EnableAudioTrack(0, true);
            _vp.SetTargetAudioSource(0, _plateAudio);
            _vp.Prepare();

            float w = 0f;
            while (!_vp.isPrepared && w < 6f) { w += Time.deltaTime; yield return null; }
            if (!_vp.isPrepared)
            {
                Debug.LogWarning("[coldopen] 実写を用意できなかった。真っ暗で始める");
                IntroFeed.Tex = Texture2D.blackTexture;
                yield break;
            }

            int vw = (int)_vp.width, vh = (int)_vp.height;
            // sRGB で作る。素材は sRGB で符号化されていて、この合成点の
            // 色はリニア。ここを間違えると実写だけ白茶ける。
            _rt = new RenderTexture(Mathf.Max(16, vw), Mathf.Max(16, vh), 0,
                                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { name = "IntroPlate" };
            _rt.Create();
            _vp.targetTexture = _rt;
            IntroFeed.Tex = _rt;
            IntroFeed.Fit = IntroFeed.FitFor(vw, vh);
            _vp.Play();
            HasClip = true;
            Debug.Log($"[coldopen] 実写 {vw}x{vh} / {_vp.length:F2} 秒 / {ClipFile}");
        }

        void StopClip()
        {
            if (_vp != null && _vp.isPlaying) _vp.Stop();
            if (_plateAudio != null) _plateAudio.Stop();
        }

        void OnDestroy()
        {
            IntroFeed.Clear();
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_probe != null) Destroy(_probe);
        }

        // ------------------------------------------------------------ 外から

        /// <summary>倒れているのをやめて立ち上がる。キーを押したときと同じ。</summary>
        public void Stand() { _stand = true; }

        /// <summary>全部飛ばして操作を渡す。Esc がこれを呼ぶ。</summary>
        public void Skip()
        {
            if (Step == OpenStep.Done) return;
            _skip = true;
            StopAllCoroutines();
            Finish();
        }

        /// <summary>
        /// いま実写として出ている絵の明るさ。自己検査用。
        /// 配線だけ通って真っ黒、を見逃さないために要る。
        /// </summary>
        public float PlateLuma()
        {
            var tex = IntroFeed.Tex as RenderTexture;
            if (tex == null) return 0f;
            if (_probe == null) _probe = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var small = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32,
                                                   RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, small);
            var prev = RenderTexture.active;
            RenderTexture.active = small;
            _probe.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
            _probe.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(small);

            float sum = 0f;
            var px = _probe.GetPixels();
            for (int i = 0; i < px.Length; i++)
                sum += px[i].r * 0.299f + px[i].g * 0.587f + px[i].b * 0.114f;
            return sum / px.Length;
        }
    }
}
