# DS4 DualTouch installer packaging (NOT published)

Status: packaging scripts exist in this folder. NO GitHub Release has
been published from this work, and no release URL below is live yet.

## What the friend-facing installer is

`Install-DS4DualTouch.ps1` (in this folder) is the installer. It is a
script, not yet a compiled EXE, because a compiled setup EXE was not
built in this pass. The script:

1. Takes a DS4 publish folder (or a zip of one) as its source.
2. Copies it to `C:\Program Files\DS4Windows` (including the
   `DualTouchKeyboard` folder next to `DS4Windows.exe`).
3. Creates or updates the Scheduled Task `DS4Windows` pointing at the
   installed exe, and starts DS4 ONLY via that task (never a second
   direct exe launch).
4. Leaves the Python runtime to DS4 itself: on first DS4 start, DS4
   shows the runtime prompt (Auto-install with DS4, or install it
   yourself). The installer does NOT pip-install anything and does
   NOT install Python itself; DS4's own auto-install path does that
   after the user picks Auto.

`build-installer.ps1` (in this folder) builds the distributable zip
from a checkout: it runs the DS4 publish, copies the publish output
plus this folder's installer script, and zips them. Run it on a build
machine with the .NET SDK, not on the user's PC over SSH.

## Exactly what the installer contains vs. downloads

Contains (in the zip, once built):
- DS4Windows publish output (self-contained win-x64 Release build),
  including `DS4Windows.exe`, `DS4Windows.dll`, runtime DLLs, and the
  `DualTouchKeyboard` folder (`ds4_keyboard.py`, adapted layout YAML,
  Gruvbox skin, fonts, this README, `INSTALLER.md`, installer scripts).
- The VIIPER sidecar already bundled by the DS4 publish (extras folder).
- No Python runtime, no pip packages, no SDL DLLs.

Downloads (at install / first-run time, only with network):
- Nothing at installer time, if the zip already contains the publish
  output. The zip itself is the download from GitHub Releases.
- At first DS4 start, ONLY if the user picks Auto-install: Python
  embeddable 3.12.8 zip from python.org (if no Python is found),
  `get-pip.py` from bootstrap.pypa.io, and pip packages
  `Pillow pynput hidapi PyYAML pystray psutil vdf` from PyPI.
- If the user picks Manual, DS4 downloads nothing and the user runs
  the pip command printed by DS4 themselves.

## Size, honestly: the 20 MB target is NOT met by a full package

Measured 2026-11-03: the self-contained win-x64 publish folder is
275,183,473 bytes (553 files) before zipping. A zip of that will be
far larger than 20 MB. A ~20 MB file can only be a DOWNLOADER exe
that pulls the DS4 package from GitHub Releases at install time, not
a package containing DS4 itself. That downloader exe is not built in
this pass; the scripts here are the packaging, and the size question
below is left for Chuck to decide.

Questions for Chuck (see the main report): accept a large zip
(roughly the publish size, compressed), accept a small downloader exe
that pulls from GitHub Releases (to be built and published later),
or accept a framework-dependent publish (smaller, but needs the
.NET 8 desktop runtime on the friend's PC)? No release is published
until he picks one.
