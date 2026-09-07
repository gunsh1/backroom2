# -*- coding: utf-8 -*-
"""
書き出した WAV から音を測る。

指定した値ではなく、出てきた波形から測り返す。
  * インパルス応答 -> Schroeder 後方積分 -> T30 を外挿して RT60
  * 帯域を分けて測り、高域が短く減衰しているか
  * 初期反射の時刻 (最初の 120ms のピーク位置)
  * 安定器の合成音 -> 120Hz とその倍音が出ているか
実行: python tools/analyse-audio.py
"""
import os
import sys
import wave

import numpy as np
from scipy import signal

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
AUD = os.path.join(ROOT, 'artifacts', 'audio')


def read(path):
    with wave.open(path, 'rb') as w:
        n, ch, sr = w.getnframes(), w.getnchannels(), w.getframerate()
        raw = np.frombuffer(w.readframes(n), dtype='<i2').astype(np.float64) / 32768.0
    if ch > 1:
        raw = raw.reshape(-1, ch).mean(axis=1)
    return raw, sr


def band(x, sr, lo, hi):
    """
    因果的な Butterworth で帯域を切る。

    FFT で窓を掛けて逆変換すると非因果なリンギングが出て、循環畳み込みで
    末尾に回り込む。後方積分は末尾から足すので、そこに床ができて減衰が
    止まって見える。実際それで帯域別の RT60 が全部 nan になった。
    """
    sos = signal.butter(4, [lo / (sr / 2), min(hi, sr / 2 * 0.99) / (sr / 2)],
                        btype='band', output='sos')
    return signal.sosfilt(sos, x)


def rt60(h, sr, lo=None, hi=None):
    """
    Schroeder 後方積分。-5dB から -35dB の傾きを取って -60dB へ外挿 (T30)。
    区間が取れなければ -5 .. -25 (T20) に落とす。
    """
    y = h if lo is None else band(h, sr, lo, hi)
    e = y ** 2
    edc = np.cumsum(e[::-1])[::-1]
    if edc[0] <= 0:
        return float('nan'), 0.0
    db = 10.0 * np.log10(np.maximum(edc / edc[0], 1e-20))
    t = np.arange(len(db)) / sr

    def fit(d0, d1):
        i0 = np.argmax(db <= d0)
        i1 = np.argmax(db <= d1)
        if i1 <= i0 + 32:
            return None
        p = np.polyfit(t[i0:i1], db[i0:i1], 1)
        if p[0] >= -1e-6:
            return None
        return -60.0 / p[0], np.corrcoef(t[i0:i1], db[i0:i1])[0, 1] ** 2

    r = fit(-5.0, -35.0) or fit(-5.0, -25.0)
    if r is None:
        return float('nan'), 0.0
    return r


def early_peaks(h, sr, window=0.16, thresh=0.12):
    """最初の window 秒のピーク時刻 (ms)。直接音のあとの反射。"""
    n = int(window * sr)
    seg = np.abs(h[:n])
    if seg.max() <= 0:
        return []
    # 直接音 (先頭) を除いて探す
    body = seg[int(0.002 * sr):]
    if body.size == 0:
        return []
    lim = body.max() * thresh
    out = []
    i = 1
    while i < len(body) - 1:
        if body[i] > lim and body[i] >= body[i - 1] and body[i] > body[i + 1]:
            out.append((i + int(0.002 * sr)) / sr * 1000.0)
            i += int(0.0015 * sr)
        else:
            i += 1
    return out


def spectrum_peaks(x, sr, top=8, fmin=40, fmax=4000):
    w = np.hanning(len(x))
    X = np.abs(np.fft.rfft(x * w))
    f = np.fft.rfftfreq(len(x), 1.0 / sr)
    m = (f >= fmin) & (f <= fmax)
    X, f = X[m], f[m]
    idx = np.argsort(X)[::-1]
    out, used = [], []
    for i in idx:
        if any(abs(f[i] - u) < 15 for u in used):
            continue
        used.append(f[i])
        out.append((f[i], 20 * np.log10(X[i] / X.max() + 1e-12)))
        if len(out) >= top:
            break
    return sorted(out, key=lambda a: a[0])


