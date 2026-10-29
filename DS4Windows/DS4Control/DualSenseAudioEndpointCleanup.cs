/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DS4Windows
{
    // 2026-10-29: Every profile switch and every DS4 restart stops and
    // restarts the VIIPER virtual DualSense device. Windows then sees a
    // new virtual USB instance and numbers its new audio endpoints
    // "2- DualSense Wireless Controller", then "3- ...", because the
    // old endpoints remain registered as disconnected.
    //
    // This helper removes ONLY disconnected DualSense audio endpoints,
    // via pnputil, following the same precedent as the Steam Input
    // reclaim in ControlService (admin check, hidden pnputil process,
    // 15 second timeout). It never removes an active endpoint, so a
    // physical controller that is currently connected is never touched,
    // and it never removes a non-DualSense endpoint.
    //
    // Callers run it synchronously on purpose: on startup it must finish
    // before the first VIIPER device is created, and on VIIPER detach
    // it must finish before the next plug, or the new endpoints would
    // be numbered again before the cleanup ran. It is best effort and
    // never throws, so a pnputil failure can never break a profile
    // switch or a startup.
    internal static class DualSenseAudioEndpointCleanup
    {
        public static void RemoveDisconnectedDualSenseAudioEndpoints(string trigger)
        {
            try
            {
                if (!Global.IsAdministrator())
                {
                    AppLogger.LogToGui(
                        $"DualSense audio cleanup ({trigger}) skipped: " +
                        "DS4Windows is not running as administrator.",
                        true);
                    return;
                }

                List<string> instanceIds =
                    EnumerateDisconnectedDualSenseAudioEndpoints(trigger);
                if (instanceIds.Count == 0)
                {
                    AppLogger.LogToGui(
                        $"DualSense audio cleanup ({trigger}): no " +
                        "disconnected DualSense audio endpoints to remove.",
                        false);
                    return;
                }

                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}): removing " +
                    $"{instanceIds.Count} disconnected DualSense audio " +
                    "endpoint(s).",
                    false);
                foreach (string instanceId in instanceIds)
                {
                    RemoveEndpoint(instanceId, trigger);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) failed: " +
                    ex.Message,
                    true);
            }
        }

        private static List<string> EnumerateDisconnectedDualSenseAudioEndpoints(
            string trigger)
        {
            List<string> instanceIds = new List<string>();
            try
            {
                string output = RunPnputil(
                    new[] { "/enum-devices", "/disconnected", "/class", "AudioEndpoint" },
                    trigger);
                if (string.IsNullOrWhiteSpace(output))
                {
                    return instanceIds;
                }

                // pnputil prints one block per device:
                //   Instance ID:                SWD\MMDEVAPI\...
                //   Device Description:         Speakers (2- DualSense ...)
                //   Class Name:                 AudioEndpoint
                // Blocks are separated by blank lines. Only the
                // /disconnected enumeration is used here, so every
                // block in this output is already a disconnected
                // endpoint; active endpoints are never listed.
                string currentInstanceId = null;
                string currentDescription = null;
                string currentClass = null;
                void FlushBlock()
                {
                    if (!string.IsNullOrWhiteSpace(currentInstanceId) &&
                        !string.IsNullOrWhiteSpace(currentDescription) &&
                        string.Equals(currentClass, "AudioEndpoint",
                            StringComparison.OrdinalIgnoreCase) &&
                        currentDescription.IndexOf("DualSense",
                            StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !instanceIds.Contains(currentInstanceId))
                    {
                        instanceIds.Add(currentInstanceId);
                    }
                    currentInstanceId = null;
                    currentDescription = null;
                    currentClass = null;
                }

                foreach (string rawLine in
                    output.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        FlushBlock();
                        continue;
                    }
                    if (line.StartsWith("Instance ID:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        currentInstanceId = ValueAfterColon(line);
                    }
                    else if (line.StartsWith("Device Description:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        currentDescription = ValueAfterColon(line);
                    }
                    else if (line.StartsWith("Class Name:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        currentClass = ValueAfterColon(line);
                    }
                }
                FlushBlock();
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) could not list " +
                    $"disconnected audio endpoints: {ex.Message}",
                    true);
            }
            return instanceIds;
        }

        private static void RemoveEndpoint(string instanceId, string trigger)
        {
            try
            {
                string output = RunPnputil(
                    new[] { "/remove-device", instanceId },
                    trigger);
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}): removed " +
                    $"disconnected endpoint {instanceId}.",
                    false);
                _ = output;
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) could not " +
                    $"remove endpoint {instanceId}: {ex.Message}",
                    true);
            }
        }

        private static string ValueAfterColon(string line)
        {
            int index = line.IndexOf(':');
            return index < 0 ? string.Empty : line.Substring(index + 1).Trim();
        }

        private static string RunPnputil(string[] args, string trigger)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory,
                    "pnputil.exe"),
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException(
                    "pnputil.exe could not be started.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(15000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"pnputil.exe timed out after 15 seconds ({trigger}).");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ?
                    output : error;
                throw new InvalidOperationException(
                    $"pnputil.exe exited with code {process.ExitCode}: " +
                    detail.Trim());
            }
            return output;
        }
    }
}
