using System.Collections.Generic;
using UnityEngine;
using Backrooms.Generation;

namespace Backrooms.Audio
{
    /// <summary>
    /// 蛍光灯の音。近い器具にだけ実際の音源を割り当てる。
    ///
    /// 器具は 1.8m 格子で無限に続くので、全部に AudioSource を持たせる
    /// わけにはいかない。どこに何があるかは Level0Layout が式で答えられる
    /// ので、聞こえる範囲だけを毎回数え直して、少数の音源を貸し出す。
    ///
    /// 1 台ずつ再生速度をわずかにずらしてある。同じ 120Hz を何台も
    /// 完全に同位相で鳴らすと 1 台の大きな音にしか聞こえない。実際の
    /// 安定器は個体差でずれていて、その差が「うなり」になる。蛍光灯だらけの
    /// 部屋があの独特のうねりを持つのはこれが理由なので、外せない。
    /// </summary>
    [DisallowMultipleComponent]
    public class FixtureVoices : MonoBehaviour
    {
        public int Seed = 1996;
        [Tooltip("同時に鳴らす器具の数")]
        public int Voices = 8;
        [Tooltip("この距離までの器具を候補にする (m)")]
        public float Range = 13f;
        [Tooltip("器具 1 台。Mix に導出がある")]
        public float Volume = Mix.Ballast;
        public float MinDistance = 1.4f;
        [Tooltip("壁越しの器具をどこまで落とすか")]
        public float OccludedVolume = 0.32f;
        public float OccludedCutoff = 620f;
        public float Interval = 0.30f;

        public int Assigned { get; private set; }
        public int Occluded { get; private set; }

        class Voice
        {
            public AudioSource Src;
            public AudioLowPassFilter Lp;
            public Vector2Int Bay = new Vector2Int(int.MinValue, int.MinValue);
            public bool Failing;
            public float NextClick;
        }

        readonly List<Voice> _voices = new List<Voice>();
        readonly List<(float d, Vector2Int bay, Vector3 p, bool fail)> _found
            = new List<(float, Vector2Int, Vector3, bool)>();
        Transform _ear;
        float _next;
        AudioSource _clickSrc;

        void Start()
        {
            SoundBank.Build();
            for (int i = 0; i < Voices; i++)
            {
                var go = new GameObject("Fix_" + i);
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.loop = true;
                s.playOnAwake = false;
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = MinDistance;
                s.maxDistance = Range + 3f;
                s.dopplerLevel = 0f;      // 器具は動かない
                s.volume = 0f;
                var lp = go.AddComponent<AudioLowPassFilter>();
                lp.cutoffFrequency = 22000f;
                _voices.Add(new Voice { Src = s, Lp = lp });
            }
            var cgo = new GameObject("Fix_Click");
            cgo.transform.SetParent(transform, false);
            _clickSrc = cgo.AddComponent<AudioSource>();
            _clickSrc.playOnAwake = false;
            _clickSrc.spatialBlend = 1f;
            _clickSrc.rolloffMode = AudioRolloffMode.Logarithmic;
            _clickSrc.minDistance = 1.5f;
            _clickSrc.maxDistance = Range;
            _clickSrc.dopplerLevel = 0f;
        }

        void Update()
        {
            if (_ear == null)
            {
                var l = FindFirstObjectByType<AudioListener>();
                if (l == null) return;
                _ear = l.transform;
            }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            Reassign(_ear.position);
        }

        void Reassign(Vector3 ear)
        {
            _found.Clear();
            int i0 = Mathf.FloorToInt((ear.x - Range) / Level0Layout.Bay);
            int i1 = Mathf.FloorToInt((ear.x + Range) / Level0Layout.Bay);
            int j0 = Mathf.FloorToInt((ear.z - Range) / Level0Layout.Bay);
            int j1 = Mathf.FloorToInt((ear.z + Range) / Level0Layout.Bay);
            float r2 = Range * Range;

            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    if (!Level0Layout.HasFixture(i, j, Seed)) continue;
                    var st = Level0Layout.LampAt(i, j, Seed);
                    if (st == Level0Layout.Lamp.Dead) continue;      // 切れた管は鳴らない
                    if (!Level0Layout.FixtureFree(i, j, Seed)) continue;
                    var p = Level0Layout.FixtureCenter(i, j);
                    float d2 = (p - ear).sqrMagnitude;
                    if (d2 > r2) continue;
                    _found.Add((d2, new Vector2Int(i, j), p, st == Level0Layout.Lamp.Flicker));
                }
            _found.Sort((a, b) => a.d.CompareTo(b.d));

