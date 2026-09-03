# -*- coding: utf-8 -*-
"""天井が実物の納まりになっているかを画像から測る。

見た目で「グリッドが太い」と言っても水掛け論になるので数字で出す。

  グリッドの占有率
      水平走査線 1 本の上で、局所中央値より暗い画素が占める割合。
      実物の 15/16" T バーは見付 24mm、タイルの割付は 600mm なので、
      タイル面がフランジと面一 (露出 1.6mm) なら 24/600 = 4.0% になる。
      タイル面を 14mm 引っ込めていたときは、下から見るとバーの側面まで
      見えるので、視線が斜めなほどこれが太る。

  タイルとバーの輝度比
      実測で T バーはタイルの 0.4-0.5 段下。ここが 1 段以上開くと
      格天井のような影の溝ができている。

  タイル面の分散
      1 枚ごとの色ムラと亀裂。完全に平坦なら 0 に近い = 「白い板」。

    python tools/ceiling-check.py [画像 ...]
"""
import os
import sys
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W = np.array([.2126, .7152, .0722])


def lin(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def luma(p):
    a = np.asarray(Image.open(p).convert('RGB'), np.float32) / 255.0
    return lin(a) @ W


def running_median(v, half):
    """局所中央値。奥行きで露出が変わるので固定閾値は使えない。"""
    n = len(v)
    out = np.empty(n, np.float32)
    for i in range(n):
        a, b = max(0, i - half), min(n, i + half + 1)
        out[i] = np.median(v[a:b])
    return out


def duty(row, half=60, k=0.90):
    med = running_median(row, half)
    dark = row < med * k
    return float(dark.mean()) * 100.0, med


def analyse(path, bands=(0.10, 0.18, 0.26)):
    Y = luma(path)
    h, w = Y.shape
    print('--- %s  (%dx%d)' % (os.path.basename(path), w, h))
    duties = []
    for f in bands:
        row = Y[int(h * f)]
        d, med = duty(row)
        duties.append(d)
        # バー = 局所中央値より暗い画素、タイル = 明るい画素
        m = running_median(row, 60)
        bar = row[row < m * 0.90]
        tile = row[row > m * 1.02]
        stops = (np.log2(tile.mean() / bar.mean()) if len(bar) and len(tile) else float('nan'))
        print('    y=%4d  グリッド占有率 %5.1f %%   タイル/バー %.2f 段   タイル平均 %.3f'
              % (int(h * f), d, stops, tile.mean() if len(tile) else 0))
    # 天井帯のなかのタイル面の細かい分散（12x12 のブロック標準偏差の中央値）
    top = Y[int(h * 0.06):int(h * 0.30)]
    bh, bw = top.shape[0] // 12 * 12, top.shape[1] // 12 * 12
    blk = top[:bh, :bw].reshape(bh // 12, 12, bw // 12, 12).transpose(0, 2, 1, 3).reshape(-1, 144)
    rel = blk.std(1) / np.maximum(blk.mean(1), 1e-4)
    clip = float((np.asarray(Image.open(path).convert('RGB')) >= 254).all(-1).mean() * 100)
    print('    グリッド占有率 平均 %.1f %%   面の相対分散(中央値) %.4f   白飛び %.2f %%'
          % (np.mean(duties), np.median(rel), clip))
    return np.mean(duties)


if __name__ == '__main__':
    args = sys.argv[1:] or [os.path.join(ROOT, 'artifacts', 'shots', '03_ceiling.png')]
    print('実物の 15/16 インチ T バー: 見付 24mm / 割付 600mm -> 幾何的な占有率 4.0 %')
    print('=' * 74)
    for a in args:
        analyse(a)
    print('=' * 74)
