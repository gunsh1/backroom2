using UnityEngine;
using UnityEngine.Rendering;
using Backrooms.Audio;
using Backrooms.Post;

namespace Backrooms.Player
{
    /// <summary>
    /// 呼びかけたときに、たまに「返事」がある。
    ///
    /// 誰も居ないことが恐怖である、という設計なので、返ってくるのは
    /// 人ではなく音だけ。ただし一度でも返ってくると、そのあとの静けさの
    /// 意味が変わる。だから確率は低く、しかも起きるたびに半分にする。
    /// 二度目が同じ確率で来ると「そういう仕掛け」になってしまう。
    ///
    /// 起きている間だけの短い演出ではなく、収まったあとに 1 分ほど
    /// 動悸を残す。息と手の震えが戻りきらないことのほうが、鳴っている
    /// 15 秒よりも長く効く。
    ///
    /// 触るもの: 音 (Sting / 女声 / 息)、カメラの揺れ、VHS の荒れ、走る速さ。
    /// どれも「基準値 + 強度 x 追加分」で書くので、強度が 0 に戻れば
    /// 必ず元の値に戻る。イベントが値を持ち逃げしない。
    /// </summary>
    [DisallowMultipleComponent]
    public class PanicEvent : MonoBehaviour
    {
        [Header("確率")]
        [Tooltip("最初に呼びかけたときに起きる確率")]
        public float FirstChance = 0.05f;
        [Tooltip("一度起きるたびに確率へ掛ける係数。0.5 で毎回半減")]
        public float ChanceDecay = 0.5f;
        /// <summary>呼びかけに反応して自分で抽選するか。測定中は切る。</summary>
        [System.NonSerialized] public bool AutoRoll = true;

        [Header("長さ")]
        [Tooltip("鳴り始めてから収まるまで (秒)")]
        public float Settle = 15f;
        [Tooltip("収まったあと動悸が残る時間 (秒)")]
        public float AfterSeconds = 60f;

        [Header("素材")]
        public AudioClip Sting;
        [Tooltip("Sting の音量。爆音で入れる")]
        public float StingVolume = Mix.Sting;
        [Tooltip("この秒数だけ遅らせて女声を出す。同時だと Sting に埋もれる")]
        public float VoiceDelay = 1.6f;
        [Tooltip("イベント時の女声の倍率")]
        public float VoiceGain = 4f;

        [Header("効き方")]
        [Tooltip("最大時のカメラの揺れ")]
        public float ShakePanic = 1.0f;
        [Tooltip("動悸が残っている間のカメラの揺れ")]
        public float ShakeAfter = 0.22f;
        [Tooltip("最大時に走る速さへ掛ける増分")]
        public float SprintBoost = 0.45f;

        public int Fired { get; private set; }
        public float Chance { get; private set; }
        /// <summary>0 = 平常、1 = 起きた瞬間。</summary>
        public float Panic { get; private set; }
        /// <summary>0 = 平常、1 = 動悸が残っている。</summary>
        public float After { get; private set; }

        Shout _shout;
        Breathing _breath;
        CameraShake _shake;
        DistantVoice _voice;
        FirstPersonController _fps;
        AudioSource _sting;
        VhsPostProcess _vhs;

        float _baseSprint;
        float _bTape, _bTrack, _bDrop, _bGrain, _bScan, _bHead, _bChroma, _bDesat;
        bool _haveVhs;

        int _seenShouts = -1;
        bool _active;
        float _t, _voiceAt;
        bool _voiceDone;

