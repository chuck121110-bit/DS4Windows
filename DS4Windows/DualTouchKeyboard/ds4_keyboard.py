#!/usr/bin/env python3
"""DS4-adapted DualTouch keyboard (real Python keyboard for DS4Windows).

This is NOT the unmodified DualTouch app and it does NOT claim to be.
DualTouch's full triton stack reads a Steam Controller over HID directly
(steamcontroller/ + triton/inputsrc.py) and cannot be fed a DualSense
without adaptation, so this file is a new, small keyboard written for DS4
that reuses DualTouch's real assets only:

  REUSED (verbatim, from mateuszklysz/DualTouch, LGPL-3.0):
    data/cfg/keyboard-layout.yaml  (Win key removed, Move renamed Hide)
    data/skins/Gruvbox.css         (colours parsed, see below)
    data/fonts/*.ttf               (bundled assets; see font note)

  STRIPPED (deliberately NOT included, NOT imported, NOT run):
    steamcontroller/ HID stack, triton/ SDL renderer, sdl3w/ + SDL3 DLLs,
    tray/ + launcher + menu + steam layer, autostart.py, steam_shortcut.py
    (shortcuts.vdf editing), cursor_ctrl.py / cursor_helper.py (scheduled
    task), win_focus.py Steam handling, key_sound.py, watchers.py,
    applog.py / appsettings.py, installer/ (Inno), tests/.

  NEW for DS4 (this file only):
    Input comes from DS4Windows over stdin as one JSON object per line
    (see PROTOCOL below), never from a Steam Controller HID device.
    Rendering is tkinter (Python standard library), not SDL, because the
    SDL3 DLL binaries are deliberately not shipped. Typing is pynput.
    No network, no registry writes, no autostart, no tray icon, no file
    writes, no Steam interaction of any kind.

PROTOCOL (stdin, JSON per line, sent by DS4Windows):
  {"type": "open"}                          (process start implies open)
  {"type": "touch", "x": 0.0-1.0, "y": 0.0-1.0}   absolute touchpad position
  {"type": "touchmove", "dx": float, "dy": float} relative touchpad delta
  {"type": "nav", "dx": -1|0|1, "dy": -1|0|1}     d-pad / left-stick step
  {"type": "tap"}                           type the highlighted key
  {"type": "button", "name": "cross|circle|square|r2|l3|triangle"}
  {"type": "shift", "held": true|false}    L2 held
  {"type": "close"}                         close the keyboard and exit

DS4 button mapping (DualSense adaptation only):
  Single DualSense touchpad controls the keyboard: touch position moves
  the highlight, tap types the highlighted key. Mouse movement is locked
  off by DS4Windows itself while the keyboard is open and restored on
  close; this process never touches the mouse.
  Cross = type highlighted key, Circle = close, Square = backspace,
  R2 = enter, L3 = caps toggle, L2 held = shift, Triangle = space.
  Square/R2/Triangle first move the highlight to Backspace/Enter/Space
  (so that key is visibly highlighted) and only then send the key.
  No badge text overlays are drawn on any key (R2/L2/L3/Square/Circle
  are not printed on the keys).

Behaviour fixes carried over from the previous port:
  Shift latch clears after typing a character (and after Space, Tab,
  Enter, Backspace, or a function key). Caps is sticky until L3 or the
  Caps key toggles it off. Key text is white. No badge text overlays.
  The window is shorter than the old 480 px window (about 350 px).
  Hide (not Move) closes the keyboard. The Windows key is removed.

Exit codes: 0 = closed normally, 2 = required import/layout missing.
On a missing import or layout the process prints an error to stderr and
exits WITHOUT opening a window, so DS4Windows shows an error and opens
no keyboard at all (no fallback to the old keyboard).
"""

import json
import os
import queue
import re
import sys
import threading
import time

try:
    import yaml
except ImportError as _e:  # DS4Windows auto-installs this; see runner.
    print("DS4_KEYBOARD_ERROR: missing PyYAML (%s)" % _e, file=sys.stderr)
    sys.exit(2)

