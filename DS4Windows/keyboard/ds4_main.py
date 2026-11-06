#!/usr/bin/env python3
"""DS4 adapter entry point for the exact DualTouch keyboard.

This file is the ONLY DS4-specific Python file in DS4Windows/keyboard/.
Everything under triton/, data/, sdl3w/ and steamcontroller/ is the exact
DualTouch project, verbatim, except the documented DS4 adaptations in
triton/controller.py (sc_triggers_off, see below; L1/R1 text arrows, see
below) and triton/screen.py (no key glyph icons, see below) and the
steam_shortcut.py stub (read-only Steam path lookup only, no shortcuts.vdf
editing, no tray, no autostart, no network, no persistence).

DS4 adaptations, all in this file, none in triton/ except the trigger flag,
the L1/R1 arrows, the glyph suppression, and (2026-11-04) the Win / Start
live-reposition disable in triton.py, see item 10 below:
  1. Input source: triton's SteamHidSource (Steam Controller HID) is replaced
     with DS4JsonSource, which reads DualSense events from DS4 over stdin as
     JSON lines. No Steam Controller HID is opened, no SDL gamepad is used.
  2. Triggers: exact triton sets sc_triggers_off = True because Steam Input
     would leak trigger presses into the game. DS4 neutralizes mapped output
     while the keyboard is open, so the leak does not apply here, and Chuck's
     mapping requires L2 = Shift and R2 = Enter. That one flag is flipped in
     triton/controller.py with a comment, and is the only triton/ edit.
  3. Joystick center-reset (Chuck's standing rule): a stick deflection steps
     exactly once, then the stick is reported as centered until it physically
     returns to center. Holding it, or rotating it in a circle without
     centering, sends nothing more. No diagonal, dominant axis only.
  4. D-pad repeat (DS4 adaptation, exact triton D-pad is edge-only): a held
     D-pad direction pulses off/on after 350 ms, then every 60 ms, so holding
     it steps like the previous DS4 port did.
  5. Touchpad, cursor only, Y flipped (2026-11-04, Chuck: up/down was
     inverted, and the touchpad must do a cursor like Steam, not move the
     selection): the single DualSense touchpad drives triton's left-pad
     POINTER, rendered as the touchcircle / touchglow disc, which moves
     with the finger and, on tap, types the key under that pointer.
     It does NOT move the DPAD grid cursor, and a tap does NOT snap to
     the DPAD cursor. D-pad / stick still move the DPAD cursor separately.
     Y flip, proved from code: DS4 DeltaY = HwY - PrevHwY, HwY grows
     downward (Mouse.cs swipeDown = HwY increase), and the normal Windows
     mouse path moves down for DeltaY positive, so finger down = dy > 0.
     Triton adjust_raw_y() maps screen_y = height * (0.5 - 1.2*raw_y/65536),
     so an unflipped dy > 0 raised raw_y and moved the pointer UP.
     Hence poll() now accumulates _pad_y with -dy. X is unchanged:
     raw_x up maps to screen_x up, finger right = pointer right.
  6. Cursor helper: triton calls cursor_ctrl.set_osk_cursor_visible(), which
     in the original relies on a scheduled-task daemon. DS4 already locks the
     Windows mouse while the keyboard is open, so that call is replaced with
     a no-op here. No scheduled task is created, no marker file is written.
  7. Settings: DS4 sends one settings JSON line on stdin at process start,
     from the per-profile keyboard sidecar. Applied here before triton.main()
     starts, using triton's own setters. No settings.json is written by this
     process, no tray menu, no autostart.

Mapping (Chuck's confirmed mapping, all handled by exact triton once the
frame is built, plus the DS4 L1/R1 adaptation below): Cross = A = type
highlighted key, Circle = B = close, Square = X = Backspace (hold
repeats), Triangle = Y = Space, L3 = Caps, L2 = Shift (hold), R2 = Enter,
L1 = Left arrow (move left through text), R1 = Right arrow (move right
through text), D-pad = one step per press, left stick = center-reset
steps, touchpad = pad cursor, touchpad tap = pad click.
Square / Triangle / Enter / L3 only highlight their key while held, they
do NOT move the cursor, which is exact triton behavior and matches Chuck's
22:14 correction. L1 / R1 likewise do NOT move the on-screen selection
cursor; they send OS Left / Right arrows to the text field, so holding
one repeats through text via normal OS key repeat.
  8. L1/R1 text arrows: exact triton's default click button is LB/RB, so
     passing LB/RB through untouched would ALSO type the highlighted key
     under the pad pointer (pad-click synthesis). DS4 disables that
     synthesis with state.set_sc_click_button((0, 0)) in main() below, so
     LB/RB do nothing in the pad path, and triton/controller.py handles
     LB/RB manually as held Left / Right arrows (same Keyboard instance
     as Shift / Enter, so Shift+Left / Shift+Right selection still works).
     Touchpad tap still types: it sends LPAD directly, unaffected.
  9. No key glyph icons: triton/screen.py suppresses every key glyph at
     the render choke point, and the layout YAMLs carry no glyph fields
     at all, so keys show text only (Space, Caps, Backspace, Shift,
     Enter, Move, all keys). Chuck knows the mapping himself; the
     Xbox-style button icons did not match the DualSense. The pad
     pointers (touchcircle / touchglow) are not key glyphs and are kept.
 10. Win does not move the keyboard (2026-11-04, Chuck: clicking Win
     moved the keyboard): triton.py's live-reposition loop forced the
     window to up-right while the Start menu was open, and back on close,
     so a Win tap (KEY_LEFTWIN, generic callback, correct) still moved
     the window. That live move is now disabled in triton.py: while the
     keyboard is open it stays put, Move (Shift+Move) is the only way to
     move it. Open-time Start up-right positioning is unchanged.
"""

