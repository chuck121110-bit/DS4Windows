# AGENT.md - DS4Windows Custom Build for Chuck

## Who this is for
- User: Chuck, timezone America/New_York, mostly voice-to-text, so messages may have transcription errors. Reply in English, never Chinese. Never use em dashes, use commas, colons, or periods instead. No emojis in message text, reactions only. Links as tappable Markdown links. Word substitutions: "load" for inject, "mod" for cheat, "technique" for exploit. WeMod is called Wand.
- Assistant: Sage, signature emoji 🏮. This is side chat "DS4Windows custom build". Coding belongs here, never in main chat. Main chat is life admin only.
- Short messages: Chuck asked for short messages on Nov 4. Keep reports short, text only, no artifact widgets, no file links, no staging file links in chat, unless he explicitly asks for a specific file.
- When he asks a question, answer in chat only, do not build a web app / page / artifact unless he explicitly asks.
- Do not restate what he asked, just do it and give a short confirmation or result.
- When he asks several questions, answer each in its own paragraph, unless he says keep it short.
- If you do not know, ask Chuck, do not hallucinate a setup or backstory.
- Trust his live test reports over hypotheses. Do not claim anything works until he says yes.
- Every time you write code, revise, review, research, then check the written code for bugs, verify the build actually contains the change, and verify with logs. That is his standing rule, Nov 5.

## Standing rules that matter here
- No credit card, never buy, check out, or spend for him. Never type or submit his email without explicit authorization in that turn.
- Never select persistent login, "don't ask again", "stay signed in", "remember me".
- Deletion: anything deleted goes to trash / Recycle Bin, never permanently, except explicit in-turn confirmations.
- Profiles rule: do not touch, change, or offer to change his DS4 profiles, Profiles.xml contents, profile files, or Actions.xml, unless he explicitly asks in that turn. Exceptions actually made and recorded in chat: global flags, and code that updates slot fallback, not profile contents. Never change his profile light bar color without explicit say-so in that turn.
- No whole PC restart unless he explicitly asks in that turn.
- DS4 deploy rule: deploy builds always, even in a game, do not hold for a game.
- DS4 restart warning: every DS4 restart reconnects his controller and can pulse it. Do not restart DS4 for a logger change without telling him first.
- Token cap for coding: per chat, coding over 30% of weekly token allowance in a rolling three-day window pauses ALL coding everywhere until weekly refresh. Never reduce effort to save tokens; in-progress tasks always finish. Enforced silently: never mention cap or usage, never hedge about a task being heavy.
- Code sync: whenever we work on code, sync the main files to GitHub or Google Drive.
- File naming: never name a file "fixed" or imply it is fixed. Just overwrite the existing file in place.
- Code: full copyable fenced code blocks, never file attachments; prefer separate override modules over editing stock/library source.

