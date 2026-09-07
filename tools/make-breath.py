"""呼吸の録音をループ素材に整える。

ずっと鳴らし続けるものなので、素材で一番効くのは「繰り返しに気付かれない」
こと。この録音には 2 つ問題があった。

  1. 前半 3 秒と後半 3 秒で rms が +4.4dB 違う。そのままループすると
     14.5 秒ごとに膨らんで、周期がはっきり聞こえる。
  2. 頭と尻は無音なので段差は無いが、念のため尻を頭に重ねておく。

1 は「呼吸 1 周期よりずっと長い窓」で均す。窓を短くすると吸う・吐くの
起伏まで潰れて、息ではなく雑音になる。周期は自己相関で 1.9 秒だったので
窓は 6 秒。均しは循環させる — しないと均した結果の頭と尻がまた食い違う。
"""
import subprocess
import numpy as np

# (元, 先, ピーク) の順。heavy は起伏が大きいぶん crest factor が高いので、
# rms を揃えるとピークが 1 を超える。ピークで揃えて、平常との音量差は
# 実行時 (Breathing.PanicGain) で付ける。
JOBS = [("artifacts/audio/normal_breath.wav",
         "BackroomsRT/Assets/_Project/Audio/Breath_Normal.wav", 0.85),
        ("artifacts/audio/heavy_breath.wav",
         "BackroomsRT/Assets/_Project/Audio/Breath_Heavy.wav", 0.95)]
FS = 48000
XFADE = 0.20          # 尻を頭に重ねる秒数
SMOOTH = 6.0          # 均しの窓 (呼吸周期 1.9s の 3 倍)


def decode(p):
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", p, "-f", "f32le", "-ac", "1", "-ar", str(FS), "-"],
        capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).astype(np.float64)


def highpass(x, fc, order=4):
    from scipy import signal
    sos = signal.butter(order, fc / (FS / 2), btype="highpass", output="sos")
    return signal.sosfilt(sos, x)


def circ_smooth(x, seconds):
    """循環させた移動平均。ループ素材なので端をまたいで均す。"""
    n = int(seconds * FS)
    if n % 2 == 0:
        n += 1
    pad = np.concatenate([x[-n:], x, x[:n]])
    c = np.cumsum(np.insert(pad, 0, 0.0))
    m = (c[n:] - c[:-n]) / n
    off = n - n // 2
    return m[off:off + len(x)]


def seam(x):
    return abs(x[0] - x[-1]) / np.abs(np.diff(x)).mean()


def build(src, dst, target_peak):
    x = decode(src)
    print(f"元 {src}: {len(x)/FS:.2f}s  peak {np.abs(x).max():.4f}  rms {np.sqrt((x**2).mean()):.5f}")
    print(f"   前 3s / 後 3s の rms 比 "
          f"{20*np.log10(np.sqrt((x[-3*FS:]**2).mean())/np.sqrt((x[:3*FS]**2).mean())):+.1f}dB")

    x = highpass(x, 60.0)

    # --- 長い揺らぎだけを均す -------------------------------------------
    env = circ_smooth(np.abs(x), SMOOTH)
    env = np.maximum(env, env.mean() * 0.35)
    g = np.clip(env.mean() / env, 0.5, 2.0)
    y = x * g
    print(f"   均し後の rms 比 "
          f"{20*np.log10(np.sqrt((y[-3*FS:]**2).mean())/np.sqrt((y[:3*FS]**2).mean())):+.1f}dB")

    # --- 尻を頭に重ねる -------------------------------------------------
    n = int(XFADE * FS)
    head_, tail = y[:n].copy(), y[-n:].copy()
    f = np.linspace(0, 1, n)
    y = y[:-n]
    y[:n] = head_ * f + tail * (1 - f)

    y *= target_peak / np.abs(y).max()
    print(f"   仕上げ {len(y)/FS:.2f}s  peak {np.abs(y).max():.3f}  "
          f"rms {np.sqrt((y**2).mean()):.4f}  継ぎ目 {seam(y):.2f} 倍")

    pcm = (np.clip(y, -1, 1) * 32767).astype("<i2")
    nb = len(pcm) * 2
    with open(dst, "wb") as fh:
        fh.write(b"RIFF" + (36 + nb).to_bytes(4, "little") + b"WAVEfmt ")
        fh.write((16).to_bytes(4, "little") + (1).to_bytes(2, "little") + (1).to_bytes(2, "little")
                 + FS.to_bytes(4, "little") + (FS * 2).to_bytes(4, "little")
                 + (2).to_bytes(2, "little") + (16).to_bytes(2, "little"))
        fh.write(b"data" + nb.to_bytes(4, "little") + pcm.tobytes())
    print("   wrote " + dst)


for src, dst, pk in JOBS:
    build(src, dst, pk)