try:
    from pynput import keyboard as _pynput_kb
    from pynput.keyboard import Controller as _Controller
    from pynput.keyboard import Key as _Key
    from pynput.keyboard import KeyCode as _KeyCode
except ImportError as _e:  # DS4Windows auto-installs this; see runner.
    print("DS4_KEYBOARD_ERROR: missing pynput (%s)" % _e, file=sys.stderr)
    sys.exit(2)

HERE = os.path.dirname(os.path.abspath(__file__))
LAYOUT_PATH = os.path.join(HERE, "data", "cfg", "keyboard-layout.yaml")
SKIN_PATH = os.path.join(HERE, "data", "skins", "Gruvbox.css")

# Gruvbox defaults (also parsed from Gruvbox.css when present).
BG = "#000000"
KEY_BG = "#141414"
KEY_FOCUSED_BG = "#2a2a2a"
KEY_PRESSED_BG = "#3f3f3f"
KEY_MOD_BG = "#1a1a1a"
ACCENT = "#1a9fff"
# Spec: key text is white (not Gruvbox cream #ebdbb2).
TEXT = "#ffffff"

WINDOW_WIDTH = 1120
WINDOW_HEIGHT = 350
ROW_HEIGHT = 52
# Touchpad delta units per highlight cell (same step as the old port).
# 2026-11-03 feedback: a little more sensitive again, not a huge jump.
TOUCH_UNITS_PER_CELL = 140.0  # was 160.0, before that 200.0

# Linux keysym-style keycode (from the YAML) -> pynput special key.
_SPECIAL = {
    "KEY_ESC": _Key.esc,
    "KEY_F1": _Key.f1, "KEY_F2": _Key.f2, "KEY_F3": _Key.f3,
    "KEY_F4": _Key.f4, "KEY_F5": _Key.f5, "KEY_F6": _Key.f6,
    "KEY_F7": _Key.f7, "KEY_F8": _Key.f8, "KEY_F9": _Key.f9,
    "KEY_F10": _Key.f10, "KEY_F11": _Key.f11, "KEY_F12": _Key.f12,
    "KEY_HOME": _Key.home, "KEY_END": _Key.end, "KEY_INSERT": _Key.insert,
    "KEY_TAB": _Key.tab, "KEY_BACKSPACE": _Key.backspace,
    "KEY_ENTER": _Key.enter, "KEY_SPACE": _Key.space,
    "KEY_LEFT": _Key.left, "KEY_UP": _Key.up,
    "KEY_DOWN": _Key.down, "KEY_RIGHT": _Key.right,
    "KEY_LEFTCTRL": _Key.ctrl_l, "KEY_LEFTALT": _Key.alt_l,
    "KEY_LEFTSHIFT": _Key.shift_l, "KEY_CAPSLOCK": _Key.caps_lock,
    "KEY_LEFTWIN": _Key.cmd_l,
}

_MODIFIER_BEHAVIORS = {"ctrl", "alt", "shift"}


def _parse_skin(path):
    """Parse Gruvbox.css variables. Missing file -> defaults. Never fails."""
    vals = {}
    try:
        with open(path, "r", encoding="utf-8") as fh:
            text = fh.read()
        for name, value in re.findall(r"--([a-z-]+):\s*(#[0-9a-fA-F]{6})", text):
            vals[name] = value
    except OSError:
        pass
    return vals


