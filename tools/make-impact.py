"""落ちて床に倒れる音を作る。

既存の素材に「体が床にぶつかる音」は無い。Step_Carpet を落として代用も
できるが、足音は靴の音であって体の音ではないので、低い成分が足りない。

作るのは 3 つの層。実際に人が硬い下地のカーペットへ落ちたときに
鳴っているもの。
  * 胴体が床を叩く低い衝撃 (55Hz -> 30Hz へ落ちる)。床そのものの共振
  * 服と体の「ばさっ」(帯域雑音の一瞬)
  * カメラ本体が転がる硬い音を 2 回 (手から落ちて跳ねる)

残響は付けない。部屋の響きは実行時に RoomAcoustics と ReverbSend が
その場所の寸法から付ける。素材に焼き込むと、どの部屋でも同じ響きになる。

他の素材と同じく 48kHz モノラル。25Hz 以下は再生系によっては出ないのに
AGC だけを動かして他の音を沈めるので落とす。
"""
import struct
import numpy as np

DST = "BackroomsRT/Assets/_Project/Audio/Impact_Fall.wav"
FS = 48000
PEAK = 0.95
SECONDS = 1.6


def env(n, attack, decay, power=1.0):
    """立ち上がりが速く、指数で減る包絡。"""
    t = np.arange(n) / FS
    a = np.clip(t / max(attack, 1e-5), 0.0, 1.0)
    d = np.exp(-t / max(decay, 1e-5))
    return (a * d) ** power


def noise_band(n, lo, hi, rng):
    """帯域雑音。FFT で帯域を切るほうが、フィルタの次数を気にせずに済む。"""
    x = rng.standard_normal(n)
    f = np.fft.rfft(x)
    fr = np.fft.rfftfreq(n, 1.0 / FS)
    g = np.exp(-((np.log2(np.maximum(fr, 1.0) / np.sqrt(lo * hi))) ** 2)
               / (2 * (np.log2(hi / lo) / 2.6) ** 2))
    return np.fft.irfft(f * g, n)


def highpass(x, fc):
    f = np.fft.rfft(x)
    fr = np.fft.rfftfreq(len(x), 1.0 / FS)
    f *= 1.0 / (1.0 + (fc / np.maximum(fr, 1e-3)) ** 4)
    return np.fft.irfft(f, len(x))


def main():
    rng = np.random.default_rng(1996)
    n = int(FS * SECONDS)
    t = np.arange(n) / FS
    out = np.zeros(n)

    # 1. 胴体。床が沈んで戻る。周波数が落ちるので「ぶつかった」に聞こえる。
    #    一定の正弦だと楽器の音になる。
    f = 55.0 * np.exp(-t / 0.085) + 30.0
    phase = 2 * np.pi * np.cumsum(f) / FS
    out += np.sin(phase) * env(n, 0.0015, 0.115) * 1.00

    # 2. 服と体。カーペットの毛が潰れる一瞬の雑音。
    out += noise_band(n, 180.0, 1400.0, rng) * env(n, 0.0008, 0.055) * 0.55

    # 3. カメラ本体。手から落ちて 2 回跳ねる。硬い。
    for delay, gain in ((0.000, 0.40), (0.062, 0.24), (0.148, 0.11)):
        k = int(delay * FS)
        body = noise_band(n - k, 900.0, 6500.0, rng) * env(n - k, 0.0004, 0.022)
        out[k:] += body * gain

    out = highpass(out, 25.0)
    out *= PEAK / np.max(np.abs(out))

    pcm = (np.clip(out, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()
    with open(DST, "wb") as fh:
        fh.write(b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt ")
        fh.write(struct.pack("<IHHIIHH", 16, 1, 1, FS, FS * 2, 2, 16))
        fh.write(b"data" + struct.pack("<I", len(pcm)) + pcm)

    rms = float(np.sqrt(np.mean(out ** 2)))
    peak_at = int(np.argmax(np.abs(out))) / FS
    print(f"{DST}")
    print(f"  {SECONDS:.2f} 秒 / {FS} Hz / mono / peak {np.max(np.abs(out)):.3f} "
          f"/ rms {rms:.4f} / ピーク位置 {peak_at * 1000:.1f} ms")
    # 減衰の確認。100ms で -20dB 以上落ちていないと「衝撃」に聞こえない。
    for ms in (50, 100, 200, 400):
        k = int(ms / 1000 * FS)
        seg = out[k:k + int(0.02 * FS)]
        db = 20 * np.log10(max(float(np.sqrt(np.mean(seg ** 2))), 1e-9) / max(rms, 1e-9))
        print(f"  {ms:4d} ms: {db:+6.1f} dB (rms 比)")


main()