## PC and access
- PC: Tailscale hostname chuck, 100.79.213.117, user chuck@chuck, SSH ~/workspace/bin/pc-ssh, SFTP ~/workspace/bin/pc-sftp, key ~/.ssh/id_ed25519_pc.
- Bare PowerShell over SSH breaks quoting, reliable method is write a .ps1 locally, SFTP to C:\Users\Chuck\AppData\Local\Temp\ (SFTP destination must be relative, AppData/Local/Temp/file.ps1, remote cwd is C:\Users\Chuck), run powershell -File.
- Large SCP transfers can break the tsproxy pipe, files usually arrive complete on retry.
- VM Tailscale access: direct curl to tailnet fails, must use tunnel_proxy="${HTTPS_PROXY%:*}:3130", curl --proxy "$tunnel_proxy".
- DS4 install: C:\Program Files\DS4Windows\DS4Windows.exe, log C:\Users\Chuck\AppData\Roaming\DS4Windows\Logs\ds4windows_log.txt, rumble log ...\Logs\rumble_log.txt, trigger log ...\Logs\trigger_effect_log.txt, Alt-Tab log ...\Logs\alttab_log.txt.
- Autostart Scheduled Task "DS4Windows" (logon, 30s, Highest). Restart DS4 only via that task, never launch a second DS4Windows.exe from SSH. Two DS4 processes can be normal: second is Bluetooth audio pacer helper, child of main.
- VIIPER exe: C:\Program Files\DS4Windows\VIIPER\viiper.exe, custom file 14,406,656 bytes, SHA-256 2166e9dc62743eb85038ed02861f818a7de31d8dc6f418897476de893a32fd41, PID 16852 from 2026-11-03, persisted through Nov 4 builds, later PID 9704 from 2026-11-05 17:28:35.
- Source on PC, root directory for this project: C:\Users\Chuck\DS4Windows-stock
  - Fork chuck121110-bit/DS4Windows, public repo, default branch main. Do NOT build from C:\Users\Chuck\DS4Windows-source (old custom branch, reference only).
  - Build: dotnet publish DS4WindowsWPF.sln -c Release -r win-x64 --self-contained true. Publish output is bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish, not bin\Release. All successful Nov 1-4 builds have exe 569,344 bytes. Never redeploy wrong 3.5 build from commit 43c4aed (exe 233,472 bytes).
  - Deploy must copy FULL publish folder, exe AND DLL, not just exe. Missing DLL caused a failed logger deploy Nov 4, fixed by full copy.
  - PC tree has been widely dirty since before Nov 3 evening, with many modified and untracked files. Multiple builds were deliberately NOT committed to avoid bundling unrelated changes. Do not commit without Chuck's decision.
- Upstream: maintained DS4Windows 5 is hbashton/DS4Windows, version 5.0.12.0, InformationalVersion VIIPERRC4.6.6. Ryochan7 DS4Windows has no VIIPER and is archived.

## Profiles, verified Nov 2
- DualSense.xml, DualShock.xml, Xbox One.xml all Color 4,5,5, LightbarMode DS4Win. MAC 50:EE:32:7B:BA:FB linked to "DualSense".
- Actions.xml: Action "Check Battery Life", Trigger Left/PS, Type BatteryCheck, notification True, LED force True, empty color 255,0,0, full color 0,255,0. Keyboard action: Name "Keyboard", Trigger PS/Circle, Type OnScreenKeyboard, Details TouchpadCursor. Action "Switch program", Trigger PS/Cross, Type SwitchPrograms.
- DualSense profile, as of Nov 5: RumbleBoost 0, AudioHaptics Enabled false (changed Nov 4 for pulses, backed up), DualSenseMuteButtonLightEnabled True (changed Nov 4, then on-disk file was found False on Nov 5, fixed to True), MutesInputOutput True, MutesMicrophone True, MutesSpeaker False, L2TriggerEffect None, R2TriggerEffect None, TriggerLab Enabled false, Switch2DualSenseAdaptiveTriggersEnabled false, XboxImpulseToAdaptiveTriggers false, Switch2MapXboxImpulseTriggersToHdRumble false (last three changed Nov 5 for trigger block, backed up).

