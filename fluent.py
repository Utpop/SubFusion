# -*- coding: utf-8 -*-
"""Fluent Design（Windows 11 风格）主题与控件。

纯 tkinter/ttk 实现，不依赖任何第三方库。
"""

import ctypes
import os
import tkinter as tk
from ctypes import wintypes
from tkinter import ttk

try:
    import tkinter.font as tkfont
except Exception:  # pragma: no cover
    tkfont = None


# --------------------------------------------------------------------------
# 配色（Fluent 2 / Windows 11 浅色主题）
# --------------------------------------------------------------------------
C_BG = "#F3F3F3"            # 应用背景
C_CARD = "#FFFFFF"          # 卡片背景
C_BORDER = "#E5E5E5"        # 卡片/控件边框
C_BORDER_STRONG = "#D1D1D1"
C_TEXT = "#1B1B1F"          # 主文本
C_TEXT_2 = "#616161"        # 次要文本
C_TEXT_3 = "#8A8A8A"        # 禁用/提示

C_ACCENT = "#0F6CBD"        # 强调色
C_ACCENT_HOVER = "#115EA3"
C_ACCENT_PRESSED = "#0C3B5E"
C_ACCENT_LIGHT = "#E8F1FF"  # 强调浅底（选中行）

C_SUBTLE = "#FAFAFA"        # 表头/悬停
C_SUBTLE_HOVER = "#F0F0F0"
C_CONTROL = "#FFFFFF"       # 输入控件底

C_DANGER = "#C42B1C"
C_DANGER_HOVER = "#A42615"
C_SUCCESS = "#0E700E"
C_WARN_TEXT = "#9A6700"

FONT_STACK = ("Segoe UI Variable Text", "Segoe UI Variable Display",
              "Segoe UI", "Microsoft YaHei UI")
FONT_MONO = ("Cascadia Mono", "Consolas", "Microsoft YaHei UI")

_SIZE_BODY = 9
_SIZE_STRONG = 9
_SIZE_TITLE = 16
_SIZE_SUBTITLE = 9
_SIZE_SMALL = 8


def pick_family(candidates):
    """挑一个系统里真实存在的字体。"""
    if tkfont is None:
        return candidates[-1]
    try:
        available = set(tkfont.families())
    except Exception:
        return candidates[-1]
    for name in candidates:
        if name in available:
            return name
    return candidates[-1]


class Fonts(object):
    family = "Segoe UI"
    mono = "Consolas"

    @classmethod
    def init(cls):
        cls.family = pick_family(FONT_STACK)
        cls.mono = pick_family(FONT_MONO)
        return cls

    @classmethod
    def body(cls, size=None, weight="normal"):
        return (cls.family, size or _SIZE_BODY, weight)

    @classmethod
    def strong(cls, size=None):
        return (cls.family, size or _SIZE_STRONG, "bold")

    @classmethod
    def title(cls):
        return (cls.family, _SIZE_TITLE, "semibold")

    @classmethod
    def subtitle(cls):
        return (cls.family, _SIZE_SUBTITLE, "normal")

    @classmethod
    def small(cls):
        return (cls.family, _SIZE_SMALL, "normal")


# --------------------------------------------------------------------------
# 窗口效果（Win11 圆角 / 标题栏配色 / Mica）
# --------------------------------------------------------------------------
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWA_CAPTION_COLOR = 35
DWMWA_SYSTEMBACKDROP_TYPE = 38
DWMWA_USE_IMMERSIVE_DARK_MODE = 20

DWMWCP_ROUND = 2
DWMSBT_MAINWINDOW = 2       # Mica
DWMSBT_TRANSIENTWINDOW = 3  # Acrylic


def _colorref(hex_color):
    """#RRGGBB -> COLORREF (0x00BBGGRR)"""
    hex_color = hex_color.lstrip("#")
    r, g, b = (int(hex_color[i:i + 2], 16) for i in (0, 2, 4))
    return (b << 16) | (g << 8) | r


GA_ROOT = 2


def enable_dpi_awareness():
    """让进程按高 DPI 原生渲染，避免系统位图拉伸导致文字模糊。

    必须在创建 Tk() 之前调用。
    """
    if os.name != "nt":
        return "non-windows"
    # 2 = PER_MONITOR_DPI_AWARE, 1 = SYSTEM_DPI_AWARE
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
        return "per-monitor"
    except Exception:
        pass
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
        return "system"
    except Exception:
        pass
    try:
        if ctypes.windll.user32.SetProcessDPIAware():
            return "legacy"
    except Exception:
        pass
    return "unavailable"


