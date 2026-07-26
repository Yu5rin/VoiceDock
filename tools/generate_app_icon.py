#!/usr/bin/env python3
"""
アプリアイコン (src/VoiceDock/app.ico) を生成する。

タスクトレイアイコン (src/VoiceDock/UI/IconFactory.cs) と同じマイク意匠・同じ 32x32
座標系で描画し、exe・ウィンドウ・タスクバーで見た目を統一する。
エクスプローラーのライト/ダーク両方で視認できるよう、ダークテーマ色の角丸プレートを敷く。

使い方: python3 tools/generate_app_icon.py
"""
from PIL import Image, ImageDraw

# IconFactory.cs と同じ 32x32 座標系で図形を定義する
BASE = 32
SS = 16  # スーパーサンプリング倍率（アンチエイリアス用）

GLYPH = (200, 200, 205, 255)   # 待機中のモノクロ色（IconFactory の Idle と同一）
PLATE = (27, 27, 31, 255)      # アプリのダークテーマ背景色 (#1B1B1F)
PLATE_EDGE = (60, 60, 68, 255) # ダークテーマの境界色 (#3C3C44)

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def draw_icon(size: int) -> Image.Image:
    """指定サイズのアイコン画像を 1 枚描画する。"""
    k = size * SS / BASE           # 32x32 座標系 → 実ピクセルへの倍率
    canvas = size * SS
    img = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # プレートとの間に余白を作るため、グリフを中心 (16,16) 基準で少し縮小する
    GLYPH_SCALE = 0.78

    def sc(v: float) -> float:
        """32x32 座標系の長さを実ピクセルへ変換する（グリフ縮小を含む）。"""
        return v * k * GLYPH_SCALE

    def gx(v: float) -> float:
        """32x32 座標系の座標を、中心基準で縮小して実ピクセルへ変換する。"""
        return (BASE / 2 + (v - BASE / 2) * GLYPH_SCALE) * k

    # 角丸プレート（背景）
    margin = 0.6 * k
    radius = BASE * 0.22 * k
    d.rounded_rectangle(
        [margin, margin, canvas - margin, canvas - margin],
        radius=radius, fill=PLATE, outline=PLATE_EDGE, width=max(1, int(0.6 * k)),
    )

    pen = sc(3.0)  # IconFactory の Pen 幅 3 と同じ

    # マイク本体（カプセル形）: rect(11,3,10,16) の角丸 = 半径 5
    d.rounded_rectangle([gx(11), gx(3), gx(21), gx(19)], radius=sc(5), fill=GLYPH)

    # マイクスタンドのアーチ: ellipse(7,8,18x16) を 20°→160°（GDI+/PIL とも時計回り）
    d.arc([gx(7), gx(8), gx(25), gx(24)], start=20, end=160, fill=GLYPH, width=int(pen))

    # 支柱と台座（丸キャップ相当の丸みを端点に付ける）
    d.line([gx(16), gx(24), gx(16), gx(28)], fill=GLYPH, width=int(pen))
    d.line([gx(11), gx(28), gx(21), gx(28)], fill=GLYPH, width=int(pen))
    r = pen / 2
    for x, y in ((16, 24), (16, 28), (11, 28), (21, 28)):
        d.ellipse([gx(x) - r, gx(y) - r, gx(x) + r, gx(y) + r], fill=GLYPH)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    images = [draw_icon(s) for s in SIZES]
    out = "src/VoiceDock/app.ico"
    images[-1].save(out, format="ICO", sizes=[(s, s) for s in SIZES],
                    append_images=images[:-1])
    print(f"wrote {out} ({', '.join(f'{s}x{s}' for s in SIZES)})")


if __name__ == "__main__":
    main()