## What is built, in order, current as of Nov 5 19:11 EST
- LED fade and battery check, confirmed closed Nov 3, then reworked Nov 5: connect, DS4 must not set blue, firmware blue fades out on its own, then only profile color fades in. Timing as of Nov 6: fade-in 300 ms, fade-out 150 ms (always double speed), applied to battery check, connect, and every other fade. Battery check: battery color, hold 3s, then fade out to profile/custom through original light bar logic. Connect fade is CUSTOM, not in stock DS4Windows. Stock has no fade, just sets color.
  - Nov 5 18:14 LED fix made battery hold 3s snap green, no fade, that broke fade-in.
  - Nov 5 18:55 fade-in fix restored explicit fade-in, profile to battery over 600 ms, hold 3s, fade-out 420 ms to profile.
  - Nov 5 19:11 ownership fix: fade clock freezes until first successful physical Bluetooth publish, hold extends to 3s of real published color, fade-out starts from full battery color into profile color 4,5,5, no matter what, LED ownership forced every tick during hold, failed direct send logged with reason. Mic LED: profile file on disk was False, fixed to True, Bluetooth speaker cache now takes MuteLedOverride every merge, failed mute send re-queued.
  - Nov 6 fade fix: the fade clock freeze from Nov 5 19:11 was the "animation sometimes never plays" bug (clock reset every tick until a physical publish succeeded, then hold expiry force-cleared the fade flags). Fade clock now always runs on wall-clock time, hold expiry no longer clears fade flags, connect fade-in starts on the first tick, timings 300/150 ms, and a backstop forces lightbar ownership on every tick where any part of the sequence (hold, either fade-in, fade-out, grace) is active, so the battery check wins over any game/Steam for the whole sequence. Owed: Chuck live test.
  - Latest build Nov 5 19:11: exe 569,344 bytes 19:11:35, dll 11,180,544 bytes 19:11:33, DS4 PID 19956 started 19:11:58, VIIPER PID 9704. Controller was disconnected at that check, log cannot prove fade yet. Live tests owed: turn controller on, fade in to profile color; mic click once, orange stays on; PS+Left, fade in green, hold 3s, fade out to profile color, even with Steam running.
- Audio: one main DualSense Wireless Controller speaker and mic, shared across profiles, never-remove rule, automatic removal is no-op, only removal path is Settings button "Remove main DualSense audio device". Keep-alive, one main audio, stop persistence, VIIPER fixed serial. Full-exit test still owed. Sonar one-time re-order under stable ID while headset on, headset mic first, DualSense mic second, still owed.
- Special Actions, Nov 3: Multi-key Press/Toggle, no key limit, System Actions (Volume, Copy/Paste removed Nov 3, Cut, Undo, Redo, Select All, Save, Close Window, plus Custom keys), Switch Programs / Cycle Programs, auto-naming.
- Alt-Tab Switch Programs, Nov 4: initial Tab/Shift+Tab, then D-pad arrows, then menu-open tracker, then fix for raw VK codes to FakerInput, then watchdog focus-change fix, then PS+D-pad action block, then regression fix: trigger button PS/Cross IS the select button, Toggle self-cancelled in same tick, fixed with crossArmed, opening Cross press ignored until released, then fresh Cross selects. Latest build includes that. Live test owed.
- PS suppression, Nov 4 20:38: PS cleared at all four virtual egress builders in ViiperOutDevice.cs, internal PS untouched, so PS+Circle, PS+Left, PS+Triangle/Square/X/Circle, PS bumper profile switch still work. Steam treats virtual DualSense PS / Xbox Guide as Guide button. Caveat: if Steam already grabbed physical controller before DS4, restart Steam or reconnect controller. Chuck confirmed PS block works.
- Rumble / Audio Haptics, Nov 4: DS4 does not randomly vibrate on its own. Ongoing pulses were Fortnite native motor rumble via VIIPER plus Fortnite audio via Audio Haptics Gain 100. Fix Nov 4 20:50: RumbleBoost 100 to 0, AudioHaptics Enabled true to false, plus ScpUtil fix so boost 0 is honoured for DualSense. Rumble logger: rumble_log.txt, with audio_source, AUDIO-SOURCE, AUDIO-HAPTICS lines, RECONNECT lines.
- Advanced triggers, Nov 4-5: game feedback sets looping trigger modes Vibrate, Choppy, Machine, AutomaticGun, Galloping, Pulse. Trigger logger trigger_effect_log.txt, idle clear after 2s, disconnect/profile switch clear. Nov 5 18:28 fix: Fortnite sending game-native frames every 16 ms, profile flags above set false, code blocks game trigger feedback at three entry points when flags off, sends None once, zeroes trigger bytes. Live test owed: play Fortnite, triggers should stay dead, no infinite buzzing.
- Gyro drift, Nov 5 18:07: gyro sensor bias, DS4 only measured once in first 5s after connect, if picked up then measurement wrong, cold controller warms up and bias shifts. Fix in DS4Sixaxis.cs: start-up calibration only averages still samples under 5 deg/s, still recalibration if all axes under 3 deg/s for 1.5s straight, nudges offset until gone, real motion resets timer. Sensitivity stays 100. Live test owed in Minecraft, set controller flat still a few seconds, turn gyro on, camera should not move.
- DualTouch keyboard adoption, Nov 3-4: entire exact DualTouch project in DS4Windows/keyboard/, DS4 is that keyboard, not a port, skins/settings per profile in Advanced, sidecar file next to profile, never inside profile. Standalone DualTouch fully removed first. Mappings: Triangle Space, Circle Close, Square Backspace, Cross type highlighted key, R2 Enter, L2 Shift, L3 Caps, L1 Left arrow, R1 Right arrow, hold repeats, touchpad tap types under disc cursor, Win SendInput scan 0x5B, Move emoji picker, Shift+Move cycles position. Trigger suspension while keyboard open, touchpad cursor Y flip, TRITON_DATA fix.
  - Keyboard focus / overlay fix, Nov 4 22:33: window created always-visible parked at -32000,-32000, open/close only SetWindowPos moves with SWP_NOACTIVATE, DS4 captures foreground window before starting Python and sends it as focus-restore target. Live test owed, and Sage test via browser controller was inconclusive: Modrinth search "zxq" stayed, Square did not delete, browser controller Backspace/Delete also did not delete in Modrinth webview, so focus for typing unproven. Modrinth Library search was left showing "zxq", Notepad with Profile.sav tab was left open at that time, both later closed by Chuck's request Nov 4 23:24-23:26, final check showed only explorer, svchost, TextInputHost, and the check task's own WindowsTerminal.
