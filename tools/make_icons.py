"""从设计稿母版生成 SubFusion 的全部图标产物。

用法（主机 python，需 Pillow + numpy）：
    python tools/make_icons.py            # 重新生成 app.ico 与界面用 PNG
    python tools/make_icons.py --check    # 只检查现有产物

产物：
    app.ico                              10 个尺寸，供 <ApplicationIcon> 与 AppWindow.SetIcon 使用
    src/SubFusion.App/Assets/app-icon.png 64px，标题栏左侧的应用图标（ms-appx 引用）
    tools/icon-preview/*.png             人工核对用的预览（不入库）

设计要点：
  * 母版四周有大片透明留白，先按 alpha 包围盒裁切，再统一补 2% 边距，保证各尺寸居中。
  * 缩放用「预乘 alpha + LANCZOS」，避免透明边缘出现黑边/白边。
  * 16/20/24/32 额外做轻微饱和度/对比度补偿：母版偏浅，直接缩小后在任务栏会糊成浅色块。
  * ICO 容器手工拼装：16~64 用 DIB（含 AND 掩码），96/128/256 用 PNG 压缩，
    与资源管理器的取用习惯一致，体积也远小于全量 BMP。
"""
import argparse, os, struct, sys
import numpy as np
from PIL import Image, ImageEnhance
from io import BytesIO

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MASTER = os.path.join(ROOT, "assets", "app-icon.png")   # 512px 设计稿母版
ICO = os.path.join(ROOT, "app.ico")
UI_PNG = os.path.join(ROOT, "src", "SubFusion.App", "Assets", "app-icon.png")
PREVIEW_DIR = os.path.join(ROOT, "tools", "icon-preview")

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
PNG_SIZES = {96, 128, 256}
UI_PNG_SIZE = 64
MARGIN = 0.02
SMALL_ENHANCE = {16: (1.45, 1.10), 20: (1.35, 1.07), 24: (1.28, 1.05), 32: (1.20, 1.03)}


def load_master():
    if not os.path.exists(MASTER):
        raise SystemExit("缺少母版 %s：先运行 tools/prepare_master.py" % MASTER)
    im = Image.open(MASTER).convert("RGBA")
    bbox = im.getchannel("A").getbbox()
    if bbox:
        im = im.crop(bbox)
    side = max(im.size)
    pad = int(round(side * MARGIN))
    canvas = Image.new("RGBA", (side + 2 * pad, side + 2 * pad), (0, 0, 0, 0))
    canvas.paste(im, ((canvas.width - im.width) // 2, (canvas.height - im.height) // 2), im)
    return canvas


def premultiplied_resize(im, size):
    arr = np.asarray(im, dtype=np.float64)
    rgb, a = arr[..., :3], arr[..., 3:4] / 255.0
    pre = np.concatenate([(rgb * a).round().clip(0, 255), arr[..., 3:4]], axis=-1).astype(np.uint8)
    small = Image.fromarray(pre, "RGBA").resize((size, size), Image.LANCZOS)
    s = np.asarray(small, dtype=np.float64)
    alpha = s[..., 3:4]
    scale = np.where(alpha > 0, 255.0 / np.maximum(alpha, 1), 0)
    out = np.concatenate([(s[..., :3] * scale).round().clip(0, 255), alpha], axis=-1).astype(np.uint8)
    return Image.fromarray(out, "RGBA")


def enhance(im, sat, contrast):
    """只调不透明像素的颜色，alpha 原样保留。"""
    rgb = ImageEnhance.Contrast(ImageEnhance.Color(im.convert("RGB")).enhance(sat)).enhance(contrast)
    out = rgb.convert("RGBA")
    out.putalpha(im.getchannel("A"))
    return out


def build_frames():
    master = load_master()
    frames = []
    for s in SIZES:
        img = premultiplied_resize(master, s)
        if s in SMALL_ENHANCE:
            img = enhance(img, *SMALL_ENHANCE[s])
        frames.append(img)
    return master, frames


def dib_bytes(img):
    """32bpp 自上而下写倒序 BGRA + 全 0 的 AND 掩码（透明度交给 alpha 通道）。"""
    size = img.width
    px = np.asarray(img, dtype=np.uint8)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    bgra = px[..., [2, 1, 0, 3]][::-1].tobytes()
    mask = bytes((((size + 31) // 32) * 4) * size)
    return header + bgra + mask


def png_bytes(img):
    buf = BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(frames, path):
    blobs = [png_bytes(img) if img.width in PNG_SIZES else dib_bytes(img) for img in frames]
    offset = 6 + 16 * len(frames)
    out = [struct.pack("<HHH", 0, 1, len(frames))]
    for img, blob in zip(frames, blobs):
        out.append(struct.pack("<BBBBHHII",
                               img.width if img.width < 256 else 0,
                               img.height if img.height < 256 else 0,
                               0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    out.extend(blobs)
    with open(path, "wb") as f:
        f.write(b"".join(out))
    return os.path.getsize(path)


def write_outputs(master, frames):
    os.makedirs(os.path.dirname(UI_PNG), exist_ok=True)
    frames[SIZES.index(UI_PNG_SIZE)].save(UI_PNG, optimize=True)
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    for s, im in zip(SIZES, frames):
        im.save(os.path.join(PREVIEW_DIR, "app-%d.png" % s))
    frames[SIZES.index(16)].resize((256, 256), Image.NEAREST).save(os.path.join(PREVIEW_DIR, "app-16-x16.png"))
    frames[SIZES.index(32)].resize((256, 256), Image.NEAREST).save(os.path.join(PREVIEW_DIR, "app-32-x8.png"))
    frames[SIZES.index(48)].save(os.path.join(PREVIEW_DIR, "app-48.png"))
    master.resize((512, 512), Image.LANCZOS).save(os.path.join(PREVIEW_DIR, "master-512.png"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()
    if args.check:
        print("app.ico", os.path.getsize(ICO), "bytes, frames:", sorted(Image.open(ICO).info.get("sizes", [])))
        print("ui png ", UI_PNG, os.path.getsize(UI_PNG), "bytes", Image.open(UI_PNG).size)
        return 0
    master, frames = build_frames()
    size = write_ico(frames, ICO)
    write_outputs(master, frames)
    print("wrote", ICO, size, "bytes")
    print("frames:", [f.width for f in frames])
    print("wrote", UI_PNG)
    return 0


if __name__ == "__main__":
    sys.exit(main())
