// RumbleLogger: DS4 custom build addition, 2026-11-04.
// Logs every actual physical vibration change, and every controller
// (re)connect association, to a separate readable file. It never causes
// vibration and never changes rumble values: callers pass the values
// they were already sending, and this class only records them.
// Game rumble passthrough IS captured here, because every physical
// rumble change funnels through DualSenseDevice's motor setters,
// SetHapticState, SetRumbleState, and setRumble, including rumble that
// DS4 is only forwarding from a game via VIIPER / XInput. At that layer
// DS4 does not reliably know the game process name, so forwarded game
// rumble is logged as game-passthrough with game=unknown, and the user
// can correlate the timestamp with the main log line
// "Automatic game audio selected '<process>'" at the same time.
// 2026-11-04 audio update: every motor line now also carries
// audio_source=<last automatic/app audio source at that time>, so a
// Fortnite audio-haptics pulse and a pure game motor rumble can be
// told apart by correlation. Audio source changes are also logged
// as AUDIO-SOURCE lines, and audio-haptics activity transitions are
// logged as AUDIO-HAPTICS lines, because audio haptics merges into
// the 3 kHz game haptics lane, not into these motor setters.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DS4Windows
{
    public static class RumbleLogger
    {
        private static readonly object logLock = new object();
        private static string logPath;
        private static bool sessionLogged;
        private static string currentAudioSource = "none";
        private static bool audioHapticsActive;
        private static string lastAudioHapticsSource = "";
        private static readonly Dictionary<string, (byte Left, byte Right, DateTime Start, string Source)> lastByController
            = new Dictionary<string, (byte, byte, DateTime, string)>();

        private static string LogPath
        {
            get
            {
                if (logPath != null) return logPath;
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DS4Windows", "Logs");
                    Directory.CreateDirectory(dir);
                    logPath = Path.Combine(dir, "rumble_log.txt");
                }
                catch
                {
                    try
                    {
                        logPath = Path.Combine(
                            AppDomain.CurrentDomain.BaseDirectory, "rumble_log.txt");
                    }
                    catch
                    {
                        logPath = "rumble_log.txt";
                    }
                }
                return logPath;
            }
        }

        public static void LogReconnect(string controller, string reason)
        {
            try
            {
                lock (logLock)
                {
                    EnsureSessionLocked();
                    WriteLocked($"RECONNECT | controller={controller} | reason={reason} | note=if a vibration lines up with this time, it was on (re)connect");
                }
            }
            catch { /* Logger must never break DS4. */ }
        }

        public static void SetAudioSource(string source)
        {
            try { lock (logLock) { if (!string.IsNullOrWhiteSpace(source)) currentAudioSource = source.Trim(); } } catch { }
        }

        public static void LogAudioSource(string selector, string displayName, int processId)
        {
            try
            {
                lock (logLock)
                {
                    EnsureSessionLocked();
                    SetAudioSourceLocked(displayName);
                    WriteLocked($"AUDIO-SOURCE | selector={selector} | source={displayName} | process={processId} | note=audio-haptics / speaker-driven haptics now follow this source; motor lines after this carry audio_source={displayName}");
                }
            }
            catch { }
        }

        public static void LogAudioHapticsActivity(string source, bool active)
        {
            try
            {
                lock (logLock)
                {
                    EnsureSessionLocked();
                    if (active == audioHapticsActive && source == lastAudioHapticsSource) return;
                    audioHapticsActive = active;
                    lastAudioHapticsSource = source ?? "";
                    WriteLocked(active
                        ? $"AUDIO-HAPTICS | state=active | source={source} | note=speaker / audio-driven haptics are now driving the controller (3 kHz haptics lane, not motor rumble)"
                        : $"AUDIO-HAPTICS | state=idle | source={source} | note=audio-driven haptics stopped");
                }
            }
            catch { }
        }

        private static void SetAudioSourceLocked(string source)
        {
            if (!string.IsNullOrWhiteSpace(source)) currentAudioSource = source.Trim();
        }

        public static void LogRumble(string controller, byte rightLightFastMotor, byte leftHeavySlowMotor)
        {
            try
            {
                string key = string.IsNullOrEmpty(controller) ? "unknown-controller" : controller;
                lock (logLock)
                {
                    EnsureSessionLocked();
                    DateTime now = DateTime.Now;
                    bool isZero = rightLightFastMotor == 0 && leftHeavySlowMotor == 0;
                    if (lastByController.TryGetValue(key, out var prev))
                    {
                        if (prev.Left == leftHeavySlowMotor && prev.Right == rightLightFastMotor)
                            return; // No change, and never log zero frames.
                        if (isZero)
                        {
                            long ms = (long)(now - prev.Start).TotalMilliseconds;
                            WriteLocked($"RUMBLE-STOP | controller={key} | duration_ms={ms} | last_left={prev.Left} last_right={prev.Right} | source={prev.Source} | audio_source={currentAudioSource}");
                            lastByController[key] = (0, 0, now, prev.Source);
                            return;
                        }
                        string source = ClassifySource();
                        WriteLocked($"RUMBLE-CHANGE | controller={key} | left={leftHeavySlowMotor} right={rightLightFastMotor} | source={source} | audio_source={currentAudioSource} | note=motor vibration; if audio_source is a game, audio-haptics from that game may also be active at the same time, see AUDIO-SOURCE / AUDIO-HAPTICS lines");
                        lastByController[key] = (leftHeavySlowMotor, rightLightFastMotor, now, source);
                        return;
                    }
                    if (isZero)
                    {
                        // First ever call, already off: record state silently, no log line.
                        lastByController[key] = (0, 0, now, "none");
                        return;
                    }
                    string startSource = ClassifySource();
                    WriteLocked($"RUMBLE-START | controller={key} | left={leftHeavySlowMotor} right={rightLightFastMotor} | source={startSource} | audio_source={currentAudioSource} | note=motor vibration; if audio_source is a game, audio-haptics from that game may also be active at the same time, see AUDIO-SOURCE / AUDIO-HAPTICS lines");
                    lastByController[key] = (leftHeavySlowMotor, rightLightFastMotor, now, startSource);
                }
            }
            catch { /* Logger must never break DS4. */ }
        }

        private static void EnsureSessionLocked()
        {
            if (sessionLogged) return;
            sessionLogged = true;
            WriteLocked("SESSION-START | rumble logger started | format: timestamp | EVENT | controller=MAC | left=0-255 right=0-255 | source | game");
        }

        private static void WriteLocked(string line)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + line + Environment.NewLine);
            }
            catch { /* Logger must never break DS4. */ }
        }

        private static string ClassifySource()
        {
            try
            {
                string stack = new StackTrace(2, false).ToString();
                if (stack.Contains("ViiperOutDevice") || stack.Contains("SetDevRumble"))
                    return "game-passthrough VIIPER/XInput (forwarded, game sent it)";
                if (stack.Contains("AudioHaptics"))
                    return "audio-haptics";
                if (stack.Contains("Mapping"))
                    return "macro/special-action";
                if (stack.Contains("SetRumblePreview"))
                    return "rumble-preview (DS4 settings test)";
                if (stack.Contains("ControlService"))
                    return "DS4-control";
                return "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
