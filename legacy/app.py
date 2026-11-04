#!/usr/bin/env python3
from __future__ import annotations

import argparse
import base64
import json
import os
import queue
import re
import subprocess
import sys
import threading
import time
import tkinter as tk
from datetime import datetime, timedelta, timezone
from pathlib import Path
from tkinter import ttk

try:
    import winsound
except Exception:
    winsound = None

APP_NAME = "Valorant Match Timer"
APP_DIR = Path(os.environ.get("APPDATA", str(Path.home()))) / "ValorantMatchTimer"
SETTINGS_FILE = APP_DIR / "settings.json"
HISTORY_FILE = APP_DIR / "history.json"
DEFAULT_LOG = Path(os.environ.get("LOCALAPPDATA", "")) / "VALORANT" / "Saved" / "Logs" / "ShooterGame.log"

BG = "#0f131a"
CARD = "#171d27"
CARD2 = "#1d2430"
BORDER = "#2a3444"
TEXT = "#e8eef7"
MUTED = "#8b96a8"
ACCENT = "#ff4655"
GREEN = "#00e0a4"
WARN = "#ffb020"

MENU_MAPS = {
    "MainMenuV2", "Login", "Init", "Entry", "CharacterSelectPersistentLevel",
    "PregameV2", "MainMenu", "",
}

CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0

TS_RE = re.compile(r"^\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\d{3})\]")
FLOW_TRANS_RE = re.compile(r"Transitioning from (\w+) to (\w+)")
FLOW_BROADCAST_RE = re.compile(r"Broadcasting state changed to (\w+)")
MAP_RE = re.compile(
    r"\[Map Name:\s*([^|\]]+?)\s*\|.*?\[Match Setup:\s*(TRUE|FALSE).*?"
    r"\[Map Ready:\s*(TRUE|FALSE).*?\[Map Complete:\s*(TRUE|FALSE).*?\[URL:\s*([^\]]*)\]"
)
GAME_RE = re.compile(r"game=([^?\s#]+)")


class GameMode:
    def __init__(self, key, label, minutes, broad_hints=()):
        self.key = key
        self.label = label
        self.seconds = minutes * 60
        self.broad_hints = tuple(h.lower() for h in broad_hints)


MODES = [
    GameMode("competitive", "Competitive", 40),
    GameMode("unrated", "Unrated", 35),
    GameMode("swiftplay", "Swiftplay", 15),
    GameMode("spikerush", "Spike Rush", 10),
    GameMode("deathmatch", "Deathmatch", 10, ["deathmatch"]),
    GameMode("tdm", "Team Deathmatch", 12, ["hurm"]),
    GameMode("escalation", "Escalation", 10, ["ggteam"]),
    GameMode("custom", "Custom", 35),
]
MODE_BY_KEY = {m.key: m for m in MODES}


def now_local():
    return datetime.now(timezone.utc).astimezone()


def parse_log_ts(line):
    m = TS_RE.match(line)
    if not m:
        return None
    try:
        dt = datetime.strptime(m.group(1), "%Y.%m.%d-%H.%M.%S:%f")
        return dt.replace(tzinfo=timezone.utc).astimezone()
    except ValueError:
        return None


def fmt_hms(seconds):
    s = int(max(0, seconds))
    h, r = divmod(s, 3600)
    m, sec = divmod(r, 60)
    return f"{h}:{m:02d}:{sec:02d}"


def fmt_match(seconds):
    s = int(max(0, seconds))
    h, r = divmod(s, 3600)
    m, sec = divmod(r, 60)
    if h:
        return f"{h}:{m:02d}:{sec:02d}"
    return f"{m:02d}:{sec:02d}"


def fmt_duration(seconds):
    s = int(max(0, seconds))
    h, r = divmod(s, 3600)
    m, sec = divmod(r, 60)
    if h:
        return f"{h}h {m:02d}m"
    if m:
        return f"{m}m {sec:02d}s"
    return f"{sec}s"


def parse_duration(text):
    if not text:
        return None
    t = str(text).strip().lower()
    if not t:
        return None
    m = re.fullmatch(r"(\d{1,2}):(\d{2})", t)
    if m:
        return int(m.group(1)) * 3600 + int(m.group(2)) * 60
    if re.fullmatch(r"\d+(?:\.\d+)?", t):
        return int(float(t) * 60)
    total = 0.0
    unit_map = {
        "h": 3600, "hr": 3600, "hrs": 3600, "hour": 3600, "hours": 3600,
        "m": 60, "min": 60, "mins": 60, "minute": 60, "minutes": 60,
        "s": 1, "sec": 1, "secs": 1, "second": 1, "seconds": 1,
    }
    found = re.findall(r"(\d+(?:\.\d+)?)\s*(hours|hour|hrs|hr|h|minutes|minute|mins|min|m|seconds|second|secs|sec|s)", t)
    for num, unit in found:
        total += float(num) * unit_map[unit]
    if total > 0:
        return int(total)
    return None


def parse_clock(text):
    if not text:
        return None
    m = re.fullmatch(r"\s*(\d{1,2}):(\d{2})\s*", str(text))
    if not m:
        return None
    hour, minute = int(m.group(1)), int(m.group(2))
    if not (0 <= hour <= 23 and 0 <= minute <= 59):
        return None
    now = now_local()
    target = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
    if target <= now:
        target += timedelta(days=1)
    return target


def short_mode(broad):
    b = (broad or "").lower()
    if "hurm" in b:
        return "Team Deathmatch"
    if "deathmatch" in b:
        return "Deathmatch"
    if "shootingrange" in b or "range" in b:
        return "Range"
    if "bomb" in b:
        return "Bomb"
    return (broad.rsplit("/", 1)[-1].replace("GameMode_C", "") or "Unknown")


def mode_key_for_broad(broad, fallback):
    b = (broad or "").lower()
    if "hurm" in b:
        return "tdm"
    if "deathmatch" in b:
        return "deathmatch"
    if "ggteam" in b:
        return "escalation"
    return fallback