def dpi_scale():
    """当前系统 DPI 缩放比例（96 DPI = 1.0）。"""
    if os.name != "nt":
        return 1.0
    try:
        dpi = ctypes.windll.user32.GetDpiForSystem()
        if dpi:
            return dpi / 96.0
    except Exception:
        pass
    try:
        hdc = ctypes.windll.user32.GetDC(0)
        dpi = ctypes.windll.gdi32.GetDeviceCaps(hdc, 88)  # LOGPIXELSY
        ctypes.windll.user32.ReleaseDC(0, hdc)
        if dpi:
            return dpi / 96.0
    except Exception:
        pass
    return 1.0


def scaled(value):
    """把逻辑像素换算成当前 DPI 下的物理像素。"""
    return int(round(value * dpi_scale()))


def apply_scaling(root):
    """把 Tk 的 point->pixel 缩放对齐到系统 DPI。"""
    if os.name != "nt":
        return None
    try:
        user32 = ctypes.windll.user32
        hdc = user32.GetDC(0)
        dpi = ctypes.windll.gdi32.GetDeviceCaps(hdc, 88)  # LOGPIXELSY
        user32.ReleaseDC(0, hdc)
        if dpi and dpi > 0:
            root.tk.call("tk", "scaling", float(dpi) / 72.0)
            return dpi
    except Exception:
        pass
    return None


def get_root_hwnd(hwnd):
    """Tk 的 winfo_id() 返回的是内部子窗口，DWM/拖放需要真正的顶层窗口。"""
    if os.name != "nt" or not hwnd:
        return None
    try:
        user32 = ctypes.windll.user32
        user32.GetAncestor.restype = wintypes.HWND
        user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
        real = user32.GetAncestor(wintypes.HWND(int(hwnd)), GA_ROOT)
        return int(real) if real else int(hwnd)
    except Exception:
        return int(hwnd) if hwnd else None


def apply_window_effects(hwnd, rounded=True, caption_color=C_BG, mica=False,
                         dark=False):
    """给窗口加上 Win11 的圆角与标题栏配色；不支持的系统会自动忽略。"""
    if os.name != "nt" or not hwnd:
        return False
    try:
        dwm = ctypes.windll.dwmapi
        hwnd = wintypes.HWND(get_root_hwnd(hwnd))
        ok = True

        if dark:
            val = ctypes.c_int(1)
            try:
                dwm.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                                          ctypes.byref(val), 4)
            except Exception:
                pass

        if rounded:
            val = ctypes.c_int(DWMWCP_ROUND)
            try:
                dwm.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
                                          ctypes.byref(val), 4)
            except Exception:
                pass

        if caption_color:
            val = ctypes.c_int(_colorref(caption_color))
            try:
                dwm.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR,
                                          ctypes.byref(val), 4)
            except Exception:
                pass

        if mica:
            val = ctypes.c_int(DWMSBT_MAINWINDOW)
            try:
                dwm.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE,
                                          ctypes.byref(val), 4)
            except Exception:
                pass
        return ok
    except Exception:
        return False


# --------------------------------------------------------------------------
# 主题应用
# --------------------------------------------------------------------------
def _sync_colors(style):
    """从当前主题读取实际颜色，让自绘控件（卡片/胶囊/进度条）与主题一致。"""
    global C_BG, C_CARD, C_BORDER, C_BORDER_STRONG, C_TEXT, C_TEXT_2
    global C_ACCENT, C_ACCENT_HOVER, C_ACCENT_LIGHT, C_SUBTLE

    def lookup(name, option, default):
        try:
            value = style.lookup(name, option)
            return value if value else default
        except Exception:
            return default

    C_BG = lookup("TFrame", "background", C_BG)
    C_CARD = lookup("TEntry", "fieldbackground", C_CARD)
    C_TEXT = lookup("TLabel", "foreground", C_TEXT)
    C_TEXT_2 = lookup("Muted.TLabel", "foreground", C_TEXT_2)
    accent = lookup("Accent.TButton", "background", "")
    if accent:
        C_ACCENT = accent
    C_BORDER = lookup("TLabelframe", "bordercolor", C_BORDER)
    C_SUBTLE = lookup("Treeview.Heading", "background", C_SUBTLE)
    if not C_ACCENT_LIGHT or C_ACCENT_LIGHT == "#E8F1FF":
        C_ACCENT_LIGHT = _mix(C_BG, C_ACCENT, 0.14)
    return C_BG


