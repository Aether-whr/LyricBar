"""生成 LyricBar 的 app.ico（多尺寸）。

图形：深色圆角方块 + 三条渐短的高亮横条（歌词行），右下角一个音符点。
小尺寸（16/24）只保留两条横条，避免糊成一团。
"""
from PIL import Image, ImageDraw

ACCENT = (76, 194, 255, 255)
ACCENT_DIM = (76, 194, 255, 120)
BG_TOP = (28, 34, 44, 255)
BG_BOTTOM = (12, 15, 20, 255)
SIZES = [16, 24, 32, 48, 64, 128, 256]


def rounded_mask(size: int, radius: float) -> Image.Image:
    mask = Image.new("L", (size * 4, size * 4), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([0, 0, size * 4 - 1, size * 4 - 1], radius=radius * 4, fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def render(size: int) -> Image.Image:
    s = size * 8  # 先画大图再缩小，边缘更干净
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # 背景：竖向渐变
    grad = Image.new("RGBA", (1, s))
    for y in range(s):
        t = y / max(1, s - 1)
        grad.putpixel(
            (0, y),
            tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(4)),
        )
    grad = grad.resize((s, s))
    img.paste(grad, (0, 0), rounded_mask(size, size * 0.22).resize((s, s), Image.LANCZOS))

    d = ImageDraw.Draw(img)

    if size < 32:
        # 16/24 px：只画两条粗横条，占满可用面积，否则缩下来就糊成一团
        pad = s * 0.17
        bar_h = s * 0.17
        gap = s * 0.15
        x0 = pad
        for i, w in enumerate((0.68, 0.46)):
            x1 = x0 + (s - 2 * pad) * w
            y = s * 0.32 + i * (bar_h + gap)
            d.rounded_rectangle([x0, y, x1, y + bar_h], radius=bar_h / 2,
                                fill=ACCENT if i == 0 else ACCENT_DIM)
        return img.resize((size, size), Image.LANCZOS)

    pad = s * 0.20
    bar_h = s * 0.105
    gap = s * 0.075
    x0 = pad
    widths = [0.60, 0.46, 0.32] if size >= 32 else [0.60, 0.42]
    y = s * 0.26

    for i, w in enumerate(widths):
        x1 = x0 + (s - 2 * pad) * w
        color = ACCENT if i == 0 else ACCENT_DIM
        d.rounded_rectangle([x0, y, x1, y + bar_h], radius=bar_h / 2, fill=color)
        y += bar_h + gap

    # 右下角音符点，让图标在托盘里也能认出是「播放相关」
    r = s * 0.085
    cx, cy = s * 0.74, s * 0.755
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=ACCENT)
    d.rounded_rectangle(
        [cx + r * 0.55, cy - r * 3.1, cx + r * 1.15, cy],
        radius=r * 0.3,
        fill=ACCENT,
    )

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    frames = [render(n) for n in SIZES]
    # 关键：把每个尺寸单独画好的图都塞进去。只传一张 256 让 PIL 自己缩，
    # 16/24 px 会把音符缩成一团糊——所以必须 append_images。
    frames[-1].save(
        "app.ico",
        format="ICO",
        sizes=[(n, n) for n in SIZES],
        append_images=frames[:-1],
    )
    frames[-1].save("app-256.png")
    print("wrote app.ico")


if __name__ == "__main__":
    main()