class MatchParser:
    def __init__(self):
        self.flow_state = ""
        self.in_match = False
        self.start_dt = None
        self.last_dt = None
        self.map_name = ""
        self.broad = ""
        self.match_map = ""
        self.match_broad = ""
        self.setup = False
        self.ready = False
        self.complete = False

    def _ignored_map(self):
        if self.map_name in MENU_MAPS:
            return True
        if "ShootingRange" in self.broad:
            return True
        if self.map_name.startswith("Range"):
            return True
        return False

    def _end(self, ts):
        if not self.in_match:
            return []
        end = ts or now_local()
        start = self.start_dt or end
        duration = max(0.0, (end - start).total_seconds())
        event = {
            "type": "match_end",
            "start_ts": start.timestamp(),
            "end_ts": end.timestamp(),
            "duration": duration,
            "map": self.match_map or self.map_name,
            "broad": self.match_broad or self.broad,
        }
        self.in_match = False
        self.start_dt = None
        self.map_name = ""
        self.broad = ""
        self.match_map = ""
        self.match_broad = ""
        self.setup = False
        self.ready = False
        self.complete = False
        return [event]

    def force_end(self, ts=None):
        return self._end(ts or self.last_dt or now_local())

    def feed(self, line, emit=True):
        events = []
        ts = parse_log_ts(line)
        if ts:
            self.last_dt = ts

        m = FLOW_TRANS_RE.search(line)
        if not m:
            m = FLOW_BROADCAST_RE.search(line)
        if m:
            new_state = m.group(1) if m.lastindex == 1 else m.group(2)
            self.flow_state = new_state
            if emit:
                events.append({"type": "phase", "phase": new_state})
            if new_state in ("TransitionToMainMenu", "MainMenu") and self.in_match:
                events.extend(self._end(ts))

        m = MAP_RE.search(line)
        if m:
            self.map_name = m.group(1).strip()
            self.setup = m.group(2) == "TRUE"
            self.ready = m.group(3) == "TRUE"
            self.complete = m.group(4) == "TRUE"
            url = m.group(5) or ""
            gm = GAME_RE.search(url)
            self.broad = gm.group(1) if gm else ""

            if self.map_name in MENU_MAPS:
                if self.in_match:
                    events.extend(self._end(ts))
            elif self._ignored_map():
                pass
            elif self.setup and self.ready and not self.complete and not self.in_match:
                self.in_match = True
                self.start_dt = ts
                self.match_map = self.map_name
                self.match_broad = self.broad
                if emit:
                    events.append({
                        "type": "match_start",
                        "start_ts": ts.timestamp() if ts else time.time(),
                        "map": self.map_name,
                        "broad": self.broad,
                    })
            elif self.complete and self.in_match:
                events.extend(self._end(ts))
        return events


class LogWatcher(threading.Thread):
    TAIL_BYTES = 8 * 1024 * 1024

    def __init__(self, out_queue, stop_event, path):
        super().__init__(daemon=True)
        self.out_queue = out_queue
        self.stop_event = stop_event
        self.path = Path(path)
        self.parser = MatchParser()
        self.pos = 0
        self.buf = ""
        self.tail_done = False
        self.missing_logged = False
        self.last_check = 0.0

    def _emit(self, event):
        event["source"] = "auto"
        self.out_queue.put(event)

    def _tail_scan(self):
        try:
            size = self.path.stat().st_size
            recent = (time.time() - self.path.stat().st_mtime) < 300
        except OSError:
            return
        self.pos = size
        if not recent:
            return
        start_pos = max(0, size - self.TAIL_BYTES)
        try:
            with open(self.path, "r", encoding="utf-8", errors="replace") as f:
                f.seek(start_pos)
                data = f.read()
        except OSError:
            return
        if start_pos > 0:
            data = data.split("\n", 1)[-1]
        for line in data.splitlines():
            self.parser.feed(line, emit=False)
        if self.parser.in_match and self.parser.start_dt:
            self._emit({
                "type": "match_start",
                "start_ts": self.parser.start_dt.timestamp(),
                "map": self.parser.match_map,
                "broad": self.parser.match_broad,
                "resumed": True,
            })

    def _read_new(self):
        with open(self.path, "r", encoding="utf-8", errors="replace") as f:
            f.seek(self.pos)
            data = f.read()
            self.pos = f.tell()
        if not data:
            return
        self.buf += data
        if "\n" not in self.buf:
            return
        parts = self.buf.split("\n")
        self.buf = parts.pop()
        for line in parts:
            for event in self.parser.feed(line):
                self._emit(event)

    def run(self):
        while not self.stop_event.is_set():
            try:
                if not self.path.exists():
                    if not self.missing_logged:
                        self.missing_logged = True
                        self._emit({"type": "status", "text": "Valorant log not found. Manual timer still works."})
                    self.stop_event.wait(5)
                    continue
                if not self.tail_done:
                    self.tail_done = True
                    self._tail_scan()
                    continue
                size = self.path.stat().st_size
                if size < self.pos:
                    self.parser = MatchParser()
                    self.pos = 0
                    self.buf = ""
                    self._tail_scan()
                    continue
                if size > self.pos:
                    self._read_new()
                else:
                    now = time.time()
                    if now - self.last_check > 30:
                        self.last_check = now
                        if self.parser.in_match and self.parser.last_dt:
                            idle = now - self.parser.last_dt.timestamp()
                            if idle > 600:
                                for event in self.parser.force_end(self.parser.last_dt):
                                    self._emit(event)
                    self.stop_event.wait(0.4)
            except Exception:
                self.stop_event.wait(1)


class History:
    LIMIT = 40

    def __init__(self, path):
        self.path = path
        self.data = {}
        try:
            self.data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            self.data = {}

    def add(self, key, seconds, map_name, broad):
        if key == "custom" or not (120 <= seconds <= 3 * 3600):
            return
        entries = self.data.setdefault(key, [])
        entries.append({
            "seconds": round(seconds, 1),
            "map": map_name,
            "broad": broad,
            "at": now_local().isoformat(timespec="seconds"),
        })
        self.data[key] = entries[-self.LIMIT:]
        self.save()

    def save(self):
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.path.write_text(json.dumps(self.data, indent=2), encoding="utf-8")
        except OSError:
            pass

    def stats(self, key):
        values = [e.get("seconds", 0) for e in self.data.get(key, []) if e.get("seconds")]
        values = values[-20:]
        if not values:
            return None, 0
        return sum(values) / len(values), len(values)

    def predicted(self, key, default_seconds):
        avg, count = self.stats(key)
        if not count:
            return default_seconds, 0, default_seconds
        if count == 1:
            return round((avg + default_seconds) / 2), 1, avg
        return round(avg), count, avg


def load_settings():
    defaults = {
        "always_on_top": True,
        "shutdown_after_match": True,
        "warning_minutes": 2,
        "selected_mode": "competitive",
        "custom_duration": "35m",
        "start_with_windows": True,
        "cutoff_iso": None,
        "geometry": "500x860",
        "log_path": str(DEFAULT_LOG),
        "show_overlay": True,
        "defer_shutdown_in_match": True,
        "overlay_pos": None,
    }
    try:
        data = json.loads(SETTINGS_FILE.read_text(encoding="utf-8"))
        defaults.update(data)
    except Exception:
        pass
    return defaults


def save_json(path, data):
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    except OSError:
        pass