def _shade(color, amount):
    """把颜色调亮（amount>0，向白混合）或调暗（amount<0，向黑混合）。"""
    if amount >= 0:
        return _mix(color, "#FFFFFF", amount)
    return _mix(color, "#000000", -amount)


def define_hero_style(style):
    """定义加大号的主操作按钮样式（基于当前主题的强调色）。"""
    accent = _lookup(style, "Accent.TButton", "background", C_ACCENT)
    fg = _lookup(style, "Accent.TButton", "foreground", "#FFFFFF")
    style.configure("Hero.TButton",
                    background=accent,
                    foreground=fg,
                    bordercolor=accent,
                    lightcolor=accent,
                    darkcolor=accent,
                    relief="flat",
                    borderwidth=0,
                    focuscolor=accent,
                    padding=(34, 14),
                    font=(Fonts.family, _SIZE_BODY + 2, "bold"))
    style.map("Hero.TButton",
              background=[("active", _shade(accent, 0.14)),
                          ("pressed", _shade(accent, -0.22)),
                          ("disabled", "#BEBEBE")],
              foreground=[("disabled", "#FFFFFF")],
              bordercolor=[("disabled", "#BEBEBE")])


def _lookup(style, name, option, default):
    try:
        value = style.lookup(name, option)
        return value if value else default
    except Exception:
        return default


def _mix(color_a, color_b, ratio):
    """把两种 #RRGGBB 按比例混合。"""

    def parse(value):
        value = (value or "#F3F3F3").lstrip("#")
        if len(value) != 6:
            return (243, 243, 243)
        try:
            return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))
        except ValueError:
            return (243, 243, 243)

    ar, ag, ab = parse(color_a)
    br, bg, bb = parse(color_b)
    mix = lambda a, b: int(round(a + (b - a) * ratio))  # noqa: E731
    return "#%02X%02X%02X" % (mix(ar, br), mix(ag, bg), mix(ab, bb))


def apply_theme(root, theme="light"):
    """应用 Fluent 主题。优先使用 sv-ttk，缺失时回退到内置手写主题。"""
    Fonts.init()
    style = ttk.Style(root)
    use_sv_ttk = False

    try:
        import sv_ttk
        sv_ttk.set_theme(theme)
        use_sv_ttk = True
    except Exception:
        _configure_manual_theme(root, style)

    _sync_colors(style)
    root.configure(background=C_BG)

    fam, mono = Fonts.family, Fonts.mono

    # 与主题无关、两种模式都要调整的项目（尺寸按 DPI 换算）
    style.configure("Treeview", rowheight=scaled(28), font=(fam, _SIZE_BODY))
    style.configure("Treeview.Heading", font=(fam, _SIZE_SMALL, "bold"),
                    padding=(8, 8))
    style.configure("TNotebook.Tab", padding=(16, 8), font=(fam, _SIZE_BODY))
    style.configure("Heading.TLabel", font=(fam, 10, "bold"))
    style.configure("TProgressbar", thickness=6)

    # 加大号主按钮
    define_hero_style(style)

    root.option_add("*Text.font", (mono, 9))
    root.option_add("*Menu.font", (fam, _SIZE_BODY))
    root.option_add("*Menu.activeBackground", C_ACCENT_LIGHT)
    root.option_add("*Menu.activeForeground", C_TEXT)

    root._fluent_sv_ttk = use_sv_ttk
    return style