            int n = Mathf.Min(Voices, _found.Count);
            Assigned = n; Occluded = 0;

            // すでに同じ器具を鳴らしている音源はそのまま使う。
            // 割り当て直すたびに Play をやり直すと、うなりが途切れて気付かれる。
            var taken = new bool[_voices.Count];
            var slot = new int[n];
            for (int k = 0; k < n; k++) slot[k] = -1;
            for (int k = 0; k < n; k++)
                for (int v = 0; v < _voices.Count; v++)
                    if (!taken[v] && _voices[v].Bay == _found[k].bay) { slot[k] = v; taken[v] = true; break; }
            for (int k = 0; k < n; k++)
            {
                if (slot[k] >= 0) continue;
                for (int v = 0; v < _voices.Count; v++)
                    if (!taken[v]) { slot[k] = v; taken[v] = true; break; }
            }

            for (int k = 0; k < n; k++)
            {
                var f = _found[k];
                var vo = _voices[slot[k]];
                bool changed = vo.Bay != f.bay || vo.Failing != f.fail;
                vo.Bay = f.bay; vo.Failing = f.fail;
                vo.Src.transform.position = f.p;
                if (changed)
                {
                    vo.Src.clip = f.fail ? SoundBank.BallastFail : SoundBank.Ballast;
                    // 個体差。0.4% までのずれで、数秒周期のうなりになる。
                    uint h = Level0Layout.Hash(f.bay.x, f.bay.y, Seed * 7 + 61);
                    vo.Src.pitch = 0.996f + (h & 0xFFFF) / 65535f * 0.008f;
                    vo.Src.time = (h >> 16) / 65535f * (vo.Src.clip.length * 0.98f);
                    vo.Src.Play();
                }

                bool blocked = Blocked(f.p, _ear.position);
                if (blocked) Occluded++;
                float want = Volume * (f.fail ? 1.25f : 1f) * (blocked ? OccludedVolume : 1f);
                vo.Src.volume = Mathf.MoveTowards(vo.Src.volume, want, Time.unscaledDeltaTime * 2.5f);
                vo.Lp.cutoffFrequency = blocked ? OccludedCutoff : 22000f;

                // 切れかけの管は点灯管が不規則に弾く
                if (f.fail && !blocked && Time.unscaledTime > vo.NextClick)
                {
                    vo.NextClick = Time.unscaledTime + 0.7f + Random.value * 4.5f;
                    _clickSrc.transform.position = f.p;
                    _clickSrc.pitch = 0.85f + Random.value * 0.35f;
                    // 点灯管が弾く音はうなりより大きいが、0.30 は基準から外れていた
                    _clickSrc.PlayOneShot(SoundBank.Clicks[Random.Range(0, SoundBank.Clicks.Length)],
                                          Volume * 3f);
                }
            }
            for (int v = 0; v < _voices.Count; v++)
            {
                if (taken[v]) continue;
                _voices[v].Src.volume = Mathf.MoveTowards(_voices[v].Src.volume, 0f,
                                                          Time.unscaledDeltaTime * 3f);
                if (_voices[v].Src.volume <= 0f && _voices[v].Src.isPlaying)
                {
                    _voices[v].Src.Stop();
                    _voices[v].Bay = new Vector2Int(int.MinValue, int.MinValue);
                }
            }
        }

        /// <summary>器具と耳の間に壁があるか。天井の高さで見る。</summary>
        bool Blocked(Vector3 a, Vector3 b)
        {
            float len = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
            int steps = Mathf.CeilToInt(len / 0.45f);
            for (int i = 1; i < steps; i++)
            {
                float t = i / (float)steps;
                float x = Mathf.Lerp(b.x, a.x, t), z = Mathf.Lerp(b.z, a.z, t);
                if (Level0Layout.Solid(x, z, Seed)) return true;
            }
            return false;
        }
    }
}
