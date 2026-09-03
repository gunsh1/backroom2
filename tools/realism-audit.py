# -*- coding: utf-8 -*-
"""「CG に見える」を数字にする。

見た目の印象で列挙すると必ず外すので、実写と CG が構造的に違う所を
測れる形にして、参照画像と自分のレンダを同じ物差しに乗せる。

  ノイズの性質      実センサのノイズは光量の平方根に比例する (ショットノイズ)。
                    一様なフィルムグレインを足しただけだと勾配が 0 に近くなる。
  テクスチャの周期  自己相関に鋭いピークが立てばタイリングが見えている。
  微小ハイライト    実在の室内には埃・傷・繊維の反射で小さな輝点が無数にある。
  直線の完全さ      実在の建築に定規のような直線は無い。長い稜線に直線を当てて残差を測る。
  周辺の解像感      実レンズは周辺で必ず眠くなる。中央と四隅の勾配エネルギーを比べる。
  低周波の色ムラ    実在の壁は場所ごとに色が違う。平坦な面を 8x8 に潰した分散。
  面のスペクトル    カーペットの空間周波数。白色雑音に近いほど「ノイズを貼った床」。

    python tools/realism-audit.py
"""
import os
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REF = os.path.join(ROOT, 'artifacts', 'reference', 'Backrooms_1.jpg')
SHOTS = os.path.join(ROOT, 'artifacts', 'shots')
W = np.array([.2126, .7152, .0722])


