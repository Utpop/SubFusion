# -*- coding: utf-8 -*-
"""字幕视频批量合并工具 —— 图形界面

双击「启动.bat」运行；也可以在命令行直接运行本文件。
"""

import csv
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime

# 程序自带的第三方依赖放在 vendor/ 下，无需用户 pip install
_VENDOR_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "vendor")
if os.path.isdir(_VENDOR_DIR) and _VENDOR_DIR not in sys.path:
    sys.path.insert(0, _VENDOR_DIR)

import core
from core import (Job, Options, SubTrack, LANG_CHOICES, LANG_NAMES,
                  detect_lang_from_name, format_duration, format_size)
from core import build_command, build_output_path, ensure_utf8

APP_NAME = core.APP_NAME
APP_VERSION = "1.1"
PROBE_WORKERS = 4          # 并发读取媒体信息的线程数


def ensure_tcl_env():
    """部分 Windows 上的 Python 会把 Tcl 数据目录放偏，这里主动定位。"""
    if os.environ.get("TCL_LIBRARY"):
        return os.environ["TCL_LIBRARY"]
    candidates = []
    for prefix in (getattr(sys, "prefix", ""), getattr(sys, "base_prefix", ""),
                   os.path.dirname(os.path.abspath(sys.executable))):
        if prefix:
            candidates.append(os.path.join(prefix, "tcl", "tcl8.6"))
            candidates.append(os.path.join(prefix, "lib", "tcl8.6"))
    local = os.environ.get("LOCALAPPDATA", "")
    if local:
        base = os.path.join(local, "Programs", "Python")
        if os.path.isdir(base):
            for name in sorted(os.listdir(base), reverse=True):
                candidates.append(os.path.join(base, name, "tcl", "tcl8.6"))
    system_drive = os.environ.get("SystemDrive", "")
    if system_drive and os.path.isdir(system_drive + "\\"):
        try:
            for name in sorted(os.listdir(system_drive + "\\")):
                if name.lower().startswith("python"):
                    candidates.append(os.path.join(system_drive + "\\", name,
                                                   "tcl", "tcl8.6"))
        except OSError:
            pass
    for cand in candidates:
        if os.path.isfile(os.path.join(cand, "init.tcl")):
            os.environ["TCL_LIBRARY"] = cand
            tk_dir = os.path.join(os.path.dirname(cand), "tk8.6")
            if os.path.isdir(tk_dir) and not os.environ.get("TK_LIBRARY"):
                os.environ["TK_LIBRARY"] = tk_dir
            return cand
    return None


TCL_DIR = ensure_tcl_env()

import tkinter as tk  # noqa: E402
from tkinter import filedialog, messagebox, ttk  # noqa: E402

try:
    import dragdrop
except Exception:  # pragma: no cover
    dragdrop = None

import fluent  # noqa: E402


