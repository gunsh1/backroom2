# -*- coding: utf-8 -*-
"""参照画像と自分のレンダを同じ指標で測って並べる。

目測をやめるための道具。色を合わせる作業では、見た目の印象は
あてにならない（実際に壁の B/R を 0.394 と目測していたが実測は 0.078）。

    python tools/compare-reference.py
"""
import os
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REF = os.path.join(ROOT, 'artifacts', 'reference', 'Backrooms_1.jpg')
MINE = os.path.join(ROOT, 'artifacts', 'shots', '01_hero.png')
W = np.array([.2126, .7152, .0722])


def lin(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def load(p):
    return np.asarray(Image.open(p).convert('RGB')).astype(np.float32) / 255.0


def patch(img, x, y, r):
    v = img[y - r:y + r, x - r:x + r].reshape(-1, 3).mean(0)
    L = lin(v)
    return v, L, float(L @ W)


def stats(img):
    Y = lin(img) @ W
    Ys = np.sort(Y.ravel())
    n = len(Ys)
    mx, mn = img.max(-1), img.min(-1)
    sat = np.where(mx > .02, (mx - mn) / np.maximum(mx, 1e-6), 0)
    return dict(med=Ys[n // 2], p1=Ys[int(n * .01)], p5=Ys[int(n * .05)],
                p95=Ys[int(n * .95)], p99=Ys[int(n * .99)],
                sat=float(sat.mean()), clip=float((img >= 0.995).all(-1).mean() * 100))


# 参照の測点（1300x731）と、こちらの測点（1920x1080）で同じ面を狙う
REF_PTS = {
    '壁':        (450, 390, 25),
    'カーペット':  (750, 520, 20),
    '天井タイル':  (760, 120, 10),
    '幅木':      (1150, 553, 4),
}
MY_PTS = {
    '壁':        (400, 560, 14),
    'カーペット':  (600, 900, 14),
    '天井タイル':  (1500, 180, 10),   # 器具から離れた素のタイル
    '幅木':      None,      # 縦スキャンで探す
}

ref, mine = load(REF), load(MINE)

print('=' * 74)
print('%-11s %-9s %-9s %-9s %-9s %-9s' % ('面', 'Y 参照', 'Y 自分', 'R/G 参照', 'R/G 自分', 'B/R 参照 / 自分'))
print('-' * 74)
for name, (x, y, r) in REF_PTS.items():
    rv, rL, rY = patch(ref, x, y, r)
    if MY_PTS[name] is None:
        col = mine[700:820, 380:420].mean(1)
        cY = lin(col) @ W
        i = int(np.argmin(cY))
        mv, mL, mY = col[i], lin(col[i]), float(cY[i])
    else:
        mx_, my_, mr = MY_PTS[name]
        mv, mL, mY = patch(mine, mx_, my_, mr)
    print('%-11s %-9.3f %-9.3f %-9.3f %-9.3f %.3f / %.3f' % (
        name, rY, mY, rL[0] / rL[1], mL[0] / mL[1], rL[2] / rL[0], mL[2] / mL[0]))
    print('%-11s   参照 sRGB %.3f/%.3f/%.3f    自分 sRGB %.3f/%.3f/%.3f' % ('', *rv, *mv))

print('-' * 74)
a, b = stats(ref), stats(mine)
for k, label in [('med', '中央値 Y'), ('p5', 'p5'), ('p95', 'p95'),
                 ('sat', '平均彩度'), ('clip', '白飛び %')]:
    print('%-11s 参照 %-9.3f 自分 %-9.3f  差 %+.3f' % (label, a[k], b[k], b[k] - a[k]))
print('%-11s 参照 %-9.1f 自分 %-9.1f' % ('p95/p5', a['p95'] / a['p5'], b['p95'] / b['p5']))
print('=' * 74)