def startup_shortcut_path():
    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    return Path(appdata) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / "Startup" / "Valorant Match Timer.lnk"


def pythonw_path():
    exe = Path(sys.executable)
    candidate = exe.with_name("pythonw.exe")
    return str(candidate if candidate.exists() else exe)


def set_startup(enabled):
    shortcut = startup_shortcut_path()
    if shortcut is None:
        return False, "APPDATA not found"
    if not enabled:
        try:
            shortcut.unlink(missing_ok=True)
            return True, "Removed from startup"
        except OSError as e:
            return False, str(e)
    app_path = Path(__file__).resolve()
    ps = (
        "$ws = New-Object -ComObject WScript.Shell\n"
        f"$s = $ws.CreateShortcut(\"{shortcut}\")\n"
        f"$s.TargetPath = \"{pythonw_path()}\"\n"
        f"$s.Arguments = '\"{app_path}\"'\n"
        f"$s.WorkingDirectory = \"{app_path.parent}\"\n"
        "$s.WindowStyle = 7\n"
        "$s.Description = \"Valorant Match Timer\"\n"
        "$s.Save()\n"
    )
    try:
        proc = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps],
            capture_output=True, text=True, timeout=20,
            creationflags=CREATE_NO_WINDOW,
        )
    except Exception as e:
        return False, str(e)
    if proc.returncode == 0 and shortcut.exists():
        return True, "Added to startup"
    return False, (proc.stderr or "Could not create shortcut").strip()


def beep():
    if winsound:
        try:
            winsound.MessageBeep(winsound.MB_ICONEXCLAMATION)
        except Exception:
            pass


