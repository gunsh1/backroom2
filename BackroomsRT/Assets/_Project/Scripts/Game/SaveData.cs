using System;
using System.IO;
using UnityEngine;

namespace Backrooms.Game
{
    /// <summary>
    /// テープをまたいで残るもの。
    ///
    /// 置き場は Application.persistentDataPath。PlayerPrefs は使わない
    /// （レジストリで、中身が見えず、退避も復元もできない）。Assets/ の
    /// 下も使わない（ビルドでは読み取り専用になるし、リポジトリが汚れる）。
    ///
    /// 書き込みは .tmp に書いてから差し替える。途中で落ちても、前の
    /// 保存が壊れて全部の印が消える、という事故が起きないようにする。
    /// </summary>
    [Serializable]
    public class SaveData
    {
        /// <summary>次に入れるテープの番号。1 から。</summary>
        public int tapeNumber = 1;
        /// <summary>一度でも出口に着いたか。</summary>
        public bool escaped;
        /// <summary>殺された回数。</summary>
        public int kills;
        /// <summary>テープが尽きた回数。</summary>
        public int runOuts;
        /// <summary>通算の経過秒。</summary>
        public float totalSeconds;

        /// <summary>
        /// 印。[x, z, y, yaw, glyph, tape] の平たい配列。
        /// 名前付きの構造体の配列にすると、5000 個で 4 倍の大きさになる。
        /// </summary>
        public float[] marks = new float[0];

        public const int MarkStride = 6;

        static string Path
        {
            get { return System.IO.Path.Combine(Application.persistentDataPath, "save.json"); }
        }

        public static SaveData Load()
        {
            try
            {
                if (!File.Exists(Path)) return new SaveData();
                var json = File.ReadAllText(Path);
                var d = JsonUtility.FromJson<SaveData>(json);
                if (d == null) return new SaveData();
                if (d.marks == null) d.marks = new float[0];
                if (d.tapeNumber < 1) d.tapeNumber = 1;
                return d;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[save] 読めないので新品から始める: " + e.Message);
                return new SaveData();
            }
        }

        public void Save()
        {
            try
            {
                string path = Path, tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(this));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[save] 書けなかった: " + e.Message);
            }
        }

        /// <summary>最初からやり直す。デバッグ用。</summary>
        public static void Wipe()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception e) { Debug.LogWarning("[save] 消せなかった: " + e.Message); }
        }

        public static string Where { get { return Path; } }
    }
}
