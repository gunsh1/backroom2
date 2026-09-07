using UnityEngine;
using Backrooms.Player;

namespace Backrooms.Audio
{
    /// <summary>
    /// 撮っている人の呼吸。止まっていても歩いていても鳴っている。
    ///
    /// ファウンドフッテージで「誰かがカメラを持っている」ことを担保して
    /// いるのは画ではなく息のほうで、無音だとカメラだけが独りでに歩いて
    /// いることになる。手持ち揺れを止めると三脚に見えるのと同じ理屈。
    ///
    /// 走ると速くなる。ただし走り出した瞬間ではない。呼吸が上がるのは
    /// 運動を始めて 15-40 秒あたりからで、戻るのはさらに遅い。この
    /// 「上がるより収まるほうが遅い」非対称が、息が上がった感じを作る。
    /// 同じ速さで戻すと、走るのをやめた瞬間に何事も無かったことになる。
    ///
    /// 上げ下げは再生速度でやる。周期と音色が同時に動くが、実際に息が
    /// 上がったときも両方動くので、これでよい。ついでに素材の繰り返し
    /// 周期も一緒に動くので、ループに気付かれにくくなる。
    /// </summary>
    [RequireComponent(typeof(FirstPersonController))]
    [DisallowMultipleComponent]
    public class Breathing : MonoBehaviour
    {
        public AudioClip Clip;
        [Tooltip("平常時の音量。Mix に導出がある")]
        public float Volume = Mix.Breath;

        [Tooltip("息が上がりきったときの再生速度。「若干」なので 1.2 倍まで")]
        public float MaxPitch = 1.20f;
        [Tooltip("息が上がりきったときの音量の倍率")]
        public float LoudGain = 1.8f;
        [Tooltip("走り続けて息が上がりきるまで (秒)")]
        public float RiseSeconds = 14f;
        [Tooltip("やめてから収まるまで (秒)。上がるより遅くすること")]
        public float FallSeconds = 35f;

        /// <summary>0 = 平常、1 = 上がりきった。</summary>
        public float Effort { get; private set; }
        public float Pitch { get { return _src != null ? _src.pitch : 1f; } }

        FirstPersonController _fps;
        AudioSource _src;

        void Awake() { _fps = GetComponent<FirstPersonController>(); }

        void Start()
        {
            if (Clip == null) return;
            var go = new GameObject("Breath");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0.05f, 0.04f);
            _src = go.AddComponent<AudioSource>();
            _src.clip = Clip;
            _src.loop = true;
            _src.playOnAwake = false;
            // 自分の息。完全な 2D にすると頭の中で鳴るので、わずかに定位させる。
            _src.spatialBlend = 0.10f;
            _src.minDistance = 2f;
            _src.maxDistance = 20f;
            _src.dopplerLevel = 0f;
            _src.volume = Volume;
            // 始まる位置を毎回変える。同じ息から始まると、
            // 開始直後に聞こえる 1 呼吸が毎回同じになる。
            _src.time = Random.value * Mathf.Max(0.01f, Clip.length - 0.05f);
            _src.Play();
        }

        void Update()
        {
            if (_src == null) return;
            float d = Time.deltaTime;
            // 直線で上げ下げする。指数で寄せると 0 に着かず、いつまでも
            // わずかに上がったままになって「おちついた」にならない。
            bool hard = _fps != null && _fps.IsSprinting;
            Effort = Mathf.Clamp01(Effort + (hard ? d / Mathf.Max(0.1f, RiseSeconds)
                                                  : -d / Mathf.Max(0.1f, FallSeconds)));
            _src.pitch = Mathf.Lerp(1f, MaxPitch, Effort);
            _src.volume = Volume * Mathf.Lerp(1f, LoudGain, Effort);
        }
    }
}