class App:
    def __init__(self, root):
        self.root = root
        self.settings = load_settings()
        self.history = History(HISTORY_FILE)
        self.events = queue.Queue()
        self.stop_event = threading.Event()
        self.log_path = Path(self.settings.get("log_path") or DEFAULT_LOG)
        self.active = None
        self.cutoff = None
        self.cutoff_fired = False
        self.warning = None
        self.toast_win = None
        self.last_match = None
        self.closed = False
        self.mode_buttons = {}
        self.start_with_windows = tk.BooleanVar(value=bool(self.settings.get("start_with_windows", True)))
        self.always_on_top = tk.BooleanVar(value=bool(self.settings.get("always_on_top", True)))
        self.shutdown_after = tk.BooleanVar(value=bool(self.settings.get("shutdown_after_match", True)))
        self.show_overlay = tk.BooleanVar(value=bool(self.settings.get("show_overlay", True)))
        self.defer_in_match = tk.BooleanVar(value=bool(self.settings.get("defer_shutdown_in_match", True)))
        self.warning_minutes = tk.IntVar(value=int(self.settings.get("warning_minutes", 2)))
        self.overlay = None
        self.overlay_pos = self.settings.get("overlay_pos")
        self.pending_shutdown = None
        self.selected_key = self.settings.get("selected_mode", "competitive")
        if self.selected_key not in MODE_BY_KEY:
            self.selected_key = "competitive"

        self._build_ui()
        if self.show_overlay.get():
            self._build_overlay()
        self._load_cutoff()
        self._ensure_startup()

        root.protocol("WM_DELETE_WINDOW", self.on_close)
        root.attributes("-topmost", bool(self.always_on_top.get()))
        root.after(200, self.tick)

        self.watcher = LogWatcher(self.events, self.stop_event, self.log_path)
        self.watcher.start()

        self.log("Ready. Set a get-off time or start a manual timer.")
        self.log("Valorant auto-detect watches ShooterGame.log; the get-off timer works over any game.")

    def _build_ui(self):
        root = self.root
        root.title(APP_NAME)
        root.configure(bg=BG)
        geometry = self.settings.get("geometry") or "500x860"
        try:
            root.geometry(geometry)
        except Exception:
            root.geometry("500x860")
        root.minsize(470, 760)

        style = ttk.Style(root)
        try:
            style.theme_use("clam")
        except Exception:
            pass
        style.configure("Timer.Horizontal.TProgressbar", troughcolor=CARD2, background=ACCENT, borderwidth=0, lightcolor=ACCENT, darkcolor=ACCENT)

        outer = tk.Frame(root, bg=BG, padx=14, pady=12)
        outer.pack(fill="both", expand=True)

        header = tk.Frame(outer, bg=BG)
        header.pack(fill="x")
        tk.Label(header, text="VALORANT MATCH TIMER", bg=BG, fg=TEXT, font=("Segoe UI", 13, "bold")).pack(side="left")
        self.status_label = tk.Label(header, text="Watching for Valorant", bg=BG, fg=MUTED, font=("Segoe UI", 9))
        self.status_label.pack(side="right")
        self.status_dot = tk.Label(header, text="●", bg=BG, fg=MUTED, font=("Segoe UI", 11))
        self.status_dot.pack(side="right", padx=(0, 5))

        getoff = tk.Frame(outer, bg=CARD, padx=14, pady=12, highlightthickness=1, highlightbackground=BORDER)
        getoff.pack(fill="x", pady=(10, 8))
        tk.Label(getoff, text="TIME LEFT BEFORE YOU GET OFF PC", bg=CARD, fg=MUTED, font=("Segoe UI", 9, "bold")).pack(anchor="w")
        self.getoff_big = tk.Label(getoff, text="—:—:—", bg=CARD, fg=TEXT, font=("Segoe UI", 38, "bold"))
        self.getoff_big.pack(anchor="w", pady=(0, 0))
        self.getoff_hint = tk.Label(getoff, text="Set the clock time you need to stop.", bg=CARD, fg=MUTED, font=("Segoe UI", 9))
        self.getoff_hint.pack(anchor="w")

        row = tk.Frame(getoff, bg=CARD)
        row.pack(fill="x", pady=(8, 0))
        tk.Label(row, text="Get off at", bg=CARD, fg=MUTED, font=("Segoe UI", 9)).pack(side="left", padx=(0, 6))
        self.clock_entry = tk.Entry(row, width=7, justify="center", bg="#0c1017", fg=TEXT, insertbackground=TEXT, relief="flat", font=("Segoe UI", 12, "bold"))
        self.clock_entry.pack(side="left", ipady=3)
        self.clock_entry.insert(0, "23:00")
        self.clock_entry.bind("<Return>", lambda e: self.set_cutoff())
        self._button(row, "Set", self.set_cutoff, width=5, accent=True).pack(side="left", padx=4)
        self._button(row, "+30m", lambda: self.quick_cutoff(30), width=5).pack(side="left", padx=2)
        self._button(row, "+1h", lambda: self.quick_cutoff(60), width=5).pack(side="left", padx=2)
        self._button(row, "Clear", self.clear_cutoff, width=6).pack(side="left", padx=2)

        match = tk.Frame(outer, bg=CARD, padx=14, pady=10, highlightthickness=1, highlightbackground=BORDER)
        match.pack(fill="x", pady=(0, 8))
        mhead = tk.Frame(match, bg=CARD)
        mhead.pack(fill="x")
        tk.Label(mhead, text="MATCH TIMER", bg=CARD, fg=MUTED, font=("Segoe UI", 9, "bold")).pack(side="left")
        self.match_status = tk.Label(mhead, text="Not in a match", bg=CARD, fg=MUTED, font=("Segoe UI", 9))
        self.match_status.pack(side="right")
        self.match_big = tk.Label(match, text="00:00", bg=CARD, fg=TEXT, font=("Segoe UI", 30, "bold"))
        self.match_big.pack(anchor="w", pady=(2, 0))
        self.match_pred = tk.Label(match, text="Pick a mode, then press Start for a manual timer.", bg=CARD, fg=MUTED, font=("Segoe UI", 9), wraplength=440, justify="left")
        self.match_pred.pack(anchor="w")
        self.progress = ttk.Progressbar(match, style="Timer.Horizontal.TProgressbar", orient="horizontal", mode="determinate", maximum=1000)
        self.progress.pack(fill="x", pady=(7, 8))

        grid = tk.Frame(match, bg=CARD)
        grid.pack(fill="x")
        for i, mode in enumerate(MODES):
            r, c = divmod(i, 4)
            grid.columnconfigure(c, weight=1)
            text = f"{mode.label}\n{mode.seconds // 60}m" if mode.key != "custom" else "Custom\nset time"
            btn = tk.Button(
                grid, text=text, command=lambda k=mode.key: self.select_mode(k),
                bg=CARD2, fg=TEXT, activebackground="#2b3648", activeforeground=TEXT,
                relief="flat", bd=0, font=("Segoe UI", 9, "bold"), height=2, cursor="hand2",
            )
            btn.grid(row=r, column=c, sticky="nsew", padx=2, pady=2)
            self.mode_buttons[mode.key] = btn

        custom_row = tk.Frame(match, bg=CARD)
        custom_row.pack(fill="x", pady=(8, 0))
        tk.Label(custom_row, text="Custom duration", bg=CARD, fg=MUTED, font=("Segoe UI", 9)).pack(side="left")
        self.custom_entry = tk.Entry(custom_row, width=10, justify="center", bg="#0c1017", fg=TEXT, insertbackground=TEXT, relief="flat", font=("Segoe UI", 11))
        self.custom_entry.pack(side="left", padx=6, ipady=2)
        self.custom_entry.insert(0, self.settings.get("custom_duration", "35m"))
        self.custom_entry.bind("<Return>", lambda e: self.start_selected())
        tk.Label(custom_row, text="e.g. 35m, 1h 10m", bg=CARD, fg=MUTED, font=("Segoe UI", 8)).pack(side="left")

        action_row = tk.Frame(match, bg=CARD)
        action_row.pack(fill="x", pady=(8, 0))
        self._button(action_row, "Start timer", self.start_selected, accent=True).pack(side="left")
        self._button(action_row, "Stop", self.stop_timer, width=7).pack(side="left", padx=6)
        self._button(action_row, "Clear stats", self.clear_stats, width=10).pack(side="left")

        self.pred_label = tk.Label(match, text="", bg=CARD, fg=GREEN, font=("Segoe UI", 8))
        self.pred_label.pack(anchor="w", pady=(6, 0))

        opts = tk.Frame(outer, bg=CARD, padx=14, pady=9, highlightthickness=1, highlightbackground=BORDER)
        opts.pack(fill="x", pady=(0, 8))
        line1 = tk.Frame(opts, bg=CARD)
        line1.pack(fill="x")
        self._check(line1, "Always on top", self.always_on_top, self.toggle_topmost).pack(side="left")
        self._check(line1, "Start with Windows", self.start_with_windows, self.toggle_startup).pack(side="left", padx=(18, 0))
        line2 = tk.Frame(opts, bg=CARD)
        line2.pack(fill="x", pady=(4, 0))
        self._check(line2, "Ask to shut down when match ends", self.shutdown_after, self.save_settings).pack(side="left")
        tk.Label(line2, text="warning", bg=CARD, fg=MUTED, font=("Segoe UI", 9)).pack(side="left", padx=(14, 4))
        spin = tk.Spinbox(line2, from_=1, to=15, width=3, textvariable=self.warning_minutes, justify="center", bg="#0c1017", fg=TEXT, relief="flat", buttonbackground=CARD2, command=self.save_settings)
        spin.pack(side="left", ipady=1)
        tk.Label(line2, text="min", bg=CARD, fg=MUTED, font=("Segoe UI", 9)).pack(side="left", padx=(4, 0))
        line3 = tk.Frame(opts, bg=CARD)
        line3.pack(fill="x", pady=(7, 0))
        self._button(line3, "Shut down in 2:00", self.manual_shutdown, accent=True).pack(side="left")
        self._button(line3, "Cancel shutdown", self.cancel_shutdown, width=14).pack(side="left", padx=6)
        line4 = tk.Frame(opts, bg=CARD)
        line4.pack(fill="x", pady=(5, 0))
        self._check(line4, "Show overlay", self.show_overlay, self.toggle_overlay).pack(side="left")
        self._check(line4, "Wait for match/OT to end before shutdown", self.defer_in_match, self.save_settings).pack(side="left", padx=(16, 0))

        logcard = tk.Frame(outer, bg=CARD, padx=8, pady=6, highlightthickness=1, highlightbackground=BORDER)
        logcard.pack(fill="both", expand=True)
        tk.Label(logcard, text="ACTIVITY", bg=CARD, fg=MUTED, font=("Segoe UI", 8, "bold")).pack(anchor="w", padx=4)
        self.log_text = tk.Text(logcard, height=5, bg="#0b0e14", fg="#9fb0c7", relief="flat", font=("Consolas", 8), wrap="word", state="disabled")
        self.log_text.pack(fill="both", expand=True, padx=4, pady=(3, 0))

        self.select_mode(self.selected_key)
        self.update_prediction_label()

    def _button(self, parent, text, command, width=None, accent=False):
        kwargs = {}
        if width:
            kwargs["width"] = width
        return tk.Button(
            parent, text=text, command=command,
            bg=ACCENT if accent else CARD2,
            fg="#ffffff" if accent else TEXT,
            activebackground="#ff5d6a" if accent else "#2b3648",
            activeforeground="#ffffff" if accent else TEXT,
            relief="flat", bd=0, font=("Segoe UI", 9, "bold"),
            padx=8, pady=4, cursor="hand2", **kwargs,
        )

    def _check(self, parent, text, variable, command):
        return tk.Checkbutton(
            parent, text=text, variable=variable, command=command,
            bg=CARD, fg=TEXT, activebackground=CARD, activeforeground=TEXT,
            selectcolor=CARD2, font=("Segoe UI", 9), bd=0, highlightthickness=0,
        )

    def _build_overlay(self):
        if self.overlay:
            try:
                self.overlay.deiconify()
                self.overlay.lift()
                return
            except Exception:
                self.overlay = None
        win = tk.Toplevel(self.root)
        self.overlay = win
        win.overrideredirect(True)
        win.attributes("-topmost", True)
        try:
            win.attributes("-alpha", 0.9)
        except Exception:
            pass
        win.configure(bg=ACCENT)
        width, height = 340, 130
        pos = self.overlay_pos
        if isinstance(pos, (list, tuple)) and len(pos) == 2:
            x, y = int(pos[0]), int(pos[1])
        else:
            x = max(10, (win.winfo_screenwidth() - width) // 2)
            y = 24
        x = max(0, min(x, win.winfo_screenwidth() - width))
        y = max(0, min(y, win.winfo_screenheight() - height))
        win.geometry(f"{width}x{height}+{x}+{y}")
        inner = tk.Frame(win, bg="#0b0e14", padx=12, pady=8)
        inner.pack(fill="both", expand=True, padx=2, pady=2)
        top = tk.Frame(inner, bg="#0b0e14")
        top.pack(fill="x")
        tk.Label(top, text="GET OFF IN", bg="#0b0e14", fg=MUTED, font=("Segoe UI", 8, "bold")).pack(side="left")
        close = tk.Label(top, text="×", bg="#0b0e14", fg=MUTED, font=("Segoe UI", 12, "bold"), cursor="hand2")
        close.pack(side="right")
        close.bind("<Button-1>", lambda e: self.toggle_overlay(False))
        self.ov_left = tk.Label(inner, text="SET TIME", bg="#0b0e14", fg=TEXT, font=("Segoe UI", 26, "bold"))
        self.ov_left.pack(anchor="w")
        self.ov_sub = tk.Label(inner, text="No get-off time set", bg="#0b0e14", fg=MUTED, font=("Segoe UI", 8), justify="left", wraplength=300)
        self.ov_sub.pack(anchor="w")

        drag = {}

        def start_move(event):
            drag["x"] = event.x_root - win.winfo_x()
            drag["y"] = event.y_root - win.winfo_y()

        def do_move(event):
            nx = event.x_root - drag.get("x", 0)
            ny = event.y_root - drag.get("y", 0)
            win.geometry(f"+{nx}+{ny}")
            self.overlay_pos = [nx, ny]

        for widget in (win, inner, top, self.ov_left, self.ov_sub):
            widget.bind("<Button-1>", start_move)
            widget.bind("<B1-Motion>", do_move)
            widget.bind("<ButtonRelease-1>", lambda e: self.save_settings())
        win.lift()
        self.render_overlay(time.time())

    def toggle_overlay(self, show=None):
        if show is None:
            show = bool(self.show_overlay.get())
        self.show_overlay.set(bool(show))
        if show:
            self._build_overlay()
            try:
                self.overlay.deiconify()
                self.overlay.lift()
            except Exception:
                pass
        elif self.overlay:
            try:
                self.overlay.withdraw()
            except Exception:
                pass
        self.save_settings()

    def render_overlay(self, now):
        if not self.overlay or not self.show_overlay.get():
            return
        try:
            if self.cutoff:
                remaining = self.cutoff.timestamp() - now
                if remaining > 0:
                    self.ov_left.configure(text=fmt_hms(remaining), fg=TEXT)
                else:
                    self.ov_left.configure(text="00:00:00", fg=WARN)
            else:
                self.ov_left.configure(text="SET TIME", fg=MUTED)
            parts = []
            if self.cutoff:
                parts.append(f"Get off at {self.cutoff:%H:%M}")
            if self.pending_shutdown:
                parts.append("Match in progress — shutdown after it ends")
            if self.active:
                a = self.active
                end_clock = datetime.fromtimestamp(a["start"] + a["expected"]).strftime("%H:%M")
                rem = a["expected"] - (now - a["start"])
                if rem < 0:
                    parts.append(f"{a['label']} OT +{fmt_match(-rem)} · ends {end_clock}")
                    self.ov_left.configure(fg=ACCENT)
                else:
                    parts.append(f"{a['label']} · ends {end_clock}")
            elif not self.pending_shutdown:
                parts.append("No match detected")
            self.ov_sub.configure(text="\n".join(parts))
        except Exception:
            pass

    def log(self, message):
        try:
            self.log_text.configure(state="normal")
            self.log_text.insert("end", f"[{now_local():%H:%M:%S}] {message}\n")
            if int(self.log_text.index("end-1c").split(".")[0]) > 400:
                self.log_text.delete("1.0", "100.0")
            self.log_text.see("end")
            self.log_text.configure(state="disabled")
        except Exception:
            pass

    def update_prediction_label(self):
        key = self.selected_key
        if key == "custom":
            seconds = parse_duration(self.custom_entry.get()) if hasattr(self, "custom_entry") else None
            seconds = seconds or MODE_BY_KEY["custom"].seconds
            self.pred_label.configure(text=f"Custom prediction: {fmt_duration(seconds)}")
            return
        mode = MODE_BY_KEY[key]
        predicted, count, avg = self.history.predicted(key, mode.seconds)
        if count:
            self.pred_label.configure(
                text=f"{mode.label} prediction: {fmt_duration(predicted)}  (avg of {count} past game{'s' if count != 1 else ''}, {fmt_duration(avg)})"
            )
        else:
            self.pred_label.configure(text=f"{mode.label} prediction: {fmt_duration(mode.seconds)}  (default — gets smarter after games)")

    def select_mode(self, key):
        if key not in MODE_BY_KEY:
            return
        self.selected_key = key
        for k, btn in self.mode_buttons.items():
            if k == key:
                btn.configure(bg=ACCENT, fg="#ffffff", activebackground="#ff5d6a")
            else:
                btn.configure(bg=CARD2, fg=TEXT, activebackground="#2b3648")
        if key == "custom":
            try:
                self.custom_entry.focus_set()
                self.custom_entry.selection_range(0, "end")
            except Exception:
                pass
        self.update_prediction_label()
        self.save_settings()

    def prediction_for(self, key):
        if key == "custom":
            seconds = parse_duration(self.custom_entry.get()) or MODE_BY_KEY["custom"].seconds
            return seconds, seconds, 0, seconds
        mode = MODE_BY_KEY[key]
        predicted, count, avg = self.history.predicted(key, mode.seconds)
        return predicted, mode.seconds, count, avg

    def start_selected(self):
        key = self.selected_key
        if key == "custom":
            seconds = parse_duration(self.custom_entry.get())
            if not seconds:
                self.log("Custom duration is invalid. Try 35m, 1h 10m, or 90.")
                beep()
                return
            if seconds > 6 * 3600:
                self.log("Custom duration is too long.")
                return
            self.start_timer(key, seconds, "manual")
        else:
            predicted, _, _, _ = self.prediction_for(key)
            self.start_timer(key, predicted, "manual")

    def start_timer(self, key, expected, source, start_ts=None, map_name=None, broad=None, resumed=False):
        label = MODE_BY_KEY.get(key, MODE_BY_KEY["custom"]).label
        start = start_ts or time.time()
        self.active = {
            "mode_key": key,
            "label": label,
            "expected": int(expected),
            "start": float(start),
            "source": source,
            "map": map_name or "",
            "broad": broad or "",
            "done": False,
        }
        when = datetime.fromtimestamp(start).strftime("%H:%M")
        if resumed:
            self.log(f"Resumed {label} match on {map_name or 'map'} (started {when}).")
        elif source == "manual":
            self.log(f"Manual timer started: {label}, {fmt_duration(expected)}.")
        else:
            self.log(f"Auto-detected match: {label} on {map_name or 'map'} (started {when}).")
        self.match_status.configure(text=f"{label} · {'auto' if source == 'auto' else 'manual'}")
        self.update_prediction_label()

    def stop_timer(self):
        if self.active:
            self.log("Match timer stopped.")
        if self.pending_shutdown and (not self.active or self.active.get("source") == "manual"):
            reason = self.pending_shutdown
            self.pending_shutdown = None
            self.root.after(600, lambda: self.begin_shutdown("Get-off time reached", reason))
        self.active = None
        self.match_status.configure(text="Not in a match")
        self.match_big.configure(text="00:00", fg=TEXT)
        self.match_pred.configure(text="Pick a mode, then press Start for a manual timer.")
        self.progress["value"] = 0

    def clear_stats(self):
        self.history.data = {}
        self.history.save()
        self.update_prediction_label()
        self.log("Prediction history cleared.")

    def set_cutoff(self):
        target = parse_clock(self.clock_entry.get())
        if not target:
            self.log("Get-off time is invalid. Use 24-hour HH:MM, e.g. 23:30.")
            beep()
            return
        self.cutoff = target
        self.cutoff_fired = False
        self.clock_entry.delete(0, "end")
        self.clock_entry.insert(0, target.strftime("%H:%M"))
        self.log(f"Get-off time set for {target:%H:%M}.")
        self.save_settings()
        self.render_getoff(time.time())

    def quick_cutoff(self, minutes):
        self.cutoff = now_local() + timedelta(minutes=minutes)
        self.cutoff_fired = False
        self.clock_entry.delete(0, "end")
        self.clock_entry.insert(0, self.cutoff.strftime("%H:%M"))
        self.log(f"Get-off time set for {self.cutoff:%H:%M} (+{minutes}m).")
        self.save_settings()
        self.render_getoff(time.time())

    def clear_cutoff(self):
        self.cutoff = None
        self.cutoff_fired = False
        self.pending_shutdown = None
        self.getoff_big.configure(text="—:—:—", fg=TEXT)
        self.getoff_hint.configure(text="Set the clock time you need to stop.")
        self.log("Get-off time cleared.")
        self.save_settings()

    def render_getoff(self, now):
        if not self.cutoff:
            self.getoff_big.configure(text="—:—:—", fg=TEXT)
            self.getoff_hint.configure(text="Set the clock time you need to stop.")
            return
        remaining = self.cutoff.timestamp() - now
        if self.cutoff_fired or remaining <= 0:
            self.getoff_big.configure(text="00:00:00", fg=WARN)
            if self.pending_shutdown:
                self.getoff_hint.configure(text=f"Get-off time reached ({self.cutoff:%H:%M}). Waiting for the match/OT to finish.")
            else:
                self.getoff_hint.configure(text=f"Get-off time reached ({self.cutoff:%H:%M}). Shutdown warning active.")
        else:
            self.getoff_big.configure(text=fmt_hms(remaining), fg=TEXT)
            self.getoff_hint.configure(text=f"Get off at {self.cutoff:%H:%M}. Works over any game.")

    def render_match(self, now):
        if not self.active:
            return
        a = self.active
        elapsed = now - a["start"]
        remaining = a["expected"] - elapsed
        if remaining >= 0:
            self.match_big.configure(text=fmt_match(remaining), fg=TEXT)
        else:
            self.match_big.configure(text=f"+{fmt_match(-remaining)}", fg=ACCENT)
        end_clock = datetime.fromtimestamp(a["start"] + a["expected"]).strftime("%H:%M")
        source = "avg prediction" if a["source"] == "auto" else "manual"
        map_part = f" · {a['map']}" if a.get("map") else ""
        self.match_status.configure(text=f"{a['label']}{map_part} · {source}")
        if remaining >= 0:
            self.match_pred.configure(text=f"Predicted end {end_clock} · {fmt_duration(a['expected'])} expected · elapsed {fmt_match(elapsed)}")
        else:
            self.match_pred.configure(text=f"Prediction ended at {end_clock} · still in match · elapsed {fmt_match(elapsed)}")
        self.progress["value"] = max(0, min(1000, int(1000 * elapsed / max(1, a["expected"]))))
        if remaining <= 0 and not a["done"]:
            a["done"] = True
            if a["source"] == "manual":
                self.log(f"Manual timer finished: {a['label']} ({fmt_duration(a['expected'])}).")
                if self.pending_shutdown:
                    reason = self.pending_shutdown
                    self.pending_shutdown = None
                    self.root.after(1200, lambda: self.begin_shutdown("Get-off time reached", reason))
                elif self.shutdown_after.get():
                    self.root.after(1200, lambda: self.begin_shutdown("Timer finished", f"{a['label']} — {fmt_duration(a['expected'])}"))
                else:
                    self.notify("Timer finished", f"{a['label']} — {fmt_duration(a['expected'])} planned.")
                    beep()

    def toggle_topmost(self):
        self.root.attributes("-topmost", bool(self.always_on_top.get()))
        self.save_settings()

    def toggle_startup(self):
        ok, message = set_startup(bool(self.start_with_windows.get()))
        if not ok:
            self.start_with_windows.set(not bool(self.start_with_windows.get()))
            self.log(f"Startup change failed: {message}")
        else:
            self.log(message)
        self.save_settings()

    def _ensure_startup(self):
        shortcut = startup_shortcut_path()
        if self.start_with_windows.get() and shortcut and not shortcut.exists():
            ok, message = set_startup(True)
            self.log(message)

    def _load_cutoff(self):
        iso = self.settings.get("cutoff_iso")
        if not iso:
            return
        try:
            target = datetime.fromisoformat(iso)
            if target.tzinfo is None:
                target = target.replace(tzinfo=now_local().tzinfo)
            if target.timestamp() > time.time() - 60:
                self.cutoff = target
                self.clock_entry.delete(0, "end")
                self.clock_entry.insert(0, target.strftime("%H:%M"))
            else:
                self.settings["cutoff_iso"] = None
        except Exception:
            pass

    def save_settings(self):
        self.settings.update({
            "always_on_top": bool(self.always_on_top.get()),
            "shutdown_after_match": bool(self.shutdown_after.get()),
            "warning_minutes": int(self.warning_minutes.get() or 2),
            "selected_mode": self.selected_key,
            "custom_duration": self.custom_entry.get().strip() if hasattr(self, "custom_entry") else "35m",
            "start_with_windows": bool(self.start_with_windows.get()),
            "show_overlay": bool(self.show_overlay.get()),
            "defer_shutdown_in_match": bool(self.defer_in_match.get()),
            "overlay_pos": self.overlay_pos,
            "cutoff_iso": self.cutoff.isoformat() if self.cutoff else None,
            "geometry": self.root.geometry(),
            "log_path": str(self.log_path),
        })
        save_json(SETTINGS_FILE, self.settings)

    def on_close(self):
        self.save_settings()
        self.stop_event.set()
        self.closed = True
        try:
            self.root.destroy()
        except Exception:
            pass

    def _drain_events(self):
        while True:
            try:
                event = self.events.get_nowait()
            except queue.Empty:
                return
            kind = event.get("type")
            if kind == "match_start":
                self.on_match_start(event)
            elif kind == "match_end":
                self.on_match_end(event)
            elif kind == "phase":
                self.on_phase(event)
            elif kind == "status":
                self.log(event.get("text", ""))

    def on_phase(self, event):
        phase = (event.get("phase") or "").lower()
        if phase == "pregame":
            self.status_label.configure(text="Agent select", fg=WARN)
            self.status_dot.configure(fg=WARN)
        elif phase == "ingame":
            self.status_label.configure(text="In match", fg=GREEN)
            self.status_dot.configure(fg=GREEN)
        elif phase in ("mainmenu", "transitiontomainmenu"):
            self.status_label.configure(text="Watching for Valorant", fg=MUTED)
            self.status_dot.configure(fg=MUTED)
        elif phase == "initialization":
            self.status_label.configure(text="Valorant starting", fg=MUTED)
            self.status_dot.configure(fg=MUTED)

    def on_match_start(self, event):
        key = mode_key_for_broad(event.get("broad", ""), self.selected_key)
        predicted, _, _, _ = self.prediction_for(key)
        if self.active and self.active.get("source") == "auto":
            if abs(self.active["start"] - event.get("start_ts", 0)) < 10:
                return
        if self.active and self.active.get("source") == "manual":
            self.log("Auto-detect took over the manual timer.")
        self.start_timer(
            key, predicted, "auto",
            start_ts=event.get("start_ts"),
            map_name=event.get("map"),
            broad=event.get("broad"),
            resumed=bool(event.get("resumed")),
        )

    def on_match_end(self, event):
        duration = float(event.get("duration") or 0)
        map_name = event.get("map") or ""
        broad = event.get("broad") or ""
        key = None
        expected = None
        if self.active:
            key = self.active["mode_key"]
            expected = self.active["expected"]
        else:
            key = mode_key_for_broad(broad, self.selected_key)
            expected, _, _, _ = self.prediction_for(key)
        label = MODE_BY_KEY.get(key, MODE_BY_KEY["custom"]).label
        recorded = False
        if self.active and self.active.get("source") == "auto" and key != "custom":
            self.history.add(key, duration, map_name, broad)
            recorded = True
        self.last_match = {
            "label": label,
            "duration": duration,
            "predicted": expected,
            "map": map_name,
            "broad": broad,
        }
        self.active = None
        self.match_status.configure(text="Not in a match")
        diff = duration - (expected or 0)
        sign = "+" if diff >= 0 else "-"
        detail = f"{label} on {map_name or 'map'}\nPlayed {fmt_duration(duration)} · predicted {fmt_duration(expected or 0)} ({sign}{fmt_duration(abs(diff))})"
        self.log(f"Match ended: {label} on {map_name or 'map'} — actual {fmt_duration(duration)}, predicted {fmt_duration(expected or 0)}.")
        if recorded:
            self.log("Saved to prediction history.")
        self.match_big.configure(text=fmt_duration(duration), fg=GREEN)
        self.match_pred.configure(text=f"Last match: {label} · {map_name or 'map'} · {fmt_duration(duration)}")
        self.progress["value"] = 1000
        self.update_prediction_label()
        beep()
        if self.pending_shutdown:
            reason = self.pending_shutdown
            self.pending_shutdown = None
            self.notify("Match ended", detail + "\n\nGet-off time was already reached. Shutdown warning next.")
            self.root.after(2000, lambda: self.begin_shutdown("Get-off time reached", reason))
        elif self.shutdown_after.get():
            self.root.after(1500, lambda: self.begin_shutdown(f"{label} ended", detail))
        else:
            self.notify("Match ended", detail)

    def notify(self, title, message):
        beep()
        if self.toast_win:
            try:
                self.toast_win.destroy()
            except Exception:
                pass
        win = tk.Toplevel(self.root)
        self.toast_win = win
        win.overrideredirect(True)
        win.attributes("-topmost", True)
        win.configure(bg=ACCENT)
        width, height = 390, 130
        x = max(10, win.winfo_screenwidth() - width - 20)
        y = max(10, win.winfo_screenheight() - height - 60)
        win.geometry(f"{width}x{height}+{x}+{y}")
        frame = tk.Frame(win, bg=CARD, padx=14, pady=10)
        frame.pack(fill="both", expand=True, padx=3, pady=3)
        tk.Label(frame, text=title, bg=CARD, fg=TEXT, font=("Segoe UI", 11, "bold")).pack(anchor="w")
        body = tk.Label(frame, text=message, bg=CARD, fg=MUTED, font=("Segoe UI", 9), justify="left", wraplength=350)
        body.pack(anchor="w", pady=(3, 6))
        self._button(frame, "OK", win.destroy, width=6).pack(side="right")
        win.after(15000, lambda: win.destroy() if win.winfo_exists() else None)
        win.lift()
        win.focus_force()

    def begin_shutdown(self, title, detail="", seconds=None):
        if self.warning:
            return
        self.pending_shutdown = None
        if seconds is None:
            seconds = max(1, int(self.warning_minutes.get() or 2)) * 60
        win = tk.Toplevel(self.root)
        self.warning = {"win": win, "deadline": time.time() + seconds, "seconds": seconds}
        win.title("Shutdown warning")
        win.configure(bg=BG)
        win.attributes("-topmost", True)
        win.resizable(False, False)
        win.protocol("WM_DELETE_WINDOW", self.cancel_shutdown)
        width, height = 480, 320
        x = max(10, (win.winfo_screenwidth() - width) // 2)
        y = max(10, (win.winfo_screenheight() - height) // 2)
        win.geometry(f"{width}x{height}+{x}+{y}")
        frame = tk.Frame(win, bg=CARD, padx=22, pady=18, highlightthickness=2, highlightbackground=ACCENT)
        frame.pack(fill="both", expand=True, padx=6, pady=6)
        tk.Label(frame, text=title.upper(), bg=CARD, fg=ACCENT, font=("Segoe UI", 12, "bold")).pack(anchor="w")
        tk.Label(frame, text=detail, bg=CARD, fg=TEXT, font=("Segoe UI", 10), justify="left", wraplength=400).pack(anchor="w", pady=(6, 8))
        tk.Label(frame, text="PC SHUTS DOWN IN", bg=CARD, fg=MUTED, font=("Segoe UI", 9, "bold")).pack(anchor="w")
        label = tk.Label(frame, text=fmt_match(seconds), bg=CARD, fg=ACCENT, font=("Segoe UI", 40, "bold"))
        label.pack(anchor="w")
        self.warning["label"] = label
        tk.Label(frame, text="Cancel below if you are still playing. Nothing happens without this countdown.", bg=CARD, fg=MUTED, font=("Segoe UI", 8), wraplength=400, justify="left").pack(anchor="w", pady=(0, 8))
        buttons = tk.Frame(frame, bg=CARD)
        buttons.pack(fill="x", side="bottom")
        cancel = self._button(buttons, "Cancel shutdown", self.cancel_shutdown, accent=True)
        cancel.configure(font=("Segoe UI", 11, "bold"), pady=8)
        cancel.pack(side="left", fill="x", expand=True)
        self._button(buttons, "Shut down now", self.execute_shutdown, width=14).pack(side="left", padx=(8, 0), fill="y")
        self.log(f"Shutdown warning: {title}. {seconds // 60} minute(s) to cancel.")
        beep()
        win.lift()
        win.focus_force()
        cancel.focus_set()

    def render_warning(self, now):
        if not self.warning:
            return
        remaining = self.warning["deadline"] - now
        if remaining <= 0:
            self.execute_shutdown()
            return
        try:
            self.warning["label"].configure(text=fmt_match(remaining))
        except Exception:
            pass

    def cancel_shutdown(self):
        if self.warning:
            try:
                self.warning["win"].destroy()
            except Exception:
                pass
            self.warning = None
            self.log("Shutdown cancelled. You can keep playing.")
            beep()

    def execute_shutdown(self):
        if self.warning:
            try:
                self.warning["win"].destroy()
            except Exception:
                pass
            self.warning = None
        self.log("Shutting down the PC now.")
        self.save_settings()
        try:
            if os.name == "nt":
                subprocess.Popen(["shutdown", "/s", "/t", "0"], creationflags=CREATE_NO_WINDOW)
            else:
                subprocess.Popen(["shutdown", "-h", "now"])
        except Exception as e:
            self.log(f"Shutdown failed: {e}")
            return
        try:
            self.root.after(900, self.root.destroy)
        except Exception:
            pass

    def manual_shutdown(self):
        self.begin_shutdown("Manual shutdown", "You pressed shut down in 2:00.")

    def tick(self):
        if self.closed:
            return
        self._drain_events()
        now = time.time()
        self.render_getoff(now)
        self.render_match(now)
        if self.cutoff and not self.cutoff_fired and now >= self.cutoff.timestamp():
            self.cutoff_fired = True
            self.log(f"Get-off time reached ({self.cutoff:%H:%M}).")
            if self.defer_in_match.get() and self.active:
                self.pending_shutdown = f"Your cutoff was {self.cutoff:%H:%M}. The match has finished, so time is up."
                self.log("Match still in progress. Shutdown deferred until the match/OT ends.")
                beep()
            else:
                self.begin_shutdown("Get-off time reached", f"Your cutoff was {self.cutoff:%H:%M}. Save and log off.")
        self.render_overlay(now)
        self.render_warning(now)
        try:
            self.root.after(250, self.tick)
        except Exception:
            pass


def run_selftest(path):
    parser = MatchParser()
    open_match = None
    count = 0
    if not Path(path).exists():
        print(f"Log not found: {path}")
        return 1
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            for event in parser.feed(line):
                if event["type"] == "match_start":
                    open_match = event
                elif event["type"] == "match_end":
                    count += 1
                    start = datetime.fromtimestamp(event["start_ts"]).strftime("%Y-%m-%d %H:%M:%S")
                    end = datetime.fromtimestamp(event["end_ts"]).strftime("%H:%M:%S")
                    print(f"{count:2d}. {start} -> {end}  {fmt_duration(event['duration']):>9}  {event.get('map') or '?':<12} {short_mode(event.get('broad'))}")
                    open_match = None
    if open_match:
        start = datetime.fromtimestamp(open_match["start_ts"]).strftime("%Y-%m-%d %H:%M:%S")
        print(f"OPEN match started {start} on {open_match.get('map')} ({short_mode(open_match.get('broad'))})")
    print(f"Total closed matches: {count}")
    return 0


_MUTEX_HANDLE = None


def single_instance_guard():
    global _MUTEX_HANDLE
    if os.name != "nt":
        return True
    try:
        import ctypes
        kernel32 = ctypes.windll.kernel32
        _MUTEX_HANDLE = kernel32.CreateMutexW(None, False, "Local\\ValorantMatchTimer")
        return kernel32.GetLastError() != 183
    except Exception:
        return True


def main():
    parser = argparse.ArgumentParser(description=APP_NAME)
    parser.add_argument("--selftest", action="store_true", help="Parse the Valorant log and print detected matches")
    parser.add_argument("--smoke", action="store_true", help="Launch the GUI briefly and exit")
    parser.add_argument("--log", default=str(DEFAULT_LOG), help="Path to ShooterGame.log")
    args = parser.parse_args()

    if args.selftest:
        return run_selftest(args.log)
    if not single_instance_guard():
        return 0

    root = tk.Tk()
    app = App(root)
    if args.log and args.log != str(DEFAULT_LOG):
        app.log_path = Path(args.log)
    if args.smoke:
        root.after(900, app.on_close)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