def _load_layout(path):
    """Load the adapted DualTouch YAML layout. Raises on missing file."""
    with open(path, "r", encoding="utf-8") as fh:
        data = yaml.safe_load(fh)
    rows = []
    for raw_row in data.get("keys", []):
        row = []
        for entry in raw_row:
            label = str(entry.get("label", ""))
            behavior = str(entry.get("behavior", "")).lower()
            keycode = str(entry.get("keycode", ""))
            # Safety net: the adapted YAML already drops Win, but never
            # let a Windows key through even if the file is edited.
            if keycode == "KEY_LEFTWIN" or label == "Win":
                continue
            # Safety net: Move must never come back; it is Hide.
            if behavior == "move":
                label, behavior = "Hide", "hide"
            row.append({
                "label": label,
                "shifted": str(entry.get("shifted", "")),
                "keycode": keycode,
                "behavior": behavior,
                "width": float(entry.get("width_weight", 1.0)),
                "modifier": bool(entry.get("modifier", False)),
            })
        if row:
            rows.append(row)
    if not rows:
        raise ValueError("layout has no rows")
    return rows


class KeyboardModel:
    """Selection + latch state. No GUI code here, so it stays testable."""

    def __init__(self, rows):
        self.rows = rows
        self.row = 0
        self.col = 0
        self.caps = False
        self.shift_latch = False
        self.shift_held = False
        self.ctrl_latch = False
        self.alt_latch = False
        self.select_mode = False
        self.touch_acc_x = 0.0
        self.touch_acc_y = 0.0
        # Triggered-key highlight: Square/R2/Triangle must show the SAME
        # highlight as an active L2 Shift key, not just move the cursor
        # (focused shade). The active-modifier shade in refresh() is
        # key_pressed_bg (#3f3f3f); a triggered key uses that shade
        # for a short visible window, then falls back to normal/selected.
        self.triggered_row = -1
        self.triggered_col = -1
        self.triggered_until = 0.0

    def mark_triggered(self):
        """Mark the currently selected key as triggered (pressed-style
        highlight, like L2 Shift active), visible for 0.6 s."""
        self.triggered_row = self.row
        self.triggered_col = self.col
        self.triggered_until = time.monotonic() + 0.6

    def is_triggered(self, ri, ci):
        return (ri == self.triggered_row and ci == self.triggered_col
                and time.monotonic() < self.triggered_until)

    def current(self):
        r = max(0, min(len(self.rows) - 1, self.row))
        c = max(0, min(len(self.rows[r]) - 1, self.col))
        return self.rows[r][c]

    def find_key(self, behavior=None, keycode=None):
        """Return (row, col) for a key, or None. Used so Square/R2/
        Triangle highlight the key they actually send."""
        for ri, row in enumerate(self.rows):
            for ci, key in enumerate(row):
                if behavior is not None and key["behavior"] == behavior:
                    return ri, ci
                if keycode is not None and key["keycode"] == keycode:
                    return ri, ci
        return None

    def move_to_key(self, behavior=None, keycode=None):
        pos = self.find_key(behavior=behavior, keycode=keycode)
        if pos is not None:
            self.row, self.col = pos
            return True
        return False

    def move(self, dx, dy):
        nr = max(0, min(len(self.rows) - 1, self.row + dy))
        self.row = nr
        self.col = max(0, min(len(self.rows[nr]) - 1, self.col + dx))

    def move_by_touch(self, dx, dy):
        self.touch_acc_x += dx
        self.touch_acc_y += dy
        steps_x = int(self.touch_acc_x / TOUCH_UNITS_PER_CELL)
        steps_y = int(self.touch_acc_y / TOUCH_UNITS_PER_CELL)
        if steps_x:
            self.touch_acc_x -= steps_x * TOUCH_UNITS_PER_CELL
            self.move(steps_x, 0)
        if steps_y:
            self.touch_acc_y -= steps_y * TOUCH_UNITS_PER_CELL
            self.move(0, steps_y)

    def move_to_fraction(self, fx, fy):
        self.row = max(0, min(len(self.rows) - 1, int(fy * len(self.rows))))
        row = self.rows[self.row]
        total = sum(k["width"] for k in row)
        acc = 0.0
        self.col = len(row) - 1
        for i, key in enumerate(row):
            acc += key["width"] / total
            if fx <= acc:
                self.col = i
                break

    @property
    def shift_active(self):
        return self.shift_held or self.shift_latch

    def clear_latches(self):
        """Shift latch clears after typing a character. Caps does not."""
        self.shift_latch = False
        self.ctrl_latch = False
        self.alt_latch = False


