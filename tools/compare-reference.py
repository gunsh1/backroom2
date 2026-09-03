# -*- coding: utf-8 -*-
"""参照画像と自分のレンダを同じ指標で測って並べる。

目測をやめるための道具。色を合わせる作業では見た目の印象はあてにならない
（実際に壁の B/R を 0.394 と目測していたが実測は 0.078 だった）。

M2 で 1 部屋から 24x21.6m のフロアに広がったので、指標の性格が変わった。

  画面全体の階調分布（中央値・p5・p95・彩度・白飛び）
      構図に依らない。参照との比較で最も意味がある。とくに p95/p5 は
      「空間がどれだけ均等に照らされているか」を表し、1 部屋しか無かった
      ときはここが 11.2 と参照 5.2 から離れていた。

  面ごとの色比 R/G と B/R
      同じ照明の下では材質そのものの性質で、測る場所にほとんど依らない。

  面ごとの輝度 Y
      器具からの距離で 4 倍変わる。参照の測点とこちらの測点は同じ位置では
      ないので、参考値として出すに留める。

測点は間取りに依存するので、生成の種を変えたら取り直すこと。

    python tools/compare-reference.py
"""
import os
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REF = os.path.join(ROOT, 'artifacts', 'reference', 'Backrooms_1.jpg')
SHOTS = os.path.join(ROOT, 'artifacts', 'shots')
MAIN = os.path.join(SHOTS, '01_hero.png')
CEIL = os.path.join(SHOTS, '03_ceiling.png')
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
    return dict(med=Ys[n // 2], p5=Ys[int(n * .05)], p95=Ys[int(n * .95)],
                sat=float(sat.mean()), clip=float((img >= 0.995).all(-1).mean() * 100))


# 参照 (1300x731) の測点
REF_PTS = {
    '壁':        (450, 390, 25),
    'カーペット':  (750, 520, 20),
    '天井タイル':  (760, 120, 10),
    '幅木':      (1150, 553, 4),
}
# こちらの測点。(画像, x, y, 半径)。間取りを変えたら取り直す。
MY_PTS = {
    '壁':        ('main', 300, 600, 14),
    'カーペット':  ('main', 300, 980, 14),
    '天井タイル':  ('ceil', 900, 420, 18),
    '幅木':      ('scan', 0, 0, 0),      # 壁際を縦にスキャンして最も暗い行
}

ref = load(REF)
imgs = {'main': load(MAIN), 'ceil': load(CEIL)}

print('=' * 78)
print('%-11s %-17s %-17s %-17s' % ('面', 'Y 参照 / 自分', 'R/G 参照 / 自分', 'B/R 参照 / 自分'))
print('-' * 78)
for name, (x, y, r) in REF_PTS.items():
    rv, rL, rY = patch(ref, x, y, r)
    which, mx_, my_, mr = MY_PTS[name]
    if which == 'scan':
        col = imgs['main'][780:900, 280:320].mean(1)
        cY = lin(col) @ W
        i = int(np.argmin(cY))
        mv, mL, mY = col[i], lin(col[i]), float(cY[i])
    else:
        mv, mL, mY = patch(imgs[which], mx_, my_, mr)
    print('%-11s %-17s %-17s %-17s' % (
        name,
        '%.3f / %.3f' % (rY, mY),
        '%.3f / %.3f' % (rL[0] / rL[1], mL[0] / mL[1]),
        '%.3f / %.3f' % (rL[2] / rL[0], mL[2] / mL[0])))

print('-' * 78)
print('画面全体の階調分布 (構図に依らない。ここが本命)')
a, b = stats(ref), stats(imgs['main'])
for k, label in [('med', '中央値 Y'), ('p5', 'p5'), ('p95', 'p95'),
                 ('sat', '平均彩度'), ('clip', '白飛び %')]:
    print('  %-11s 参照 %-9.3f 自分 %-9.3f  差 %+.3f' % (label, a[k], b[k], b[k] - a[k]))
print('  %-11s 参照 %-9.1f 自分 %-9.1f  差 %+.1f'
      % ('p95/p5', a['p95'] / a['p5'], b['p95'] / b['p5'],
         b['p95'] / b['p5'] - a['p95'] / a['p5']))
print('=' * 78)
