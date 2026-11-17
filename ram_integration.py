"""Local integration with the installed Roblox Account Manager (RAM).

Only account names and display metadata are read. Mizuauto never reads RAM's
account database, cookies, or saved login information.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
import re
import urllib.error
import urllib.parse
import urllib.request

import psutil


RAM_EXE = "Roblox Account Manager.exe"


class RamError(Exception):
    pass


@dataclass(frozen=True)
class RamSettings:
    port: int
    web_enabled: bool
    list_enabled: bool
    launch_enabled: bool
    multi_enabled: bool
    place_id: str
    join_delay_ms: int


@dataclass(frozen=True)
class RamAccount:
    username: str
    alias: str = ""
    group: str = ""


def find_ram_executable(preferred: str = "") -> Path | None:
    if preferred:
        path = Path(preferred)
        if path.is_file() and path.name.lower() == RAM_EXE.lower():
            return path
    for process in psutil.process_iter(["name", "exe"]):
        try:
            if (process.info["name"] or "").lower() == RAM_EXE.lower():
                path = Path(process.info["exe"] or "")
                if path.is_file():
                    return path
        except (OSError, psutil.Error):
            continue
    home = Path.home()
    roots = (home / "Downloads", home / "OneDrive" / "Downloads")
    candidates = []
    for root in roots:
        candidates.extend(root.glob("Roblox.Account.Manager.*/" + RAM_EXE))
        candidates.append(root / RAM_EXE)
    existing = [path for path in candidates if path.is_file()]
    return max(existing, key=lambda path: path.stat().st_mtime) if existing else None


def read_ram_settings(executable: Path) -> RamSettings:
    path = executable.parent / "RAMSettings.ini"
    try:
        lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines()
    except OSError as exc:
        raise RamError("Không đọc được cài đặt Roblox Account Manager.") from exc
    safe_keys = {"EnableWebServer", "WebServerPort", "AllowGetAccounts", "AllowLaunchAccount", "EnableMultiRbx", "SavedPlaceId", "AccountJoinDelayMS"}
    values = {}
    for line in lines:
        if "=" in line:
            key, value = line.split("=", 1)
            if key.strip() in safe_keys:
                values[key.strip()] = value.strip()
    try:
        port = int(values.get("WebServerPort", "7963"))
        if not 1 <= port <= 65535:
            raise ValueError
    except ValueError as exc:
        raise RamError("Cổng API của Roblox Account Manager không hợp lệ.") from exc
    try:
        delay = max(0, min(30000, int(values.get("AccountJoinDelayMS", "2500"))))
    except ValueError:
        delay = 2500
    yes = lambda key: values.get(key, "false").lower() == "true"
    place = values.get("SavedPlaceId", "")
    if place and not re.fullmatch(r"[1-9]\d*", place):
        place = ""
    return RamSettings(port, yes("EnableWebServer"), yes("AllowGetAccounts"), yes("AllowLaunchAccount"), yes("EnableMultiRbx"), place, delay)


def _request(port: int, endpoint: str, parameters: dict[str, str]) -> str:
    url = f"http://127.0.0.1:{port}/{endpoint}?{urllib.parse.urlencode(parameters)}"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(url, timeout=8) as response:
            return response.read(1024 * 1024).decode("utf-8-sig", errors="replace")
    except urllib.error.HTTPError as exc:
        if exc.code in (401, 403):
            raise RamError("RAM từ chối truy cập. Kiểm tra mật khẩu API và quyền trong Settings.") from None
        raise RamError(f"RAM trả về lỗi HTTP {exc.code}.") from None
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        raise RamError("Không kết nối được RAM. Hãy mở RAM và bật Enable Web Server.") from None


def list_ram_accounts(port: int, password: str) -> list[RamAccount]:
    try:
        raw = _request(port, "GetAccountsJson", {"Password": password, "IncludeCookies": "false"})
        records = json.loads(raw)
        if not isinstance(records, list):
            raise ValueError
        accounts = [RamAccount(str(item.get("Username", "")).strip(), str(item.get("Alias") or "").strip(), str(item.get("Group") or "").strip()) for item in records if isinstance(item, dict)]
        return [account for account in accounts if account.username]
    except (ValueError, TypeError):
        raw = _request(port, "GetAccounts", {"Password": password})
        return [RamAccount(name.strip()) for name in raw.split(",") if name.strip()]


def launch_ram_account(port: int, password: str, username: str, place_id: str) -> None:
    if not re.fullmatch(r"[1-9]\d*", place_id):
        raise RamError("Place ID phải là một số dương.")
    if not username:
        raise RamError("Chưa chọn tài khoản.")
    _request(port, "LaunchAccount", {"Account": username, "PlaceId": place_id, "Password": password})