class Typer:
    """Sends keys with pynput. One place, so latch rules stay visible."""

    def __init__(self):
        self.ctl = _Controller()

    def _with_modifiers(self, mods, fn):
        pressed = []
        try:
            for mod in mods:
                self.ctl.press(mod)
                pressed.append(mod)
            fn()
        finally:
            for mod in reversed(pressed):
                try:
                    self.ctl.release(mod)
                except Exception:
                    pass

    def send_char(self, ch, ctrl=False, alt=False, shift=False):
        mods = []
        if ctrl:
            mods.append(_Key.ctrl_l)
        if alt:
            mods.append(_Key.alt_l)
        if shift:
            mods.append(_Key.shift_l)
        if mods:
            # With modifiers, press the character key directly; pynput
            # types the shifted form when Shift is held.
            key = _KeyCode.from_char(ch)
            self._with_modifiers(mods, lambda: (self.ctl.press(key),
                                                self.ctl.release(key)))
        else:
            self.ctl.type(ch)

    def send_win_h(self):
        """Windows voice typing: Win+H. Keyboard stays open; only
        Windows voice typing itself can close anything."""
        try:
            self.ctl.press(_Key.cmd_l)
            self.ctl.press(_KeyCode.from_char("h"))
            self.ctl.release(_KeyCode.from_char("h"))
            self.ctl.release(_Key.cmd_l)
        except Exception:
            pass

    def send_special(self, key, ctrl=False, alt=False, shift=False):
        mods = []
        if ctrl:
            mods.append(_Key.ctrl_l)
        if alt:
            mods.append(_Key.alt_l)
        if shift:
            mods.append(_Key.shift_l)
        self._with_modifiers(
            mods, lambda: (self.ctl.press(key), self.ctl.release(key)))


def _display_char(model, key):
    """Character to type for a Char key, honouring Shift latch/held/Caps."""
    label = key["label"]
    shifted = key["shifted"]
    if len(label) == 1 and label.isalpha():
        # Caps XOR Shift flips case; both together cancel out.
        upper = model.caps != model.shift_active
        return label.upper() if upper else label.lower()
    if model.shift_active and shifted:
        return shifted
    return label


def activate(model, typer, key):
    """Type/act on the highlighted key. Returns True if the window closes."""
    behavior = key["behavior"]
    keycode = key["keycode"]
    if behavior == "hide":
        return True
    if behavior == "shift":
        model.shift_latch = True
        return False
    if behavior == "ctrl":
        model.ctrl_latch = not model.ctrl_latch
        return False
    if behavior == "alt":
        model.alt_latch = not model.alt_latch
        return False
    if behavior == "select":
        model.select_mode = not model.select_mode
        return False
    if behavior == "dictation":
        typer.send_win_h()
        # Keyboard stays open; do not clear selection or close.
        return False
    if behavior == "paste":
        if model.shift_active:
            typer.send_special(_KeyCode.from_char("c"), ctrl=True)
        else:
            typer.send_special(_KeyCode.from_char("v"), ctrl=True)
        model.clear_latches()
        model.select_mode = False
        return False
    if behavior == "backspace":
        typer.send_special(_Key.backspace, ctrl=model.ctrl_latch,
                           alt=model.alt_latch)
        model.clear_latches()
        model.select_mode = False
        return False
    if keycode == "KEY_CAPSLOCK":
        model.caps = not model.caps
        return False
    special = _SPECIAL.get(keycode)
    if special is not None and keycode not in ("KEY_SPACE",):
        shift = model.shift_active
        if keycode in ("KEY_LEFT", "KEY_UP", "KEY_DOWN", "KEY_RIGHT"):
            # Select mode: Shift+Arrow extends a selection.
            if model.select_mode:
                shift = True
            typer.send_special(special, shift=shift)
            return False
        typer.send_special(special, ctrl=model.ctrl_latch,
                           alt=model.alt_latch, shift=shift)
        model.clear_latches()
        model.select_mode = False
        return False
    # Plain character key (includes Space: KEY_SPACE has label "Space"
    # in the YAML, so type a real space, not the word).
    if keycode == "KEY_SPACE":
        typer.send_special(_Key.space, ctrl=model.ctrl_latch,
                           alt=model.alt_latch)
        model.clear_latches()
        model.select_mode = False
        return False
    ch = _display_char(model, key)
    if ch:
        typer.send_char(ch, ctrl=model.ctrl_latch, alt=model.alt_latch,
                        shift=False)
        # Shift latch clears after typing a character; Caps stays on.
        model.clear_latches()
        model.select_mode = False
    return False


