using UnityEngine;

namespace Backrooms.Audio
{
    /// <summary>
    /// 常に鳴っている床。空調の唸りと部屋の空気。
    ///
    /// 位置を持たせない。空調のダクトは天井裏を建物じゅうに走っていて、
    /// どこか 1 点から聞こえるものではない。むしろ「どこにも音源が無いのに
    /// 音がしている」ことが Backrooms の不気味さの一部になる。
    /// </summary>
    [DisallowMultipleComponent]
    public class AmbientBed : MonoBehaviour
    {
        public float HvacVolume = Mix.Hvac;
        public float ToneVolume = Mix.Tone;

        AudioSource _hvac, _tone;

        void Start()
        {
            SoundBank.Build();
            _hvac = Make("Hvac", SoundBank.Hvac, HvacVolume, 1.0f);
            _tone = Make("RoomTone", SoundBank.RoomTone, ToneVolume, 0.87f);
        }

        AudioSource Make(string name, AudioClip clip, float vol, float pitch)
        {
            var go = new GameObject("Amb_" + name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = true;
            s.volume = vol;
            s.pitch = pitch;
            s.spatialBlend = 0f;          // 2D
            s.playOnAwake = false;
            s.Play();
            // 同じ長さのループが 2 本、頭を揃えて始まると位相が噛み合って
            // うねる。片方をずらす。
            s.time = name == "RoomTone" ? clip.length * 0.37f : 0f;
            return s;
        }
    }
}
