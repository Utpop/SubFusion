"""把设计稿整理成仓库内的图标母版 assets/app-icon.png（512px，透明背景）。

设计稿本身是 1254px 的方形 PNG，四周留白较多；这里只做等比缩放，不做裁切，
裁边与留白交给 tools/make_icons.py（按 alpha 包围盒裁切后统一补边）。
"""
import os
from PIL import Image

SRC = r"C:/Users/LocalAcc/Downloads/SubFusion Video Editing Icon.png"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "app-icon.png")


def main():
    im = Image.open(SRC).convert("RGBA")
    size = 512
    im = im.resize((size, size), Image.LANCZOS)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    im.save(OUT, optimize=True)
    print(OUT, os.path.getsize(OUT), "bytes", im.size)


if __name__ == "__main__":
    main()