def main():
    try:
        rows = _load_layout(LAYOUT_PATH)
    except Exception as exc:
        print("DS4_KEYBOARD_ERROR: cannot load layout: %s" % exc,
              file=sys.stderr)
        return 2
    skin = _parse_skin(SKIN_PATH)
    key_bg = skin.get("key-background-color", KEY_BG)
    key_focused_bg = skin.get("key-focused-background-color", KEY_FOCUSED_BG)
    key_pressed_bg = skin.get("key-toggleon-background-color", KEY_PRESSED_BG)
    key_mod_bg = skin.get("key-meta-background-color", KEY_MOD_BG)
    bg = skin.get("background-color", BG)

    # tkinter is standard library; import here so the import check above
    # (yaml/pynput) runs first and failures stay in the same error shape.
    import tkinter as tk

    model = KeyboardModel(rows)
    typer = Typer()
    events = queue.Queue()
    closing = threading.Event()

    def stdin_reader():
        try:
            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                try:
                    events.put(json.loads(line))
                except ValueError:
                    continue
        except Exception:
            pass
        finally:
            events.put({"type": "close"})

    threading.Thread(target=stdin_reader, daemon=True).start()

    root = tk.Tk()
    root.title("DS4 DualTouch Keyboard")
    root.overrideredirect(True)
    root.attributes("-topmost", True)
    sw = root.winfo_screenwidth()
    sh = root.winfo_screenheight()
    width = min(WINDOW_WIDTH, max(400, sw - 40))
    height = WINDOW_HEIGHT
    x = (sw - width) // 2
    y = max(0, sh - height - 20)
    root.geometry("%dx%d+%d+%d" % (width, height, x, y))
    root.configure(bg=bg)
    # Do not steal focus from the game/app being typed into, like the
    # old WPF window's WS_EX_NOACTIVATE. Best effort, Windows only.
    try:
        import ctypes
        hwnd = ctypes.windll.user32.GetParent(root.winfo_id())
        if not hwnd:
            hwnd = root.winfo_id()
        ex = ctypes.windll.user32.GetWindowLongW(hwnd, -20)
        ctypes.windll.user32.SetWindowLongW(
            hwnd, -20, ex | 0x08000000 | 0x00000080)
    except Exception:
        pass

    def draw_mic(canvas):
        """Line-drawn microphone icon, NOT an emoji: a vertical oval
        in the middle, a U shape around it, and a stand line below.
        White strokes on the key background, so it stays readable."""
        try:
            canvas.delete("all")
            w = max(20, canvas.winfo_width())
            h = max(20, canvas.winfo_height())
            cx = w / 2.0
            # Vertical oval in the middle.
            ow, oh = w * 0.26, h * 0.42
            oy0 = h * 0.12
            canvas.create_oval(cx - ow / 2, oy0, cx + ow / 2, oy0 + oh,
                               outline=TEXT, width=2, fill="")
            # U shape around the oval (bottom half arc).
            uw, uh = w * 0.52, h * 0.62
            uy0 = h * 0.08
            canvas.create_arc(cx - uw / 2, uy0, cx + uw / 2, uy0 + uh,
                              start=180, extent=180,
                              outline=TEXT, width=2, style="arc")
            # Stand: vertical line from the U down, plus a base line.
            stand_top = uy0 + uh * 0.78
            stand_bot = h * 0.88
            canvas.create_line(cx, stand_top, cx, stand_bot,
                               fill=TEXT, width=2)
            canvas.create_line(cx - w * 0.22, stand_bot,
                               cx + w * 0.22, stand_bot,
                               fill=TEXT, width=2)
        except Exception:
            pass

    buttons = []
    for ri, row in enumerate(rows):
        total = sum(k["width"] for k in row)
        for ci, key in enumerate(row):
            label = key["label"]
            if key["behavior"] == "backspace":
                label = "Backspace"
            if key["behavior"] == "dictation":
                # Line-icon canvas, not an emoji Button.
                btn = tk.Canvas(
                    root, bg=key_mod_bg if key["modifier"] else key_bg,
                    highlightthickness=0, bd=0)
                btn.bind("<Button-1>", lambda e, r=ri, c=ci: events.put(
                    {"type": "mouseclick", "row": r, "col": c}))
            else:
                btn = tk.Button(
                    root, text=label, fg=TEXT,
                    bg=key_mod_bg if key["modifier"] else key_bg,
                    activebackground=key_pressed_bg, activeforeground=TEXT,
                    font=("Segoe UI Semibold", 13),
                    bd=0, highlightthickness=0, relief="flat",
                    command=lambda r=ri, c=ci: events.put(
                        {"type": "mouseclick", "row": r, "col": c}))
            btn.place(relx=sum(k["width"] for k in row[:ci]) / total,
                      rely=ri / len(rows),
                      relwidth=key["width"] / total,
                      relheight=1.0 / len(rows))
            buttons.append((ri, ci, btn))

    def refresh():
        for ri, ci, btn in buttons:
            key = rows[ri][ci]
            is_sel = (ri == model.row and ci == model.col)
            triggered = model.is_triggered(ri, ci)
            if key["behavior"] == "dictation":
                # Line-icon canvas: background follows selection /
                # triggered state, icon is redrawn in white strokes.
                if triggered:
                    btn.configure(bg=key_pressed_bg,
                                  highlightthickness=1,
                                  highlightbackground=ACCENT,
                                  highlightcolor=ACCENT)
                elif is_sel:
                    btn.configure(bg=key_focused_bg,
                                  highlightthickness=1,
                                  highlightbackground=ACCENT,
                                  highlightcolor=ACCENT)
                else:
                    btn.configure(
                        bg=key_mod_bg if key["modifier"] else key_bg,
                        highlightthickness=0)
                draw_mic(btn)
                continue
            if key["behavior"] == "backspace":
                text = "Backspace"
            elif key["keycode"] == "KEY_CAPSLOCK":
                text = "Caps"
            elif key["behavior"] == "hide":
                text = "Hide"
            elif key["behavior"] == "paste":
                text = "Copy" if model.shift_active else "Paste"
            elif key["behavior"] == "select":
                text = "Select"
            elif key["behavior"] == "shift":
                text = "Shift"
            elif key["behavior"] in ("ctrl", "alt"):
                text = key["label"]
            elif key["keycode"] == "KEY_SPACE":
                text = "Space"
            elif key["behavior"] == "backspace":
                text = "Backspace"
            else:
                text = _display_char(model, key) if len(
                    key["label"]) == 1 else key["label"]
                if key["keycode"] in _SPECIAL and key["behavior"] == "":
                    # Function / nav keys keep their YAML label.
                    text = key["label"]
            btn.configure(text=text)
            if triggered:
                # SAME shade as an active L2 Shift key
                # (key_pressed_bg), not just the focused cursor shade.
                # This wins over the selected/focused shade, so a
                # Square/R2/Triangle press is visibly different from
                # merely moving the cursor onto Backspace/Enter/Space.
                btn.configure(bg=key_pressed_bg,
                              highlightthickness=1,
                              highlightbackground=ACCENT,
                              highlightcolor=ACCENT)
            elif is_sel:
                btn.configure(bg=key_focused_bg,
                              highlightthickness=1,
                              highlightbackground=ACCENT,
                              highlightcolor=ACCENT)
            else:
                btn.configure(
                    bg=key_mod_bg if key["modifier"] else key_bg,
                    highlightthickness=0)
            # Active modifier latch: pressed shade, still readable.
            if ((key["behavior"] == "shift" and model.shift_active)
                    or (key["keycode"] == "KEY_CAPSLOCK" and model.caps)
                    or (key["behavior"] == "ctrl" and model.ctrl_latch)
                    or (key["behavior"] == "alt" and model.alt_latch)
                    or (key["behavior"] == "select" and model.select_mode)):
                if not is_sel:
                    btn.configure(bg=key_pressed_bg)
        root.update_idletasks()

    def close_window():
        closing.set()
        try:
            root.destroy()
        except Exception:
            pass

    def drain():
        if closing.is_set():
            return
        try:
            while True:
                ev = events.get_nowait()
                kind = ev.get("type")
                if kind == "close":
                    close_window()
                    return
                elif kind == "touch":
                    try:
                        model.move_to_fraction(float(ev.get("x", 0.5)),
                                               float(ev.get("y", 0.5)))
                    except (TypeError, ValueError):
                        pass
                elif kind == "touchmove":
                    try:
                        model.move_by_touch(float(ev.get("dx", 0)),
                                            float(ev.get("dy", 0)))
                    except (TypeError, ValueError):
                        pass
                elif kind == "nav":
                    # D-pad safety net (C# is the primary quantizer):
                    # never move diagonally in keyboard mode. If a
                    # diagonal nav ever arrives, keep the horizontal
                    # step only, so no diagonal key jump occurs.
                    _ndx = int(ev.get("dx", 0))
                    _ndy = int(ev.get("dy", 0))
                    if _ndx != 0 and _ndy != 0:
                        _ndy = 0
                    model.move(_ndx, _ndy)
                elif kind == "tap":
                    if activate(model, typer, model.current()):
                        close_window()
                        return
                elif kind == "mouseclick":
                    model.row = int(ev.get("row", model.row))
                    model.col = int(ev.get("col", model.col))
                    if activate(model, typer, model.current()):
                        close_window()
                        return
                elif kind == "button":
                    name = ev.get("name", "")
                    if name == "circle":
                        close_window()
                        return
                    elif name == "square":
                        # Highlight Backspace with the SAME pressed
                        # shade as active L2 Shift, then send it.
                        model.move_to_key(behavior="backspace")
                        model.mark_triggered()
                        typer.send_special(_Key.backspace)
                        model.clear_latches()
                        model.select_mode = False
                    elif name == "r2":
                        # Highlight Enter with the SAME pressed shade
                        # as active L2 Shift, then send it.
                        model.move_to_key(keycode="KEY_ENTER")
                        model.mark_triggered()
                        typer.send_special(_Key.enter)
                        model.clear_latches()
                        model.select_mode = False
                    elif name == "triangle":
                        # Triangle (Y on the layout glyph) is Space:
                        # highlight Space with the SAME pressed shade
                        # as active L2 Shift, then send a space.
                        model.move_to_key(keycode="KEY_SPACE")
                        model.mark_triggered()
                        typer.send_special(_Key.space)
                        model.clear_latches()
                        model.select_mode = False
                    elif name == "l3":
                        model.caps = not model.caps
                    elif name == "cross":
                        if activate(model, typer, model.current()):
                            close_window()
                            return
                elif kind == "shift":
                    model.shift_held = bool(ev.get("held", False))
                elif kind == "open":
                    pass
        except queue.Empty:
            pass
        refresh()
        root.after(20, drain)

    print("DS4 keyboard ready", file=sys.stderr)
    refresh()
    root.after(20, drain)
    try:
        root.mainloop()
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