def lin(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def load(p):
    return np.asarray(Image.open(p).convert('RGB'), np.float32) / 255.0


def luma(a):
    return lin(a) @ W


def blocks(Y, n):
    """n x n のブロックに切って (平均, 標準偏差) を返す。"""
    h, w = Y.shape
    bh, bw = h // n * n, w // n * n
    b = Y[:bh, :bw].reshape(bh // n, n, bw // n, n).transpose(0, 2, 1, 3).reshape(-1, n * n)
    return b.mean(1), b.std(1)


# --------------------------------------------------------------- 1 ノイズ
def noise_law(Y, label):
    """平坦なブロックだけを残し、平均輝度と標準偏差の関係を両対数で回帰する。

    ショットノイズなら sigma ∝ L^0.5。一様なグレインを足しただけなら 0 に近い。
    """
    m, s = blocks(Y, 8)
    # 平坦さ: 同じ場所を 16 画素ブロックで見たときの分散が小さいものだけ
    keep = (m > 0.02) & (s > 0) & (s < m * 0.25)
    m, s = m[keep], s[keep]
    if len(m) < 200:
        return None
    # 輝度で 12 分位に分け、各分位の中央値どうしで回帰する（外れ値に強い）
    q = np.quantile(m, np.linspace(0, 1, 13))
    xs, ys = [], []
    for a, b in zip(q[:-1], q[1:]):
        sel = (m >= a) & (m < b)
        if sel.sum() < 20:
            continue
        xs.append(np.median(m[sel]))
        ys.append(np.median(s[sel]))
    if len(xs) < 5:
        return None
    k = np.polyfit(np.log(xs), np.log(ys), 1)[0]
    return k, float(np.median(ys)), len(m)


# ------------------------------------------------- 2 テクスチャの繰り返し
def repeat_peak(a, box, nb=3):
    """矩形を切り出し、水平方向の自己相関のピークを大きい順に返す。

    細かいエンボスの周期 (10px 前後) と、テクスチャが 1 周する周期 (数百 px) の
    両方を見たいので、遅れは幅の 4/5 まで探す。
    """
    x0, y0, x1, y1 = box
    c = luma(a[y0:y1, x0:x1])
    c = c - c.mean(1, keepdims=True)
    n = c.shape[1]
    f = np.fft.rfft(c, n * 2, axis=1)
    ac = np.fft.irfft(f * np.conj(f), axis=1)[:, :n].mean(0)
    ac /= max(ac[0], 1e-9)
    lo = max(6, n // 60)
    hi = int(n * 0.8)
    seg = ac[lo:hi].copy()
    out = []
    for _ in range(nb):
        i = int(np.argmax(seg))
        out.append((i + lo, float(seg[i])))
        a0, b0 = max(0, i - 6), min(len(seg), i + 7)
        seg[a0:b0] = -1
    return out


# --------------------------------------------------- 3 微小なハイライト
def speckles(a):
    """周囲より 1.6 倍以上明るく、面積 40 画素以下の孤立点を数える。"""
    Y = luma(a)
    k = 9
    pad = np.pad(Y, k // 2, mode='edge')
    win = np.lib.stride_tricks.sliding_window_view(pad, (k, k))
    med = np.median(win.reshape(Y.shape[0], Y.shape[1], -1), axis=2)
    hot = (Y > med * 1.6) & (Y > 0.05)
    # 連結成分を数えず、孤立度で近似する（8 近傍のうち 5 個以上が非 hot）
    nb = np.zeros_like(hot, np.int16)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dx == 0 and dy == 0:
                continue
            nb += np.roll(np.roll(hot, dy, 0), dx, 1)
    isolated = hot & (nb <= 4)
    return int(isolated.sum()), float(hot.mean() * 100)


# ------------------------------------------------------- 4 直線の完全さ
def edge_straightness(a, box, axis='h'):
    """帯の中で各列（行）の最大勾配位置を拾い、直線を当てた残差 RMS。"""
    x0, y0, x1, y1 = box
    Y = luma(a[y0:y1, x0:x1])
    if axis == 'h':
        g = np.abs(np.diff(Y, axis=0))
        pos = np.argmax(g, axis=0).astype(np.float64)
        t = np.arange(len(pos), dtype=np.float64)
    else:
        g = np.abs(np.diff(Y, axis=1))
        pos = np.argmax(g, axis=1).astype(np.float64)
        t = np.arange(len(pos), dtype=np.float64)
    good = np.abs(pos - np.median(pos)) < 12
    if good.sum() < 20:
        return None
    p = np.polyfit(t[good], pos[good], 1)
    res = pos[good] - np.polyval(p, t[good])
    return float(np.sqrt((res ** 2).mean()))


# --------------------------------------------------- 5 周辺の解像感低下
def sharpness_falloff(a):
    Y = luma(a)
    h, w = Y.shape
    def energy(y0, y1, x0, x1):
        p = Y[y0:y1, x0:x1]
        gx = np.diff(p, axis=1)
        gy = np.diff(p, axis=0)
        return float((gx ** 2).mean() + (gy ** 2).mean())
    ch, cw = h // 4, w // 4
    center = energy(h // 2 - ch // 2, h // 2 + ch // 2, w // 2 - cw // 2, w // 2 + cw // 2)
    corners = np.mean([energy(0, ch, 0, cw), energy(0, ch, w - cw, w),
                       energy(h - ch, h, 0, cw), energy(h - ch, h, w - cw, w)])
    return center, corners, corners / max(center, 1e-9)


# ----------------------------------------------------- 6 低周波の色ムラ
def lowfreq_drift(a, box):
    x0, y0, x1, y1 = box
    p = a[y0:y1, x0:x1]
    im = Image.fromarray((np.clip(p, 0, 1) * 255).astype(np.uint8)).resize((8, 8), Image.BOX)
    q = lin(np.asarray(im, np.float32) / 255.0)
    Yq = q @ W
    return float(Yq.std() / max(Yq.mean(), 1e-6)), float(np.std(q[..., 0] / np.maximum(q[..., 1], 1e-6)))


# ------------------------------------------------------- 7 面のスペクトル
def spectrum_slope(a, box):
    """放射平均パワースペクトルの傾き。白色雑音なら 0 に近い。"""
    x0, y0, x1, y1 = box
    p = luma(a[y0:y1, x0:x1])
    p = p - p.mean()
    n = min(p.shape)
    p = p[:n, :n] * np.hanning(n)[:, None] * np.hanning(n)[None, :]
    P = np.abs(np.fft.fftshift(np.fft.fft2(p))) ** 2
    cy, cx = n // 2, n // 2
    yy, xx = np.mgrid[0:n, 0:n]
    r = np.hypot(yy - cy, xx - cx).astype(int)
    prof = np.bincount(r.ravel(), P.ravel()) / np.maximum(np.bincount(r.ravel()), 1)
    lo, hi = 3, n // 3
    k = np.polyfit(np.log(np.arange(lo, hi)), np.log(np.maximum(prof[lo:hi], 1e-12)), 1)[0]
    return float(k)


# ----------------------------------------------------- 8 色の多様性
def color_diversity(a):
    """32^3 の箱に落として、実際に使われている色の数と色相の広がりを見る。

    実写は照明と材質と汚れの積で色が連続的に散る。CG は材質の数だけしか
    色が無く、ティントで作った色は 1 本の線の上に並ぶ。
    """
    q = (np.clip(a, 0, 1) * 31).astype(np.int32)
    idx = q[..., 0] * 1024 + q[..., 1] * 32 + q[..., 2]
    used = len(np.unique(idx))
    mx, mn = a.max(-1), a.min(-1)
    sat = np.where(mx > .04, (mx - mn) / np.maximum(mx, 1e-6), 0)
    # 色相（最大チャンネルの位置ではなく、正規化した色度の分散で見る）
    ssum = a.sum(-1, keepdims=True) + 1e-6
    chroma = a / ssum
    m = mx > .04
    spread = float(np.std(chroma[m][:, 0]) + np.std(chroma[m][:, 2]))
    return used, float(sat[m].mean()), spread


# ------------------------------------------- 9 床際の接地の暗さ
def contact_darkening(a, x, y0, y1):
    """幅木の足元から床へ縦に走査し、接地部で暗くなるかを見る。

    実在の室内では、毛足の際にほこりが溜まり接触の遮蔽も効くので、
    取り合いに必ず数 % の落ち込みがある。無ければ物が床から浮いて見える。
    """
    prof = luma(a[y0:y1, x - 6:x + 6]).mean(1)
    if len(prof) < 12:
        return None
    lo = float(prof.min())
    base = float(np.median(prof[-len(prof) // 3:]))
    return (1.0 - lo / max(base, 1e-6)) * 100.0


# ================================================================= 実行
def report(path, label, boxes):
    a = load(path)
    Y = luma(a)
    h, w = Y.shape[:2]
    print('=' * 76)
    print('%s   %dx%d' % (label, w, h))
    print('-' * 76)

    nl = noise_law(Y)if False else noise_law(Y, label)
    if nl:
        print('  ノイズ  sigma ∝ L^%.2f   中央値 sigma %.4f   (実センサは 0.5 付近)' % (nl[0], nl[1]))

    sp, hot = speckles(a)
    print('  微小輝点  %6d 個 (%.4f %% が周囲比 1.6 倍以上)' % (sp, hot))

    c, k, ratio = sharpness_falloff(a)
    print('  解像感  中央 %.5f / 四隅 %.5f  四隅/中央 %.2f  (実レンズは 1 未満)' % (c, k, ratio))

    mn = a.reshape(-1, 3).min(0)
    print('  最小値  R %.3f  G %.3f  B %.3f   (実写は黒でも 0 にならない)' % tuple(mn))

    used, sat, spread = color_diversity(a)
    print('  色の数  32^3 の箱で %5d 色使用 (%.2f %%)   色度の広がり %.4f'
          % (used, used / 32768 * 100, spread))

    for name, box, kind in boxes:
        if kind == 'repeat':
            peaks = repeat_peak(a, box)
            txt = '  '.join('%dpx:%.2f' % pk for pk in peaks)
            print('  繰り返し [%-10s] %s  (自己相関 0.2 超で目に付く)' % (name, txt))
        elif kind == 'drift':
            d, rg = lowfreq_drift(a, box)
            print('  色ムラ  [%-10s] 低周波の輝度分散 %.4f  R/G のばらつき %.4f' % (name, d, rg))
        elif kind == 'spectrum':
            s = spectrum_slope(a, box)
            print('  周波数  [%-10s] スペクトル傾き %.2f  (白色雑音 = 0、実素材は -1 〜 -2)' % (name, s))
        elif kind == 'contact':
            v = contact_darkening(a, box[0], box[1], box[3])
            if v is not None:
                print('  接地  　[%-10s] 取り合いの落ち込み %.1f %%  (実写は 8-25 %%)' % (name, v))
        elif kind == 'edge':
            r = edge_straightness(a, box)
            if r is not None:
                print('  直線性  [%-10s] 残差 RMS %.2f px  (実写の建築は 1 px 前後)' % (name, r))


REF_BOXES = [
    ('壁',       (330, 300, 620, 470), 'repeat'),
    ('壁',       (330, 300, 620, 470), 'drift'),
    ('カーペット', (600, 470, 900, 600), 'spectrum'),
    ('幅木上端',  (980, 520, 1240, 560), 'edge'),
    ('床際',     (1150, 545, 0, 585), 'contact'),
]
MY_BOXES = [
    ('壁',       (60, 380, 480, 700), 'repeat'),
    ('壁',       (60, 380, 480, 700), 'drift'),
    ('カーペット', (140, 860, 520, 1040), 'spectrum'),
    ('幅木上端',  (120, 800, 500, 850), 'edge'),
    ('床際',     (300, 830, 0, 900), 'contact'),
]

report(REF, '参照  Backrooms_1.jpg', REF_BOXES)
report(os.path.join(SHOTS, '01_hero.png'), '自分  01_hero.png', MY_BOXES)
print('=' * 76)

# 近景の材質だけを見るカット
CLOSE = [
    ('カーペット', (200, 700, 900, 1050), 'spectrum'),
    ('壁',        (100, 60, 560, 420), 'repeat'),
    ('幅木上端',   (150, 430, 600, 520), 'edge'),
    ('床際',      (400, 600, 0, 700), 'contact'),
]
report(os.path.join(SHOTS, '06_detail.png'), '自分  06_detail.png (近景)', CLOSE)
print('=' * 76)