def _configure_manual_theme(root, style):
    """没有 sv-ttk 时使用的手写 Fluent 主题（原实现）。"""
    style.theme_use("clam") if "clam" in style.theme_names() else None
    fam, mono = Fonts.family, Fonts.mono
    mono_font = (mono, 9)

    # 容器
    style.configure("TFrame", background=C_BG)
    style.configure("Card.TFrame", background=C_CARD, relief="flat")
    style.configure("TLabelframe", background=C_CARD, bordercolor=C_BORDER,
                    relief="flat", borderwidth=1)
    style.configure("TLabelframe.Label", background=C_CARD, foreground=C_TEXT,
                    font=(fam, _SIZE_SMALL, "bold"))

    # 文本
    style.configure("TLabel", background=C_BG, foreground=C_TEXT,
                    font=(fam, _SIZE_BODY))
    style.configure("Card.TLabel", background=C_CARD, foreground=C_TEXT,
                    font=(fam, _SIZE_BODY))
    style.configure("Muted.TLabel", background=C_BG, foreground=C_TEXT_2,
                    font=(fam, _SIZE_BODY))
    style.configure("MutedCard.TLabel", background=C_CARD, foreground=C_TEXT_2,
                    font=(fam, _SIZE_BODY))
    style.configure("Heading.TLabel", background=C_CARD, foreground=C_TEXT,
                    font=(fam, 10, "bold"))

    # 按钮（ttk，用于次要操作）
    style.configure("TButton", background=C_CARD, foreground=C_TEXT,
                    bordercolor=C_BORDER_STRONG, lightcolor=C_CARD,
                    darkcolor=C_CARD, relief="flat", borderwidth=1,
                    focuscolor=C_ACCENT, padding=(12, 6),
                    font=(fam, _SIZE_BODY))
    style.map("TButton",
              background=[("active", C_SUBTLE_HOVER), ("pressed", "#E0E0E0"),
                          ("disabled", C_SUBTLE)],
              bordercolor=[("active", C_BORDER_STRONG), ("disabled", C_BORDER)],
              foreground=[("disabled", C_TEXT_3)])
    style.configure("Toolbutton", padding=(8, 6))

    # 强调按钮（ttk 版，配合 tk 版使用）
    style.configure("Accent.TButton", background=C_ACCENT, foreground="#FFFFFF",
                    bordercolor=C_ACCENT, lightcolor=C_ACCENT,
                    darkcolor=C_ACCENT, relief="flat", borderwidth=0,
                    padding=(16, 7), font=(fam, _SIZE_BODY, "bold"))
    style.map("Accent.TButton",
              background=[("active", C_ACCENT_HOVER),
                          ("pressed", C_ACCENT_PRESSED),
                          ("disabled", "#BEBEBE")],
              foreground=[("disabled", "#FFFFFF")])

    # 输入控件
    style.configure("TEntry", fieldbackground=C_CONTROL, background=C_CONTROL,
                    foreground=C_TEXT, bordercolor=C_BORDER_STRONG,
                    lightcolor=C_CONTROL, darkcolor=C_CONTROL,
                    insertcolor=C_TEXT, padding=(8, 6), relief="flat",
                    borderwidth=1, font=(fam, _SIZE_BODY))
    style.map("TEntry",
              bordercolor=[("focus", C_ACCENT), ("hover", C_TEXT_3)],
              lightcolor=[("focus", C_ACCENT)])

    style.configure("TCombobox", fieldbackground=C_CONTROL, background=C_CARD,
                    foreground=C_TEXT, bordercolor=C_BORDER_STRONG,
                    lightcolor=C_CONTROL, arrowcolor=C_TEXT_2,
                    padding=(8, 5), relief="flat", borderwidth=1,
                    font=(fam, _SIZE_BODY))
    style.map("TCombobox",
              fieldbackground=[("readonly", C_CONTROL)],
              bordercolor=[("focus", C_ACCENT), ("hover", C_TEXT_3)],
              arrowcolor=[("disabled", C_TEXT_3)],
              foreground=[("disabled", C_TEXT_3)])

    style.configure("TSpinbox", fieldbackground=C_CONTROL, background=C_CONTROL,
                    foreground=C_TEXT, bordercolor=C_BORDER_STRONG,
                    lightcolor=C_CONTROL, arrowcolor=C_TEXT_2,
                    padding=(6, 5), relief="flat", borderwidth=1,
                    font=(fam, _SIZE_BODY))
    style.map("TSpinbox",
              bordercolor=[("focus", C_ACCENT), ("hover", C_TEXT_3)],
              arrowcolor=[("hover", C_ACCENT)])

    style.configure("TCheckbutton", background=C_CARD, foreground=C_TEXT,
                    indicatorcolor=C_CONTROL, indicatorbackground=C_CONTROL,
                    bordercolor=C_BORDER_STRONG, relief="flat",
                    padding=(4, 4), font=(fam, _SIZE_BODY))
    style.map("TCheckbutton",
              indicatorcolor=[("selected", C_ACCENT), ("active", C_ACCENT)],
              background=[("active", C_CARD)],
              foreground=[("disabled", C_TEXT_3)])

    style.configure("Vertical.TScrollbar", background=C_SUBTLE,
                    troughcolor=C_SUBTLE, bordercolor=C_SUBTLE,
                    arrowcolor=C_TEXT_2, relief="flat", borderwidth=0, width=10)
    style.map("Vertical.TScrollbar", background=[("active", "#DADADA")])
    style.configure("Horizontal.TScrollbar", background=C_SUBTLE,
                    troughcolor=C_SUBTLE, bordercolor=C_SUBTLE,
                    arrowcolor=C_TEXT_2, relief="flat", borderwidth=0, width=10)
    style.map("Horizontal.TScrollbar", background=[("active", "#DADADA")])

    # 表格
    style.configure("Treeview", background=C_CARD, fieldbackground=C_CARD,
                    foreground=C_TEXT, bordercolor=C_BORDER, borderwidth=1,
                    relief="flat", rowheight=30, font=(fam, _SIZE_BODY))
    style.configure("Treeview.Heading", background=C_SUBTLE,
                    foreground=C_TEXT_2, bordercolor=C_BORDER,
                    relief="flat", borderwidth=0,
                    font=(fam, _SIZE_SMALL, "bold"), padding=(8, 8))
    style.map("Treeview.Heading",
              background=[("active", "#EFEFEF")],
              foreground=[("active", C_TEXT)])
    style.map("Treeview",
              background=[("selected", C_ACCENT_LIGHT)],
              foreground=[("selected", C_TEXT)])

    # 标签页（Fluent TabView 近似）
    style.configure("TNotebook", background=C_BG, borderwidth=0, tabmargins=0)
    style.configure("TNotebook.Tab", background="transparent",
                    foreground=C_TEXT_2, borderwidth=0, padding=(16, 8),
                    font=(fam, _SIZE_BODY))
    style.map("TNotebook.Tab",
              background=[("selected", C_CARD), ("active", C_SUBTLE)],
              foreground=[("selected", C_TEXT), ("active", C_TEXT)],
              expand=[("selected", (0, 0, 0, 0))])

    # 分隔线
    style.configure("Separator.TSeparator", background=C_BORDER)

    # 进度条（ttk 版作为兜底）
    style.configure("TProgressbar", background=C_ACCENT, troughcolor="#E5E5E5",
                    borderwidth=0, relief="flat", thickness=6)

    root.option_add("*TCombobox*Listbox.background", C_CARD)
    root.option_add("*TCombobox*Listbox.foreground", C_TEXT)
    root.option_add("*TCombobox*Listbox.selectBackground", C_ACCENT_LIGHT)
    root.option_add("*TCombobox*Listbox.selectForeground", C_TEXT)
    root.option_add("*TCombobox*Listbox.font", (fam, _SIZE_BODY))
    root.option_add("*Text.background", C_CARD)
    root.option_add("*Menu.background", C_CARD)
    root.option_add("*Menu.foreground", C_TEXT)
    root.option_add("*Menu.activeBackground", C_ACCENT_LIGHT)
    root.option_add("*Menu.activeForeground", C_TEXT)
    root.option_add("*Menu.font", (fam, _SIZE_BODY))

    return style