def welch_db(x, sr):
    f, p = signal.welch(x, sr, nperseg=8192)
    return f, 10 * np.log10(p + 1e-20)


def tape_response(dry, wet, sr):
    """
    平坦な雑音の前後からテープの伝達特性を出す。
    素材そのものの傾きが割り算で消えるので、帯域端が正しく出る。
    """
    f, pd = welch_db(dry, sr)
    _, pw = welch_db(wet, sr)
    h = pw - pd
    m = (f >= 300) & (f <= 1500)
    h -= h[m].mean()          # 通過域を 0dB に合わせる
    def cross(lo_side, thr):
        idx = np.where(f > 1500)[0] if not lo_side else np.where(f < 300)[0][::-1]
        for i in idx:
            if h[i] < thr:
                return f[i]
        return None
    return h, f, cross(False, -3), cross(False, -20), cross(True, -3)


def main():
    if not os.path.isdir(AUD):
        print('%s が無い。先に .\\tools\\backrooms.ps1 audio を実行する。' % AUD)
        return 1

    print('=== 合成した素材 ===')
    for name, want in [('src_ballast.wav', [120, 240, 360]),
                       ('src_ballast_fail.wav', [120, 240, 360]),
                       ('src_hvac.wav', None),
                       ('src_roomtone.wav', None)]:
        p = os.path.join(AUD, name)
        if not os.path.exists(p):
            print('  %-24s (無い)' % name)
            continue
        x, sr = read(p)
        pk = spectrum_peaks(x, sr, top=6)
        s = '  '.join('%.0fHz %+.0fdB' % (f, d) for f, d in pk)
        print('  %-24s %s' % (name, s))
        if want:
            got = [f for f, _ in pk]
            hit = [w for w in want if any(abs(g - w) < 6 for g in got)]
            print('%-26s 期待した倍音 %s -> 検出 %s' % ('', want, hit))
        # ループの継ぎ目。信号の実効値と比べても意味がない。帯域の広い
        # 雑音は隣り合う標本どうしでも大きく変わるので、比べる相手は
        # 「普通の隣接標本の差」。これが 1 に近ければ継ぎ目は見えていない。
        step = abs(x[-1] - x[0])
        typ = np.sqrt(np.mean(np.diff(x) ** 2))
        print('%-26s ループ継ぎ目の段差 %.5f / 隣接標本の差 %.5f = %.1f 倍'
              % ('', step, typ, step / max(typ, 1e-12)))

    print()
    print('=== 足音の作り ===')
    for name in ('src_stepL.wav', 'src_stepR.wav'):
        p = os.path.join(AUD, name)
        if not os.path.exists(p):
            print('  %-14s (無い)' % name)
            continue
        x, sr = read(p)
        # 包絡。1 歩に「かかと」と「蹴り出し」の 2 つ山があるか
        env = np.abs(signal.hilbert(x))
        w = int(0.018 * sr)
        env = np.convolve(env, np.ones(w) / w, mode='same')
        # 山の数を数えると減衰中の揺らぎを拾う。包絡そのものを 20ms 刻みで
        # 出したほうが確か。かかとの山、いったん落ちて、蹴り出しの山、が見える。
        bins = [env[int(t * sr):int((t + 0.02) * sr)].max()
                for t in np.arange(0, len(x) / sr - 0.02, 0.02)]
        top = max(bins)
        prof = ''.join('%d' % min(9, int(round(v / top * 9))) for v in bins)
        # 立ち上がり: 頂点の 10% から頂点まで
        ti = int(np.argmax(env))
        st = ti
        while st > 0 and env[st] > env[ti] * 0.1:
            st -= 1
        # 蹴り出し: 80ms より後で一番強いところ
        after = env[int(0.08 * sr):]
        toe = (int(np.argmax(after)) + int(0.08 * sr)) / sr * 1000
        print('  %-14s 立ち上がり %.1f ms  かかと %.0f ms  蹴り出し %.0f ms  '
              'rms %.4f  長さ %.0f ms'
              % (name, (ti - st) / sr * 1000, ti / sr * 1000, toe,
                 x.std(), len(x) / sr * 1000))
        print('%-16s 包絡 (20ms 刻み, 0-9): %s' % ('', prof))

    print()
    print('=== 部屋のインパルス応答 ===')
    print('%-8s %8s %8s %8s %8s   %s' %
          ('場所', 'RT60', 'RT60(500)', 'RT60(4k)', 'hf比', '初期反射のピーク (ms)'))
    for key in ('tight', 'room', 'open'):
        p = os.path.join(AUD, 'ir_%s.wav' % key)
        if not os.path.exists(p):
            print('  ir_%s.wav が無い' % key)
            continue
        h, sr = read(p)
        t_all, _ = rt60(h, sr)
        t_500, _ = rt60(h, sr, 355, 710)
        t_4k, _ = rt60(h, sr, 2800, 5600)
        pk = early_peaks(h, sr)
        pks = ' '.join('%.0f' % v for v in pk[:9])
        ratio = (t_4k / t_500) if (t_500 and t_500 == t_500 and t_500 > 0) else float('nan')
        print('%-8s %8.3f %8.3f %8.3f %8.2f   %s' % (key, t_all, t_500, t_4k, ratio, pks))

    print()
    print('=== テープの帯域 (平坦な雑音の前後から) ===')
    pd_, pw_ = os.path.join(AUD, 'tape_dry.wav'), os.path.join(AUD, 'tape_wet.wav')
    if os.path.exists(pd_) and os.path.exists(pw_):
        dry, sr = read(pd_)
        wet, _ = read(pw_)
        n = min(len(dry), len(wet))
        h, f, f3, f20, lo3 = tape_response(dry[:n], wet[:n], sr)
        print('  低域 -3dB %s Hz / 高域 -3dB %s Hz / -20dB %s Hz'
              % ('%.0f' % lo3 if lo3 else '-',
                 '%.0f' % f3 if f3 else '-',
                 '%.0f' % f20 if f20 else '-'))
        for probe in (100, 200, 1000, 4000, 8000, 12000):
            i = int(np.argmin(np.abs(f - probe)))
            print('    %6d Hz  %+6.1f dB' % (probe, h[i]))
    else:
        print('  tape_dry / tape_wet が無い')

    print()
    print('=== 実行中に録った音 (live.wav) ===')
    lp = os.path.join(AUD, 'live.wav')
    if os.path.exists(lp):
        x, sr = read(lp)
        print('  %.2f s  rms %.4f  peak %.3f' % (len(x) / sr, x.std(), np.abs(x).max()))
        pk = spectrum_peaks(x, sr, top=8, fmin=40, fmax=2000)
        print('  強い成分: ' + '  '.join('%.0fHz %+.0fdB' % (f, d) for f, d in pk))
        got = [f for f, _ in pk]
        for want in (120, 240, 360):
            hit = any(abs(g - want) < 8 for g in got)
            print('    %4d Hz (安定器の%s倍音): %s' % (want, want // 120, 'あり' if hit else 'なし'))
        f, p = welch_db(x, sr)
        ref = p[(f >= 300) & (f <= 1500)].mean()
        hi = [fr for fr, v in zip(f, p) if fr > 1500 and v - ref < -20]
        print('  高域 -20dB: %s Hz (テープの帯域制限が掛かっている)'
              % ('%.0f' % hi[0] if hi else '検出なし'))
    else:
        print('  live.wav が無い')

    print()
    print('=== 通した音 ===')
    for key in ('tight', 'room', 'open'):
        p = os.path.join(AUD, 'demo_%s.wav' % key)
        if not os.path.exists(p):
            continue
        x, sr = read(p)
        print('  demo_%-6s rms %.4f  peak %.3f' % (key, x.std(), np.abs(x).max()))
    return 0


if __name__ == '__main__':
    sys.exit(main())
