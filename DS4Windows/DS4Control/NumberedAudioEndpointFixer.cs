/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    /// <summary>
    /// Automatic numbered audio endpoint fixer, 2026-11-02, Chuck's request.
    ///
    /// VIIPER itself is reset to upstream logic in this build: it creates
    /// a new bus/device on each connect, with no memory of a previous
    /// device. That is intentional. This fixer does NOT remember a
    /// device, it does NOT cache a bus/dev/port, and it does NOT try
    /// to prevent a new device. Instead, every time DS4 starts and
    /// every time a controller connects, it scans what the audio
    /// endpoints will be, and if any Sony endpoint is numbered
    /// ("1 - ", "2 - ", "3 - ", "4 - ", "5 - ", in general any
    /// "<number> - " prefix), it fixes it automatically.
    ///
    /// Why numbering happens, verified on Chuck's PC:
    /// A previous virtual Sony audio device leaves its endpoints
    /// registered as Disconnected/Unknown, e.g.
    ///   Speakers (DualSense Wireless Controller) - Disconnected
    ///   Headset Microphone (DualSense Wireless Controller) - Disconnected
    /// Windows will not reuse a friendly name that is still reserved,
    /// so the next device is named
    ///   Speakers (2- DualSense Wireless Controller)
    ///   Headset Microphone (2- DualSense Wireless Controller)
    /// and later devices become "3- ", "4- ", and so on. The old
    /// cleanup only looked at pnputil /disconnected, which misses
    /// some of these ghosts, and only at DualSense PID 0CE6.
    ///
    /// Fix, in order, general for any controller/profile/PID/number:
    /// 1. Pre-creation scan: remove every non-active Sony audio
    ///    endpoint ghost, via pnputil /remove-device on its
    ///    SWD\MMDEVAPI\{GUID} instance, plus the existing
    ///    DualSenseAudioEndpointCleanup, which removes non-present
    ///    Sony USB audio devices via SetupAPI DIF_REMOVE. This
    ///    frees the plain name before the next device is created.
    /// 2. Post-connect scan: if a numbered Sony endpoint is active,
    ///    first remove non-active Sony ghosts as in step 1, then
    ///    re-enumerate its parent Sony USB audio function device
    ///    only (VID_054C, any PID including 0CE6 DualSense and
    ///    05C4 DualShock, MI_00, the audio function, never the
    ///    gamepad/HID interface) via pnputil /disable-device then
    ///    /enable-device, once per connect per endpoint, wait,
    ///    then re-scan and log plain / still numbered / could not fix.
    /// 3. Never remove an active endpoint outright, never touch a
    ///    non-Sony device (Arctis, Sonar, Voicemeeter, Steam,
    ///    Virtual Desktop, etc.), never touch Bluetooth BTHENUM
    ///    devices, never throw, never block input. If re-enumeration
    ///    fails, the device is left working, numbered, and logged.
    /// </summary>
    internal static class NumberedAudioEndpointFixer
    {
        private static readonly Regex NumberedInParens =
            new Regex(@"\(\s*\d+\s*-\s*", RegexOptions.Compiled);
        private static readonly Regex NumberedAtStart =
            new Regex(@"^\s*\d+\s*-\s+", RegexOptions.Compiled);

        private sealed class EndpointInfo
        {
            public string InstanceId;
            public string Description;
            public string Status;
        }

        public static void RunPreCreationScan()
        {
            try
            {
                Log("Numbered audio fixer: pre-creation scan, removing non-active Sony audio ghosts so the next device gets a plain name.");
                try { DualSenseAudioEndpointCleanup.CleanupStaleDualSenseAudioDevices(); } catch { }
                int removed = RemoveNonActiveSonyEndpoints();
                Log($"Numbered audio fixer: pre-creation scan removed {removed} non-active Sony audio endpoint ghost(s).");
            }
            catch (Exception ex)
            {
                Log($"Numbered audio fixer: pre-creation scan failed, continuing without it: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void RunDelayedPostConnectScan(string context)
        {
            try
            {
                // Audio endpoints appear a moment after the virtual
                // device is plugged in. Run in the background, never
                // block the connect path.
                Task.Run(() =>
                {
                    try
                    {
                        Thread.Sleep(2000);
                        RunPostConnectScan(context);
                    }
                    catch (Exception ex)
                    {
                        Log($"Numbered audio fixer: delayed post-connect scan failed: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"Numbered audio fixer: could not start delayed scan: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void RunPostConnectScan(string context)
        {
            try
            {
                List<EndpointInfo> before = EnumerateAudioEndpoints();
                List<EndpointInfo> numbered = new List<EndpointInfo>();
                foreach (EndpointInfo ep in before)
                {
                    if (IsSonyName(ep.Description) && IsStarted(ep.Status) && IsNumbered(ep.Description))
                    {
                        numbered.Add(ep);
                        Log($"Numbered audio fixer [{context}]: found numbered Sony endpoint: {ep.Description} ({ep.InstanceId}, {ep.Status})");
                    }
                }
                if (numbered.Count == 0)
                {
                    Log($"Numbered audio fixer [{context}]: no numbered Sony audio endpoints found.");
                    return;
                }

                // Step 1: free the plain name by removing ghosts.
                int ghosts = RemoveNonActiveSonyEndpoints();
                Log($"Numbered audio fixer [{context}]: removed {ghosts} non-active Sony audio ghost(s) before re-enumeration.");
                Thread.Sleep(1000);

                // Step 2: re-enumerate the parent Sony USB audio
                // function device(s), once, audio function only.
                List<string> parents = EnumeratePresentSonyUsbAudioDevices();
                if (parents.Count == 0)
                {
                    Log($"Numbered audio fixer [{context}]: no present Sony USB audio device found to re-enumerate, leaving numbered endpoint(s) working.");
                }
                else
                {
                    foreach (string parent in parents)
                    {
                        try
                        {
                            RunPnputil(new[] { "/disable-device", parent });
                            Thread.Sleep(1000);
                            RunPnputil(new[] { "/enable-device", parent });
                            Log($"Numbered audio fixer [{context}]: re-enumerated Sony USB audio device {parent}.");
                        }
                        catch (Exception ex)
                        {
                            Log($"Numbered audio fixer [{context}]: could not re-enumerate {parent}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    Thread.Sleep(3000);
                }

                // Step 3: re-scan and report.
                List<EndpointInfo> after = EnumerateAudioEndpoints();
                foreach (EndpointInfo old in numbered)
                {
                    bool stillNumbered = false;
                    bool plainFound = false;
                    foreach (EndpointInfo ep in after)
                    {
                        if (string.Equals(ep.InstanceId, old.InstanceId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (IsNumbered(ep.Description)) { stillNumbered = true; }
                        }
                        string stripped = StripNumberPrefix(ep.Description);
                        string oldStripped = StripNumberPrefix(old.Description);
                        if (IsStarted(ep.Status) && string.Equals(stripped, oldStripped, StringComparison.OrdinalIgnoreCase) && !IsNumbered(ep.Description))
                        {
                            plainFound = true;
                        }
                    }
                    if (plainFound && !stillNumbered)
                    {
                        Log($"Numbered audio fixer [{context}]: fixed to plain name: {StripNumberPrefix(old.Description)} (was {old.Description})");
                    }
                    else if (stillNumbered)
                    {
                        Log($"Numbered audio fixer [{context}]: still numbered after re-enumeration, left working: {old.Description} ({old.InstanceId})");
                    }
                    else
                    {
                        Log($"Numbered audio fixer [{context}]: could not confirm fix, endpoint changed or disappeared, left working: was {old.Description} ({old.InstanceId})");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Numbered audio fixer [{context}]: post-connect scan failed, continuing without it: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static int RemoveNonActiveSonyEndpoints()
        {
            int removed = 0;
            try
            {
                List<EndpointInfo> all = EnumerateAudioEndpoints();
                foreach (EndpointInfo ep in all)
                {
                    if (!IsSonyName(ep.Description)) { continue; }
                    if (IsStarted(ep.Status)) { continue; }
                    try
                    {
                        RunPnputil(new[] { "/remove-device", ep.InstanceId });
                        removed++;
                        Log($"Numbered audio fixer: removed non-active Sony audio endpoint ghost: {ep.Description} ({ep.InstanceId}, {ep.Status})");
                    }
                    catch (Exception ex)
                    {
                        Log($"Numbered audio fixer: could not remove ghost {ep.Description} ({ep.InstanceId}): {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Numbered audio fixer: ghost removal failed: {ex.GetType().Name}: {ex.Message}");
            }
            return removed;
        }

        private static List<EndpointInfo> EnumerateAudioEndpoints()
        {
            List<EndpointInfo> result = new List<EndpointInfo>();
            try
            {
                string output = RunPnputil(new[] { "/enum-devices", "/class", "AudioEndpoint" });
                if (string.IsNullOrWhiteSpace(output)) { return result; }
                string currentId = null;
                string currentDesc = null;
                string currentStatus = null;
                void Flush()
                {
                    if (!string.IsNullOrWhiteSpace(currentId) && !string.IsNullOrWhiteSpace(currentDesc))
                    {
                        result.Add(new EndpointInfo { InstanceId = currentId, Description = currentDesc, Status = currentStatus ?? string.Empty });
                    }
                    currentId = null; currentDesc = null; currentStatus = null;
                }
                foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
                    if (line.StartsWith("Instance ID:", StringComparison.OrdinalIgnoreCase)) { currentId = ValueAfterColon(line); }
                    else if (line.StartsWith("Device Description:", StringComparison.OrdinalIgnoreCase)) { currentDesc = ValueAfterColon(line); }
                    else if (line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase)) { currentStatus = ValueAfterColon(line); }
                }
                Flush();
            }
            catch (Exception ex)
            {
                Log($"Numbered audio fixer: endpoint enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }

        private static List<string> EnumeratePresentSonyUsbAudioDevices()
        {
            List<string> result = new List<string>();
            try
            {
                string output = RunPnputil(new[] { "/enum-devices", "/connected", "/class", "USB" });
                if (string.IsNullOrWhiteSpace(output)) { return result; }
                string currentId = null;
                void Flush()
                {
                    if (!string.IsNullOrWhiteSpace(currentId) &&
                        currentId.StartsWith(@"USB\VID_054C&", StringComparison.OrdinalIgnoreCase) &&
                        currentId.IndexOf("&MI_00", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !result.Contains(currentId))
                    {
                        result.Add(currentId);
                    }
                    currentId = null;
                }
                foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
                    if (line.StartsWith("Instance ID:", StringComparison.OrdinalIgnoreCase)) { currentId = ValueAfterColon(line); }
                }
                Flush();
            }
            catch (Exception ex)
            {
                Log($"Numbered audio fixer: Sony USB enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }

        private static bool IsSonyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return false; }
            string stripped = StripNumberPrefix(name);
            if (stripped.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            if (stripped.IndexOf("DualShock", StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            // Sony's generic name for DualShock/DualSense audio, as
            // shown in Windows, e.g. "Headset Microphone (Wireless Controller)".
            // Xbox devices say "Xbox Wireless Controller", so exclude those.
            if (stripped.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0 &&
                stripped.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) < 0) { return true; }
            return false;
        }

        private static bool IsNumbered(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return false; }
            return NumberedInParens.IsMatch(name) || NumberedAtStart.IsMatch(name);
        }

        private static string StripNumberPrefix(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return name; }
            string result = NumberedInParens.Replace(name, "(");
            result = NumberedAtStart.Replace(result, string.Empty);
            return result;
        }

        private static bool IsStarted(string status)
        {
            return !string.IsNullOrWhiteSpace(status) &&
                status.IndexOf("Started", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ValueAfterColon(string line)
        {
            int index = line.IndexOf(':');
            return index < 0 ? string.Empty : line.Substring(index + 1).Trim();
        }

        private static string RunPnputil(string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args) { startInfo.ArgumentList.Add(arg); }
            using Process process = Process.Start(startInfo);
            if (process == null) { throw new InvalidOperationException("pnputil.exe could not be started."); }
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(15000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("pnputil.exe timed out after 15 seconds.");
            }
            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException($"pnputil.exe exited with code {process.ExitCode}: {detail.Trim()}");
            }
            return output;
        }

        private static void Log(string message)
        {
            try { AppLogger.LogToGui(message, false); } catch { }
        }
    }
}
