# -*- coding: utf-8 -*-
"""
「键盘 + 鼠标」简笔图标 —— 生成器。

核心思路（避免前几版"糊成一团"的教训）
--------------------------------------
前两版失败原因：键帽用**实心小方块**，缩到小尺寸后彼此粘连，整体变成黑块。

本版改为**负空间构图**：
  - 键盘外壳 = 实心圆角矩形（黑）
  - 键帽 = 在外壳上"挖"出的白色圆角方格（即负空间）
这样键与键之间由外壳本体自然分隔，对比强、不易粘连，
即使缩到 16px，也能读成"一个带格子的方块"= 键盘。

鼠标同理：实心椭圆 + 挖白，得到轮廓感。

绘制基准 1024，输出六档 ico（256/128/64/48/32/16）。

用法：py tools/make_icon.py <输出.ico>
"""
import sys
from PIL import Image, ImageDraw

BASE = 1024

INK = (0, 0, 0, 255)
CLEAR = (0, 0, 0, 0)


def draw_keyboard(d, x, y, w, h):
    """实心键盘外壳，内部挖白色键帽格。"""
    d.rounded_rectangle([x, y, x + w, y + h], radius=44, fill=INK)

    # 内缩边距（决定外壳"厚度"）
    mx = 66
    my = 66
    ix0, iy0 = x + mx, y + my
    ix1, iy1 = x + w - mx, y + h - my

    rows, cols = 3, 5
    gap = 32

    kw = (ix1 - ix0 - gap * (cols - 1)) / cols
    kh = (iy1 - iy0 - gap * (rows - 1)) / rows

    for r_i in range(rows):
        for c_i in range(cols):
            # 末行中间三格留给空格键
            if r_i == rows - 1 and 1 <= c_i <= 3:
                continue
            kx = ix0 + c_i * (kw + gap)
            ky = iy0 + r_i * (kh + gap)
            d.rounded_rectangle(
                [kx, ky, kx + kw, ky + kh], radius=18, fill=CLEAR
            )

    # 空格键：末行中间三格合并
    sx = ix0 + 1 * (kw + gap)
    sy = iy0 + (rows - 1) * (kh + gap)
    sw = kw * 3 + gap * 2
    d.rounded_rectangle([sx, sy, sx + sw, sy + kh], radius=18, fill=CLEAR)


def draw_mouse(d, x, y, w, h):
    """鼠标：实心圆角矩形本体 + 挖出「上半部横缝」（滚轮位）。

    放弃椭圆+中缝的写法——那条路子在小尺寸下退化成圈+竖线的怪形状。
    改用图标设计中最常见的鼠标简笔形态：
      一个上圆下方的胶囊体，靠上部挖一条横向短缝表示滚轮/左右键分界。
    该形态在小尺寸下仍能读出"鼠标"。
    """
    r = w / 2  # 顶部半圆半径

    # 本体：胶囊（上方半圆 + 下方圆角）—— 用 rounded_rectangle 近似
    d.rounded_rectangle([x, y, x + w, y + h], radius=int(r), fill=INK)

    # 挖滚轮：中缝上端的一个短横条（比缝宽），这是鼠标最靠前的部件
    gap_w = w * 0.42
    gap_h = h * 0.052
    gx = x + (w - gap_w) / 2
    gy = y + h * 0.255
    d.rounded_rectangle(
        [gx, gy, gx + gap_w, gy + gap_h], radius=int(gap_h / 2), fill=CLEAR
    )

    # 挖中缝（左右键分界）：滚轮下方往下延伸一小段
    seam_w = w * 0.070
    sx = x + (w - seam_w) / 2
    d.rounded_rectangle(
        [sx, gy + gap_h, sx + seam_w, y + h * 0.455],
        radius=int(seam_w / 2),
        fill=CLEAR,
    )


def render():
    img = Image.new("RGBA", (BASE, BASE), CLEAR)
    d = ImageDraw.Draw(img)

    draw_keyboard(d, 56, 560, 574, 320)
    draw_mouse(d, 664, 296, 300, 476)

    return img


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "AppIcon.ico"
    base = render()

    sizes = [256, 128, 64, 48, 32, 16]
    base.save(out, format="ICO", sizes=[(s, s) for s in sizes])

    stem = out.rsplit(".", 1)[0]
    base.resize((256, 256), Image.LANCZOS).save(stem + "_p256.png")
    base.resize((48, 48), Image.LANCZOS).resize((288, 288), Image.NEAREST).save(
        stem + "_p48.png"
    )
    print("written:", out)


if __name__ == "__main__":
    main()
