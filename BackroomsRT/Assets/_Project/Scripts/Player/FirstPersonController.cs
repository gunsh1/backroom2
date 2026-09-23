using UnityEngine;

namespace Backrooms.Player
{
    /// <summary>
    /// フォトリアル前提の一人称コントローラ。
    ///
    /// カムコーダ版にあった誇張した手ブレ（Perlin ドリフト・大きなロール）は入れない。
    /// 生身の視界を再現するので、揺れは実際の歩行に伴う分だけに絞る。
    /// VR 移植時はこのコンポーネントを外して XR リグに差し替えるだけで済むよう、
    /// 視点は必ず子の Head を介して動かす。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        [Header("移動")]
        public float WalkSpeed = 1.40f;      // 実際の歩行速度
        public float SprintSpeed = 3.60f;
        public float CrouchSpeed = 0.80f;
        public float Acceleration = 14f;
        public float Gravity = -9.81f;

        [Header("姿勢")]
        public float StandHeight = 1.78f;
        public float CrouchHeight = 1.25f;
        public float EyeOffset = -0.10f;     // 頭頂から目までの距離
        public float CrouchLerpSpeed = 9f;

        [Header("視線")]
        public float MouseSensitivity = 2.2f;   // レガシー入力の Mouse X/Y は既に平滑化済み
        public float PitchLimit = 85f;

        [Header("歩行に伴う視点の揺れ（控えめに）")]
        public float StepFrequency = 1.9f;   // 歩/秒。1.4m/s なら歩幅 0.74m
        public float BobVertical = 0.012f;   // m
        public float BobLateral = 0.008f;    // m
        public float BobRoll = 0.28f;        // deg

        public Transform Head { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsCrouching { get; private set; }
        public float SpeedNormalized { get; private set; }
        /// <summary>接地した瞬間に立つフラグ。足音のトリガに使う。</summary>
        public bool SteppedThisFrame { get; private set; }
        /// <summary>いま踏んだのが左足か。足音を左右で撃ち分けるのに使う。</summary>
        public bool LastStepWasLeft { get { return _lastStepWasLeft; } }
        /// <summary>いまの歩幅の周期 (秒)。踏む間隔をゆらすのに使う。</summary>
        public float StrideSeconds
        {
            get
            {
                float f = StepFrequency * (IsSprinting ? 1.45f : 1f);
                return f > 0.01f ? 1f / f : 0.5f;
            }
        }

        /// <summary>
        /// 自動テスト用の入力差し込み口。これを立てるとキーボードとマウスを
        /// 一切読まなくなるので、生成した間取りを歩いて通れるかを
        /// バッチで確かめられる。
        /// </summary>
        [System.NonSerialized] public bool UseExternalInput;
        [System.NonSerialized] public Vector2 ExternalMove;
        /// <summary>差し込み入力での走り。息の上がり方はここでしか試せない。</summary>
        [System.NonSerialized] public bool ExternalSprint;

        /// <summary>
        /// 走る速さに掛ける係数。1 で元気、小さいほど息が上がっている。
        /// Breathing が書く。SprintSpeed を直接書き換えないのは、
        /// PanicEvent が同じ値を握っていて上書き合戦になるから。
        /// </summary>
        [System.NonSerialized] public float SprintFactor = 1f;

        CharacterController _cc;
        Vector3 _velocity;
        float _yaw, _pitch;
        float _bobPhase;
        float _currentHeight;
        bool _lastStepWasLeft;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _currentHeight = StandHeight;
            ApplyHeight(StandHeight);
            _cc.radius = 0.28f;
            _cc.slopeLimit = 45f;
            _cc.stepOffset = 0.28f;

            Head = transform.Find("Head");
            if (Head == null)
            {
                var go = new GameObject("Head");
                go.transform.SetParent(transform, false);
                Head = go.transform;
            }
            _yaw = transform.eulerAngles.y;
        }

        void Start()
        {
            // カーソルの掴み外しは GameSession が持つ。ここで掴むと、
            // ポーズ中に誰かが勝手に掴み直すことになる。GameSession が
            // 居ない場面 (キャプチャ、単体での確認) だけ自分で掴む。
            if (FindFirstObjectByType<Backrooms.Capture.FlatCaptureRunner>() != null) return;
            if (FindFirstObjectByType<Backrooms.Game.GameSession>() != null) return;
            LockCursor(true);
        }

        void Update()
        {
            SteppedThisFrame = false;
            if (!UseExternalInput) HandleLook();
            HandleCrouch();
            HandleMove();
            ApplyHeadMotion();
        }

        // ------------------------------------------------------------------
        void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void HandleLook()
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            float dx = Input.GetAxisRaw("Mouse X") * MouseSensitivity;
            float dy = Input.GetAxisRaw("Mouse Y") * MouseSensitivity;
            _yaw += dx;
            _pitch = Mathf.Clamp(_pitch - dy, -PitchLimit, PitchLimit);
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        void HandleCrouch()
        {
            bool wantCrouch = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);