        void Start()
        {
            Chance = FirstChance;
            _shout = FindFirstObjectByType<Shout>();
            _breath = FindFirstObjectByType<Breathing>();
            _voice = FindFirstObjectByType<DistantVoice>();
            _fps = GetComponentInParent<FirstPersonController>();
            if (_fps == null) _fps = FindFirstObjectByType<FirstPersonController>();
            if (_fps != null) _baseSprint = _fps.SprintSpeed;

            var cam = Camera.main;
            if (cam != null)
            {
                _shake = cam.GetComponent<CameraShake>();
                if (_shake == null) _shake = cam.gameObject.AddComponent<CameraShake>();
            }

            var go = new GameObject("Sting");
            go.transform.SetParent(transform, false);
            _sting = go.AddComponent<AudioSource>();
            _sting.playOnAwake = false;
            _sting.spatialBlend = 0f;      // どこから、ではない。全部から来る
            _sting.dopplerLevel = 0f;

            // VHS の基準値を控えておく。イベントはここからの差分だけを足す。
            var vol = FindFirstObjectByType<Volume>();
            if (vol != null && vol.profile != null && vol.profile.TryGet(out _vhs))
            {
                _bTape = _vhs.tape.value; _bTrack = _vhs.tracking.value;
                _bDrop = _vhs.dropout.value; _bGrain = _vhs.grain.value;
                _bScan = _vhs.scanline.value; _bHead = _vhs.headSwitch.value;
                _bChroma = _vhs.chromaBand.value; _bDesat = _vhs.desaturate.value;
                _haveVhs = true;
            }
        }

        void Update()
        {
            // 呼びかけを見張る。Shout に手を入れずに済む。
            if (_shout != null)
            {
                if (_seenShouts < 0) _seenShouts = _shout.Played;
                else if (_shout.Played > _seenShouts)
                {
                    _seenShouts = _shout.Played;
                    if (AutoRoll && !_active && Random.value < Chance) Fire();
                }
            }

            if (_active)
            {
                _t += Time.deltaTime;
                Panic = Mathf.Clamp01(1f - _t / Mathf.Max(0.1f, Settle));
                After = _t <= Settle ? 1f
                      : Mathf.Clamp01(1f - (_t - Settle) / Mathf.Max(0.1f, AfterSeconds));
                if (!_voiceDone && _t >= _voiceAt)
                {
                    _voiceDone = true;
                    if (_voice != null) _voice.Fire(VoiceGain);
                }
                if (Panic <= 0f && After <= 0f) _active = false;
            }
            else { Panic = 0f; After = 0f; }

            Apply();
        }

        /// <summary>今すぐ起こす。確率は消費する。キャプチャからも叩ける。</summary>
        public bool Fire()
        {
            _active = true;
            _t = 0f;
            _voiceDone = false;
            _voiceAt = VoiceDelay;
            Fired++;
            Chance *= ChanceDecay;

            if (_sting != null && Sting != null)
            {
                _sting.clip = Sting;
                _sting.volume = StingVolume;
                _sting.Play();
            }
            return true;
        }

        /// <summary>
        /// 強度を各所へ配る。全て「基準 + 強度 x 追加分」で書くので、
        /// 強度が 0 に戻れば必ず元の値に戻る。
        /// </summary>
        void Apply()
        {
            float p = Panic;
            // 動悸ぶんは異常時と重ねない。異常時のほうが常に上。
            float afterOnly = Mathf.Max(0f, After - p);

            if (_shake != null) _shake.Amount = p * ShakePanic + afterOnly * ShakeAfter;
            if (_breath != null) { _breath.Panic = p; _breath.After = After; }
            if (_fps != null && _baseSprint > 0f)
                _fps.SprintSpeed = _baseSprint * (1f + p * SprintBoost);

            if (!_haveVhs || _vhs == null) return;
            // テープが暴れる。ClampedFloatParameter が上限で止めるので、
            // 足しすぎても値が飛ぶことはない。
            _vhs.tape.value = _bTape + p * 2.2f;
            _vhs.tracking.value = _bTrack + p * 2.2f;
            _vhs.dropout.value = _bDrop + p * 1.8f;
            _vhs.grain.value = _bGrain + p * 1.2f;
            _vhs.scanline.value = _bScan + p * 0.5f;
            _vhs.headSwitch.value = _bHead + p * 1.2f;
            // 色差の帯域が落ちて色が抜ける。テープが暴れると真っ先に死ぬのは色。
            _vhs.chromaBand.value = Mathf.Lerp(_bChroma, 10f, p);
            _vhs.desaturate.value = Mathf.Lerp(_bDesat, 0.70f, p);
        }
    }
}