- Screen tool / browser controller, Nov 4: Downloads\SageScreenTool and Downloads\SageBrowserControl, Tailscale only, port 8766, /stream video only, /controls controls only, root links both, no autostart. Started only by Chuck himself or Sage with explicit permission for that start, stopped after use, tasks deleted, port closed. Use it to test what is built, from the screen, instead of asking Chuck yes/no for every test.

## Open live tests, do not claim working until Chuck says yes
- Keyboard overlay focus: PS+Circle, Modrinth search bar stays selected, Discord overlay stays open, typing lands in game/app.
- Alt-Tab: PS+Cross open, D-pad Right/Left move, Cross selects, second PS+Cross closes, 30s timeout, normal after close.
- Triggers / rumble: Fortnite, triggers dead, no infinite buzzing.
- Gyro: Minecraft, flat still, camera does not drift.
- LED: connect fade-in starts instantly and runs 300 ms, mic orange stays on, PS+Left battery fade in 300 ms, hold 3s, fade out 150 ms to profile color, even with Steam running; repeat battery check in a game to confirm the game never wins the LED.
- Audio: full-exit test, Sonar re-order, haptics lane Ready vs Needs attention, mute orange after enabling mute light.
- GitHub release v5.0.12-custom-20261103, release ID 433089892, page exists, full zip NOT attached, asset upload failed 401, uploads.github.com not registered. Do not re-create release. Chuck may give GitHub login via Secure Vault later.
- Modrinth custom build: separate side chat, custom Modrinth in Downloads, share / update all button errors, profiles untouched, do not touch unless Chuck says resume in that chat.

## How to work
- Build from current PC tree, never overwrite with an older snapshot. Verify exe AND DLL timestamps/sizes after deploy, DS4/VIIPER start times, log lines.
- Restart DS4 only via Scheduled Task DS4Windows, with a finally-block restart so DS4 is never left down.
- Keep reports short, text only, no widgets, no file links, unless he explicitly asks.
- When a fix fails, say so plainly, find the real root cause in code / logs, do not repeat a broken fix.
- Steam: if Steam already grabbed the physical controller before DS4, Steam can write lightbar / triggers / rumble directly to the physical controller, bypassing DS4. Reconnect controller or restart Steam once, so Steam only sees the virtual controller via HidHide. DS4 deliberately skips automatic reclaim for Bluetooth because restarting can loop disconnects.
- PC was shut down at 9 AM Nov 5 by his request, even if a game was open, then turned back on later that day.
