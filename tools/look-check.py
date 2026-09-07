"""キャプチャの「見え方」を数値にする。

彩度・色相・白飛び・水平方向の解像感を測る。VHS の掛かり具合は
言葉では前後を比べられないので、同じ構図の前後を同じ指標で出す。
    python tools/look-check.py <before_dir> <after_dir> [名前...]
"""
import sys, os
import numpy as np
from PIL import Image


def load(path):
    im = np.asarray(Image.open(path).convert("RGB"), dtype=np.float32) / 255.0
    # ピラーボックスの黒帯は測定から外す。両脇が真っ黒なので平均を引きずる。
    col = im.reshape(-1, im.shape[1], 3).mean(axis=(0, 2))
    keep = col > 0.02
    if keep.any():
        x0, x1 = np.argmax(keep), len(keep) - np.argmax(keep[::-1])
        im = im[:, x0:x1]
    return im


def stats(im):
    r, g, b = im[..., 0], im[..., 1], im[..., 2]
    mx, mn = im.max(-1), im.min(-1)
    sat = np.where(mx > 1e-4, (mx - mn) / np.maximum(mx, 1e-4), 0.0)
    y = 0.2126 * r + 0.7152 * g + 0.0722 * b
    # 水平方向の隣接差。帯域を落とすとここが小さくなる
    dx = np.abs(np.diff(y, axis=1)).mean()
    # 白飛び: 3 チャンネルとも 0.96 以上
    blown = float((im.min(-1) > 0.96).mean())
    return dict(Y=float(y.mean()), sat=float(sat.mean()),
                R=float(r.mean()), G=float(g.mean()), B=float(b.mean()),
                dx=float(dx), blown=blown * 100.0)


def main():
    a, b = sys.argv[1], sys.argv[2]
    names = sys.argv[3:] or sorted(
        n for n in os.listdir(a) if n.endswith(".png") and os.path.exists(os.path.join(b, n)))
    print(f"{'':14s} {'Y':>6s} {'彩度':>6s} {'G/R':>6s} {'B/R':>6s} "
          f"{'横の差':>7s} {'白飛び%':>7s}")
    acc = {}
    for n in names:
        for tag, d in (("前", a), ("後", b)):
            p = os.path.join(d, n)
            if not os.path.exists(p):
                continue
            s = stats(load(p))
            acc.setdefault(tag, []).append(s)
            print(f"{n[:-4]:>11s} {tag}  {s['Y']:6.3f} {s['sat']:6.3f} "
                  f"{s['G']/max(s['R'],1e-6):6.3f} {s['B']/max(s['R'],1e-6):6.3f} "
                  f"{s['dx']:7.4f} {s['blown']:7.3f}")
    print()
    for tag in ("前", "後"):
        if tag not in acc:
            continue
        m = {k: np.mean([s[k] for s in acc[tag]]) for k in acc[tag][0]}
        print(f"平均 {tag}   Y {m['Y']:.3f}  彩度 {m['sat']:.3f}  "
              f"G/R {m['G']/m['R']:.3f}  B/R {m['B']/m['R']:.3f}  "
              f"横の差 {m['dx']:.4f}  白飛び {m['blown']:.3f}%")


main()