            // 立ち上がれるだけの空間があるか確認する（低い天井の区画で必要になる）
            if (IsCrouching && !wantCrouch)
            {
                var origin = transform.position + Vector3.up * (CrouchHeight - _cc.radius);
                if (Physics.SphereCast(origin, _cc.radius * 0.95f, Vector3.up, out _,
                                       StandHeight - CrouchHeight, ~0, QueryTriggerInteraction.Ignore))
                    wantCrouch = true;
            }
            IsCrouching = wantCrouch;

            float target = IsCrouching ? CrouchHeight : StandHeight;
            _currentHeight = Mathf.Lerp(_currentHeight, target, Time.deltaTime * CrouchLerpSpeed);
            ApplyHeight(_currentHeight);
        }

        void ApplyHeight(float h)
        {
            _cc.height = h;
            _cc.center = new Vector3(0f, h * 0.5f, 0f);
        }

        void HandleMove()
        {
            Vector2 input = ExternalMove;
            bool sprintHeld = ExternalSprint;
            if (!UseExternalInput)
            {
                sprintHeld = false;
                input = Vector2.zero;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) input.y += 1f;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) input.y -= 1f;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) input.x += 1f;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) input.x -= 1f;
                sprintHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            }
            if (input.sqrMagnitude > 1f) input.Normalize();

            IsSprinting = sprintHeld && !IsCrouching && input.y > 0.1f;
            float target = IsCrouching ? CrouchSpeed
                         : (IsSprinting ? SprintSpeed * Mathf.Max(0.1f, SprintFactor) : WalkSpeed);

            Vector3 wish = (transform.right * input.x + transform.forward * input.y) * target;
            Vector3 horizontal = new Vector3(_velocity.x, 0f, _velocity.z);
            horizontal = Vector3.MoveTowards(horizontal, wish, Acceleration * Time.deltaTime);
            _velocity.x = horizontal.x;
            _velocity.z = horizontal.z;

            _velocity.y = _cc.isGrounded ? -1.5f : _velocity.y + Gravity * Time.deltaTime;
            _cc.Move(_velocity * Time.deltaTime);

            Vector3 flat = new Vector3(_cc.velocity.x, 0f, _cc.velocity.z);
            SpeedNormalized = Mathf.Clamp01(flat.magnitude / SprintSpeed);
        }

        void ApplyHeadMotion()
        {
            Vector3 flat = new Vector3(_cc.velocity.x, 0f, _cc.velocity.z);
            float moving = Mathf.Clamp01(flat.magnitude / WalkSpeed);
            float freq = StepFrequency * Mathf.Lerp(1f, 1.45f, IsSprinting ? 1f : 0f);

            float prevPhase = _bobPhase;
            // 位相は 1 歩あたり pi 進める。2pi にすると半周ごとの判定が
            // 1 秒に 2 倍の回数当たり、StepFrequency という名前に反して
            // 3.8 歩/秒になる。1.4m/s で割ると歩幅 0.37m のすり足で、
            // 実際に「タタッタタッ」と聞こえていた。人の歩幅は 0.7〜0.75m。
            _bobPhase += Time.deltaTime * freq * Mathf.PI * moving;

            // 半周ごとに 1 歩。足音とカメラの沈み込みを同じ位相で扱う。
            if (Mathf.FloorToInt(prevPhase / Mathf.PI) != Mathf.FloorToInt(_bobPhase / Mathf.PI) && moving > 0.1f)
            {
                SteppedThisFrame = true;
                _lastStepWasLeft = !_lastStepWasLeft;
            }

            float amp = IsSprinting ? 1.7f : 1f;
            float bobY = -Mathf.Abs(Mathf.Sin(_bobPhase)) * BobVertical * moving * amp;
            float bobX = Mathf.Sin(_bobPhase * 0.5f) * BobLateral * moving * amp;
            float roll = Mathf.Sin(_bobPhase * 0.5f + 0.35f) * BobRoll * moving * amp;

            Head.localPosition = new Vector3(bobX, _currentHeight + EyeOffset + bobY, 0f);
            Head.localRotation = Quaternion.Euler(_pitch, 0f, roll);
        }

        /// <summary>向きだけを変える。速度は保つので歩きながら曲がれる。</summary>
        public void SetYaw(float yaw)
        {
            _yaw = yaw;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        /// <summary>キャプチャ・デバッグ用に姿勢を直接指定する。</summary>
        public void SetPose(Vector3 position, float yaw, float pitch)
        {
            if (_cc == null) _cc = GetComponent<CharacterController>();
            _cc.enabled = false;
            transform.position = position;
            _cc.enabled = true;

            _yaw = yaw;
            _pitch = Mathf.Clamp(pitch, -PitchLimit, PitchLimit);
            _velocity = Vector3.zero;
            _bobPhase = 0f;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

            if (Head == null) Head = transform.Find("Head");
            if (Head != null)
            {
                Head.localPosition = new Vector3(0f, _currentHeight + EyeOffset, 0f);
                Head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            }
        }
    }
}