# --------------------------------------------------------------------------
# 卡片容器
# --------------------------------------------------------------------------
class Card(tk.Frame):
    """白色圆角感卡片：白底 + 1px 浅灰边框。"""

    def __init__(self, master, padding=16, **kwargs):
        kwargs.setdefault("background", C_CARD)
        kwargs.setdefault("highlightthickness", 1)
        kwargs.setdefault("highlightbackground", C_BORDER)
        kwargs.setdefault("highlightcolor", C_BORDER)
        kwargs.setdefault("bd", 0)
        tk.Frame.__init__(self, master, **kwargs)
        self.padding = padding

    def body(self):
        """返回带内边距的内容区。"""
        inner = tk.Frame(self, background=C_CARD)
        inner.pack(fill="both", expand=True, padx=self.padding, pady=self.padding)
        return inner


# --------------------------------------------------------------------------
# 按钮
# --------------------------------------------------------------------------
class Button(ttk.Button):
    """Fluent 风格按钮：accent=True 为主按钮（蓝色实心）。

    用 ttk 实现，因此能直接吃到 sv-ttk 主题；没有 sv-ttk 时也可用。
    """

    def __init__(self, master, text="", command=None, accent=False,
                 danger=False, subtle=False, width=None, hero=False, **kwargs):
        style_name = "TButton"
        if accent and hero:
            style_name = "Hero.TButton"
        elif accent:
            style_name = "Accent.TButton"
        elif subtle:
            style_name = "Toolbutton"
        try:
            ttk.Button.__init__(self, master, text=text, command=command,
                                style=style_name, **kwargs)
        except Exception:
            ttk.Button.__init__(self, master, text=text, command=command,
                                **kwargs)
        if width:
            self.configure(width=width)
        self.configure(cursor="hand2")

    def set_enabled(self, enabled):
        self.configure(state="normal" if enabled else "disabled")


