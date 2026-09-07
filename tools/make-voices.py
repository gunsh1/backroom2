"""録音した呼び声を Unity の素材に整える。

やること
  * 48kHz モノラルに揃える (male は 22.05kHz、female は 48kHz ステレオだった)
  * 80Hz ハイパス。声の基音は男 100Hz / 女 180Hz なので下は要らない
  * 無音を切り、頭 8ms / 尻 40ms のフェードを付ける
  * 群ごとに「声が出ている区間の rms」を揃える。並べてランダムに撫でるので、
    素材ごとの録音レベルの差がそのまま「遠近の差」に聞こえてしまう
  * ピークが 1 を超えている素材があった (1.414) ので 0.95 に収める

距離や壁の効果はここでは焼かない。遠くの声は鳴らす場所が毎回変わるので、
減衰も遮蔽も実行時に掛ける。
"""
import os, re, subprocess
import numpy as np

SRC = "artifacts/audio"
DST = "BackroomsRT/Assets/_Project/Audio/Voices"
FS = 48000


def decode(path):
    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", path, "-f", "f32le", "-ac", "1", "-ar", str(FS), "-"],
        capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).astype(np.float64)


def highpass(x, fc, order=4):
    from scipy import signal
    sos = signal.butter(order, fc / (FS / 2), btype="highpass", output="sos")
    return signal.sosfilt(sos, x)


def trim(x, lead=0.030, tail=0.150):
    e = np.abs(x)
    thr = e.max() * 0.015
    idx = np.nonzero(e > thr)[0]
    if not len(idx):
        return x
    a = max(0, idx[0] - int(lead * FS))
    b = min(len(x), idx[-1] + int(tail * FS))
    return x[a:b]


def fade(x, inn=0.008, out=0.040):
    y = x.copy()
    n = int(inn * FS)
    if n and n < len(y):
        y[:n] *= np.linspace(0, 1, n)
    n = int(out * FS)
    if n and n < len(y):
        y[-n:] *= np.linspace(1, 0, n)
    return y


def voiced_rms(x):
    """声が出ている所だけの rms。間の無音を含めると長い素材ほど小さく出る。"""
    e = np.abs(x)
    m = e > e.max() * 0.056          # -25 dB
    return float(np.sqrt((x[m] ** 2).mean())) if m.any() else 1e-9


def build(group, files, target_rms, peak_cap=0.95):
    outs = []
    for i, f in enumerate(sorted(files)):
        x = trim(highpass(decode(os.path.join(SRC, f)), 80.0))
        outs.append((f, fade(x)))
    rms = [voiced_rms(x) for _, x in outs]
    print(f"[{group}] 加工前の声区間 rms: " + " ".join(f"{r:.4f}" for r in rms))
    rows = []
    for i, (f, x) in enumerate(outs):
        g = target_rms / rms[i]
        y = x * g
        pk = np.abs(y).max()
        if pk > peak_cap:
            y *= peak_cap / pk
        stem = re.sub(r"[^a-z0-9]+", "_", os.path.splitext(f)[0].lower())
        stem = re.sub(r"_?(fe)?male_?", "", stem).strip("_") or "call"
        name = f"{group}_{i+1:02d}_{stem}.wav"
        pcm = np.clip(y, -1, 1)
        pcm = (pcm * 32767).astype("<i2")
        with open(os.path.join(DST, name), "wb") as fh:
            n = len(pcm) * 2
            fh.write(b"RIFF" + (36 + n).to_bytes(4, "little") + b"WAVEfmt ")
            fh.write((16).to_bytes(4, "little") + (1).to_bytes(2, "little")
                     + (1).to_bytes(2, "little") + FS.to_bytes(4, "little")
                     + (FS * 2).to_bytes(4, "little") + (2).to_bytes(2, "little")
                     + (16).to_bytes(2, "little"))
            fh.write(b"data" + n.to_bytes(4, "little") + pcm.tobytes())
        rows.append((name, len(y) / FS, float(np.abs(y).max()), voiced_rms(y)))
    return rows


os.makedirs(DST, exist_ok=True)
allf = os.listdir(SRC)
male = [f for f in allf if "male" in f and "female" not in f]
female = [f for f in allf if "female" in f]
print(f"male {len(male)} / female {len(female)}")

# 揃える先。呼び声は素材のうち最も控えめなものに寄せる。上に引っ張ると
# 元から潰れていた素材 (peak 1.414) の歪みまで持ち上がる。
rows = build("male", male, 0.150)
rows += build("female", female, 0.150)
print(f"\n{'file':40s} {'秒':>6s} {'peak':>6s} {'声rms':>7s}")
for n, d, p, r in rows:
    print(f"{n:40s} {d:6.2f} {p:6.3f} {r:7.4f}")
