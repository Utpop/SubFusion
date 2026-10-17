# -*- coding: utf-8 -*-
"""一键把本工具打包成单文件 exe（不含 ffmpeg）。

用法：双击本文件，或在命令行执行  python 打包成exe.py

前置条件（脚本会自己检查并提示）：
  1. 系统里有一个能正常 import tkinter 的 Python 3.8+
  2. 联网（用国内镜像装 pyinstaller 与 sv-ttk）

产物：dist/字幕视频合并工具.exe（约 12 MB）

⚠️ 本机特别注意
  这台电脑的 Python 是「残缺安装」：python.exe 在 C:\\Python314，
  而 Lib / tcl / DLLs 在 %LOCALAPPDATA%\\Programs\\Python\\Python314。
  PyInstaller 依赖 sys.prefix 找模块，必须先把 PYTHONHOME 指到完整目录，
  否则打包出来的 exe 会缺少 tkinter（运行时报 No module named 'tkinter'）。
"""

import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
VENV = os.path.join(HERE, ".buildenv")
MIRROR = "https://mirrors.cloud.tencent.com/pypi/simple"

# 可能有完整库的目录（按顺序尝试）
PYTHON_HOME_CANDIDATES = [
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Python", "Python314"),
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Python", "Python313"),
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Python", "Python312"),
    r"C:\Python314", r"C:\Python313", r"C:\Python312",
    sys.prefix,
]


def find_python_home():
    """找到含 Lib/tkinter 的 Python 安装目录（残缺安装时它与 python.exe 不同目录）。"""
    for home in PYTHON_HOME_CANDIDATES:
        if home and os.path.isfile(os.path.join(home, "Lib", "tkinter", "__init__.py")):
            return home
    return None


def find_python(python_home):
    """找一个能 import tkinter 的解释器（带上 PYTHONHOME 一起验证）。"""
    candidates = [sys.executable]
    for home in PYTHON_HOME_CANDIDATES:
        if home and os.path.isfile(os.path.join(home, "python.exe")):
            candidates.append(os.path.join(home, "python.exe"))
    candidates += ["python.exe", "python3.exe"]

    env = dict(os.environ)
    env.pop("PYTHONPATH", None)
    if python_home:
        env["PYTHONHOME"] = python_home

    for path in candidates:
        try:
            probe = subprocess.run([path, "-c", "import tkinter"],
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   env=env, timeout=60,
                                   creationflags=0x08000000)
            if probe.returncode == 0:
                return path
        except Exception:
            continue
    return None


def main():
    print("=" * 66)
    print("打包 字幕视频批量合并工具")
    print("=" * 66)

    python_home = find_python_home()
    python = find_python(python_home)
    if not python:
        print("[错误] 没找到能 import tkinter 的 Python，请先安装完整版 Python。")
        return 1
    print("使用解释器：%s" % python)
    print("PYTHONHOME：%s" % (python_home or "(沿用默认)"))

    # 让 PyInstaller 能找到标准库与 tcl/tk
    env = dict(os.environ)
    env.pop("PYTHONPATH", None)
    if python_home:
        env["PYTHONHOME"] = python_home

    # 1) 建虚拟环境
    if not os.path.isfile(os.path.join(VENV, "Scripts", "python.exe")):
        print("\n[1/3] 创建打包环境…")
        code = subprocess.run([python, "-m", "venv", VENV], env=env).returncode
        if code != 0:
            print("[错误] 创建虚拟环境失败")
            return 1
    else:
        print("\n[1/3] 复用已有打包环境")

    venv_python = os.path.join(VENV, "Scripts", "python.exe")

    # 2) 安装依赖
    print("\n[2/3] 安装 pyinstaller 与 sv-ttk…")
    proc = subprocess.run([venv_python, "-m", "pip", "install", "--disable-pip-version-check",
                           "--timeout", "60", "-i", MIRROR, "pyinstaller", "sv-ttk"],
                          env=env)
    if proc.returncode != 0:
        print("[错误] 依赖安装失败（检查网络或代理），可手动执行：")
        print('  "%s" -m pip install -i %s pyinstaller sv-ttk' % (venv_python, MIRROR))
        return 1

    # 3) 打包
    print("\n[3/3] 开始打包…")
    pyinstaller = os.path.join(VENV, "Scripts", "pyinstaller.exe")
    cmd = [
        pyinstaller, "--noconfirm", "--clean", "--onefile", "--windowed",
        "--name", "SubtitleMerger",
        "--icon", os.path.join(HERE, "app.ico"),
        "--add-data", os.path.join(HERE, "app.ico") + ";.",
        "--collect-all", "sv_ttk",
        "--distpath", os.path.join(HERE, "dist"),
        "--workpath", os.path.join(HERE, "build"),
        "--specpath", HERE,
        os.path.join(HERE, "subtitle_merger.py"),
    ]
    print("命令：%s" % " ".join(cmd))
    code = subprocess.run(cmd, cwd=HERE, env=env).returncode

    built = os.path.join(HERE, "dist", "SubtitleMerger.exe")
    if code != 0 or not os.path.isfile(built):
        print("\n[错误] 打包失败，退出码 %d" % code)
        return 1

    target = os.path.join(HERE, "dist", "字幕视频合并工具.exe")
    if os.path.exists(target):
        os.remove(target)
    os.rename(built, target)

    # 收尾：清掉中间产物
    for name in ("build", "SubtitleMerger.spec"):
        path = os.path.join(HERE, name)
        if os.path.isdir(path):
            shutil.rmtree(path, ignore_errors=True)
        elif os.path.isfile(path):
            os.remove(path)

    size = os.path.getsize(target) / 1048576.0
    print("\n" + "=" * 66)
    print("打包完成：%s（%.1f MB）" % (target, size))
    print("把它拷到任意 Windows 电脑即可运行（那台机器需要装有 ffmpeg）。")
    print("=" * 66)
    return 0


if __name__ == "__main__":
    sys.exit(main())
