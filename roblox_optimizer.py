"""Small Windows desktop companion for long-running Roblox sessions.

It never injects code into Roblox or changes the game's process memory.
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from queue import Empty, SimpleQueue

import psutil
from PIL import Image, ImageTk
from ram_integration import RamError, find_ram_executable, launch_ram_account, list_ram_accounts, read_ram_settings


APP_NAME = "Mizuauto"
ROBLOX_DIR = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData/Local")) / "Roblox"
SETTINGS_FILE = ROBLOX_DIR / "GlobalBasicSettings_13.xml"
DATA_DIR = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData/Local")) / "Mizuauto"
STATE_FILE = DATA_DIR / "state.json"
LEGACY_STATE_FILE = DATA_DIR.parent / "RobloxGonMay" / "state.json"
ASSET_DIR = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent)) / "assets"
LOGO_FILE = ASSET_DIR / "mizulogo.png"
MIB = 1024 * 1024
PROCESS_NAME = "robloxplayerbeta.exe"
AUTO_TRIM_THRESHOLD_MB = 384
CPU_OPTIONS = {"Tắt": 0, "2 luồng": 2, "1 luồng": 1}

user32 = ctypes.WinDLL("user32", use_last_error=True)
EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows.argtypes = [EnumWindowsProc, wintypes.LPARAM]
user32.EnumWindows.restype = wintypes.BOOL
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.IsIconic.argtypes = [wintypes.HWND]
user32.IsIconic.restype = wintypes.BOOL
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetWindowRect.restype = wintypes.BOOL
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.ShowWindow.restype = wintypes.BOOL
user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT]
user32.SetWindowPos.restype = wintypes.BOOL
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.PostMessageW.restype = wintypes.BOOL
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.SetForegroundWindow.restype = wintypes.BOOL
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL
psapi = ctypes.WinDLL("psapi", use_last_error=True)
psapi.EmptyWorkingSet.argtypes = [wintypes.HANDLE]
psapi.EmptyWorkingSet.restype = wintypes.BOOL
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
shell32.IsUserAnAdmin.restype = wintypes.BOOL
shell32.ShellExecuteW.argtypes = [wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.LPCWSTR, ctypes.c_int]
shell32.ShellExecuteW.restype = ctypes.c_void_p


def get_windows() -> dict[int, list[int]]:
    result: dict[int, list[int]] = {}

    @EnumWindowsProc
    def callback(hwnd: int, _lparam: int) -> bool:
        if user32.IsWindowVisible(hwnd) and user32.GetWindowTextLengthW(hwnd) > 0:
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value:
                result.setdefault(pid.value, []).append(hwnd)
        return True

    if not user32.EnumWindows(callback, 0):
        raise ctypes.WinError(ctypes.get_last_error())
    return result


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise ctypes.WinError(ctypes.get_last_error())
    return rect.left, rect.top, rect.right, rect.bottom


def is_roblox_process(pid: int) -> bool:
    try:
        return psutil.Process(pid).name().lower() == PROCESS_NAME
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        return False


def window_belongs_to(hwnd: int, pid: int) -> bool:
    actual_pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(actual_pid))
    return actual_pid.value == pid and is_roblox_process(pid)


def running_roblox() -> list[psutil.Process]:
    result = []
    for process in psutil.process_iter(["name"]):
        if (process.info["name"] or "").lower() == PROCESS_NAME:
            result.append(process)
    return result


def older_optimizer_running() -> bool:
    old_names = {"robloxgonmay.exe", "robloxgonmay_v2.exe", "robloxgonmay_v3.exe", "robloxgonmay_v4.exe", "robloxgonmay_v5.exe"}
    visible_windows = get_windows()
    for process in psutil.process_iter(["name"]):
        name = (process.info["name"] or "").lower()
        if name in old_names or (name == "mizuauto.exe" and process.pid != os.getpid() and visible_windows.get(process.pid)):
            return True
    return False


def game_windows_open() -> bool:
    windows = get_windows()
    now = time.time()
    for process in running_roblox():
        try:
            if windows.get(process.pid) or now - process.create_time() < 60:
                return True
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    return False


def trim_working_set(pid: int) -> tuple[float, float]:
    if not is_roblox_process(pid):
        raise ProcessLookupError(f"PID {pid} không còn là Roblox")
    process = psutil.Process(pid)
    before = process.memory_info().rss / MIB
    handle = kernel32.OpenProcess(0x1000 | 0x0100, False, pid)  # QUERY_LIMITED_INFORMATION | SET_QUOTA
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        if not psapi.EmptyWorkingSet(handle):
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        kernel32.CloseHandle(handle)
    after = process.memory_info().rss / MIB
    return before, after


def trim_batch(targets: list[tuple[int, float]], min_rss_mb: float = 0) -> list[tuple[int, float, str]]:
    """Trim each selected process once, skipping a PID reused by a new process."""
    results = []
    for pid, created_at in targets:
        try:
            process = psutil.Process(pid)
            if process.create_time() != created_at:
                results.append((pid, 0.0, "Phiên đã thay đổi"))
                continue
            if min_rss_mb and process.memory_info().rss / MIB < min_rss_mb:
                results.append((pid, 0.0, ""))
                continue
            before, after = trim_working_set(pid)
            results.append((pid, max(0.0, before - after), ""))
        except (psutil.NoSuchProcess, ProcessLookupError):
            results.append((pid, 0.0, "Phiên đã thoát"))
        except (OSError, psutil.Error) as exc:
            results.append((pid, 0.0, str(exc)))
    return results


def load_state() -> dict:
    for path in (STATE_FILE, LEGACY_STATE_FILE):
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
    return {"pending_low_graphics": False, "backup": ""}


def save_state(state: dict) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    temporary = STATE_FILE.with_suffix(".tmp")
    temporary.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, STATE_FILE)


def cpu_choice(available: list[int], threads: int, pid: int) -> list[int]:
    """Distribute separate Roblox instances over their originally allowed CPUs."""
    if not available:
        raise ValueError("Tiến trình không có luồng CPU khả dụng")
    count = min(threads, len(available))
    offset = pid % len(available)
    return [available[(offset + index) % len(available)] for index in range(count)]


def matching_roblox(pid: int, created_at: float) -> psutil.Process:
    process = psutil.Process(pid)
    if process.create_time() != created_at or process.name().lower() != PROCESS_NAME:
        raise psutil.NoSuchProcess(pid)
    return process


def replace_xml_value(xml: str, tag: str, name: str, value: str) -> str:
    pattern = rf'(<{tag}\s+name="{re.escape(name)}">)([^<]*)(</{tag}>)'
    matches = list(re.finditer(pattern, xml))
    if len(matches) != 1:
        raise ValueError(f"Không tìm thấy đúng một mục {name} trong cấu hình Roblox")
    match = matches[0]
    return xml[: match.start(2)] + value + xml[match.end(2) :]


def low_graphics_xml(xml: str) -> str:
    # These are Roblox's own persisted user settings. Leave every other field intact.
    for tag, name, value in (
        ("token", "SavedQualityLevel", "1"),
        ("token", "GraphicsOptimizationMode", "0"),
        ("int", "FramerateCap", "60"),
    ):
        xml = replace_xml_value(xml, tag, name, value)
    return xml


def current_settings() -> str:
    try:
        xml = SETTINGS_FILE.read_text(encoding="utf-8")
        values = {}
        for tag, name in (("token", "SavedQualityLevel"), ("token", "GraphicsOptimizationMode"), ("int", "FramerateCap")):
            match = re.search(rf'<{tag}\s+name="{name}">([^<]*)</{tag}>', xml)
            values[name] = match.group(1) if match else "?"
        return f"Đồ họa: {values['SavedQualityLevel']}/10  ·  FPS: {values['FramerateCap']}  ·  Ưu tiên hiệu năng: {values['GraphicsOptimizationMode']}"
    except OSError:
        return "Chưa tìm thấy cấu hình Roblox"


def apply_low_graphics(state: dict) -> bool:
    if game_windows_open():
        state["pending_low_graphics"] = True
        save_state(state)
        return False
    original = SETTINGS_FILE.read_text(encoding="utf-8")
    updated = low_graphics_xml(original)
    if updated != original:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        backup = DATA_DIR / f"GlobalBasicSettings_{time.strftime('%Y%m%d_%H%M%S')}.bak"
        shutil.copy2(SETTINGS_FILE, backup)
        state["backup"] = str(backup)
        temporary = SETTINGS_FILE.with_suffix(".xml.tmp")
        temporary.write_text(updated, encoding="utf-8")
        os.replace(temporary, SETTINGS_FILE)
    state["pending_low_graphics"] = False
    save_state(state)
    return True


def restore_settings(state: dict) -> bool:
    backup = Path(state.get("backup") or "")
    if not backup.is_file():
        raise FileNotFoundError("Chưa có bản sao cấu hình do công cụ tạo")
    if game_windows_open():
        raise RuntimeError("Hãy đóng tất cả cửa sổ Roblox trước khi khôi phục cấu hình")
    shutil.copy2(backup, SETTINGS_FILE)
    state["pending_low_graphics"] = False
    state["backup"] = ""
    save_state(state)
    return True


class App:
    def __init__(self, startup_auto: bool = True) -> None:
        self.root = tk.Tk()
        self.root.title(APP_NAME)
        self.root.geometry("920x610")
        self.root.minsize(780, 530)
        self.state = load_state()
        self.afk_windows: dict[int, list[tuple[int, tuple[int, int, int, int]]]] = {}
        self.last_alert: dict[int, float] = {}
        self.baseline_rss: dict[int, float] = {}
        self.auto_trim = tk.BooleanVar(value=False)
        self.discovered_targets: dict[int, float] = {}
        self.realtime_results: SimpleQueue[list[tuple[int, float, str]]] = SimpleQueue()
        self.realtime_worker: threading.Thread | None = None
        try:
            self.cpu_threads = int(self.state.get("cpu_threads", 0))
        except (TypeError, ValueError):
            self.cpu_threads = 0
        if self.cpu_threads not in CPU_OPTIONS.values():
            self.cpu_threads = 0
        self.cpu_setting = tk.StringVar(value=next(name for name, count in CPU_OPTIONS.items() if count == self.cpu_threads))
        saved_originals = self.state.get("cpu_originals", {})
        self.cpu_originals: dict[str, dict] = saved_originals if isinstance(saved_originals, dict) else {}
        self.cpu_failed: set[tuple[int, float]] = set()
        self.account_window: tk.Toplevel | None = None
        self.account_results: SimpleQueue[tuple] = SimpleQueue()
        self.account_worker: threading.Thread | None = None
        self.account_cancel = threading.Event()
        self.account_generation = 0
        try:
            saved_interval = float(self.state.get("interval_seconds", 1))
        except (TypeError, ValueError):
            saved_interval = 1.0
        if not math.isfinite(saved_interval):
            saved_interval = 1.0
        self.interval_seconds = max(0.1, min(3600.0, saved_interval))
        self.interval_entry = tk.StringVar(value=f"{self.interval_seconds:g}")
        self.next_trim_at = 0.0
        self.waiting_for_old = False
        self.realtime_text = tk.StringVar(value="Auto tắt")
        self.threshold_gb = tk.StringVar(value="4")
        self.summary = tk.StringVar(value="Đang quét...")
        self.session_count = tk.StringVar(value="0")
        self.ram_total = tk.StringVar(value="0.00 GB")
        self.private_total = tk.StringVar(value="0.00 GB")
        self.settings_text = tk.StringVar(value=current_settings())
        self.status = tk.StringVar(value="")
        self.notice_until = 0.0
        self._build_ui()
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.refresh()
        self.root.after(50, self.realtime_tick)
        if startup_auto and self.state.get("auto_enabled", True):
            self.root.after(400, self.start_auto)

    def _build_ui(self) -> None:
        colors = {"bg": "#14121E", "card": "#211C2C", "line": "#3A3046", "text": "#FFF4FA", "muted": "#C2B1C3", "accent": "#F176BA", "accent_hover": "#F69ACB", "blue": "#67569E", "blue_hover": "#8470B5", "red": "#AA456B", "red_hover": "#C45F86"}
        self.colors = colors
        self.root.configure(bg=colors["bg"])
        style = ttk.Style(self.root)
        style.theme_use("clam")
        style.configure("Dark.Treeview", background=colors["card"], fieldbackground=colors["card"], foreground=colors["text"], borderwidth=0, bordercolor=colors["line"], lightcolor=colors["line"], darkcolor=colors["line"], relief="flat", rowheight=32, font=("Segoe UI", 10))
        style.configure("Dark.Treeview.Heading", background="#30283D", foreground=colors["muted"], relief="flat", font=("Segoe UI", 9, "bold"))
        style.map("Dark.Treeview", background=[("selected", "#765074")], foreground=[("selected", "#FFFFFF")])
        style.map("Dark.Treeview.Heading", background=[("active", "#453552")])
        style.configure("Cpu.TCombobox", fieldbackground="#171321", background="#30283D", foreground=colors["text"], arrowcolor=colors["accent"], bordercolor=colors["line"], lightcolor=colors["line"], darkcolor=colors["line"], padding=6, relief="flat")
        style.map("Cpu.TCombobox", fieldbackground=[("readonly", "#171321")], foreground=[("readonly", colors["text"])], selectbackground=[("readonly", "#171321")], selectforeground=[("readonly", colors["text"])])

        def label(parent, value, size=10, bold=False, color="text", **kwargs):
            return tk.Label(parent, text=value, bg=parent.cget("bg"), fg=colors[color], font=("Segoe UI", size, "bold" if bold else "normal"), **kwargs)

        def button(parent, value, command, kind="blue", width=120):
            widget = tk.Button(parent, text=value, command=command, bg=colors[kind], fg="#281421" if kind == "accent" else "#FFFFFF", activebackground=colors[kind + "_hover"], activeforeground="#281421" if kind == "accent" else "#FFFFFF", bd=0, relief="flat", width=max(8, width // 12), padx=7, pady=9, font=("Segoe UI", 10, "bold"), cursor="hand2")
            widget._base_bg = colors[kind]
            widget._hover_bg = colors[kind + "_hover"]
            widget.bind("<Enter>", lambda _event: widget.configure(bg=widget._hover_bg))
            widget.bind("<Leave>", lambda _event: widget.configure(bg=widget._base_bg))
            return widget

        outer = tk.Frame(self.root, bg=colors["bg"], padx=18, pady=18)
        outer.pack(fill="both", expand=True)
        brand = tk.Frame(outer, bg=colors["bg"])
        brand.pack(fill="x", pady=(0, 12))
        try:
            logo = Image.open(LOGO_FILE).convert("RGBA")
            self.logo_image = ImageTk.PhotoImage(logo.resize((58, 58), Image.Resampling.LANCZOS))
            self.root.iconphoto(True, self.logo_image)
            tk.Label(brand, image=self.logo_image, bg=colors["bg"], bd=0).pack(side="left")
        except OSError:
            self.logo_image = None
        label(brand, "Mizuauto", 22, True).pack(side="left", padx=(12, 0))
        self.privilege_badge = label(brand, "CẦN ADMIN", 9, True, "muted")
        self.privilege_badge.pack(side="right", pady=(18, 0))
        button(brand, "Tài khoản", self.open_account_manager, width=112).pack(side="right", padx=(0, 16), pady=(9, 0))

        table_card = tk.Frame(outer, bg=colors["card"], highlightbackground=colors["line"], highlightthickness=1, padx=14, pady=12)
        table_card.pack(fill="both", expand=True)
        table_header = tk.Frame(table_card, bg=colors["card"])
        table_header.pack(fill="x", pady=(0, 9))
        label(table_header, "Danh sách phiên", 15, True).pack(side="left")
        self.session_badge = label(table_header, "0 phiên", 9, True, "accent")
        self.session_badge.pack(side="left", padx=(12, 0), pady=(4, 0))

        columns = ("pid", "ram", "change", "private", "cpu", "time", "window", "auto")
        table_body = tk.Frame(table_card, bg=colors["card"])
        table_body.pack(fill="both", expand=True)
        self.table = ttk.Treeview(table_body, columns=columns, show="headings", selectmode="extended", style="Dark.Treeview", height=7)
        for key, caption, width in (("pid", "PID", 65), ("ram", "RAM", 115), ("change", "TĂNG", 105), ("private", "ĐÃ CẤP PHÁT", 135), ("cpu", "CPU", 70), ("time", "THỜI GIAN", 100), ("window", "CỬA SỔ", 95), ("auto", "AUTO", 95)):
            self.table.heading(key, text=caption)
            self.table.column(key, width=width, anchor="center")
        self.table.tag_configure("high", background="#4A2B40", foreground="#FFE9F3")
        self.table.tag_configure("auto", background="#30253B", foreground="#FFF0F8")
        scrollbar = tk.Scrollbar(table_body, orient="vertical", command=self.table.yview, bg="#5D4766", activebackground=colors["accent"], troughcolor=colors["card"], highlightthickness=0, bd=0, relief="flat", width=10)
        self.scrollbar = scrollbar
        self.table.configure(yscrollcommand=scrollbar.set)
        self.table.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        self.context_menu = tk.Menu(self.root, tearoff=False, bg=colors["card"], fg=colors["text"], activebackground="#765074", activeforeground="#FFFFFF")
        self.context_menu.add_command(label="Thu hồi 1 lần", command=self.trim_selected)
        self.context_menu.add_command(label="Đóng phiên đã chọn", command=self.close_selected)
        self.table.bind("<Button-3>", self.show_context_menu)

        auto_card = tk.Frame(outer, bg=colors["card"], highlightbackground=colors["line"], highlightthickness=1, padx=16, pady=13)
        auto_card.pack(fill="x", pady=(12, 0))
        auto_header = tk.Frame(auto_card, bg=colors["card"])
        auto_header.pack(fill="x", pady=(0, 11))
        label(auto_header, "AUTO", 12, True, "accent").pack(side="left")
        auto_row = tk.Frame(auto_card, bg=colors["card"])
        auto_row.pack(fill="x")
        label(auto_row, "Chu kỳ", 10, color="muted").pack(side="left")
        self.interval_box = tk.Entry(auto_row, textvariable=self.interval_entry, width=6, bg="#171321", fg=colors["text"], insertbackground=colors["text"], highlightbackground=colors["line"], highlightcolor=colors["accent"], highlightthickness=1, relief="flat", justify="center", font=("Segoe UI", 11))
        self.interval_box.pack(side="left", padx=(8, 4), ipady=7)
        self.interval_box.bind("<Return>", lambda _event: self.apply_interval())
        label(auto_row, "giây", 10, color="muted").pack(side="left", padx=(0, 7))
        button(auto_row, "Áp dụng", self.apply_interval, width=105).pack(side="left", padx=(0, 8))
        self.auto_button = button(auto_row, "Bật Auto", self.toggle_auto_trim, "accent", width=126)
        self.auto_button.pack(side="left")
        cpu_row = tk.Frame(auto_card, bg=colors["card"])
        cpu_row.pack(fill="x", pady=(9, 0))
        label(cpu_row, "CPU", 10, color="muted").pack(side="left")
        self.cpu_box = ttk.Combobox(cpu_row, textvariable=self.cpu_setting, values=list(CPU_OPTIONS), state="readonly", width=10, style="Cpu.TCombobox", font=("Segoe UI", 10))
        self.cpu_box.pack(side="left", padx=(13, 0))
        self.cpu_box.bind("<<ComboboxSelected>>", self.change_cpu_mode)
        tk.Label(auto_card, textvariable=self.realtime_text, bg=colors["card"], fg=colors["muted"], anchor="w", font=("Segoe UI", 10)).pack(fill="x", pady=(11, 0))
        tk.Label(auto_card, textvariable=self.status, bg=colors["card"], fg=colors["accent"], anchor="w", font=("Segoe UI", 9)).pack(fill="x", pady=(3, 0))

    def show_context_menu(self, event) -> None:
        item = self.table.identify_row(event.y)
        if not item:
            return
        if item not in self.table.selection():
            self.table.selection_set(item)
        try:
            self.context_menu.tk_popup(event.x_root, event.y_root)
        finally:
            self.context_menu.grab_release()

    def update_scrollbar(self) -> None:
        _first, last = self.table.yview()
        if last >= 0.999:
            self.scrollbar.pack_forget()
        elif not self.scrollbar.winfo_manager():
            self.scrollbar.pack(side="right", fill="y")

    def selected_pids(self) -> list[int]:
        result = [int(item) for item in self.table.selection() if is_roblox_process(int(item))]
        if not result:
            messagebox.showinfo(APP_NAME, "Hãy chọn một hoặc nhiều phiên Roblox trong bảng.")
        return result

    def show_status(self, message: str, seconds: float = 15) -> None:
        self.status.set(message)
        self.notice_until = time.time() + seconds

    def open_account_manager(self) -> None:
        if self.account_window and self.account_window.winfo_exists():
            self.account_window.lift()
            self.account_window.focus_force()
            return
        executable = find_ram_executable(self.state.get("ram_exe", ""))
        if executable is None:
            selected = filedialog.askopenfilename(title="Chọn Roblox Account Manager.exe", filetypes=[("Roblox Account Manager", "Roblox Account Manager.exe"), ("EXE", "*.exe")])
            if not selected:
                return
            executable = Path(selected)
            if executable.name.lower() != "roblox account manager.exe":
                messagebox.showerror(APP_NAME, "Hãy chọn Roblox Account Manager.exe")
                return
        try:
            settings = read_ram_settings(executable)
        except RamError as exc:
            messagebox.showerror(APP_NAME, str(exc))
            return
        self.state["ram_exe"] = str(executable)
        save_state(self.state)
        self.ram_executable = executable
        self.ram_settings = settings
        self.account_generation += 1
        self.account_cancel = threading.Event()
        self.account_worker = None
        colors = self.colors
        win = tk.Toplevel(self.root, bg=colors["bg"])
        self.account_window = win
        win.title("Mizuauto · Tài khoản")
        win.geometry("700x535")
        win.minsize(600, 470)
        win.transient(self.root)
        win.protocol("WM_DELETE_WINDOW", self.close_account_manager)
        body = tk.Frame(win, bg=colors["bg"], padx=20, pady=18)
        body.pack(fill="both", expand=True)

        def text_label(parent, caption, size=10, color="text", bold=False):
            return tk.Label(parent, text=caption, bg=parent.cget("bg"), fg=colors[color], font=("Segoe UI", size, "bold" if bold else "normal"))

        def flat_button(parent, caption, command, kind="blue"):
            return tk.Button(parent, text=caption, command=command, bg=colors[kind], fg="#281421" if kind == "accent" else "#FFFFFF", activebackground=colors[kind + "_hover"], activeforeground="#FFFFFF", bd=0, relief="flat", padx=13, pady=8, cursor="hand2", font=("Segoe UI", 10, "bold"))

        header = tk.Frame(body, bg=colors["bg"])
        header.pack(fill="x", pady=(0, 14))
        text_label(header, "Tài khoản Roblox", 17, bold=True).pack(side="left")
        flat_button(header, "Mở RAM", self.open_ram).pack(side="right")
        card = tk.Frame(body, bg=colors["card"], highlightbackground=colors["line"], highlightthickness=1, padx=14, pady=14)
        card.pack(fill="both", expand=True)
        connection = tk.Frame(card, bg=colors["card"])
        connection.pack(fill="x", pady=(0, 11))
        text_label(connection, "Mật khẩu API", color="muted").pack(side="left")
        self.account_password = tk.StringVar()
        password_entry = tk.Entry(connection, textvariable=self.account_password, show="●", bg="#171321", fg=colors["text"], insertbackground=colors["text"], highlightbackground=colors["line"], highlightcolor=colors["accent"], highlightthickness=1, relief="flat", font=("Segoe UI", 10))
        password_entry.pack(side="left", fill="x", expand=True, padx=(10, 9), ipady=7)
        password_entry.bind("<Return>", lambda _event: self.connect_ram())
        self.connect_button = flat_button(connection, "Kết nối", self.connect_ram, "accent")
        self.connect_button.pack(side="left")
        table_frame = tk.Frame(card, bg=colors["card"])
        table_frame.pack(fill="both", expand=True)
        self.account_table = ttk.Treeview(table_frame, columns=("username", "alias", "group"), show="headings", selectmode="extended", style="Dark.Treeview", height=6)
        for column, caption, width in (("username", "TÀI KHOẢN", 180), ("alias", "TÊN GỢI NHỚ", 190), ("group", "NHÓM", 130)):
            self.account_table.heading(column, text=caption)
            self.account_table.column(column, width=width, anchor="w")
        self.account_table.pack(side="left", fill="both", expand=True)
        account_scrollbar = tk.Scrollbar(table_frame, orient="vertical", command=self.account_table.yview, bg="#5D4766", troughcolor=colors["card"], width=10)
        self.account_table.configure(yscrollcommand=account_scrollbar.set)
        account_scrollbar.pack(side="right", fill="y")
        launch_row = tk.Frame(card, bg=colors["card"])
        launch_row.pack(fill="x", pady=(12, 0))
        text_label(launch_row, "Place ID", color="muted").pack(side="left")
        self.place_id = tk.StringVar(value=settings.place_id)
        tk.Entry(launch_row, textvariable=self.place_id, width=17, bg="#171321", fg=colors["text"], insertbackground=colors["text"], highlightbackground=colors["line"], highlightcolor=colors["accent"], highlightthickness=1, relief="flat", justify="center", font=("Segoe UI", 10)).pack(side="left", padx=(9, 9), ipady=7)
        flat_button(launch_row, "Chọn tất cả", self.select_all_accounts).pack(side="left")
        self.launch_accounts_button = flat_button(launch_row, "Mở đã chọn", self.launch_selected_accounts, "accent")
        self.launch_accounts_button.pack(side="right")
        self.account_status = tk.StringVar(value="Nhập mật khẩu API rồi bấm Kết nối" if settings.web_enabled else "Bật Enable Web Server trong RAM")
        tk.Label(card, textvariable=self.account_status, bg=colors["card"], fg=colors["muted"], anchor="w", font=("Segoe UI", 9)).pack(fill="x", pady=(11, 0))
        win.after(100, self.poll_account_results)
        win.lift()
        win.focus_force()

    def close_account_manager(self) -> None:
        self.account_generation += 1
        self.account_cancel.set()
        if self.account_window and self.account_window.winfo_exists():
            self.account_password.set("")
            self.account_window.destroy()
        self.account_window = None

    def open_ram(self) -> None:
        windows = get_windows()
        for process in psutil.process_iter(["name"]):
            if (process.info["name"] or "").lower() == "roblox account manager.exe":
                for hwnd in windows.get(process.pid, []):
                    user32.ShowWindow(hwnd, 9)
                    user32.SetForegroundWindow(hwnd)
                    return
        try:
            subprocess.Popen([str(self.ram_executable)], cwd=str(self.ram_executable.parent))
        except OSError as exc:
            self.account_status.set(f"Không mở được RAM: {exc}")

    def select_all_accounts(self) -> None:
        self.account_table.selection_set(self.account_table.get_children())

    def connect_ram(self) -> None:
        if self.account_worker and self.account_worker.is_alive():
            return
        if not self.ram_settings.web_enabled or not self.ram_settings.list_enabled:
            self.account_status.set("Bật Enable Web Server và Allow Get Accounts trong RAM")
            return
        password = self.account_password.get()
        generation = self.account_generation
        port = self.ram_settings.port
        self.connect_button.configure(state="disabled")
        self.account_status.set("Đang kết nối RAM...")

        def task():
            try:
                accounts = list_ram_accounts(port, password)
                self.account_results.put((generation, "accounts", accounts))
            except RamError as exc:
                self.account_results.put((generation, "error", str(exc)))

        self.account_worker = threading.Thread(target=task, daemon=True)
        self.account_worker.start()

    def launch_selected_accounts(self) -> None:
        if self.account_worker and self.account_worker.is_alive():
            return
        if not self.ram_settings.launch_enabled:
            self.account_status.set("Bật Allow Launch Account trong RAM")
            return
        if not self.ram_settings.multi_enabled:
            self.account_status.set("Bật Multi Roblox trong RAM")
            return
        usernames = [self.account_table.item(item, "values")[0] for item in self.account_table.selection()]
        if not usernames:
            self.account_status.set("Chọn tài khoản cần mở")
            return
        place_id = self.place_id.get().strip()
        if not re.fullmatch(r"[1-9]\d*", place_id):
            self.account_status.set("Place ID phải là một số dương")
            return
        password = self.account_password.get()
        generation = self.account_generation
        cancelled = self.account_cancel
        port = self.ram_settings.port
        delay = self.ram_settings.join_delay_ms / 1000
        self.launch_accounts_button.configure(state="disabled")
        self.account_status.set(f"Đang mở {len(usernames)} tài khoản...")

        def task():
            completed = 0
            try:
                for index, username in enumerate(usernames):
                    if cancelled.is_set():
                        break
                    launch_ram_account(port, password, username, place_id)
                    completed += 1
                    self.account_results.put((generation, "progress", completed, len(usernames)))
                    if index < len(usernames) - 1 and cancelled.wait(delay):
                        break
                self.account_results.put((generation, "done", completed))
            except RamError as exc:
                self.account_results.put((generation, "error", str(exc)))

        self.account_worker = threading.Thread(target=task, daemon=True)
        self.account_worker.start()

    def poll_account_results(self) -> None:
        if not self.account_window or not self.account_window.winfo_exists():
            return
        try:
            while True:
                result = self.account_results.get_nowait()
                if result[0] != self.account_generation:
                    continue
                kind = result[1]
                if kind == "accounts":
                    self.account_table.delete(*self.account_table.get_children())
                    for index, account in enumerate(result[2]):
                        self.account_table.insert("", "end", iid=str(index), values=(account.username, account.alias, account.group))
                    self.account_status.set(f"Đã kết nối · {len(result[2])} tài khoản")
                    self.connect_button.configure(state="normal")
                elif kind == "progress":
                    self.account_status.set(f"Đã gửi lệnh mở {result[2]}/{result[3]} tài khoản")
                elif kind == "done":
                    self.account_status.set(f"Đã gửi lệnh mở {result[2]} tài khoản")
                    self.launch_accounts_button.configure(state="normal")
                elif kind == "error":
                    self.account_status.set(result[2])
                    self.connect_button.configure(state="normal")
                    self.launch_accounts_button.configure(state="normal")
        except Empty:
            pass
        self.account_window.after(100, self.poll_account_results)

    def change_cpu_mode(self, _event=None) -> None:
        threads = CPU_OPTIONS.get(self.cpu_setting.get(), 0)
        self.cpu_threads = threads
        self.state["cpu_threads"] = threads
        save_state(self.state)
        self.cpu_failed.clear()
        if threads and not shell32.IsUserAnAdmin():
            self.show_status("Đang xin quyền quản trị cho CPU...")
            self.relaunch_admin()
            return
        self.sync_cpu_limits()
        self.show_status("Đã tắt giới hạn CPU" if not threads else f"CPU: tối đa {threads} luồng mỗi phiên")

    def restore_cpu_limit(self, pid_key: str, snapshot: dict) -> None:
        try:
            pid = int(pid_key)
            process = matching_roblox(pid, float(snapshot["created_at"]))
        except psutil.AccessDenied:
            return
        except (ValueError, KeyError, TypeError, psutil.NoSuchProcess):
            self.cpu_originals.pop(pid_key, None)
            self.state["cpu_originals"] = self.cpu_originals
            save_state(self.state)
            return
        restored = True
        try:
            process.cpu_affinity(snapshot["affinity"])
        except (OSError, ValueError, KeyError, TypeError, psutil.Error):
            restored = False
        try:
            process.nice(snapshot["priority"])
        except (OSError, ValueError, KeyError, TypeError, psutil.Error):
            restored = False
        if not restored:
            return
        self.cpu_originals.pop(pid_key, None)
        self.state["cpu_originals"] = self.cpu_originals
        save_state(self.state)

    def sync_cpu_limits(self) -> None:
        if not shell32.IsUserAnAdmin():
            return
        for pid_key, snapshot in list(self.cpu_originals.items()):
            try:
                active = self.cpu_threads and self.discovered_targets.get(int(pid_key)) == float(snapshot["created_at"])
            except (ValueError, KeyError, TypeError):
                active = False
            if not active:
                self.restore_cpu_limit(pid_key, snapshot)
        if not self.cpu_threads:
            return
        for pid, created_at in self.discovered_targets.items():
            try:
                process = matching_roblox(pid, created_at)
                pid_key = str(pid)
                snapshot = self.cpu_originals.get(pid_key)
                if not isinstance(snapshot, dict) or snapshot.get("created_at") != created_at:
                    snapshot = {"created_at": created_at, "affinity": process.cpu_affinity(), "priority": process.nice()}
                    self.cpu_originals[pid_key] = snapshot
                    self.state["cpu_originals"] = self.cpu_originals
                    save_state(self.state)
                wanted = cpu_choice(snapshot["affinity"], self.cpu_threads, pid)
                if process.cpu_affinity() != wanted:
                    process.cpu_affinity(wanted)
                if process.nice() != psutil.BELOW_NORMAL_PRIORITY_CLASS:
                    process.nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
                self.cpu_failed.discard((pid, created_at))
            except (OSError, ValueError, KeyError, TypeError, psutil.Error) as exc:
                if (pid, created_at) not in self.cpu_failed:
                    self.cpu_failed.add((pid, created_at))
                    self.show_status(f"Không thể giới hạn CPU PID {pid}: {exc}")

    def refresh(self) -> None:
        try:
            windows = get_windows()
            processes = running_roblox()
            live_pids = set()
            discovered: dict[int, float] = {}
            total_rss = total_private = total_cpu = 0.0
            high = []
            now = time.time()
            threshold = float(self.threshold_gb.get()) * 1024
            for process in processes:
                try:
                    with process.oneshot():
                        pid = process.pid
                        mem = process.memory_info()
                        rss = mem.rss / MIB
                        private = getattr(mem, "private", 0) / MIB
                        cpu = process.cpu_percent(interval=None) / (psutil.cpu_count() or 1)
                        elapsed = max(0, now - process.create_time())
                    live_pids.add(pid)
                    self.baseline_rss.setdefault(pid, rss)
                    delta = rss - self.baseline_rss[pid]
                    total_rss += rss
                    total_private += private
                    total_cpu += cpu
                    hours, rem = divmod(int(elapsed), 3600)
                    minutes = rem // 60
                    win = windows.get(pid, [])
                    if win:
                        discovered[pid] = process.create_time()
                    window_state = "Thu gọn" if win and all(user32.IsIconic(h) for h in win) else ("Mở" if win else "Chưa có")
                    values = (pid, f"{rss / 1024:.2f} GB", f"{delta / 1024:+.2f} GB", f"{private / 1024:.2f} GB", f"{cpu:.1f}%", f"{hours}g {minutes:02d}p", window_state, "BẬT" if self.auto_trim.get() and win else "—")
                    tag = ("high",) if rss >= threshold else (("auto",) if self.auto_trim.get() and win else ())
                    if self.table.exists(str(pid)):
                        self.table.item(str(pid), values=values, tags=tag)
                    else:
                        self.table.insert("", "end", iid=str(pid), values=values, tags=tag)
                    if rss >= threshold:
                        high.append(pid)
                        if now - self.last_alert.get(pid, 0) > 1800:
                            self.last_alert[pid] = now
                            if self.root.state() == "iconic":
                                self.root.bell()
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    continue
            for item in self.table.get_children():
                if int(item) not in live_pids:
                    self.table.delete(item)
                    self.baseline_rss.pop(int(item), None)
            self.root.after_idle(self.update_scrollbar)
            self.discovered_targets = discovered
            self.sync_cpu_limits()
            self.summary.set(f"{len(live_pids)} phiên  ·  RAM {total_rss / 1024:.2f} GB  ·  Bộ nhớ riêng {total_private / 1024:.2f} GB  ·  CPU {total_cpu:.1f}%")
            self.session_count.set(str(len(discovered)))
            self.ram_total.set(f"{total_rss / 1024:.2f} GB")
            self.private_total.set(f"{total_private / 1024:.2f} GB")
            self.session_badge.configure(text=f"{len(discovered)} phiên")
            privilege = "quản trị" if shell32.IsUserAnAdmin() else "thường"
            self.privilege_badge.configure(text="● ADMIN" if privilege == "quản trị" else "● CẦN ADMIN", fg=self.colors["accent"] if privilege == "quản trị" else "#F4B46C")
            if self.state.get("pending_low_graphics") and not game_windows_open():
                try:
                    apply_low_graphics(self.state)
                    self.show_status("Đã áp dụng cấu hình tiết kiệm cho lần mở Roblox tiếp theo.")
                except (OSError, ValueError) as exc:
                    self.show_status(f"Không thể áp dụng cấu hình: {exc}")
            elif high and now > self.notice_until:
                self.status.set(f"RAM cao: PID {', '.join(map(str, high))}")
            elif self.state.get("pending_low_graphics") and now > self.notice_until:
                self.status.set("Đang chờ tất cả phiên Roblox đóng để ghi cấu hình mới.")
            if self.waiting_for_old and not older_optimizer_running():
                self.root.after(0, self.start_auto)
        except Exception as exc:
            self.status.set(f"Lỗi theo dõi: {exc}")
        self.root.after(2000, self.refresh)

    def afk_selected(self) -> None:
        pids = self.selected_pids()
        if not pids:
            return
        windows = get_windows()
        changed = 0
        denied = 0
        for pid in pids:
            for hwnd in windows.get(pid, []):
                if user32.IsIconic(hwnd):
                    continue
                try:
                    rect = window_rect(hwnd)
                    width = rect[2] - rect[0]
                    height = rect[3] - rect[1]
                    if width > 680 or height > 500:
                        if not user32.SetWindowPos(hwnd, 0, rect[0], rect[1], 640, 480, 0x0004 | 0x0010):
                            denied += 1
                            continue
                    user32.ShowWindow(hwnd, 6)  # SW_MINIMIZE
                    if user32.IsIconic(hwnd):
                        self.afk_windows.setdefault(pid, []).append((hwnd, rect))
                        changed += 1
                    else:
                        denied += 1
                        user32.SetWindowPos(hwnd, 0, rect[0], rect[1], width, height, 0x0004 | 0x0010)
                except OSError:
                    denied += 1
                    continue
        self.show_status(f"Đã thu gọn {changed} cửa sổ. {denied} cửa sổ bị từ chối." + (" Hãy mở lại công cụ bằng quyền quản trị." if denied else ""))

    def restore_selected(self) -> None:
        pids = self.selected_pids()
        if not pids:
            return
        changed = 0
        denied = 0
        for pid in pids:
            for hwnd, rect in self.afk_windows.pop(pid, []):
                if not user32.IsWindowVisible(hwnd) or not window_belongs_to(hwnd, pid):
                    continue
                user32.ShowWindow(hwnd, 9)  # SW_RESTORE
                size_ok = user32.SetWindowPos(hwnd, 0, rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1], 0x0004 | 0x0010)
                if not user32.IsIconic(hwnd) and size_ok:
                    changed += 1
                else:
                    denied += 1
        self.show_status(f"Đã khôi phục {changed} cửa sổ." + (f" {denied} cửa sổ bị từ chối; hãy chạy bằng quyền quản trị." if denied else ""))

    def close_selected(self) -> None:
        pids = self.selected_pids()
        if not pids:
            return
        if not messagebox.askyesno(APP_NAME, f"Đóng {len(pids)} phiên đã chọn? Tiến độ chưa lưu trong game có thể mất."):
            return
        windows = get_windows()
        sent = 0
        for pid in pids:
            for hwnd in windows.get(pid, []):
                if user32.PostMessageW(hwnd, 0x0010, 0, 0):  # WM_CLOSE, graceful request
                    sent += 1
        self.show_status(f"Đã gửi yêu cầu đóng tới {sent} cửa sổ." + (" Nếu bị từ chối, hãy chạy bằng quyền quản trị hoặc thoát trong game." if sent == 0 else ""))

    def trim_selected(self) -> None:
        if self.auto_trim.get():
            self.show_status("Chế độ mỗi 1 giây đang chạy. Bấm Dừng trước khi thu hồi thủ công.")
            return
        pids = self.selected_pids()
        if not pids:
            return
        released = 0.0
        ok = denied = 0
        for pid in pids:
            try:
                before, after = trim_working_set(pid)
                released += max(0.0, before - after)
                ok += 1
            except (OSError, psutil.Error):
                denied += 1
        self.show_status(f"Đã thu hồi tạm khoảng {released / 1024:.2f} GB từ {ok} phiên. {denied} phiên bị từ chối." + (" Hãy mở lại bằng quyền quản trị." if denied else " Bộ nhớ có thể tăng lại khi game tiếp tục chạy."))

    def toggle_auto_trim(self) -> None:
        if self.auto_trim.get() or self.waiting_for_old:
            self.stop_realtime()
        else:
            self.start_auto()

    def set_auto_button(self, caption: str, kind: str) -> None:
        widget = self.auto_button
        widget._base_bg = self.colors[kind]
        widget._hover_bg = self.colors[kind + "_hover"]
        foreground = "#281421" if kind == "accent" else "#FFFFFF"
        widget.configure(text=caption, bg=widget._base_bg, fg=foreground, activebackground=widget._hover_bg, activeforeground=foreground)

    def start_auto(self) -> None:
        if self.auto_trim.get():
            return
        if older_optimizer_running():
            self.waiting_for_old = True
            self.state["auto_enabled"] = True
            save_state(self.state)
            self.set_auto_button("Hủy chờ", "red")
            self.show_status("Đóng bản cũ để bật Auto.")
            return
        self.waiting_for_old = False
        if not shell32.IsUserAnAdmin():
            self.state["auto_enabled"] = True
            save_state(self.state)
            self.show_status("Đang xin quyền quản trị...")
            if not self.relaunch_admin():
                self.state["auto_enabled"] = False
                save_state(self.state)
                self.show_status("Chưa có quyền quản trị.")
            return
        self.auto_trim.set(True)
        self.state["auto_enabled"] = True
        save_state(self.state)
        self.set_auto_button("Dừng", "red")
        self.next_trim_at = 0.0
        self.realtime_text.set(f"{len(self.discovered_targets)} phiên · mỗi {self.interval_seconds:g} giây")
        self.show_status("")

    def stop_realtime(self) -> None:
        self.auto_trim.set(False)
        self.waiting_for_old = False
        self.state["auto_enabled"] = False
        save_state(self.state)
        self.set_auto_button("Bật Auto", "accent")
        self.realtime_text.set("Auto tắt")
        self.show_status("Đã dừng")

    def apply_interval(self) -> None:
        try:
            value = float(self.interval_entry.get().strip().replace(",", "."))
            if not math.isfinite(value) or not 0.1 <= value <= 3600:
                raise ValueError
        except ValueError:
            self.interval_entry.set(f"{self.interval_seconds:g}")
            self.show_status("Chu kỳ: 0,1–3600 giây")
            return
        self.interval_seconds = value
        self.interval_entry.set(f"{value:g}")
        self.state["interval_seconds"] = value
        save_state(self.state)
        self.next_trim_at = time.monotonic() + value
        self.show_status(f"Chu kỳ: {value:g} giây")

    def _run_realtime_batch(self, targets: list[tuple[int, float]]) -> None:
        self.realtime_results.put(trim_batch(targets, AUTO_TRIM_THRESHOLD_MB))

    def realtime_tick(self) -> None:
        try:
            while True:
                results = self.realtime_results.get_nowait()
                if not self.auto_trim.get():
                    continue
                released = sum(amount for _, amount, error in results if not error)
                errors = [error for _, _, error in results if error and error not in ("Phiên đã thay đổi", "Phiên đã thoát")]
                if errors:
                    self.stop_realtime()
                    self.show_status(f"Đã dừng tự động vì Windows từ chối thu hồi: {errors[0]}")
                    continue
                if results:
                    result_text = f"thu hồi {released / 1024:.2f} GB" if released > 0 else "RAM ổn"
                    self.realtime_text.set(f"{len(self.discovered_targets)} phiên · {result_text} · {time.strftime('%H:%M:%S')}")
        except Empty:
            pass
        due = time.monotonic() >= self.next_trim_at
        idle = self.realtime_worker is None or not self.realtime_worker.is_alive()
        if self.auto_trim.get() and self.discovered_targets and due and idle:
            targets = list(self.discovered_targets.items())
            self.realtime_worker = threading.Thread(target=self._run_realtime_batch, args=(targets,), daemon=True)
            self.realtime_worker.start()
            self.next_trim_at = time.monotonic() + self.interval_seconds
        elif self.auto_trim.get() and not self.discovered_targets:
            self.realtime_text.set("Đang chờ Roblox...")
        self.root.after(50, self.realtime_tick)

    def on_close(self) -> None:
        self.auto_trim.set(False)
        if self.account_window:
            self.close_account_manager()
        if shell32.IsUserAnAdmin():
            for pid_key, snapshot in list(self.cpu_originals.items()):
                self.restore_cpu_limit(pid_key, snapshot)
        self.root.destroy()

    def relaunch_admin(self) -> bool:
        if shell32.IsUserAnAdmin():
            self.show_status("Công cụ đã chạy bằng quyền quản trị.")
            return True
        if getattr(sys, "frozen", False):
            executable, parameters = sys.executable, None
        else:
            executable, parameters = sys.executable, f'"{Path(__file__).resolve()}"'
        result = shell32.ShellExecuteW(None, "runas", executable, parameters, None, 1)
        if (result or 0) <= 32:
            self.show_status("Windows chưa cấp quyền quản trị; công cụ vẫn tiếp tục theo dõi bình thường.")
            return False
        else:
            self.on_close()
            return True

    def set_low(self) -> None:
        try:
            applied = apply_low_graphics(self.state)
            self.show_status("Đã ghi cấu hình: đồ họa 1, ưu tiên hiệu năng, giới hạn 60 FPS." if applied else "Đã lên lịch. Giữ công cụ mở; cấu hình sẽ được ghi sau khi tất cả cửa sổ Roblox đóng.")
        except (OSError, ValueError) as exc:
            messagebox.showerror(APP_NAME, str(exc))

    def restore_config(self) -> None:
        try:
            restore_settings(self.state)
            self.show_status("Đã khôi phục cấu hình Roblox từ bản sao của công cụ.")
        except (OSError, RuntimeError) as exc:
            messagebox.showerror(APP_NAME, str(exc))

    def show_game_help(self) -> None:
        messagebox.showinfo(APP_NAME, "Trong mỗi cửa sổ Roblox: nhấn Esc → Settings → đặt Graphics Mode = Manual, Graphics Quality = mức thấp nhất, Maximum Frame Rate = 60 FPS. Đây là cách áp dụng ngay cho phiên đang chạy.\n\nNếu đã ở mức 1 và 60 FPS mà RAM vẫn tăng đến 6 GB, phần lớn do nội dung game hoặc phiên chạy lâu; hãy đóng và mở lại phiên sau khi lưu tiến độ.")

    def run(self) -> None:
        self.root.mainloop()


if __name__ == "__main__":
    if "--diagnose" in sys.argv:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8")
        print(json.dumps({"processes": [{"pid": p.pid, "rss_mb": round(p.memory_info().rss / MIB)} for p in running_roblox()], "settings": current_settings()}, ensure_ascii=False))
    else:
        App().run()