# --------------------------------------------------------------------------
# 圆角进度条
# --------------------------------------------------------------------------
class ProgressBar(tk.Canvas):
    """Fluent 风格细进度条（圆角、无边框）。"""

    def __init__(self, master, width=280, height=6, track="#E5E5E5",
                 fill=C_ACCENT, background=None, **kwargs):
        if background is None:
            try:
                background = master.cget("background")
            except Exception:
                background = C_BG
        tk.Canvas.__init__(self, master, width=width, height=height,
                           highlightthickness=0, bd=0, background=background,
                           **kwargs)
        self.width_px = width
        self.height_px = height
        self.track_color = track
        self.fill_color = fill
        self.value = 0.0
        self.maximum = 100.0
        self._items = []
        self.bind("<Configure>", lambda e: self._redraw())
        self._redraw()

    def _rounded(self, color, x0, x1):
        r = self.height_px / 2.0
        y0, y1 = 0, self.height_px
        items = []
        if x1 - x0 <= 0:
            return items
        if x1 - x0 < 2 * r:
            r = (x1 - x0) / 2.0
        items.append(self.create_rectangle(x0 + r, y0, x1 - r, y1, fill=color,
                                           outline=color, width=0))
        items.append(self.create_oval(x0, y0, x0 + 2 * r, y1, fill=color,
                                      outline=color, width=0))
        items.append(self.create_oval(x1 - 2 * r, y0, x1, y1, fill=color,
                                      outline=color, width=0))
        return items

    def _redraw(self):
        self.delete("all")
        width = max(self.winfo_width(), 1)
        self.width_px = width
        self._rounded(self.track_color, 0, width)
        percent = 0.0
        if self.maximum:
            percent = max(0.0, min(1.0, float(self.value) / float(self.maximum)))
        if percent > 0:
            self._rounded(self.fill_color, 0, width * percent)

    def set_value(self, value):
        self.value = value
        self._redraw()

    def set_maximum(self, maximum):
        self.maximum = maximum
        self._redraw()

    def configure(self, cnf=None, **kwargs):
        if kwargs:
            if "value" in kwargs:
                self.set_value(kwargs.pop("value"))
            if "maximum" in kwargs:
                self.set_maximum(kwargs.pop("maximum"))
        if cnf and isinstance(cnf, dict):
            if "value" in cnf:
                self.set_value(cnf.pop("value"))
            if "maximum" in cnf:
                self.set_maximum(cnf.pop("maximum"))
        if cnf or kwargs:
            try:
                tk.Canvas.configure(self, cnf, **kwargs)
            except Exception:
                pass

    def __setitem__(self, key, value):
        if key == "value":
            self.set_value(value)
        elif key == "maximum":
            self.set_maximum(value)
        else:
            tk.Canvas.__setitem__(self, key, value)

    def __getitem__(self, key):
        if key == "value":
            return self.value
        if key == "maximum":
            return self.maximum
        return tk.Canvas.__getitem__(self, key)