import json
import os
import sys
import threading
import time
from contextlib import suppress

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from steamcontroller import SCButtons, SCStatus, SteamControllerInput

# ---------------------------------------------------------------------------
# Shared DS4 frame, filled by the stdin reader thread, read by DS4JsonSource.
# ---------------------------------------------------------------------------

_lock = threading.Lock()
_frame = {
    "buttons": 0,          # raw DualSense buttons, DS4 names
    "l2": 0,               # 0..255
    "r2": 0,               # 0..255
    "lx": 128,             # 0..255, 128 = center
    "ly": 128,             # 0..255, 128 = center, low = up
    "touch_dx": 0.0,       # pending touch delta, consumed by poll()
    "touch_dy": 0.0,
    "touch_t": 0.0,        # last touchmove time
    "tap": False,          # pending touchpad tap, consumed by poll()
    "ps": False,           # PS held; C# already withholds buttons then
}
_settings = {}
_settings_seen = threading.Event()


def _reader():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if not isinstance(msg, dict):
            continue
        with _lock:
            if msg.get("type") == "settings":
                _settings.update(msg)
                _settings_seen.set()
                # Live re-apply for an already-running Python session
                # (DS4 re-sends settings on re-open): triton's restore
                # target is otherwise frozen at first-open time.
                try:
                    _frt_live = msg.get("focus_restore_target")
                    if isinstance(_frt_live, int) and _frt_live > 0:
                        from triton import state as _state_live
                        _state_live.set_focus_restore_target(_frt_live)
                except Exception:
                    pass
                continue
            if msg.get("type") == "state":
                _frame["buttons"] = int(msg.get("buttons", 0) or 0)
                _frame["l2"] = int(msg.get("l2", 0) or 0)
                _frame["r2"] = int(msg.get("r2", 0) or 0)
                _frame["lx"] = int(msg.get("lx", 128) or 128)
                _frame["ly"] = int(msg.get("ly", 128) or 128)
                _frame["ps"] = bool(msg.get("ps", False))
            elif msg.get("type") == "touchmove":
                _frame["touch_dx"] += float(msg.get("dx", 0) or 0)
                _frame["touch_dy"] += float(msg.get("dy", 0) or 0)
                _frame["touch_t"] = time.monotonic()
            elif msg.get("type") == "tap":
                _frame["tap"] = True


