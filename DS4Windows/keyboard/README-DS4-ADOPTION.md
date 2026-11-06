# DualTouch keyboard, adopted into DS4Windows

Exact DualTouch project (github.com/mateuszklysz/DualTouch, GNU LGPL-3.0),
in DS4Windows/keyboard/, adapted ONLY for DualSense controller input.

## What is exact, verbatim

- triton/ : the full SDL3 on-screen keyboard UI, layouts, skins, diacritics,
  pad/stick/trigger handling, window positioning, key sounds.
- data/ : keyboard layouts (QWERTY, AZERTY, QWERTZ, Dvorak, Colemak, ABC),
  bundled Gruvbox skin, fonts, glyph images.
- sdl3w/ : SDL3 / SDL3_ttf Python bindings and DLLs, used by triton to render.
- steamcontroller/ : the Triton input-report types. The HID driver itself is
  NOT used: DS4 feeds input over stdin instead (see ds4_main.py).
- applog.py, appsettings.py, key_sound.py, steam_assets.py, win_focus.py,
  cursor_ctrl.py : supporting modules, verbatim. key_sound and steam_assets
  only READ Steam files at runtime (sounds, OSK themes) and degrade to
  silence / bundled skin when Steam is absent.

## What is deliberately NOT included (virus-free, no persistence)

- tray/ launcher, autostart.py, watchers.py, cursor_helper.py : no tray icon,
  no Start menu entry, no scheduled task, no autostart.
- steam_shortcut.py (original) : replaced by the stub in this folder, which
  keeps ONLY the read-only find_steam_path() lookup. No shortcuts.vdf
  editing, no shortcut registration, no Steam Input layer switching.
- Installer / build tool / Inno Setup : none. This folder ships inside the
  DS4Windows build and is read from the DS4 install folder.
- No network calls, no exfiltration, no registry writes, no Run keys.
  The only registry read is the SteamPath lookup above.

## DS4-only files and the one triton edit

- ds4_main.py : DS4 entry point. Replaces SteamHidSource with DS4JsonSource
  (stdin JSON from DS4), applies per-profile settings from DS4, enforces
  joystick center-reset and D-pad repeat, maps the single DualSense
  touchpad to triton's left pad, and no-ops the cursor-helper call (DS4
  already locks the Windows mouse while the keyboard is open).
- steam_shortcut.py (stub, this folder) : read-only Steam path lookup only.
- triton/controller.py, ONE line: sc_triggers_off = True changed to False,
  with a comment. Exact triton disables triggers because Steam Input would
  leak them into the game; DS4 neutralizes mapped output while the keyboard
  is open, so the leak does not apply, and Chuck's mapping requires
  L2 = Shift and R2 = Enter.

## Mapping (exact triton, once the DS4 frame is built)

Cross = type highlighted key, Circle = close, Square = Backspace (hold
repeats), Triangle = Space, L3 = Caps, L2 = Shift (hold), R2 = Enter,
D-pad = one step per press (hold repeats via DS4 pulse), left stick =
one step per deflection, must return to center first, no diagonal,
touchpad = pad cursor, touchpad tap = pad click (types key under pad
cursor; if the pad never moved, the tap snaps to the highlighted key).
Square / Triangle / Enter / L3 only highlight their key while held, they
do not move the cursor.

## Settings (DS4 Advanced, per profile, keyboard box)

Skin (Gruvbox bundled, plus Steam OSK themes read at runtime from the
Steam install: DefaultTheme, Digital, NightShift, Ruby, Grape, Cerulean,
Seafoam, Pumpkin), layout, size (small / medium / full), transparency
(off / low / medium / high), split layout, diacritics, lift-off typing,
key sounds, rumble, touchpad sensitivity. Stored per profile in
<profile>.keyboard.json next to the profile XML, NOT in settings.json,
NOT in the profile XML itself, so profiles are never edited.