# --------------------------------------------------------------------------
# 信息条（InfoBar）
# --------------------------------------------------------------------------
class HeroButton(tk.Button):
    """加大号主操作按钮（tk 自绘，颜色不受主题图片元素限制）。"""

    def __init__(self, master, text="", command=None, **kwargs):
        kwargs.setdefault("text", text)
        kwargs.setdefault("command", command)
        kwargs.setdefault("background", C_ACCENT)
        kwargs.setdefault("foreground", "#FFFFFF")
        kwargs.setdefault("activebackground", C_ACCENT_HOVER)
        kwargs.setdefault("activeforeground", "#FFFFFF")
        kwargs.setdefault("disabledforeground", "#FFFFFF")
        kwargs.setdefault("relief", "flat")
        kwargs.setdefault("bd", 0)
        kwargs.setdefault("highlightthickness", 0)
        kwargs.setdefault("padx", 30)
        kwargs.setdefault("pady", 12)
        kwargs.setdefault("cursor", "hand2")
        kwargs.setdefault("font", (Fonts.family, _SIZE_BODY + 2, "bold"))
        tk.Button.__init__(self, master, **kwargs)

        self._colors = {"normal": C_ACCENT, "hover": C_ACCENT_HOVER,
                        "pressed": C_ACCENT_PRESSED, "disabled": "#BEBEBE"}
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        self.bind("<ButtonPress-1>", self._on_press)
        self.bind("<ButtonRelease-1>", self._on_release)

    def _on_enter(self, _event):
        if str(self["state"]) != "disabled":
            self.configure(background=self._colors["hover"])

    def _on_leave(self, _event):
        self.configure(background=self._colors["normal"])

    def _on_press(self, _event):
        if str(self["state"]) != "disabled":
            self.configure(background=self._colors["pressed"])

    def _on_release(self, _event):
        if str(self["state"]) != "disabled":
            self.configure(background=self._colors["hover"])

    def set_enabled(self, enabled):
        state = "normal" if enabled else "disabled"
        self.configure(state=state,
                       background=self._colors["normal" if enabled else "disabled"])


class DropZone(tk.Canvas):
    """Fluent 风格拖放区：虚线圆角边框 + 居中提示文字。"""

    def __init__(self, master, height=104, main_text="拖放文件到这里",
                 sub_text="", **kwargs):
        tk.Canvas.__init__(self, master, height=height, highlightthickness=0,
                           bd=0, background=C_CARD, **kwargs)
        self._main = main_text
        self._sub = sub_text
        self.bind("<Configure>", lambda e: self._draw())
        self._draw()

    def set_text(self, main_text=None, sub_text=None):
        if main_text is not None:
            self._main = main_text
        if sub_text is not None:
            self._sub = sub_text
        self._draw()

    def _draw(self):
        self.delete("all")
        w = self.winfo_width()
        h = self.winfo_height()
        if w < 40 or h < 40:
            return
        m, r = 12, 8
        x0, y0 = m, m
        x1, y1 = w - m, h - m
        color = "#9CB8D8"
        dash = (5, 4)
        self.create_line(x0 + r, y0, x1 - r, y0, dash=dash, fill=color)
        self.create_line(x0 + r, y1, x1 - r, y1, dash=dash, fill=color)
        self.create_line(x0, y0 + r, x0, y1 - r, dash=dash, fill=color)
        self.create_line(x1, y0 + r, x1, y1 - r, dash=dash, fill=color)
        self.create_arc(x0, y0, x0 + 2 * r, y0 + 2 * r, start=90, extent=90,
                        style="arc", outline=color, dash=dash)
        self.create_arc(x1 - 2 * r, y0, x1, y0 + 2 * r, start=0, extent=90,
                        style="arc", outline=color, dash=dash)
        self.create_arc(x0, y1 - 2 * r, x0 + 2 * r, y1, start=180, extent=90,
                        style="arc", outline=color, dash=dash)
        self.create_arc(x1 - 2 * r, y1 - 2 * r, x1, y1, start=270, extent=90,
                        style="arc", outline=color, dash=dash)

        cy = h / 2.0 - (10 if self._sub else 0)
        self.create_text(w / 2.0, cy, text=self._main,
                         font=(Fonts.family, 11), fill=C_ACCENT)
        if self._sub:
            self.create_text(w / 2.0, cy + 26, text=self._sub,
                             font=Fonts.small(), fill=C_TEXT_2)


# --------------------------------------------------------------------------
# 状态胶囊
# --------------------------------------------------------------------------
class Pill(tk.Frame):
    """右上角那种小胶囊标签。"""

    def __init__(self, master, text="", color=C_SUCCESS, **kwargs):
        kwargs.setdefault("background", C_CARD)
        tk.Frame.__init__(self, master, **kwargs)
        self.dot = tk.Label(self, text="  ", background=color,
                            font=("Segoe UI", 6))
        self.dot.pack(side="left", padx=(0, 6))
        self.label = tk.Label(self, text=text, background=C_CARD,
                              foreground=C_TEXT_2, font=Fonts.small())
        self.label.pack(side="left")

    def set(self, text, color=C_SUCCESS):
        self.dot.configure(background=color)
        self.label.configure(text=text)
