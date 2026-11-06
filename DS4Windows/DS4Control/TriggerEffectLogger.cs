// TriggerEffectLogger: DS4 custom build addition, 2026-11-04.
// Logs adaptive trigger effect changes, and every trigger clear,
// to a separate readable file, like the rumble logger.
// It never causes a trigger effect and never changes trigger values:
// callers pass the values they were already sending, and this class
// only records them. Game native trigger modes that loop on the
// DualSense hardware (Vibrate, Choppy, Machine, AutomaticGun,
// Galloping, Pulse) keep running until a None effect arrives, exactly
// like a rainbow light bar keeps animating until its mode changes,
// so a looping effect with no fresh game frame is only visible here.
using System;
using System.IO;
using System.Text;

namespace DS4Windows
{
    public static class TriggerEffectLogger
    {
        private static readonly object logLock = new object();
        private static string logPath;
        private static bool sessionLogged;
        private static string lastDedupKey = "";
        private static DateTime lastDedupUtc = DateTime.MinValue;

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
                    logPath = Path.Combine(dir, "trigger_effect_log.txt");
                }
                catch
                {
                    try
                    {
                        logPath = Path.Combine(
                            AppDomain.CurrentDomain.BaseDirectory, "trigger_effect_log.txt");
                    }
                    catch
                    {
                        logPath = "trigger_effect_log.txt";
                    }
                }
                return logPath;
            }
        }

        private static void EnsureSessionLocked()
        {
            if (sessionLogged) return;
            sessionLogged = true;
            WriteLocked("SESSION-START | note=adaptive trigger effect log, one line per trigger change or clear, looping game modes are visible here");
        }

        private static void WriteLocked(string line)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine); } catch { }
        }

        private static string FormatRaw(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return "-";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                if (i > 0) sb.Append('-');
                sb.Append(raw[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public static void LogEffect(string source, string trigger, byte mode, byte[] raw, string note)
        {
            try
            {
                lock (logLock)
                {
                    // Viiper logs a game effect and DualSenseDevice logs the
                    // same raw effect a millisecond later. Collapse that
                    // duplicate, by trigger and mode, inside 200 ms.
                    string key = trigger + "|" + mode;
                    DateTime now = DateTime.UtcNow;
                    if (key == lastDedupKey && (now - lastDedupUtc).TotalMilliseconds < 200) return;
                    lastDedupKey = key;
                    lastDedupUtc = now;
                    EnsureSessionLocked();
                    WriteLocked("TRIGGER | source=" + source + " | trigger=" + trigger + " | mode=0x" + mode.ToString("X2") + " | raw=" + FormatRaw(raw) + " | note=" + note);
                }
            }
            catch { /* Logger must never break DS4. */ }
        }

        public static void LogClear(string reason)
        {
            try
            {
                lock (logLock)
                {
                    EnsureSessionLocked();
                    lastDedupKey = "";
                    WriteLocked("TRIGGER-CLEAR | reason=" + reason + " | note=None sent to both triggers once, last effect reset to zero");
                }
            }
            catch { /* Logger must never break DS4. */ }
        }

        public static void LogNote(string note)
        {
            try
            {
                lock (logLock)
                {
                    EnsureSessionLocked();
                    WriteLocked("TRIGGER-NOTE | note=" + note);
                }
            }
            catch { /* Logger must never break DS4. */ }
        }
    }
}