# DS4 button bits sent by C# (DualTouchKeyboardRunner.SendState).
BTN_CROSS = 1
BTN_CIRCLE = 2
BTN_SQUARE = 4
BTN_TRIANGLE = 8
BTN_L3 = 16
BTN_DPAD_UP = 32
BTN_DPAD_DOWN = 64
BTN_DPAD_LEFT = 128
BTN_DPAD_RIGHT = 256
BTN_L1 = 512
BTN_R1 = 1024


class DS4JsonSource:
    """Pollable input source in triton's SteamControllerInput shape."""

    def __init__(self):
        self._seq = 0
        self._pad_x = 0.0
        self._pad_y = 0.0
        self._stick_zone = "NEUTRAL"   # last zone actually sent
        self._stick_latched = False    # True until stick returns to center
        self._dpad_bits = 0
        self._dpad_held_since = 0.0
        self._dpad_next_pulse = 0.0
        self._dpad_pulse_off = False

    def _stick_zone_for(self, lx, ly):
        dx = lx - 128
        dy = 128 - ly  # DS4 LY low = up, triton lstick_y positive = up
        if abs(dx) < 40 and abs(dy) < 40:
            return "NEUTRAL", 0, 0
        if abs(dy) >= abs(dx):
            return ("UP" if dy > 0 else "DOWN"), 0, (32767 if dy > 0 else -32767)
        return ("RIGHT" if dx > 0 else "LEFT"), (32767 if dx > 0 else -32767), 0

    def poll(self):
        with _lock:
            btn = _frame["buttons"]
            l2 = _frame["l2"]
            r2 = _frame["r2"]
            lx = _frame["lx"]
            ly = _frame["ly"]
            dx = _frame["touch_dx"]
            dy = _frame["touch_dy"]
            touch_t = _frame["touch_t"]
            tap = _frame["tap"]
            _frame["touch_dx"] = 0.0
            _frame["touch_dy"] = 0.0
            _frame["tap"] = False
        now = time.monotonic()
        sens = float(_settings.get("touch_sensitivity", 140) or 140)
        if sens <= 0:
            sens = 140.0
        scale = 8020.0 / sens
        if dx or dy:
            self._pad_x = max(-131072.0, min(131072.0, self._pad_x + dx * scale))
            # Y FLIP (2026-11-04): DS4 dy > 0 = finger DOWN (HwY grows
            # down), triton adjust_raw_y() negates raw_y, so +dy moved
            # the pointer UP. Negate once here; X is not flipped.
            self._pad_y = max(-65536.0, min(65536.0, self._pad_y - dy * scale))
        touched = (now - touch_t) < 0.25
        # DS4 tap fix (2026-11-04): a touchpad tap / physical click is
        # sent by C# as a single "tap" frame, often AFTER the finger
        # lifts, so touch_t can be stale and LPADTOUCH would be off.
        # Force touch on a tap frame: a tap IS a finger on the pad, and
        # handle_pad_input's touch gate must see it, or the tap types
        # nothing. This does not affect cursor movement, only the tap.
        if tap:
            touched = True
        # No tap snap: a tap types the key under the PAD cursor at its
        # current position (default: left-pad centre, 1/4 width, 1/2
        # height, matching triton's initial pointer), never the DPAD
        # cursor. The pad cursor and the DPAD cursor are separate.
        # Joystick center-reset, dominant axis only.
        zone, sx, sy = self._stick_zone_for(lx, ly)
        if zone == "NEUTRAL":
            self._stick_latched = False
            sx = sy = 0
        elif self._stick_latched:
            sx = sy = 0  # held or rotated without centering: nothing more
        else:
            self._stick_zone = zone
            self._stick_latched = True
        # D-pad repeat pulse: held direction clears for one poll, then returns.
        dpad = 0
        if btn & BTN_DPAD_UP:
            dpad |= SCButtons.DPAD_UP
        if btn & BTN_DPAD_DOWN:
            dpad |= SCButtons.DPAD_DOWN
        if btn & BTN_DPAD_LEFT:
            dpad |= SCButtons.DPAD_LEFT
        if btn & BTN_DPAD_RIGHT:
            dpad |= SCButtons.DPAD_RIGHT
        if dpad != 0:
            if dpad != self._dpad_bits:
                self._dpad_bits = dpad
                self._dpad_held_since = now
                self._dpad_next_pulse = now + 0.35
                self._dpad_pulse_off = False
            elif now >= self._dpad_next_pulse:
                if not self._dpad_pulse_off:
                    dpad = 0
                    self._dpad_pulse_off = True
                    self._dpad_next_pulse = now + 0.02
                else:
                    self._dpad_pulse_off = False
                    self._dpad_next_pulse = now + 0.06
        else:
            self._dpad_bits = 0
            self._dpad_pulse_off = False
        buttons = dpad
        if btn & BTN_CROSS:
            buttons |= SCButtons.A
        if btn & BTN_CIRCLE:
            buttons |= SCButtons.B
        if btn & BTN_SQUARE:
            buttons |= SCButtons.X
        if btn & BTN_TRIANGLE:
            buttons |= SCButtons.Y
        if btn & BTN_L3:
            buttons |= SCButtons.L3
        if btn & BTN_L1:
            buttons |= SCButtons.LB
        if btn & BTN_R1:
            buttons |= SCButtons.RB
        if l2 >= 128:
            buttons |= SCButtons.LT
        if r2 >= 128:
            buttons |= SCButtons.RT
        if touched:
            buttons |= SCButtons.LPADTOUCH
        if tap:
            buttons |= SCButtons.LPAD
        self._seq += 1
        return SteamControllerInput(
            status=SCStatus.INPUT,
            seq=self._seq,
            buttons=int(buttons),
            ltrig=int(l2 * 32767 // 255),
            rtrig=int(r2 * 32767 // 255),
            lpad_x=int(self._pad_x),
            lpad_y=int(self._pad_y),
            rpad_x=0,
            rpad_y=0,
            lstick_x=int(sx),
            lstick_y=int(sy),
            rstick_x=0,
            rstick_y=0,
            lpad_press=0,
            rpad_press=0,
        )

    def set_lizard(self, enabled):
        pass

    def haptic_click(self):
        pass

    def haptic_pad_click(self):
        pass

    def addExit(self):
        pass

    def close(self):
        pass


def main():
    threading.Thread(target=_reader, daemon=True).start()
    # Wait briefly for DS4's settings line; defaults apply if it never comes.
    _settings_seen.wait(timeout=1.0)
    # Fallback if DS4 did not set it: triton resources only search
    # TRITON_DATA on Windows, so point it at this folder's data dir
    # before the first triton import, or keyboard-layout.yaml is not found.
    os.environ.setdefault("TRITON_DATA", os.path.join(os.path.dirname(os.path.abspath(__file__)), "data"))
    from triton import inputsrc, screen, skins, state
    import cursor_ctrl

    # No scheduled-task cursor daemon in DS4: DS4 locks the Windows mouse
    # while the keyboard is open, so this is a no-op.
    cursor_ctrl.set_osk_cursor_visible = lambda visible: None
    # Replace the Steam Controller HID source with the DS4 JSON source.
    inputsrc.SteamHidSource = DS4JsonSource
    with suppress(Exception):
        skins.set_active_skin(str(_settings.get("skin") or "Gruvbox"))
    with suppress(Exception):
        skins.set_transparency(str(_settings.get("transparency") or "off"))
    with suppress(Exception):
        screen.set_osk_size(str(_settings.get("size") or "medium"))
    with suppress(Exception):
        state.set_kb_layout(str(_settings.get("layout") or "QWERTY"))
    with suppress(Exception):
        state.set_split_layout(bool(_settings.get("split", False)))
    with suppress(Exception):
        state.set_diacritics_enabled(bool(_settings.get("diacritics", True)))
    with suppress(Exception):
        state.set_sc_liftoff_enter(bool(_settings.get("liftoff", False)))
    # DS4 L1/R1 are text arrows, not pad-click buttons: disable exact
    # triton's default LB/RB click-button synthesis (see item 8 above).
    # (0, 0) is a truthy tuple, so controller.py's `or (LB, RB)` fallback
    # does not re-enable it. Touchpad tap (LPAD) is unaffected.
    with suppress(Exception):
        state.set_sc_click_button((0, 0))
    # DS4 tap fix (2026-11-04): exact triton defaults sc_pad_click_enter
    # to False, and controller.py then STRIPS the LPAD bit before
    # handle_pad_input, so a DS4 touchpad tap / physical click could
    # never type, no matter what the cursor was on. DS4's tap is the
    # ONLY pad-click source (C# Mouse touchesEnded / touchButtonUp
    # -> RequestTouchpadTap -> SendTap -> LPAD for one poll), so enable
    # pad-click entry for DS4 only. This does NOT re-enable L1/R1
    # click synthesis: click_button stays (0, 0), so LB/RB still do
    # nothing in the pad path and stay as text arrows. A tap now types
    # the key under the pad cursor even if L2 Shift is held (pad-click
    # path is independent of the trigger's shift role) or R2 Enter is
    # held (right pad is not used by DS4).
    with suppress(Exception):
        state.set_sc_pad_click_enter(True)
    with suppress(Exception):
        state.set_key_sound_enabled(bool(_settings.get("key_sound", True)))
    with suppress(Exception):
        state.set_rumble_enabled(bool(_settings.get("rumble", True)))
    # DS4 overlay fix (2026-11-04): DS4-only always-visible parked window.
    # Must run BEFORE triton.main(), because screen.Screen() reads
    # is_osk_always_visible() at construction: visible but parked at
    # -32000,-32000, so open/close are only SetWindowPos moves with
    # SWP_NOACTIVATE, never a ShowWindow that steals focus, deselects
    # a text box, or closes a Discord overlay. The keyboard is not its
    # own focused window: it stays an overlay, the game/app stays focused.
    with suppress(Exception):
        state.set_osk_always_visible(bool(_settings.get("always_visible", True)))
    # DS4 focus-restore safety net (not the main fix): DS4 captured the
    # foreground HWND before starting Python. Validate it here against
    # the shell/desktop/OSK exclusions from triton/win_focus.py, then
    # record it so triton's open restore + 0.25s sentinel can hand
    # focus back. triton's _restore_foreground already refuses to fight
    # a real foreground app, so this never steals a deliberate click.
    with suppress(Exception):
        _frt = _settings.get("focus_restore_target")
        if isinstance(_frt, int) and _frt > 0:
            _valid = True
            try:
                import ctypes as _ct
                from ctypes import wintypes as _wt
                _u = _ct.windll.user32
                if not _u.IsWindow(_ct.c_void_p(_frt)):
                    _valid = False
                else:
                    _pid = _wt.DWORD()
                    _u.GetWindowThreadProcessId(_ct.c_void_p(_frt), _ct.byref(_pid))
                    if not _pid.value or _pid.value == os.getpid():
                        _valid = False
                    else:
                        _buf = _ct.create_unicode_buffer(256)
                        _u.GetClassNameW(_ct.c_void_p(_frt), _buf, 256)
                        if _buf.value in ("Progman", "WorkerW", "Shell_TrayWnd",
                                "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
                                "ForegroundStaging", "MultitaskingViewFrame",
                                "XamlExplorerHostIslandWindow", "SDL_app"):
                            _valid = False
            except Exception:
                _valid = True
            if _valid:
                state.set_focus_restore_target(_frt)
    from triton import triton

    triton.main()


if __name__ == "__main__":
    main()
