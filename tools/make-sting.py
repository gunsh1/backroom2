"""イベントの音を Unity の素材に整える。

horror_time.wav は 44.1kHz ステレオで、ピークが 1.169 と既に潰れている。
潰れているものを持ち上げても歪みが増えるだけなので、下げて収める。
48kHz モノラルに揃えるのは他の素材と同じ理由 — 定位と部屋の響きは
実行時に付ける。25Hz より下は再生系によっては出ないうえ、AGC だけを
動かして他の音を無意味に沈めるので落とす。
"""
import subprocess
import numpy as np

SRC = "artifacts/audio/horror_time.wav"
DST = "BackroomsRT/Assets/_Project/Audio/Sting_Horror.wav"
FS = 48000
PEAK = 0.95


def decode(p):
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", p, "-f", "f32le", "-ac", "1", "-ar", str(FS), "-"],
        capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).astype(np.float64)


def highpass(x, fc, order=4):
    from scipy import signal
    sos = signal.butter(order, fc / (FS / 2), btype="highpass", output="sos")
    return signal.sosfilt(sos, x)


x = decode(SRC)
print(f"元: {len(x)/FS:.2f}s  peak {np.abs(x).max():.3f}  rms {np.sqrt((x**2).mean()):.4f}"
      + ("   <- 1 を超えている (潰れ)" if np.abs(x).max() > 1.0 else ""))

y = highpass(x, 25.0)
# 頭と尻に短いフェード。切り口のプチッが一番安っぽい
n = int(0.004 * FS)
y[:n] *= np.linspace(0, 1, n)
n = int(0.030 * FS)
y[-n:] *= np.linspace(1, 0, n)
y *= PEAK / np.abs(y).max()
print(f"仕上げ: {len(y)/FS:.2f}s  peak {np.abs(y).max():.3f}  rms {np.sqrt((y**2).mean()):.4f}")

pcm = (np.clip(y, -1, 1) * 32767).astype("<i2")
nb = len(pcm) * 2
with open(DST, "wb") as fh:
    fh.write(b"RIFF" + (36 + nb).to_bytes(4, "little") + b"WAVEfmt ")
    fh.write((16).to_bytes(4, "little") + (1).to_bytes(2, "little") + (1).to_bytes(2, "little")
             + FS.to_bytes(4, "little") + (FS * 2).to_bytes(4, "little")
             + (2).to_bytes(2, "little") + (16).to_bytes(2, "little"))
    fh.write(b"data" + nb.to_bytes(4, "little") + pcm.tobytes())
print("wrote " + DST)
