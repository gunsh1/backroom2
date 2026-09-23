using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Backrooms.Player
{
    /// <summary>
    /// 手持ちライト。
    ///
    /// もとは 18 分の電池を自前で持っていたが、時計を 2 つ持つと必ず
    /// 食い違う。残量は TapeState から読むだけにした。副産物として、
    /// すでに調整済みの「残り 22% から弱り始める」が「毎テープの最後の
    /// 3 分 18 秒でライトが死んでいく」に変わる。独立した 2 系統より良い。
    ///
    /// フォトリアルの観点では、均一な円錐を投げる懐中電灯が最も嘘に見える。
    /// 実物はホットスポットと周辺の落ち込みを持ち、電池が減ると光量だけでなく
    /// 色温度が下がって赤みが増し、フィラメントの揺れがわずかに乗る。
    /// </summary>
    public class Flashlight : MonoBehaviour
    {
        [Header("バッテリ")]
        [Tooltip("残りこの割合を切ると弱り始める。テープ残量に対する割合")]
        public float FadeStartFraction = 0.22f;

        [Header("光")]
        public float PeakLumens = 850f;
        public float PeakTemperature = 3400f;   // 新品のハロゲン電球色
        public float DeadTemperature = 2200f;   // 電池が減ると赤くなる
        public float SpotAngle = 46f;
        public float InnerSpotPercent = 42f;
        public float Range = 24f;

        [Header("入力")]
        public KeyCode ToggleKey = KeyCode.F;

        public Light Light { get; private set; }
        public bool On { get; private set; }
        /// <summary>テープ残量の読み出し。TapeState が無い場面では常に新品。</summary>
        public float Battery01
        {
            get { return _tape != null ? _tape.Remaining01 : _standalone; }
        }
        public bool Dead { get { return Battery01 <= 0f; } }

        HDAdditionalLightData _hd;
        Backrooms.Game.TapeState _tape;
        float _standalone = 1f;
        float _flickerSeed;

        void Awake()
        {
            Light = GetComponent<Light>();
            if (Light == null) Light = gameObject.AddComponent<Light>();
            _hd = GetComponent<HDAdditionalLightData>();
            if (_hd == null) _hd = gameObject.AddComponent<HDAdditionalLightData>();

            try
            {
                _hd.SetLightTypeAndShape(HDLightTypeAndShape.ConeSpot);
                _hd.SetSpotAngle(SpotAngle, InnerSpotPercent);
                _hd.SetIntensity(PeakLumens, LightUnit.Lumen);
                _hd.range = Range;
                _hd.affectsVolumetric = true;
            }
            catch (Exception e) { Debug.LogWarning("[Flashlight] HD light setup: " + e.Message); }

            Light.useColorTemperature = true;
            Light.colorTemperature = PeakTemperature;
            Light.shadows = LightShadows.Soft;
            Light.enabled = false;
            _flickerSeed = UnityEngine.Random.value * 100f;
        }

        void Update()
        {
            if (_tape == null) _tape = FindFirstObjectByType<Backrooms.Game.TapeState>();
            // 書き出し中や単体での確認では、誰かが押した F を拾わない。
            if (Application.isFocused && !Dead && Input.GetKeyDown(ToggleKey)) On = !On;
            if (Dead) On = false;

            Light.enabled = On;
            if (!On) return;

            // 残量が減るほど暗く、赤く、そして不安定になる
            float health = Mathf.Clamp01(Battery01 / Mathf.Max(0.01f, FadeStartFraction));
            float level = Mathf.Lerp(0.18f, 1f, health * health);

            float instability = (1f - health) * 0.35f;
            float flicker = 1f;
            if (instability > 0.001f)
            {
                float n = Mathf.PerlinNoise(_flickerSeed + Time.time * 11f, 0f);
                flicker = 1f - instability * n * n;
            }

            try { _hd.SetIntensity(PeakLumens * level * flicker, LightUnit.Lumen); }
            catch { Light.intensity = PeakLumens * level * flicker; }
            Light.colorTemperature = Mathf.Lerp(DeadTemperature, PeakTemperature, health);
        }

        /// <summary>キャプチャ・デバッグ用。TapeState が無い場面でだけ残量も効く。</summary>
        public void ForceState(bool on, float battery01 = 1f)
        {
            _standalone = Mathf.Clamp01(battery01);
            On = on && !Dead;
            Light.enabled = On;
        }
    }
}
