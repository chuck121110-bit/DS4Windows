"""DS4 stub for DualTouch's steam_shortcut module.

Only the read-only Steam-path lookup is kept, copied verbatim from the
original find_steam_path(): it reads HKCU\\Software\\Valve\\Steam ->
SteamPath and returns it, or None. That lookup is what triton's skin
system (steam_assets) and key_sound use to READ Steam OSK themes and
Steam keyboard sounds at runtime. Nothing here writes, edits, or creates
anything: no shortcuts.vdf editing, no shortcut registration, no tray,
no autostart, no scheduled task, no network. Every other function from
the original steam_shortcut.py is deliberately NOT included.
"""

import os

_STEAM_REG_KEY = r"Software\Valve\Steam"
_STEAM_REG_VALUE = "SteamPath"


def find_steam_path():
    """Locate the Steam install from HKCU\\Software\\Valve\\Steam -> SteamPath.
    Returns the path (as a str) or None."""
    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _STEAM_REG_KEY) as key:
            path, _ = winreg.QueryValueEx(key, _STEAM_REG_VALUE)
        if path:
            return path.strip().replace("/", os.sep)
    except OSError:
        pass
    return None
