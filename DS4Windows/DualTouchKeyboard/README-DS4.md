# DS4 DualTouch keyboard (real Python keyboard, DS4-adapted)

Source of the reused assets: mateuszklysz/DualTouch, LGPL-3.0.
This folder is part of the DS4Windows project (DS4Windows/DualTouchKeyboard/)
and is copied to the DS4 publish folder by the .csproj Content rule.

## Architecture, plainly

DualTouch's full triton stack reads a Steam Controller over HID directly
(`steamcontroller/` + `triton/inputsrc.py`) and cannot be fed a DualSense
without adaptation. So this is NOT the unmodified Steam HID stack running
on a DualSense, and it does not claim to be. It is a DS4-adapted Python
keyboard: DS4Windows owns the DualSense, translates its input, and feeds
this process over stdin as JSON lines (see `ds4_keyboard.py` header for
the full protocol). Rendering is tkinter (Python standard library), not
SDL, because the SDL3 DLL binaries are deliberately not shipped.

## Exactly which DualTouch files were reused

- `data/cfg/keyboard-layout.yaml`: verbatim DualTouch QWERTY 75% layout,
  with exactly two adaptations: the Windows key entry removed, and the
  Move entry renamed to Hide (`behavior: hide`).
- `data/skins/Gruvbox.css`: verbatim DualTouch Gruvbox skin. Colours are
  parsed from it; key text is forced white per the DS4 spec, not the
  skin's cream `#ebdbb2`.
- `data/fonts/DejaVuSansCondensed-Bold.ttf`,
  `data/fonts/NotoSansSymbols2-Regular.ttf`,
  `data/fonts/Selawik-Semibold.ttf`: verbatim DualTouch fonts, shipped
  as assets. Note: tkinter renders with system fonts (Segoe UI Semibold
  first); it does not load these TTF files directly, so the on-screen
  font is a system font, not an embedded TTF render. The TTFs are kept
  in the package as the real DualTouch font assets.

## Exactly which DualTouch files were stripped (not included, not run)

- `steamcontroller/` (Steam Controller HID stack) and `triton/`
  (SDL renderer, controller, state, screen, vkb, pad, stick, etc.).
- `sdl3w/` and `sdl3w/dll/SDL3.dll`, `SDL3_ttf.dll` (binaries).
- `tray/` (tray icon, launcher, Steam layer, menu, battery mixin),
  `autostart.py` (scheduled-task / registry autostart),
  `steam_shortcut.py` (edits Steam `shortcuts.vdf`, steam:// URLs),
  `cursor_ctrl.py` / `cursor_helper.py` (cursor-helper scheduled task),
  `win_focus.py` (Steam foreground handling), `key_sound.py`,
  `steam_assets.py`, `watchers.py`, `applog.py`, `appsettings.py`,
  `installer/` (DualTouch's Inno installer), `tests/`, `docs/`.

Audit result (re-checked 2026-11-03 on the audited clone): the triton
core itself contains no network, exfiltration, or hidden code (one grep
hit for "subprocess" in a comment only). The persistence / Steam-editing
behaviour lives outside triton, in the files listed above as stripped,
plus `tray/app.py` pushing autostart into the registry on start and
`tray/launcher.py` / `tray/steam.py` registering the Steam shortcut.
None of that is in this DS4 build. This keyboard makes no network
calls, writes no files, touches no registry keys, creates no scheduled
tasks, shows no tray icon, and never touches Steam.

## Exactly which files were newly written for DS4

- `ds4_keyboard.py`: the whole keyboard (new code, ~700 lines). Reads
  the layout YAML with PyYAML, renders with tkinter, types with pynput,
  reads DS4 events from stdin, exits on stdin close / Hide / Circle.
- `../DualTouchKeyboardRunner.cs` (C#): DS4-side runner, runtime
  prompt, auto-install, process start/stop, stdin feed.
- `../DS4Forms/DualTouchRuntimePrompt.cs` (C#): non-modal prompt window.

## Runtime dependencies (installed by DS4 code, never manually)

`Pillow pynput hidapi PyYAML pystray psutil vdf`, via DS4's own
auto-install path (`DualTouchKeyboardRunner.StartInstallIfNeeded`).
The keyboard process itself uses only PyYAML + pynput + tkinter;
Pillow, hidapi, pystray, psutil, vdf are installed because they are
DualTouch's listed requirements, not because this keyboard calls them.

## DualSense adaptation only

Single DualSense touchpad controls the keyboard (touch position moves
the highlight, tap types the highlighted key). Mouse movement is locked
off by DS4Windows while the keyboard is open and restored on close.
Cross = type, Circle = close, Square = backspace, R2 = enter,
L3 = caps toggle, L2 held = shift. No badge text overlays. Shift latch
clears after typing a character. Window is about 350 px tall (shorter
than the old 480 px window). Hide replaces Move. No Windows key.
No other DualTouch behaviour was changed, because no other DualTouch
behaviour is present in this build.
