using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Backrooms.Player
{
    /// <summary>
    /// 手持ちライト。バッテリ残量が体験の長さを決める。
    ///
    /// フォトリアルの観点では、均一な円錐を投げる懐中電灯が最も嘘に見える。
    /// 実物はホットスポットと周辺の落ち込みを持ち、電池が減ると光量だけでなく
    /// 色温度が下がって赤みが増し、フィラメントの揺れがわずかに乗る。
    /// </summary>
    public class Flashlight : MonoBehaviour
    {
        [Header("バッテリ")]
        [Tooltip("新品からの持続時間（分）。ここが体験の長さを決める")]
        public float BatteryMinutes = 18f;
        [Tooltip("残りこの割合を切ると弱り始める")]
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
        public float Battery01 { get; private set; } = 1f;
        public bool Dead { get; private set; }
        /// <summary>バッテリが尽きた瞬間に一度だけ発火する。</summary>
        public event Action OnBatteryDead;

        HDAdditionalLightData _hd;
        float _drained;
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
            if (!Dead && Input.GetKeyDown(ToggleKey)) On = !On;

            if (On && !Dead)
            {
                _drained += Time.deltaTime;
                float total = Mathf.Max(1f, BatteryMinutes * 60f);
                Battery01 = Mathf.Clamp01(1f - _drained / total);
                if (Battery01 <= 0f)
                {
                    Dead = true;
                    On = false;
                    OnBatteryDead?.Invoke();
                }
            }

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

        /// <summary>キャプチャ・デバッグ用。</summary>
        public void ForceState(bool on, float battery01 = 1f)
        {
            On = on && !Dead;
            Battery01 = Mathf.Clamp01(battery01);
            _drained = (1f - Battery01) * BatteryMinutes * 60f;
            Light.enabled = On;
        }
    }
}
