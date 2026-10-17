# -*- coding: utf-8 -*-
"""Windows 原生文件拖放支持（WM_DROPFILES），纯 ctypes 实现，零第三方依赖。

用法：
    handler = enable_drop(hwnd, callback)   # callback(list_of_paths)
    ...
    handler.disable()

失败时 enable_drop 返回 None，调用方应降级到「添加文件/文件夹」按钮。
"""

import ctypes
import os
from ctypes import wintypes

WM_DROPFILES = 0x0233
GWL_WNDPROC = -4

_LIVE_HANDLERS = []


class _DropHandler(object):
    def __init__(self, hwnd, callback):
        self.hwnd = hwnd
        self.callback = callback
        self._old_proc = None
        self._new_proc = None

    # ---------------- 安装 ----------------
    def install(self):
        user32 = _user32()
        # Tk 的 winfo_id 可能是内部子窗口，拖放必须注册到顶层窗口
        hwnd = self.hwnd
        try:
            user32.GetAncestor.restype = wintypes.HWND
            user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
            real = user32.GetAncestor(wintypes.HWND(hwnd), 2)  # GA_ROOT
            if real:
                self.hwnd = hwnd = int(real)
        except Exception:
            pass

        # 让提升权限的进程也能收到拖放消息
        try:
            _user32().ChangeWindowMessageFilterEx(
                wintypes.HWND(self.hwnd), WM_DROPFILES, 1, None)  # MSGFLT_ALLOW
        except Exception:
            try:
                _user32().ChangeWindowMessageFilter(WM_DROPFILES, 1)
            except Exception:
                pass

        self._new_proc = _WNDPROC(self._wnd_proc)
        old = _GetWindowLongPtr(self.hwnd, GWL_WNDPROC)
        if not old:
            return False
        self._old_proc = old
        _SetWindowLongPtr(self.hwnd, GWL_WNDPROC,
                          ctypes.cast(self._new_proc, ctypes.c_void_p))
        _shell32().DragAcceptFiles(wintypes.HWND(self.hwnd), True)
        _LIVE_HANDLERS.append(self)
        return True

    def disable(self):
        try:
            _shell32().DragAcceptFiles(wintypes.HWND(self.hwnd), False)
        except Exception:
            pass
        if self._old_proc:
            try:
                _SetWindowLongPtr(self.hwnd, GWL_WNDPROC,
                                  ctypes.c_void_p(self._old_proc))
            except Exception:
                pass
        if self in _LIVE_HANDLERS:
            _LIVE_HANDLERS.remove(self)

    # ---------------- 消息处理 ----------------
    def _wnd_proc(self, hwnd, msg, wparam, lparam):
        try:
            if msg == WM_DROPFILES:
                paths = _drag_query_files(wparam)
                if paths and self.callback:
                    self.callback(paths)
                return 0
        except Exception:
            pass
        try:
            return _CallWindowProc(self._old_proc, hwnd, msg, wparam, lparam)
        except Exception:
            return 0


def _drag_query_files(wparam):
    shell32 = _shell32()
    hdrop = wintypes.HANDLE(wparam)
    try:
        count = shell32.DragQueryFileW(hdrop, 0xFFFFFFFF, None, 0)
        paths = []
        for i in range(count):
            size = shell32.DragQueryFileW(hdrop, i, None, 0) + 1
            buf = ctypes.create_unicode_buffer(size)
            shell32.DragQueryFileW(hdrop, i, buf, size)
            paths.append(buf.value)
        return paths
    finally:
        try:
            shell32.DragFinish(hdrop)
        except Exception:
            pass


def enable_drop(hwnd, callback):
    """在窗口上启用文件拖放。成功返回 handler，失败返回 None。"""
    if os.name != "nt" or not hwnd:
        return None
    try:
        handler = _DropHandler(int(hwnd), callback)
        if handler.install():
            return handler
        handler.disable()
    except Exception:
        pass
    return None


def _user32():
    global _USER32
    if _USER32 is None:
        _USER32 = ctypes.WinDLL("user32", use_last_error=True)
    return _USER32


def _shell32():
    global _SHELL32
    if _SHELL32 is None:
        _SHELL32 = ctypes.WinDLL("shell32", use_last_error=True)
    return _SHELL32


_USER32 = None
_SHELL32 = None
_WNDPROC = None
_CallWindowProc = None
_GetWindowLongPtr = None
_SetWindowLongPtr = None


def _init_types():
    global _WNDPROC, _CallWindowProc, _GetWindowLongPtr, _SetWindowLongPtr
    user32 = _user32()
    shell32 = _shell32()

    LRESULT = ctypes.c_ssize_t
    _WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT,
                                  wintypes.WPARAM, wintypes.LPARAM)

    if ctypes.sizeof(ctypes.c_void_p) == 8:
        _SetWindowLongPtr = user32.SetWindowLongPtrW
        _GetWindowLongPtr = user32.GetWindowLongPtrW
    else:
        _SetWindowLongPtr = user32.SetWindowLongW
        _GetWindowLongPtr = user32.GetWindowLongW
    _SetWindowLongPtr.restype = ctypes.c_void_p
    _SetWindowLongPtr.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_void_p]
    _GetWindowLongPtr.restype = ctypes.c_void_p
    _GetWindowLongPtr.argtypes = [wintypes.HWND, ctypes.c_int]

    _CallWindowProc = user32.CallWindowProcW
    _CallWindowProc.restype = LRESULT
    _CallWindowProc.argtypes = [ctypes.c_void_p, wintypes.HWND, wintypes.UINT,
                                wintypes.WPARAM, wintypes.LPARAM]

    shell32.DragAcceptFiles.argtypes = [wintypes.HWND, wintypes.BOOL]
    shell32.DragAcceptFiles.restype = wintypes.BOOL
    shell32.DragQueryFileW.argtypes = [wintypes.HANDLE, wintypes.UINT,
                                       ctypes.c_wchar_p, wintypes.UINT]
    shell32.DragQueryFileW.restype = wintypes.UINT
    shell32.DragFinish.argtypes = [wintypes.HANDLE]

    try:
        user32.ChangeWindowMessageFilterEx.argtypes = [
            wintypes.HWND, wintypes.UINT, wintypes.DWORD, ctypes.c_void_p]
        user32.ChangeWindowMessageFilterEx.restype = wintypes.BOOL
    except Exception:
        pass


_init_types()