# --------------------------------------------------------------------------
# 字幕编辑对话框
# --------------------------------------------------------------------------
class SubtitleDialog(object):
    """为单个视频挑选/编辑字幕轨。"""

    def __init__(self, parent, job, candidates, all_subs):
        self.job = job
        self.result = None
        self.edited = False

        self.top = tk.Toplevel(parent)
        self.top.title("字幕轨设置 —— %s" % job.name)
        self.top.transient(parent)
        self.top.minsize(760, 480)
        self.top.grab_set()
        try:
            self.top.configure(background=fluent.C_BG)
        except Exception:
            pass

        self.tracks = [t.clone() for t in job.subs]
        self.candidates = list(candidates) or list(all_subs)

        self._build()
        self._center_on(parent, 900, 580)
        self._refresh_selected()
        self.top.protocol("WM_DELETE_WINDOW", self._cancel)
        self.top.wait_window()

    def _center_on(self, parent, width, height):
        """把对话框居中到主窗口。"""
        try:
            parent.update_idletasks()
            px = parent.winfo_rootx()
            py = parent.winfo_rooty()
            pw = parent.winfo_width()
            ph = parent.winfo_height()
            width = fluent.scaled(width)
            height = fluent.scaled(height)
            x = max(0, px + (pw - width) // 2)
            y = max(0, py + (ph - height) // 3)
            self.top.geometry("%dx%d+%d+%d" % (width, height, x, y))
        except Exception:
            self.top.geometry("900x580")

    def _build(self):
        top = self.top
        top.columnconfigure(0, weight=1)
        top.rowconfigure(1, weight=1)

        head = ttk.Frame(top, padding=(12, 10, 12, 4))
        head.grid(row=0, column=0, sticky="ew")
        ttk.Label(head, text="视频：%s" % self.job.video,
                  style="Heading.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(head, text="（勾选要合并的字幕，可设置语言与默认轨）",
                  style="Muted.TLabel").grid(row=0, column=1, sticky="w", padx=12)

        body = ttk.Frame(top, padding=(12, 4, 12, 4))
        body.grid(row=1, column=0, sticky="nsew")
        body.columnconfigure(0, weight=1)
        body.columnconfigure(2, weight=1)
        body.rowconfigure(0, weight=1)

        # ---- 左：候选 ----
        left = ttk.LabelFrame(body, text=" 候选字幕 ", padding=(6, 6, 6, 6))
        left.grid(row=0, column=0, sticky="nsew")
        left.rowconfigure(0, weight=1)
        self.lst_cand = tk.Listbox(left, selectmode="extended",
                                   font=fluent.Fonts.body(),
                                   background=fluent.C_CARD,
                                   foreground=fluent.C_TEXT,
                                   selectbackground=fluent.C_ACCENT_LIGHT,
                                   selectforeground=fluent.C_TEXT,
                                   highlightthickness=1,
                                   highlightbackground=fluent.C_BORDER,
                                   relief="flat", borderwidth=0,
                                   activestyle="none")
        self.lst_cand.grid(row=0, column=0, sticky="nsew")
        sb1 = ttk.Scrollbar(left, orient="vertical", command=self.lst_cand.yview)
        sb1.grid(row=0, column=1, sticky="ns")
        self.lst_cand.configure(yscrollcommand=sb1.set)
        for path in self.candidates:
            self.lst_cand.insert("end", path)
        self.lst_cand.bind("<Double-1>", lambda e: self._add())

        mid = ttk.Frame(body, padding=(8, 0, 8, 0))
        mid.grid(row=0, column=1, sticky="ns")
        fluent.Button(mid, text="添加 →", width=10, command=self._add).pack(pady=6)
        fluent.Button(mid, text="← 移除", width=10, command=self._remove).pack(pady=6)
        fluent.Button(mid, text="浏览…", width=10, command=self._browse).pack(pady=6)

        # ---- 右：已选 ----
        right = ttk.LabelFrame(body, text=" 已选字幕轨 ", padding=(6, 6, 6, 6))
        right.grid(row=0, column=2, sticky="nsew")
        right.rowconfigure(0, weight=1)
        right.columnconfigure(0, weight=1)
        self.lst_sel = tk.Listbox(right, font=fluent.Fonts.body(),
                                  background=fluent.C_CARD,
                                  foreground=fluent.C_TEXT,
                                  selectbackground=fluent.C_ACCENT_LIGHT,
                                  selectforeground=fluent.C_TEXT,
                                  highlightthickness=1,
                                  highlightbackground=fluent.C_BORDER,
                                  relief="flat", borderwidth=0,
                                  activestyle="none")
        self.lst_sel.grid(row=0, column=0, sticky="nsew")
        sb2 = ttk.Scrollbar(right, orient="vertical", command=self.lst_sel.yview)
        sb2.grid(row=0, column=1, sticky="ns")
        self.lst_sel.configure(yscrollcommand=sb2.set)
        self.lst_sel.bind("<<ListboxSelect>>", lambda e: self._on_select_track())
        self.lst_sel.bind("<Double-1>", lambda e: self._remove)

        move = ttk.Frame(right)
        move.grid(row=1, column=0, sticky="w", pady=(6, 0))
        fluent.Button(move, text="上移", width=8,
                      command=lambda: self._move(-1)).pack(side="left")
        fluent.Button(move, text="下移", width=8,
                      command=lambda: self._move(1)).pack(side="left", padx=6)

        # ---- 下方：选中轨属性 ----
        props = ttk.LabelFrame(top, text=" 轨道属性 ", padding=(10, 8, 10, 8))
        props.grid(row=2, column=0, sticky="ew", padx=12, pady=(4, 6))

        ttk.Label(props, text="语言：").pack(side="left")
        self.var_lang = tk.StringVar(value="chi")
        self.cmb_lang = ttk.Combobox(props, textvariable=self.var_lang, width=10,
                                     state="readonly",
                                     values=["%s  %s" % (c, n) for c, n in LANG_CHOICES])
        self.cmb_lang.pack(side="left")
        self.cmb_lang.bind("<<ComboboxSelected>>", lambda e: self._apply_props())

        ttk.Label(props, text="轨道名：").pack(side="left", padx=(14, 0))
        self.var_title = tk.StringVar()
        ent_title = ttk.Entry(props, textvariable=self.var_title, width=18)
        ent_title.pack(side="left")
        ent_title.bind("<KeyRelease>", lambda e: self._apply_props())

        self.var_default = tk.BooleanVar(value=False)
        ttk.Checkbutton(props, text="默认轨", variable=self.var_default,
                        command=self._apply_props).pack(side="left", padx=(14, 0))
        self.var_forced = tk.BooleanVar(value=False)
        ttk.Checkbutton(props, text="强制显示", variable=self.var_forced,
                        command=self._apply_props).pack(side="left", padx=(8, 0))

        ttk.Label(props, text="（默认轨只会有一个）",
                  foreground="#888888").pack(side="left", padx=(10, 0))

        # ---- 底部按钮 ----
        bottom = ttk.Frame(top, padding=(12, 4, 12, 10))
        bottom.grid(row=3, column=0, sticky="ew")
        fluent.Button(bottom, text="确定", width=10, accent=True,
                      command=self._ok).pack(side="right")
        fluent.Button(bottom, text="取消", width=10,
                      command=self._cancel).pack(side="right", padx=8)

    # ---------------- 操作 ----------------
    def _add(self):
        indexes = self.lst_cand.curselection()
        if not indexes:
            return
        for idx in indexes:
            path = self.lst_cand.get(idx)
            if any(t.path == path for t in self.tracks):
                continue
            lang = detect_lang_from_name(path)
            kind = "bitmap" if os.path.splitext(path)[1].lower() in core.BITMAP_SUB_EXTS else "text"
            self.tracks.append(SubTrack(path, lang=lang, kind=kind,
                                        default=(len(self.tracks) == 0)))
        self.edited = True
        self._refresh_selected()

    def _remove(self):
        idx = self._selected_index()
        if idx is None:
            return
        self.tracks.pop(idx)
        self.edited = True
        self._refresh_selected()

    def _browse(self):
        paths = filedialog.askopenfilenames(
            title="选择字幕文件",
            filetypes=[("字幕文件", "*.srt *.ass *.ssa *.sup *.sub *.vtt"),
                       ("所有文件", "*.*")])
        for path in paths:
            if any(t.path == path for t in self.tracks):
                continue
            lang = detect_lang_from_name(path)
            kind = "bitmap" if os.path.splitext(path)[1].lower() in core.BITMAP_SUB_EXTS else "text"
            self.tracks.append(SubTrack(path, lang=lang, kind=kind,
                                        default=(len(self.tracks) == 0)))
        self.edited = True
        self._refresh_selected()

    def _move(self, delta):
        idx = self._selected_index()
        if idx is None:
            return
        new_idx = idx + delta
        if 0 <= new_idx < len(self.tracks):
            self.tracks[idx], self.tracks[new_idx] = self.tracks[new_idx], self.tracks[idx]
            self.edited = True
            self._refresh_selected(select=new_idx)

    def _selected_index(self):
        sel = self.lst_sel.curselection()
        return sel[0] if sel else None

    def _refresh_selected(self, select=None):
        self.lst_sel.delete(0, "end")
        for track in self.tracks:
            self.lst_sel.insert("end", track.label())
        if self.tracks:
            idx = select if select is not None else 0
            idx = max(0, min(idx, len(self.tracks) - 1))
            self.lst_sel.selection_clear(0, "end")
            self.lst_sel.selection_set(idx)
            self.lst_sel.activate(idx)
        self._on_select_track()

    def _on_select_track(self):
        idx = self._selected_index()
        if idx is None or idx >= len(self.tracks):
            return
        track = self.tracks[idx]
        label = "%s  %s" % (track.lang, LANG_NAMES.get(track.lang, track.lang))
        for value in self.cmb_lang["values"]:
            if value.startswith(track.lang + " "):
                label = value
                break
        self.var_lang.set(label)
        self.var_title.set(track.title)
        self.var_default.set(track.default)
        self.var_forced.set(track.forced)
        self._current = idx

    def _apply_props(self):
        idx = getattr(self, "_current", None)
        if idx is None or idx >= len(self.tracks):
            return
        track = self.tracks[idx]
        value = self.var_lang.get()
        track.lang = value.split()[0] if value else "und"
        track.title = self.var_title.get().strip()
        if self.var_default.get():
            for other in self.tracks:
                other.default = False
        track.default = self.var_default.get()
        track.forced = self.var_forced.get()
        self.edited = True
        self._refresh_selected(select=idx)
        self._current = idx

    def _ok(self):
        self.result = self.tracks
        self.top.destroy()

    def _cancel(self):
        self.result = None
        self.top.destroy()


# --------------------------------------------------------------------------
# 主界面
# --------------------------------------------------------------------------
class App(object):
    def __init__(self, root):
        self.root = root
        self.cfg = core.load_config()

        self.ffmpeg = self.cfg.get("ffmpeg") or core.find_ffmpeg()
        self.ffprobe = self.cfg.get("ffprobe") or core.find_ffprobe()
        self.caps = {}

        self.videos = []
        self.subs = []
        self.jobs = []              # list[Job]
        self.job_by_id = {}
        self.uid_seq = 0

        self.running = False
        self.cancel_event = threading.Event()
        self.msg_queue = queue.Queue()
        self.temp_dir = None
        self.started_at = None
        self.drop_handler = None
        self.last_output_dir = ""
        self._probe_generation = 0
        self._probe_cancel = threading.Event()
        self._closing = False

        self.options = Options()

        self._build_ui()
        self._load_config_to_ui()
        self._update_tool_labels()

        self.root.after(120, self._drain_queue)
        self.root.after(300, self._setup_drop)
        self.root.after(200, self._check_tools_async)

    # ---------------- 界面 ----------------
    def _build_ui(self):
        root = self.root
        root.title("%s v%s" % (APP_NAME, APP_VERSION))
        root.minsize(1120, 800)
        self._set_icon()

        self.style = fluent.apply_theme(root)
        fluent.apply_scaling(root)

        root.columnconfigure(0, weight=1)
        root.rowconfigure(0, weight=1)

        shell = tk.Frame(root, background=fluent.C_BG)
        shell.grid(row=0, column=0, sticky="nsew")
        shell.columnconfigure(0, weight=1)
        shell.rowconfigure(3, weight=1)
        self.shell = shell

        # ================= 应用栏 =================
        bar = tk.Frame(shell, background=fluent.C_BG)
        bar.grid(row=0, column=0, sticky="ew", padx=20, pady=(18, 4))
        tk.Label(bar, text="字幕视频批量合并", background=fluent.C_BG,
                 foreground=fluent.C_TEXT,
                 font=(fluent.Fonts.family, 15, "bold")).pack(side="left")
        tk.Label(bar, text="   软字幕 / 硬字幕 · 批量处理 · 默认不重编码",
                 background=fluent.C_BG, foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.subtitle()).pack(side="left")

        right = tk.Frame(bar, background=fluent.C_BG)
        right.pack(side="right")
        self.pill_ffmpeg = fluent.Pill(right, text="正在检测 ffmpeg…",
                                       color=fluent.C_TEXT_3)
        self.pill_ffmpeg.pack(side="left", padx=(0, 10))
        fluent.Button(right, text="更改", command=self.choose_ffmpeg).pack(side="left")

        # ================= 拖放卡片 =================
        drop_card = fluent.Card(shell, padding=0)
        drop_card.grid(row=1, column=0, sticky="ew", padx=20, pady=(8, 10))
        self.frame_drop = fluent.DropZone(
            drop_card, height=108,
            main_text="把视频、字幕或文件夹拖到这里",
            sub_text="也可以点下方按钮添加 · 支持 MP4 / MKV / MOV 视频，SRT / ASS / SUP 字幕")
        self.frame_drop.pack(fill="x", padx=1, pady=1)
        self.lbl_drop = self.frame_drop

        # ================= 工具条 =================
        tools = tk.Frame(shell, background=fluent.C_BG)
        tools.grid(row=2, column=0, sticky="ew", padx=20, pady=(0, 10))

        fluent.Button(tools, text="添加视频",
                      command=self.add_videos).pack(side="left")
        fluent.Button(tools, text="添加字幕",
                      command=self.add_subtitles).pack(side="left", padx=8)
        fluent.Button(tools, text="添加文件夹",
                      command=self.add_folder).pack(side="left")
        fluent.Button(tools, text="移除选中",
                      command=self.remove_selected).pack(side="left", padx=(16, 0))
        fluent.Button(tools, text="清空",
                      command=self.clear_all).pack(side="left", padx=8)

        self.lbl_count = tk.Label(tools, text="尚未添加文件",
                                  background=fluent.C_BG,
                                  foreground=fluent.C_TEXT_2,
                                  font=fluent.Fonts.body())
        self.lbl_count.pack(side="right")

        # ================= 文件列表卡片 =================
        list_card = fluent.Card(shell, padding=0)
        list_card.grid(row=3, column=0, sticky="nsew", padx=20, pady=(0, 10))
        list_card.rowconfigure(1, weight=1)
        list_card.columnconfigure(0, weight=1)

        list_head = tk.Frame(list_card, background=fluent.C_CARD)
        list_head.grid(row=0, column=0, sticky="ew", padx=16, pady=(14, 10))
        tk.Label(list_head, text="待处理文件", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT,
                 font=(fluent.Fonts.family, 10, "bold")).pack(side="left")
        tk.Label(list_head, text="   双击行可编辑字幕轨，右键有更多操作",
                 background=fluent.C_CARD, foreground=fluent.C_TEXT_3,
                 font=fluent.Fonts.small()).pack(side="left")

        cols = ("name", "duration", "res", "subs", "state", "progress",
                "speed", "eta", "output")
        heads = {"name": "视频", "duration": "时长", "res": "分辨率",
                 "subs": "字幕轨", "state": "状态", "progress": "进度",
                 "speed": "速度", "eta": "剩余", "output": "输出文件"}
        widths = {"name": 250, "duration": 62, "res": 92, "subs": 250,
                  "state": 158, "progress": 86, "speed": 66, "eta": 66,
                  "output": 230}
        anchors = {"duration": "center", "res": "center", "state": "center",
                   "progress": "center", "speed": "center", "eta": "center"}

        table = tk.Frame(list_card, background=fluent.C_CARD)
        table.grid(row=1, column=0, sticky="nsew", padx=16, pady=(0, 16))
        table.rowconfigure(0, weight=1)
        table.columnconfigure(0, weight=1)

        self.tree = ttk.Treeview(table, columns=cols, show="headings")
        for col in cols:
            self.tree.heading(col, text=heads[col])
            self.tree.column(col, width=widths[col],
                             anchor=anchors.get(col, "w"),
                             stretch=(col in ("name", "subs", "output", "state")))
        vsb = ttk.Scrollbar(table, orient="vertical", command=self.tree.yview)
        hsb = ttk.Scrollbar(table, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=vsb.set, xscrollcommand=hsb.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        vsb.grid(row=0, column=1, sticky="ns")
        hsb.grid(row=1, column=0, sticky="ew")

        self.tree.tag_configure("done", background="#EAF7EA",
                                foreground=fluent.C_TEXT)
        self.tree.tag_configure("warn", background="#FFF8E1",
                                foreground=fluent.C_WARN_TEXT)
        self.tree.tag_configure("running", background="#FFF7E0",
                                foreground=fluent.C_TEXT)
        self.tree.tag_configure("failed", background="#FDF3F2",
                                foreground=fluent.C_DANGER)
        self.tree.tag_configure("skipped", background=fluent.C_SUBTLE,
                                foreground=fluent.C_TEXT_2)
        self.tree.tag_configure("pending", background=fluent.C_CARD,
                                foreground=fluent.C_TEXT)
        self.tree.bind("<Double-1>", lambda e: self.edit_subtitles())
        self.tree.bind("<Button-3>", self._popup)

        self.menu = tk.Menu(root, tearoff=0)
        self.menu.add_command(label="设置字幕轨…", command=self.edit_subtitles)
        self.menu.add_command(label="移除选中", command=self.remove_selected)
        self.menu.add_separator()
        self.menu.add_command(label="打开视频所在文件夹",
                              command=lambda: self.open_path("video"))
        self.menu.add_command(label="打开输出文件",
                              command=lambda: self.open_path("output"))

        # ================= 设置卡片（TabView） =================
        set_card = fluent.Card(shell, padding=0)
        set_card.grid(row=4, column=0, sticky="ew", padx=20, pady=(0, 10))
        set_card.columnconfigure(0, weight=1)

        self.style.configure("TNotebook", background=fluent.C_CARD)
        notebook = ttk.Notebook(set_card)
        notebook.pack(fill="x", padx=1, pady=1)

        tab_out = tk.Frame(notebook, background=fluent.C_CARD)
        tab_enc = tk.Frame(notebook, background=fluent.C_CARD)
        tab_sub = tk.Frame(notebook, background=fluent.C_CARD)
        tab_log = tk.Frame(notebook, background=fluent.C_CARD)
        notebook.add(tab_out, text="  输出  ")
        notebook.add(tab_enc, text="  编码  ")
        notebook.add(tab_sub, text="  字幕与匹配  ")
        notebook.add(tab_log, text="  日志  ")
        self._notebook_tabs = [tab_out, tab_enc, tab_sub, tab_log]

        # ---- 输出 ----
        out_body = tk.Frame(tab_out, background=fluent.C_CARD)
        out_body.pack(fill="x", padx=16, pady=16)

        row1 = tk.Frame(out_body, background=fluent.C_CARD)
        row1.pack(fill="x", pady=(0, 12))
        tk.Label(row1, text="输出容器", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_container = tk.StringVar(value="mkv")
        cmb = ttk.Combobox(row1, textvariable=self.var_container, width=10,
                           state="readonly", values=["mkv", "mp4", "same"])
        cmb.pack(side="left", padx=(8, 24))
        cmb.bind("<<ComboboxSelected>>", lambda e: self._on_container_change())

        tk.Label(row1, text="字幕方式", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_mode = tk.StringVar(value="soft")
        cmb_mode = ttk.Combobox(row1, textvariable=self.var_mode, width=12,
                                state="readonly", values=["soft", "hard"])
        cmb_mode.pack(side="left", padx=(8, 24))
        cmb_mode.bind("<<ComboboxSelected>>", lambda e: self._on_mode_change())

        tk.Label(row1, text="文件名后缀", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_suffix = tk.StringVar(value="")
        ttk.Entry(row1, textvariable=self.var_suffix, width=10).pack(side="left",
                                                                     padx=(8, 0))

        row2 = tk.Frame(out_body, background=fluent.C_CARD)
        row2.pack(fill="x", pady=(0, 12))
        tk.Label(row2, text="输出目录", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_outdir = tk.StringVar(value="")
        ttk.Entry(row2, textvariable=self.var_outdir).pack(side="left", fill="x",
                                                           expand=True, padx=8)
        fluent.Button(row2, text="浏览", command=self.choose_outdir).pack(side="left")
        fluent.Button(row2, text="同源文件",
                      command=self.use_source_dir).pack(side="left", padx=8)

        row3 = tk.Frame(out_body, background=fluent.C_CARD)
        row3.pack(fill="x", pady=(0, 10))
        self.var_keep_subs = tk.BooleanVar(value=True)
        ttk.Checkbutton(row3, text="保留视频原有字幕轨",
                        variable=self.var_keep_subs).pack(side="left")
        self.var_keep_meta = tk.BooleanVar(value=True)
        ttk.Checkbutton(row3, text="保留元数据",
                        variable=self.var_keep_meta).pack(side="left", padx=(20, 0))
        self.var_overwrite = tk.BooleanVar(value=True)
        ttk.Checkbutton(row3, text="允许覆盖同名文件",
                        variable=self.var_overwrite).pack(side="left", padx=(20, 0))

        row4 = tk.Frame(out_body, background=fluent.C_CARD)
        row4.pack(fill="x")
        self.var_verify = tk.BooleanVar(value=True)
        ttk.Checkbutton(row4, text="处理完成后校验输出（核对字幕轨、音轨与时长）",
                        variable=self.var_verify).pack(side="left")

        # ---- 编码 ----
        enc_body = tk.Frame(tab_enc, background=fluent.C_CARD)
        enc_body.pack(fill="x", padx=16, pady=16)

        erow1 = tk.Frame(enc_body, background=fluent.C_CARD)
        erow1.pack(fill="x", pady=(0, 12))
        tk.Label(erow1, text="视频编码", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_vcodec = tk.StringVar(value="copy")
        self.cmb_vcodec = ttk.Combobox(erow1, textvariable=self.var_vcodec,
                                       width=14, state="readonly",
                                       values=["copy", "libx264", "libx265",
                                               "h264_nvenc"])
        self.cmb_vcodec.pack(side="left", padx=(8, 24))
        self.cmb_vcodec.bind("<<ComboboxSelected>>",
                             lambda e: self._on_vcodec_change())

        tk.Label(erow1, text="音频编码", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_acodec = tk.StringVar(value="copy")
        ttk.Combobox(erow1, textvariable=self.var_acodec, width=10,
                     state="readonly", values=["copy", "aac"]).pack(side="left",
                                                                    padx=(8, 24))

        tk.Label(erow1, text="并发任务", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_workers = tk.StringVar(value="1")
        ttk.Spinbox(erow1, from_=1, to=4, increment=1, width=5,
                    textvariable=self.var_workers).pack(side="left", padx=(8, 0))

        erow2 = tk.Frame(enc_body, background=fluent.C_CARD)
        erow2.pack(fill="x")
        tk.Label(erow2, text="CRF", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_crf = tk.StringVar(value="20")
        ttk.Spinbox(erow2, from_=0, to=51, increment=1, width=6,
                    textvariable=self.var_crf).pack(side="left", padx=(8, 24))
        tk.Label(erow2, text="preset", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_preset = tk.StringVar(value="medium")
        ttk.Combobox(erow2, textvariable=self.var_preset, width=12,
                     state="readonly",
                     values=["ultrafast", "veryfast", "fast", "medium",
                             "slow", "veryslow"]).pack(side="left", padx=(8, 12))
        tk.Label(erow2, text="（仅重新编码时生效）", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_3,
                 font=fluent.Fonts.small()).pack(side="left")

        # ---- 字幕与匹配 ----
        sub_body = tk.Frame(tab_sub, background=fluent.C_CARD)
        sub_body.pack(fill="x", padx=16, pady=16)

        srow1 = tk.Frame(sub_body, background=fluent.C_CARD)
        srow1.pack(fill="x", pady=(0, 12))
        self.var_convert = tk.BooleanVar(value=True)
        ttk.Checkbutton(srow1, text="字幕自动转为 UTF-8（中文/日文不乱码）",
                        variable=self.var_convert).pack(side="left")
        self.var_recursive = tk.BooleanVar(value=True)
        ttk.Checkbutton(srow1, text="扫描子文件夹",
                        variable=self.var_recursive).pack(side="left", padx=(24, 0))

        srow2 = tk.Frame(sub_body, background=fluent.C_CARD)
        srow2.pack(fill="x", pady=(0, 12))
        tk.Label(srow2, text="文件名匹配阈值", background=fluent.C_CARD,
                 foreground=fluent.C_TEXT_2,
                 font=fluent.Fonts.small()).pack(side="left")
        self.var_threshold = tk.StringVar(value="0.72")
        ttk.Spinbox(srow2, from_=0.4, to=1.0, increment=0.02, width=7,
                    textvariable=self.var_threshold).pack(side="left", padx=(8, 12))
        fluent.Button(srow2, text="重新匹配", command=self.rematch).pack(side="left")

        srow3 = tk.Frame(sub_body, background=fluent.C_CARD)
        srow3.pack(fill="x", pady=(0, 12))
        self.var_hard_multi = tk.BooleanVar(value=True)
        ttk.Checkbutton(srow3, text="硬字幕时叠加全部字幕轨（关闭则只烧录第一条）",
                        variable=self.var_hard_multi).pack(side="left")

        self.lbl_hint = tk.Label(sub_body, text="", background=fluent.C_CARD,
                                 foreground=fluent.C_WARN_TEXT, justify="left",
                                 anchor="w", wraplength=900,
                                 font=fluent.Fonts.small())
        self.lbl_hint.pack(fill="x")
        # 提示文字宽度随窗口自适应
        self.lbl_hint.bind(
            "<Configure>",
            lambda e: self.lbl_hint.configure(wraplength=max(240, e.width - 4)))

        # ---- 日志 ----
        log_body = tk.Frame(tab_log, background=fluent.C_CARD)
        log_body.pack(fill="both", expand=True, padx=16, pady=16)
        log_body.rowconfigure(0, weight=1)
        log_body.columnconfigure(0, weight=1)
        self.txt_log = tk.Text(log_body, height=8, wrap="word",
                               font=(fluent.Fonts.mono, 9), relief="flat",
                               background=fluent.C_SUBTLE,
                               foreground=fluent.C_TEXT,
                               highlightthickness=1,
                               highlightbackground=fluent.C_BORDER,
                               padx=8, pady=8)
        self.txt_log.grid(row=0, column=0, sticky="nsew")
        lsb = ttk.Scrollbar(log_body, orient="vertical",
                            command=self.txt_log.yview)
        lsb.grid(row=0, column=1, sticky="ns")
        self.txt_log.configure(yscrollcommand=lsb.set, state="disabled")

        # ================= 底部操作栏 =================
        bottom = tk.Frame(shell, background=fluent.C_BG)
        bottom.grid(row=5, column=0, sticky="ew", padx=20, pady=(0, 18))
        bottom.columnconfigure(1, weight=1)

        self.btn_start = fluent.HeroButton(bottom, text="开始处理",
                                           command=self.start)
        self.btn_start.grid(row=0, column=0, sticky="w")
        self.btn_stop = fluent.Button(bottom, text="停止", command=self.stop)
        self.btn_stop.grid(row=0, column=1, sticky="w", padx=(10, 0))

        status_area = tk.Frame(bottom, background=fluent.C_BG)
        status_area.grid(row=0, column=2, sticky="e")
        self.lbl_status = tk.Label(status_area, text="就绪",
                                   background=fluent.C_BG,
                                   foreground=fluent.C_TEXT_2,
                                   font=fluent.Fonts.body())
        self.lbl_status.pack(anchor="e")
        self.progress = fluent.ProgressBar(status_area, width=300, height=6)
        self.progress.pack(anchor="e", pady=(6, 0))

        action_row = tk.Frame(bottom, background=fluent.C_BG)
        action_row.grid(row=1, column=0, columnspan=3, sticky="ew", pady=(14, 0))
        fluent.Button(action_row, text="打开输出目录",
                      command=self.open_output_dir).pack(side="left")
        fluent.Button(action_row, text="导出报告",
                      command=self.export_report).pack(side="left", padx=10)
        fluent.Button(action_row, text="清空日志",
                      command=self.clear_log).pack(side="left")

        self._center(1220, 900)
        root.protocol("WM_DELETE_WINDOW", self.on_close)
        root.after(150, self._apply_window_effects)
        root.after(300, self._equalize_tabs)

    def _equalize_tabs(self):
        """让所有设置页等高，切换标签时界面不跳动。"""
        tabs = getattr(self, "_notebook_tabs", None)
        if not tabs:
            return
        try:
            heights = []
            for tab in tabs:
                tab.update_idletasks()
                heights.append(tab.winfo_reqheight())
            target = max(heights) if heights else 0
            if target <= 0:
                return
            for tab in tabs:
                tab.configure(height=target)
                tab.pack_propagate(False)
        except Exception:
            pass

    def _apply_window_effects(self):
        try:
            self.root.update_idletasks()
            fluent.apply_window_effects(self.root.winfo_id(), rounded=True,
                                        caption_color=fluent.C_BG, mica=False)
        except Exception:
            pass


    def _set_icon(self):
        """设置窗口图标（打包后从临时资源目录读取 app.ico）。"""
        candidates = []
        base = getattr(sys, "_MEIPASS", None)
        if base:
            candidates.append(os.path.join(base, "app.ico"))
        candidates.append(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                       "app.ico"))
        for path in candidates:
            if os.path.isfile(path):
                try:
                    self.root.iconbitmap(path)
                    return
                except Exception:
                    continue

    def _center(self, width, height):
        self.root.update_idletasks()
        sw = self.root.winfo_screenwidth()
        sh = self.root.winfo_screenheight()
        # 逻辑尺寸 -> 当前 DPI 下的物理尺寸
        width = min(fluent.scaled(width), sw - 60)
        height = min(fluent.scaled(height), sh - 80)
        x = max(0, (sw - width) // 2)
        y = max(0, (sh - height) // 4)
        self.root.geometry("%dx%d+%d+%d" % (width, height, x, y))

    # ---------------- 配置 ----------------
    def _load_config_to_ui(self):
        cfg = self.cfg
        if cfg.get("container"):
            self.var_container.set(cfg["container"])
        if cfg.get("mode"):
            self.var_mode.set(cfg["mode"])
        if cfg.get("vcodec"):
            self.var_vcodec.set(cfg["vcodec"])
        if cfg.get("acodec"):
            self.var_acodec.set(cfg["acodec"])
        if cfg.get("crf") is not None:
            self.var_crf.set(str(cfg["crf"]))
        if cfg.get("preset"):
            self.var_preset.set(cfg["preset"])
        if cfg.get("workers"):
            self.var_workers.set(str(cfg["workers"]))
        if cfg.get("suffix") is not None:
            self.var_suffix.set(cfg["suffix"])
        if cfg.get("outdir"):
            self.var_outdir.set(cfg["outdir"])
        if "keep_existing_subs" in cfg:
            self.var_keep_subs.set(bool(cfg["keep_existing_subs"]))
        if "keep_metadata" in cfg:
            self.var_keep_meta.set(bool(cfg["keep_metadata"]))
        if "convert_encoding" in cfg:
            self.var_convert.set(bool(cfg["convert_encoding"]))
        if "hard_multi" in cfg:
            self.var_hard_multi.set(bool(cfg["hard_multi"]))
        if "verify" in cfg:
            self.var_verify.set(bool(cfg["verify"]))
        if cfg.get("threshold"):
            self.var_threshold.set(str(cfg["threshold"]))
        if cfg.get("recursive") is not None:
            self.var_recursive.set(bool(cfg["recursive"]))
        self._last_dir = cfg.get("last_dir") or os.path.expanduser("~")

    def _save_config(self):
        self.cfg.update({
            "ffmpeg": self.ffmpeg or "",
            "ffprobe": self.ffprobe or "",
            "container": self.var_container.get(),
            "mode": self.var_mode.get(),
            "vcodec": self.var_vcodec.get(),
            "acodec": self.var_acodec.get(),
            "crf": self.var_crf.get(),
            "preset": self.var_preset.get(),
            "workers": self.var_workers.get(),
            "suffix": self.var_suffix.get(),
            "outdir": self.var_outdir.get(),
            "keep_existing_subs": self.var_keep_subs.get(),
            "keep_metadata": self.var_keep_meta.get(),
            "convert_encoding": self.var_convert.get(),
            "hard_multi": self.var_hard_multi.get(),
            "verify": self.var_verify.get(),
            "threshold": self.var_threshold.get(),
            "recursive": self.var_recursive.get(),
            "last_dir": getattr(self, "_last_dir", ""),
        })
        core.save_config(self.cfg)

    # ---------------- 工具定位 ----------------
    def _update_tool_labels(self):
        if not self.ffmpeg:
            self.pill_ffmpeg.set("ffmpeg 未找到", fluent.C_DANGER)
            return
        base = core.bundled_dir()
        if base and os.path.abspath(os.path.dirname(self.ffmpeg)) == os.path.abspath(base):
            self.pill_ffmpeg.set("ffmpeg 内置版本", fluent.C_SUCCESS)
        else:
            self.pill_ffmpeg.set("ffmpeg 已就绪", fluent.C_SUCCESS)

    def choose_ffmpeg(self):
        path = filedialog.askopenfilename(
            title="选择 ffmpeg.exe",
            initialdir=os.path.dirname(self.ffmpeg) if self.ffmpeg else "C:/",
            filetypes=[("ffmpeg", "ffmpeg.exe ffmpeg"), ("所有文件", "*.*")])
        if path:
            self.ffmpeg = path
            directory = os.path.dirname(path)
            guess = os.path.join(directory, "ffprobe.exe")
            if os.path.isfile(guess):
                self.ffprobe = guess
            self._save_config()
            self._update_tool_labels()
            self.log("已设置 ffmpeg：%s" % path)
            self._check_tools_async()

    def _check_tools_async(self):
        def worker():
            caps = core.ffmpeg_capabilities(self.ffmpeg)
            version = core.ffmpeg_version(self.ffmpeg)
            self.msg_queue.put(("caps", (caps, version)))
        threading.Thread(target=worker, daemon=True).start()

    # ---------------- 拖放 ----------------
    def _setup_drop(self):
        if dragdrop is None:
            self.frame_drop.set_text(
                main_text="拖放组件不可用",
                sub_text="请使用下方「添加视频 / 添加字幕 / 添加文件夹」按钮")
            return
        try:
            self.root.update_idletasks()
            hwnd = fluent.get_root_hwnd(self.root.winfo_id())
            handler = dragdrop.enable_drop(hwnd, self.on_drop)
        except Exception as exc:
            handler = None
            self.log("拖放初始化失败：%s" % exc)
        if handler:
            self.drop_handler = handler
        else:
            self.frame_drop.set_text(
                main_text="拖放不可用",
                sub_text="请使用下方「添加视频 / 添加字幕 / 添加文件夹」按钮")

    def on_drop(self, paths):
        self.add_paths(paths)

    # ---------------- 添加文件 ----------------
    def add_videos(self):
        paths = filedialog.askopenfilenames(
            title="选择视频文件",
            initialdir=getattr(self, "_last_dir", "C:/"),
            filetypes=[("视频文件", " ".join("*" + e for e in sorted(core.VIDEO_EXTS))),
                       ("所有文件", "*.*")])
        if paths:
            self.add_paths(paths)

    def add_subtitles(self):
        paths = filedialog.askopenfilenames(
            title="选择字幕文件",
            initialdir=getattr(self, "_last_dir", "C:/"),
            filetypes=[("字幕文件", "*.srt *.ass *.ssa *.sup *.sub *.vtt"),
                       ("所有文件", "*.*")])
        if paths:
            self.add_paths(paths)

    def add_folder(self):
        folder = filedialog.askdirectory(title="选择文件夹",
                                         initialdir=getattr(self, "_last_dir", "C:/"))
        if folder:
            self.add_paths([folder])

    def add_paths(self, paths):
        if self.running:
            messagebox.showinfo(APP_NAME, "正在处理中，请先停止再添加文件。")
            return
        if not paths:
            return
        videos, subs, _skipped = core.collect_files(paths, self.var_recursive.get())
        if not videos and not subs:
            messagebox.showinfo(APP_NAME, "没有找到视频或字幕文件。")
            return

        if paths and os.path.isdir(paths[0]):
            self._last_dir = paths[0]
        elif os.path.isfile(paths[0]):
            self._last_dir = os.path.dirname(paths[0])

        new_videos = [v for v in videos if v not in self.videos]
        new_subs = [s for s in subs if s not in self.subs]
        self.videos.extend(new_videos)
        self.subs.extend(new_subs)
        self.log("添加：视频 %d 个，字幕 %d 个" % (len(new_videos), len(new_subs)))
        self.rematch()

    def remove_selected(self):
        if self.running:
            return
        jobs = self._selected_jobs()
        if not jobs:
            return
        for job in jobs:
            if job.video in self.videos:
                self.videos.remove(job.video)
            self.jobs.remove(job)
            self.job_by_id.pop(job.uid, None)
            self.tree.delete(job.uid)
        self.lbl_count.configure(text="视频 %d 个 / 字幕 %d 个"
                                 % (len(self.videos), len(self.subs)))

    def clear_all(self):
        if self.running:
            return
        self.videos = []
        self.subs = []
        self.jobs = []
        self.job_by_id = {}
        self.tree.delete(*self.tree.get_children())
        self.lbl_count.configure(text="未添加文件")
        self.progress["value"] = 0
        self.set_status("已清空")

    # ---------------- 匹配 ----------------
    def rematch(self):
        if self.running:
            return
        try:
            threshold = float(self.var_threshold.get())
        except ValueError:
            threshold = 0.72
            self.var_threshold.set("0.72")

        mapping, unmatched = core.match_subtitles(self.videos, self.subs, threshold)

        old_jobs = {job.video: job for job in self.jobs}
        self.jobs = []
        self.job_by_id = {}
        self.tree.delete(*self.tree.get_children())

        for video in self.videos:
            old = old_jobs.get(video)
            uid = old.uid if old else self._next_uid()
            job = Job(video, [], "", duration=(old.duration if old else None),
                      info=(old.info if old else {}))
            job.uid = uid
            job.user_edited = getattr(old, "user_edited", False)

            auto_paths = mapping.get(video, [])
            if job.user_edited and old is not None:
                tracks = []
                auto_set = set(auto_paths)
                for track in old.subs:
                    if track.path not in auto_set:
                        tracks.append(track.clone())
                for path in auto_paths:
                    if not any(t.path == path for t in tracks):
                        tracks.append(self._make_track(path))
                job.subs = tracks
            else:
                job.subs = [self._make_track(p) for p in auto_paths]
                for i, track in enumerate(job.subs):
                    track.default = (i == 0)

            self.jobs.append(job)
            self.job_by_id[uid] = job
            self._insert_job(job)

        self.lbl_count.configure(text="视频 %d 个 / 字幕 %d 个 / 未匹配字幕 %d 个"
                                 % (len(self.videos), len(self.subs),
                                    len(unmatched)))
        self.set_status("匹配完成，共 %d 个视频" % len(self.videos))
        if unmatched and self.videos:
            self.log("未匹配的字幕 %d 个（可双击视频行手动指定）" % len(unmatched))
        self._probe_jobs_async()

    def _make_track(self, path):
        kind = "bitmap" if os.path.splitext(path)[1].lower() in core.BITMAP_SUB_EXTS else "text"
        return SubTrack(path, lang=detect_lang_from_name(path), kind=kind)

    def _next_uid(self):
        self.uid_seq += 1
        return "j%d" % self.uid_seq

    def _insert_job(self, job):
        subs_text = "、".join(t.label() for t in job.subs) if job.subs else "（无）"
        self.tree.insert("", "end", iid=job.uid, values=(
            job.name,
            format_duration(job.duration) if job.duration else "-",
            self._res_text(job),
            subs_text,
            job.state,
            "",
            "",
            "",
            os.path.basename(job.output) if job.output else "",
        ), tags=("pending",))

    def _res_text(self, job):
        info = job.info or {}
        if info.get("width") and info.get("height"):
            return "%dx%d" % (info["width"], info["height"])
        return "-"

    @staticmethod
    def _plan_text(plan):
        """把 plan 描述成一句人话，便于日志和报告阅读。"""
        if not plan:
            return ""
        if plan.get("mode") == "hard":
            return "烧录 %d 条字幕，无字幕轨" % plan.get("burned_subs", 0)
        return "%d 条字幕轨" % plan.get("expected_subs", 0)

    def _probe_jobs_async(self, only_missing=True):
        """并发读取媒体信息。新的一轮会取消上一轮，避免过期结果覆盖界面。"""
        if not self.ffprobe or not self.jobs:
            return
        targets = [j for j in self.jobs if not only_missing or j.duration is None]
        if not targets:
            return

        self._probe_cancel.set()
        cancel = threading.Event()
        self._probe_cancel = cancel
        self._probe_generation += 1
        generation = self._probe_generation

        by_path = {j.video: j.uid for j in targets}
        paths = list(by_path.keys())
        queue_ref = self.msg_queue

        def on_result(path, info):
            if generation != self._probe_generation:
                return
            uid = by_path.get(path)
            if uid:
                queue_ref.put(("probe", (uid, info)))

        def worker():
            core.probe_many(self.ffprobe, paths, PROBE_WORKERS, on_result, cancel)

        threading.Thread(target=worker, daemon=True).start()

    def _ensure_probed(self, jobs):
        """处理前同步补齐媒体信息：字幕轨序号、进度百分比都依赖它。"""
        missing = [j.video for j in jobs if not j.info]
        if not missing or not self.ffprobe:
            return
        self.set_status("正在读取媒体信息（%d 个）…" % len(missing))
        self.root.update_idletasks()
        results = core.probe_many(self.ffprobe, missing, PROBE_WORKERS)
        for job in jobs:
            info = results.get(job.video)
            if info:
                job.info = info
                job.duration = info.get("duration") or job.duration
                self._update_job_row(job)

    def _drop_invalid_subs(self):
        """处理前剔除 ffmpeg 无法识别的字幕（空文件、乱码、损坏的图形字幕）。

        否则一个坏字幕会让整个视频任务失败，而用户其实只想合并好的那条。
        """
        if not self.ffprobe:
            return
        paths = set()
        for job in self.jobs:
            for track in job.subs:
                if track.enabled:
                    paths.add(track.path)
        if not paths:
            return

        self.set_status("正在校验字幕文件（%d 个）…" % len(paths))
        self.root.update_idletasks()

        bad = {}
        with ThreadPoolExecutor(max_workers=PROBE_WORKERS) as pool:
            futures = {pool.submit(core.probe_subtitle, self.ffprobe, p): p
                       for p in paths}
            for future in as_completed(futures):
                path = futures[future]
                try:
                    ok, detail = future.result()
                except Exception:
                    ok, detail = True, ""
                if not ok:
                    bad[path] = detail

        if not bad:
            return
        for job in self.jobs:
            kept = []
            for track in job.subs:
                if track.path in bad:
                    self.log("[跳过] %s：字幕 %s 无法识别（%s）"
                             % (job.name, track.name, bad[track.path]))
                else:
                    kept.append(track)
            job.subs = kept
            self._update_job_row(job)
        self.log("已剔除 %d 个无效字幕文件，其余照常处理" % len(bad))

    # ---------------- 字幕编辑 ----------------
    def edit_subtitles(self):
        if self.running:
            return
        jobs = self._selected_jobs()
        if not jobs:
            return
        job = jobs[0]
        directory = os.path.dirname(job.video)
        candidates = [s for s in self.subs if os.path.dirname(s) == directory]
        if not candidates:
            candidates = self.subs

        dialog = SubtitleDialog(self.root, job, candidates, self.subs)
        if dialog.result is None:
            return
        job.subs = dialog.result
        job.user_edited = True
        self._update_job_row(job)
        self.set_status("已更新 %s 的字幕轨" % job.name)

    # ---------------- 处理 ----------------
    def start(self):
        if self.running:
            return
        if not self.ffmpeg:
            messagebox.showerror(APP_NAME, "未找到 ffmpeg，请先手动指定。")
            return
        if not self.jobs:
            messagebox.showinfo(APP_NAME, "请先添加视频文件。")
            return

        options = self._collect_options()
        if options is None:
            return
        self.options = options

        if options.mode == "hard" and self.caps and not self.caps.get("libass"):
            if not messagebox.askyesno(
                    APP_NAME,
                    "当前 ffmpeg 未编译 libass，硬字幕（烧录）可能无法工作。\n"
                    "仍要继续吗？"):
                return

        if not self.var_outdir.get().strip() and self.videos:
            messagebox.showinfo(APP_NAME, "请先选择输出目录。")
            return

        # 处理前补齐媒体信息，保证字幕轨序号与进度计算正确
        self._ensure_probed(self.jobs)
        # 剔除无法识别的字幕，避免单个坏字幕毁掉整个任务
        self._drop_invalid_subs()

        os.makedirs(options.output_dir, exist_ok=True)
        self.temp_dir = tempfile.mkdtemp(prefix="submerge_")
        self.cancel_event = threading.Event()
        self.running = True
        self.started_at = time.time()
        self.btn_start.set_enabled(False)
        self.btn_stop.set_enabled(True)

        total = len(self.jobs)
        self.progress.configure(maximum=total, value=0)
        self.done_count = 0

        for job in self.jobs:
            job.state = Job.STATE_PENDING
            job.progress = 0.0
            job.speed = ""
            job.eta = ""
            job.message = ""
            job.output = build_output_path(job.video, options, options.output_dir)
            self._update_job_row(job)

        self.set_status("开始处理 %d 个视频..." % total)
        self.log("=" * 60)
        self.log("开始处理：%d 个视频，容器=%s，方式=%s，输出=%s"
                 % (total, options.container,
                    "硬字幕" if options.mode == "hard" else "软字幕",
                    options.output_dir))

        thread = threading.Thread(target=self._dispatch, args=(list(self.jobs),),
                                  daemon=True)
        thread.start()

    def _collect_options(self):
        options = Options()
        options.container = self.var_container.get()
        options.mode = self.var_mode.get()
        options.vcodec = self.var_vcodec.get()
        options.acodec = self.var_acodec.get()
        options.keep_existing_subs = self.var_keep_subs.get()
        options.keep_metadata = self.var_keep_meta.get()
        options.convert_encoding = self.var_convert.get()
        options.hard_multi = self.var_hard_multi.get()
        options.verify = self.var_verify.get()
        options.suffix = self.var_suffix.get().strip()
        options.overwrite = self.var_overwrite.get()
        options.output_dir = self.var_outdir.get().strip()

        try:
            options.crf = int(float(self.var_crf.get()))
        except ValueError:
            options.crf = 20
        options.preset = self.var_preset.get()
        try:
            options.workers = max(1, min(4, int(float(self.var_workers.get()))))
        except ValueError:
            options.workers = 1

        if not options.output_dir:
            return None

        if options.container == "mp4":
            options.keep_existing_subs = options.keep_existing_subs and True
        if options.mode == "hard" and options.vcodec == "copy":
            self.log("提示：硬字幕需要重新编码视频，已强制使用 libx264。")
            options.vcodec = "libx264"
        return options

    def _dispatch(self, jobs):
        try:
            with ThreadPoolExecutor(max_workers=self.options.workers) as pool:
                futures = {pool.submit(self._run_job, job): job for job in jobs}
                for future in as_completed(futures):
                    job = futures[future]
                    if self.cancel_event.is_set():
                        for pending in futures:
                            pending.cancel()
                        break
                    try:
                        future.result()
                    except Exception as exc:
                        job.state = Job.STATE_FAILED
                        job.message = str(exc)[:200]
                    self.msg_queue.put(("job_done", job))
                    if self.cancel_event.is_set():
                        break
        except Exception as exc:  # pragma: no cover
            self.msg_queue.put(("fatal", "%s\n%s" % (exc, traceback.format_exc())))
        self.msg_queue.put(("all_done", None))

    def _run_job(self, job):
        if self.cancel_event.is_set():
            job.state = Job.STATE_CANCELLED
            return
        options = self.options
        job.state = Job.STATE_RUNNING
        job.started_at = time.time()
        job.progress = 0.0
        self.msg_queue.put(("job_update", job))

        # 1) 字幕预处理（编码转换 / 硬字幕副本）
        enabled = [t for t in job.subs if t.enabled]
        cwd = None
        if not enabled:
            job.state = Job.STATE_SKIPPED
            job.message = "没有可用字幕轨，已跳过"
            self.msg_queue.put(("log", "[跳过] %s：%s" % (job.name, job.message)))
            return

        if options.mode == "hard":
            hard_dir = os.path.join(self.temp_dir, "hard")
            prepared, hard_failed = core.prepare_hard_subs(enabled, hard_dir,
                                                           options.convert_encoding)
            cwd = hard_dir
            for name, reason in hard_failed:
                self.msg_queue.put(("log", "[跳过] %s：字幕 %s 预处理失败（%s）"
                                    % (job.name, name, reason)))
            if not prepared:
                job.state = Job.STATE_FAILED
                job.message = "字幕文件无法读取"
                return
        else:
            prepared = []
            for track in enabled:
                if options.convert_encoding and track.kind == "text":
                    new_path, is_temp, encoding = ensure_utf8(track.path,
                                                               self.temp_dir)
                    if encoding:
                        track.encoding = encoding
                    if is_temp:
                        clone = track.clone()
                        clone.path = new_path
                        prepared.append(clone)
                        continue
                prepared.append(track)

        if not prepared:
            job.state = Job.STATE_SKIPPED
            job.message = "没有可用字幕轨，已跳过"
            self.msg_queue.put(("log", "[跳过] %s：%s" % (job.name, job.message)))
            return

        temp_job = Job(job.video, prepared, job.output, job.duration, job.info)
        temp_job.uid = job.uid

        cmd, notes, plan = build_command(temp_job, options, self.ffmpeg)
        for note in notes:
            self.msg_queue.put(("log", "[提示] %s：%s" % (job.name, note)))

        command_text = " ".join('"%s"' % part if " " in part else part
                                for part in cmd)
        if len(command_text) > 460:
            command_text = command_text[:460] + " …（已截断）"
        self.msg_queue.put(("log", "[命令] %s" % command_text))

        def on_progress(percent, speed, eta):
            job.progress = percent
            job.speed = "%.2fx" % speed if speed > 0 else ""
            if eta is not None:
                job.eta = format_duration(eta)
            else:
                job.eta = "--:--"
            self.msg_queue.put(("progress", job))

        log_lines = []
        code, error = core.run_ffmpeg(cmd, job.duration, on_progress,
                                      self.cancel_event, log_lines, cwd=cwd)

        job.finished_at = time.time()
        if self.cancel_event.is_set():
            job.state = Job.STATE_CANCELLED
            job.message = "已取消"
        elif code == 0 and os.path.isfile(job.output):
            job.state = Job.STATE_DONE
            job.progress = 100.0
            job.message = ""
            try:
                job.size = os.path.getsize(job.output)
            except OSError:
                job.size = None
            # 输出校验：确认流结构真的符合预期（尤其是字幕是否挂上）
            if options.verify:
                ok, issues, _detail = core.verify_output(
                    self.ffprobe, job.output, plan, job.info, ffmpeg=self.ffmpeg)
                job.verify_ok = ok
                job.verify_issues = issues
                if ok:
                    self.msg_queue.put(("log", "[校验] %s：输出正常（%s）"
                                        % (job.name, self._plan_text(plan))))
                else:
                    for issue in issues:
                        self.msg_queue.put(("log", "[校验] %s：%s"
                                            % (job.name, issue)))
        else:
            job.state = Job.STATE_FAILED
            job.message = (error or "ffmpeg 返回码 %s" % code)[:200]
            for line in log_lines[-6:]:
                self.msg_queue.put(("log", "[错误] %s：%s" % (job.name, line)))

    def stop(self):
        if self.running:
            self.cancel_event.set()
            self.set_status("正在停止，请稍候...")
            self.btn_stop.set_enabled(False)

    # ---------------- 队列与界面更新 ----------------
    def _drain_queue(self):
        if self._closing:
            return
        try:
            while True:
                kind, payload = self.msg_queue.get_nowait()
                if kind == "progress":
                    self._update_progress_cell(payload)
                elif kind == "job_update":
                    self._update_job_row(payload)
                elif kind == "job_done":
                    self._on_job_done(payload)
                elif kind == "probe":
                    self._on_probe(payload)
                elif kind == "caps":
                    self._on_caps(payload)
                elif kind == "log":
                    self.log(payload)
                elif kind == "fatal":
                    self.log(payload)
                elif kind == "all_done":
                    self._on_all_done()
        except queue.Empty:
            pass
        self.root.after(120, self._drain_queue)

    def _on_probe(self, payload):
        uid, info = payload
        job = self.job_by_id.get(uid)
        if not job:
            return
        job.info = info
        job.duration = info.get("duration") or job.duration
        self._update_job_row(job)

    def _on_caps(self, payload):
        caps, version = payload
        self.caps = caps or {}
        if version:
            self.log("ffmpeg：%s" % version.split(" Copyright")[0])
        missing = [k for k in ("libass", "x264") if not self.caps.get(k)]
        if missing:
            self.log("注意：ffmpeg 缺少 %s" % "、".join(missing))
        self._update_hint()

    def _update_progress_cell(self, job):
        if not self.tree.exists(job.uid):
            return
        self.tree.set(job.uid, "progress", "%.1f%%" % job.progress)
        self.tree.set(job.uid, "speed", job.speed)
        self.tree.set(job.uid, "eta", job.eta)
        self.tree.set(job.uid, "state", "处理中 %.0f%%" % job.progress)

    def _update_job_row(self, job):
        if not self.tree.exists(job.uid):
            return
        subs_text = "、".join(t.label() for t in job.subs) if job.subs else "（无）"
        state_text = job.state
        if job.state == Job.STATE_RUNNING:
            state_text = "处理中 %.0f%%" % job.progress
        elif job.state == Job.STATE_DONE:
            if job.verify_ok is False:
                state_text = "完成（校验异常）"
            elif job.verify_ok:
                state_text = "完成（已校验）"
        elif job.state == Job.STATE_FAILED and job.message:
            state_text = "失败：%s" % job.message[:40]
        elif job.state == Job.STATE_SKIPPED and job.message:
            state_text = "跳过：%s" % job.message[:30]

        tag = "pending"
        if job.state == Job.STATE_DONE:
            tag = "warn" if job.verify_ok is False else "done"
        elif job.state == Job.STATE_RUNNING:
            tag = "running"
        elif job.state == Job.STATE_FAILED:
            tag = "failed"
        elif job.state in (Job.STATE_SKIPPED, Job.STATE_CANCELLED):
            tag = "skipped"

        self.tree.item(job.uid, values=(
            job.name,
            format_duration(job.duration) if job.duration else "-",
            self._res_text(job),
            subs_text,
            state_text,
            ("%.1f%%" % job.progress) if job.progress else "",
            job.speed,
            job.eta,
            os.path.basename(job.output) if job.output else "",
        ), tags=(tag,))

    def _on_job_done(self, job):
        self.done_count += 1
        self.progress["value"] = self.done_count
        self._update_job_row(job)
        elapsed = ""
        if job.started_at and job.finished_at:
            elapsed = "，用时 %s" % format_duration(job.finished_at - job.started_at)
        if job.state == Job.STATE_DONE:
            self.log("[完成] %s -> %s%s"
                     % (job.name, os.path.basename(job.output), elapsed))
            if job.verify_ok is False:
                self.log("[校验异常] %s：%s"
                         % (job.name, "；".join(job.verify_issues)))
        elif job.state == Job.STATE_FAILED:
            self.log("[失败] %s：%s" % (job.name, job.message))
        elif job.state == Job.STATE_SKIPPED:
            self.log("[跳过] %s：%s" % (job.name, job.message))
        elif job.state == Job.STATE_CANCELLED:
            self.log("[取消] %s" % job.name)
        self.set_status("已完成 %d / %d" % (self.done_count, len(self.jobs)))

    def _on_all_done(self):
        self.running = False
        self.btn_start.set_enabled(True)
        self.btn_stop.set_enabled(False)
        done = sum(1 for j in self.jobs if j.state == Job.STATE_DONE)
        failed = sum(1 for j in self.jobs if j.state == Job.STATE_FAILED)
        skipped = sum(1 for j in self.jobs if j.state == Job.STATE_SKIPPED)
        cancelled = sum(1 for j in self.jobs if j.state == Job.STATE_CANCELLED)
        warned = sum(1 for j in self.jobs
                     if j.state == Job.STATE_DONE and j.verify_ok is False)
        total_time = format_duration(time.time() - (self.started_at or time.time()))
        self.set_status("全部结束：完成 %d / 失败 %d / 跳过 %d%s，总用时 %s"
                        % (done, failed, skipped,
                           " / 取消 %d" % cancelled if cancelled else "",
                           total_time))
        self.log("=" * 60)
        self.log("全部结束：完成 %d，失败 %d，跳过 %d，取消 %d，总用时 %s"
                 % (done, failed, skipped, cancelled, total_time))
        if warned:
            self.log("注意：%d 个输出文件校验异常，详见上方 [校验异常] 行" % warned)
        core.clear_utf8_cache()
        self._cleanup_temp()
        self.last_output_dir = self.options.output_dir

    def _cleanup_temp(self):
        if self.temp_dir and os.path.isdir(self.temp_dir):
            try:
                shutil.rmtree(self.temp_dir, ignore_errors=True)
            except Exception:
                pass
        self.temp_dir = None

    def _on_container_change(self):
        self._update_hint()

    def _on_mode_change(self):
        self._update_hint()
        if self.var_mode.get() == "hard" and self.var_vcodec.get() == "copy":
            self.var_vcodec.set("libx264")

    def _on_vcodec_change(self):
        self._update_hint()

    def _update_hint(self):
        hints = []
        if self.var_container.get() == "mp4":
            hints.append("MP4 不支持图形字幕（SUP），这类字幕会被跳过；"
                         "文本字幕将转为 mov_text（ASS 样式可能丢失）。")
        if self.var_mode.get() == "hard":
            hints.append("硬字幕会重新编码视频，速度较慢；"
                         "中文字幕依赖系统字体，若出现方块字请改用软字幕。")
            if self.caps and not self.caps.get("libass"):
                hints.append("当前 ffmpeg 缺少 libass，硬字幕可能失败。")
        if self.var_vcodec.get() == "copy" and self.var_container.get() == "mp4":
            hints.append("源视频为非 H.264/HEVC 编码时，MP4 可能需要重新编码。")
        self.lbl_hint.configure(text=" ".join(hints))

    # ---------------- 杂项 ----------------
    def choose_outdir(self):
        initial = self.var_outdir.get() or getattr(self, "_last_dir", "C:/")
        folder = filedialog.askdirectory(title="选择输出目录", initialdir=initial)
        if folder:
            self.var_outdir.set(folder)
            self._last_dir = folder

    def use_source_dir(self):
        if not self.videos:
            return
        self.var_outdir.set(os.path.dirname(self.videos[0]))

    def open_output_dir(self):
        folder = self.var_outdir.get().strip() or getattr(self, "last_output_dir", "")
        if folder and os.path.isdir(folder):
            self._open_path(folder)
        else:
            messagebox.showinfo(APP_NAME, "输出目录还不存在。")

    def open_path(self, which):
        jobs = self._selected_jobs()
        if not jobs:
            return
        job = jobs[0]
        if which == "video":
            self._open_path(os.path.dirname(job.video))
        else:
            if job.output and os.path.isfile(job.output):
                self._open_path(os.path.dirname(job.output))
            else:
                messagebox.showinfo(APP_NAME, "输出文件还不存在。")

    @staticmethod
    def _open_path(path):
        if not path or not os.path.exists(path):
            return
        try:
            if os.name == "nt":
                if os.path.isdir(path):
                    os.startfile(path)
                else:
                    subprocess.Popen(["explorer", "/select,", os.path.normpath(path)])
            elif sys.platform == "darwin":
                subprocess.Popen(["open", path])
            else:
                subprocess.Popen(["xdg-open", path])
        except Exception:
            pass

    def _selected_jobs(self):
        picked = []
        for item in self.tree.selection():
            job = self.job_by_id.get(item)
            if job:
                picked.append(job)
        return picked

    def _popup(self, event):
        item = self.tree.identify_row(event.y)
        if item:
            if item not in self.tree.selection():
                self.tree.selection_set(item)
            self.menu.tk_popup(event.x_root, event.y_root)

    def export_report(self):
        if not self.jobs:
            messagebox.showinfo(APP_NAME, "没有可导出的任务。")
            return
        default = "合并报告_%s.csv" % datetime.now().strftime("%Y%m%d_%H%M%S")
        initial_dir = self.var_outdir.get().strip() or getattr(self, "_last_dir", "C:/")
        path = filedialog.asksaveasfilename(title="导出报告",
                                            defaultextension=".csv",
                                            initialfile=default,
                                            initialdir=initial_dir,
                                            filetypes=[("CSV", "*.csv")])
        if not path:
            return
        try:
            with open(path, "w", newline="", encoding="utf-8-sig") as fh:
                writer = csv.writer(fh)
                writer.writerow(["视频文件", "分辨率", "时长(秒)", "字幕轨",
                                 "状态", "输出校验", "输出文件", "大小(字节)", "说明"])
                for job in self.jobs:
                    if job.verify_ok is True:
                        verify_text = "通过"
                    elif job.verify_ok is False:
                        verify_text = "；".join(job.verify_issues)
                    else:
                        verify_text = "未校验"
                    writer.writerow([
                        job.video,
                        self._res_text(job),
                        ("%.3f" % job.duration) if job.duration else "",
                        " | ".join("%s[%s]" % (os.path.basename(t.path), t.lang)
                                   for t in job.subs if t.enabled),
                        job.state,
                        verify_text,
                        job.output,
                        job.size or "",
                        job.message,
                    ])
        except Exception as exc:
            messagebox.showerror(APP_NAME, "导出失败：%s" % exc)
            return
        self.log("已导出报告：%s" % path)

    def clear_log(self):
        self.txt_log.configure(state="normal")
        self.txt_log.delete("1.0", "end")
        self.txt_log.configure(state="disabled")

    def log(self, text):
        self.txt_log.configure(state="normal")
        stamp = datetime.now().strftime("%H:%M:%S")
        self.txt_log.insert("end", "[%s] %s\n" % (stamp, text))
        self.txt_log.see("end")
        self.txt_log.configure(state="disabled")

    def set_status(self, text):
        self.lbl_status.configure(text=text)

    def on_close(self):
        if self.running:
            if not messagebox.askyesno(APP_NAME, "正在处理中，确定退出吗？"):
                return
            self.cancel_event.set()
        self._closing = True
        self._probe_cancel.set()
        if self.drop_handler:
            try:
                self.drop_handler.disable()
            except Exception:
                pass
        self._cleanup_temp()
        core.clear_utf8_cache()
        self._save_config()
        self.root.destroy()


# --------------------------------------------------------------------------
def main():
    fluent.enable_dpi_awareness()      # 必须在创建 Tk 之前，保证文字清晰
    try:
        root = tk.Tk()
    except tk.TclError as exc:
        message = ("无法启动图形界面：\n%s\n\nTcl 目录：%s\n\n"
                   "请安装完整版 Python（勾选 tcl/tk 组件）后重试。"
                   % (exc, TCL_DIR or "未找到"))
        try:
            print(message)
        except Exception:
            pass
        try:
            import ctypes
            ctypes.windll.user32.MessageBoxW(0, message, APP_NAME, 0x10)
        except Exception:
            pass
        return 1

    App(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
