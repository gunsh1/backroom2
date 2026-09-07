using System;
using System.IO;

namespace Backrooms.Audio
{
    /// <summary>16bit PCM の WAV を書く。測るために外へ出す口。</summary>
    public static class Wav
    {
        public static void Write(string path, float[] interleaved, int channels, int sampleRate)
        {
            int frames = interleaved.Length / channels;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                int dataBytes = frames * channels * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);                       // PCM
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * 2);      // byte rate
                w.Write((short)(channels * 2));          // block align
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                for (int i = 0; i < frames * channels; i++)
                {
                    float v = interleaved[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    w.Write((short)Math.Round(v * 32767.0));
                }
            }
        }
    }
}
