/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Drawing;
using static System.Math;
using static DS4Windows.Global;
using System.Diagnostics;
using DS4WinWPF.DS4Control;

namespace DS4Windows
{
    public class DS4LightBar
    {
        private readonly static byte[/* Light On duration */, /* Light Off duration */] BatteryIndicatorDurations =
        {
            { 28, 252 }, // on 10% of the time at 0
            { 28, 252 },
            { 56, 224 },
            { 84, 196 },
            { 112, 168 },
            { 140, 140 },
            { 168, 112 },
            { 196, 84 },
            { 224, 56 }, // on 80% of the time at 80, etc.
            { 252, 28 }, // on 90% of the time at 90
            { 0, 0 }     // use on 100%. 0 is for "charging" OR anything sufficiently-"charged"
        };

        static double[] counters = new double[Global.MAX_DS4_CONTROLLER_COUNT] { 0, 0, 0, 0, 0, 0, 0, 0 };
        public static Stopwatch[] fadewatches = new Stopwatch[Global.MAX_DS4_CONTROLLER_COUNT]
        {
            new Stopwatch(), new Stopwatch(), new Stopwatch(), new Stopwatch(),
            new Stopwatch(), new Stopwatch(), new Stopwatch(), new Stopwatch(),
        };

        static bool[] fadedirection = new bool[Global.MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        static DateTime[] oldnow = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT]
        {
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
        };

        public static bool[] forcelight = new bool[Global.MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        public static DS4Color[] forcedColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        // 2026-10-31 battery hold, enforced here every tick: immediate
        // battery color for 3 seconds, then normal logic (original reset).
        public static DateTime[] BatteryHoldUntil = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        public static DS4Color[] BatteryHoldColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        // 2026-11-01 connect force: first tick after a gap (controller
        // just turned back on) forces pure custom/profile color for
        // 3 seconds, no battery/flash/charging/rainbow intermediate,
        // so no yellow step sticks on connect.
        public static DateTime[] ConnectHoldUntil = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        public static DS4Color[] ConnectHoldColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DateTime[] lastLightBarTick = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Device[] lastLightBarDevice = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool[] connectFadeRequested = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        // 2026-11-01 fade: color changes fade in/out over 600 ms instead
        // of snapping, including blue firmware to profile on connect.
        private static DS4Color[] fadeFromColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] fadeToColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] lastSentColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool[] lastSentValid = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DateTime[] fadeStart = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool[] fading = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        // 2026-11-03 battery fade-out only: when a battery hold ends,
        // fade explicitly from the battery color to the actual profile
        // color (custom LED if set, else main profile color), never back
        // to whatever color happened to be before. Connect fade-in,
        // battery fade-in, and all other logic are untouched.
        private static bool[] batteryHoldWasActive = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool[] batteryFadingOut = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DateTime[] batteryFadeOutGraceUntil = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];

        // 2026-11-03 vibration + fade-out fix: a battery fade-out is a
        // lightbar hold for transport purposes. The previous fade-out
        // only changed the color in software; DualSenseDevice gated
        // its direct BT control send on BatteryHoldUntil, which is
        // already MinValue during fade-out, so the interpolated colors
        // and the final profile color were left to the speaker-clocked
        // path, which stalls when the VIIPER audio stream is waiting,
        // and the BT cache kept the stale battery color. This accessor
        // lets DualSenseDevice treat fade-out (plus a short grace after
        // it, so the final profile color lands) as hold-active.
        public static bool IsBatteryFadeOutActive(int deviceNum)
        {
            if (deviceNum < 0 || deviceNum >= Global.MAX_DS4_CONTROLLER_COUNT)
                return false;
            return batteryFadingOut[deviceNum] ||
                (batteryFadeOutGraceUntil[deviceNum] != DateTime.MinValue &&
                 DateTime.UtcNow < batteryFadeOutGraceUntil[deviceNum]);
        }
        // 2026-11-03 connect snap fix: DualSenseDevice calls this
        // when a connect-hold physical send was deferred (BT audio
        // pacer helper restarting at connect) and then succeeds.
        // The software 600 ms fade had already expired by then, so
        // without a replay the controller only ever receives the
        // final color, i.e. a snap. This flag makes the next
        // updateLightBar tick start a fresh fade from black, with
        // a fresh ConnectHold, exactly like a fresh connect.
        // Battery check logic is untouched; this is connect only.
        public static void RequestConnectFade(int deviceNum)
        {
            if (deviceNum < 0 || deviceNum >= Global.MAX_DS4_CONTROLLER_COUNT)
                return;
            connectFadeRequested[deviceNum] = true;
        }
        private static DateTime[] batteryFadeOutStart = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] batteryFadeOutFrom = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] batteryFadeOutTo = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private const double FadeDurationMs = 600.0;
        public static byte[] forcedFlash = new byte[Global.MAX_DS4_CONTROLLER_COUNT];
        internal const int PULSE_FLASH_DURATION = 2000;
        internal const double PULSE_FLASH_SEGMENTS = PULSE_FLASH_DURATION / 40;
        internal const int PULSE_CHARGING_DURATION = 4000;
        internal const double PULSE_CHARGING_SEGMENTS = (PULSE_CHARGING_DURATION / 40) - 2;

        public static void updateLightBar(DS4Device device, int deviceNum)
        {
            DateTime tickNow = DateTime.UtcNow;
            // 2026-11-03 connect snap fix: a time gap alone is not
            // a reliable fresh-connect signal. A quick off/on
            // can reconnect in under 5 seconds, and the target
            // then equals the last sent color, so the generic
            // fade never triggers and the first output is the
            // full color, a snap. A new device object for the
            // slot is a real connect (a profile switch never
            // changes the device object), and a replay request
            // from DualSenseDevice means transport deferred
            // the original fade. Either counts as fresh here.
            bool deviceChanged = !ReferenceEquals(lastLightBarDevice[deviceNum], device);
            lastLightBarDevice[deviceNum] = device;
            bool connectFadeDue = connectFadeRequested[deviceNum];
            connectFadeRequested[deviceNum] = false;
            bool isFreshConnect = lastLightBarTick[deviceNum] == DateTime.MinValue ||
                (tickNow - lastLightBarTick[deviceNum]) > TimeSpan.FromSeconds(5) ||
                deviceChanged || connectFadeDue;
            lastLightBarTick[deviceNum] = tickNow;
            DS4Color color = new DS4Color();
            bool useForceLight = forcelight[deviceNum];
            LightbarSettingInfo lightbarSettingInfo = getLightbarSettingsInfo(deviceNum);
            LightbarDS4WinInfo lightModeInfo = lightbarSettingInfo.ds4winSettings;
            bool useLightRoutine = lightbarSettingInfo.mode == LightbarMode.DS4Win;
            //bool useLightRoutine = false;
            if (!defaultLight && !useForceLight && useLightRoutine)
            {
                if (lightModeInfo.useCustomLed)
                {
                    color = lightModeInfo.m_CustomLed; //getCustomColor(deviceNum);
                }
                else
                {
                    double rainbow = lightModeInfo.rainbow;// getRainbow(deviceNum);
                    if (rainbow > 0)
                    {
                        // Display rainbow
                        DateTime now = DateTime.UtcNow;
                        if (now >= oldnow[deviceNum] + TimeSpan.FromMilliseconds(10)) //update by the millisecond that way it's a smooth transtion
                        {
                            int diffMs = now.Subtract(oldnow[deviceNum]).Milliseconds;
                            oldnow[deviceNum] = now;
                            if (device.isCharging())
                                //counters[deviceNum] -= 1.5 * 3 / rainbow;
                                counters[deviceNum] -= 360.0 * (diffMs / 1000.0 / rainbow);
                            else
                                //counters[deviceNum] += 1.5 * 3 / rainbow;
                                counters[deviceNum] += 360.0 * (diffMs / 1000.0 / rainbow);
                        }

                        if (counters[deviceNum] < 0)
                            counters[deviceNum] = 180000;
                        else if (counters[deviceNum] > 180000)
                            counters[deviceNum] = 0;

                        double maxSat = lightModeInfo.maxRainbowSat; // GetMaxSatRainbow(deviceNum);
                        if (lightModeInfo.ledAsBattery)
                        {
                            byte useSat = (byte)(maxSat == 1.0 ?
                                device.getBattery() * 2.55 :
                                device.getBattery() * 2.55 * maxSat);
                            color = HuetoRGB((float)counters[deviceNum] % 360, useSat);
                        }
                        else
                            color = HuetoRGB((float)counters[deviceNum] % 360,
                                (byte)(maxSat == 1.0 ? 255 : 255 * maxSat));

                    }
                    else if (lightModeInfo.ledAsBattery)
                    {
                        ref DS4Color fullColor = ref lightModeInfo.m_Led; //ref getMainColor(deviceNum);
                        ref DS4Color lowColor = ref lightModeInfo.m_LowLed; //ref getLowColor(deviceNum);
                        color = getTransitionedColor(ref lowColor, ref fullColor, device.getBattery());
                    }
                    else
                    {
                        color = getMainColor(deviceNum);
                    }
                }

                if (device.getBattery() <= lightModeInfo.flashAt && !defaultLight && !device.isCharging())
                {
                    ref DS4Color flashColor = ref lightModeInfo.m_FlashLed; //ref getFlashColor(deviceNum);
                    if (!(flashColor.red == 0 &&
                        flashColor.green == 0 &&
                        flashColor.blue == 0))
                        color = flashColor;

                    if (lightModeInfo.flashType == 1)
                    {
                        double ratio = 0.0;

                        if (!fadewatches[deviceNum].IsRunning)
                        {
                            bool temp = fadedirection[deviceNum];
                            fadedirection[deviceNum] = !temp;
                            fadewatches[deviceNum].Restart();
                            ratio = temp ? 100.0 : 0.0;
                        }
                        else
                        {
                            long elapsed = fadewatches[deviceNum].ElapsedMilliseconds;

                            if (fadedirection[deviceNum])
                            {
                                if (elapsed < PULSE_FLASH_DURATION)
                                {
                                    elapsed = elapsed / 40;
                                    ratio = 100.0 * (elapsed / PULSE_FLASH_SEGMENTS);
                                }
                                else
                                {
                                    ratio = 100.0;
                                    fadewatches[deviceNum].Stop();
                                }
                            }
                            else
                            {
                                if (elapsed < PULSE_FLASH_DURATION)
                                {
                                    elapsed = elapsed / 40;
                                    ratio = (0 - 100.0) * (elapsed / PULSE_FLASH_SEGMENTS) + 100.0;
                                }
                                else
                                {
                                    ratio = 0.0;
                                    fadewatches[deviceNum].Stop();
                                }
                            }
                        }

                        DS4Color tempCol = new DS4Color(0, 0, 0);
                        color = getTransitionedColor(ref color, ref tempCol, ratio);
                    }
                }

                int idleDisconnectTimeout = getIdleDisconnectTimeout(deviceNum);
                if (idleDisconnectTimeout > 0 && lightModeInfo.ledAsBattery &&
                    (!device.isCharging() || device.getBattery() >= 100))
                {
                    // Fade lightbar by idle time
                    TimeSpan timeratio = new TimeSpan(DateTime.UtcNow.Ticks - device.lastActive.Ticks);
                    double botratio = timeratio.TotalMilliseconds;
                    double topratio = TimeSpan.FromSeconds(idleDisconnectTimeout).TotalMilliseconds;
                    double ratio = 100.0 * (botratio / topratio), elapsed = ratio;
                    if (ratio >= 50.0 && ratio < 100.0)
                    {
                        DS4Color emptyCol = new DS4Color(0, 0, 0);
                        color = getTransitionedColor(ref color, ref emptyCol,
                            (uint)(-100.0 * (elapsed = 0.02 * (ratio - 50.0)) * (elapsed - 2.0)));
                    }
                    else if (ratio >= 100.0)
                    {
                        DS4Color emptyCol = new DS4Color(0, 0, 0);
                        color = getTransitionedColor(ref color, ref emptyCol, 100.0);
                    }
                        
                }

                if (device.isCharging() && device.getBattery() < 100)
                {
                    switch (lightModeInfo.chargingType)
                    {
                        case 1:
                        {
                            double ratio = 0.0;

                            if (!fadewatches[deviceNum].IsRunning)
                            {
                                bool temp = fadedirection[deviceNum];
                                fadedirection[deviceNum] = !temp;
                                fadewatches[deviceNum].Restart();
                                ratio = temp ? 100.0 : 0.0;
                            }
                            else
                            {
                                long elapsed = fadewatches[deviceNum].ElapsedMilliseconds;

                                if (fadedirection[deviceNum])
                                {
                                    if (elapsed < PULSE_CHARGING_DURATION)
                                    {
                                        elapsed = elapsed / 40;
                                        if (elapsed > PULSE_CHARGING_SEGMENTS)
                                            elapsed = (long)PULSE_CHARGING_SEGMENTS;
                                        ratio = 100.0 * (elapsed / PULSE_CHARGING_SEGMENTS);
                                    }
                                    else
                                    {
                                        ratio = 100.0;
                                        fadewatches[deviceNum].Stop();
                                    }
                                }
                                else
                                {
                                    if (elapsed < PULSE_CHARGING_DURATION)
                                    {
                                        elapsed = elapsed / 40;
                                        if (elapsed > PULSE_CHARGING_SEGMENTS)
                                            elapsed = (long)PULSE_CHARGING_SEGMENTS;
                                        ratio = (0 - 100.0) * (elapsed / PULSE_CHARGING_SEGMENTS) + 100.0;
                                    }
                                    else
                                    {
                                        ratio = 0.0;
                                        fadewatches[deviceNum].Stop();
                                    }
                                }
                            }

                            DS4Color emptyCol = new DS4Color(0, 0, 0);
                            color = getTransitionedColor(ref color, ref emptyCol, ratio);
                            break;
                        }
                        case 2:
                        {
                            counters[deviceNum] += 0.167;
                            color = HuetoRGB((float)counters[deviceNum] % 360, 255);
                            break;
                        }
                        case 3:
                        {
                            color = lightModeInfo.m_ChargingLed; //getChargingColor(deviceNum);
                            break;
                        }
                        default: break;
                    }
                }
            }
            else if (useForceLight)
            {
                color = forcedColor[deviceNum];
                useLightRoutine = true;
            }
            else if (shuttingdown)
            {
                color = new DS4Color(0, 0, 0);
                useLightRoutine = true;
            }
            else if (useLightRoutine)
            {
                if (device.getConnectionType() == ConnectionType.BT)
                    color = new DS4Color(32, 64, 64);
                else
                    color = new DS4Color(0, 0, 0);
            }

            if (useLightRoutine)
            {
                bool distanceprofile = DistanceProfiles[deviceNum] || tempprofileDistance[deviceNum];
                //distanceprofile = (ProfilePath[deviceNum].ToLower().Contains("distance") || tempprofilename[deviceNum].ToLower().Contains("distance"));
                if (distanceprofile && !defaultLight)
                {
                    // Thing I did for Distance
                    float rumble = device.getLeftHeavySlowRumble() / 2.55f;
                    byte max = Max(color.red, Max(color.green, color.blue));
                    if (device.getLeftHeavySlowRumble() > 100)
                    {
                        DS4Color maxCol = new DS4Color(max, max, 0);
                        DS4Color redCol = new DS4Color(255, 0, 0);
                        color = getTransitionedColor(ref maxCol, ref redCol, rumble);
                    }
                    else
                    {
                        DS4Color maxCol = new DS4Color(max, max, 0);
                        DS4Color redCol = new DS4Color(255, 0, 0);
                        DS4Color tempCol = getTransitionedColor(ref maxCol,
                            ref redCol, 39.6078f);
                        color = getTransitionedColor(ref color, ref tempCol,
                            device.getLeftHeavySlowRumble());
                    }
                }

                /*DS4HapticState haptics = new DS4HapticState
                {
                    LightBarColor = color
                };
                */
                if (Global.openRGBSyncEnabled &&
                    OpenRGBServer.Instance.TryGetColor(deviceNum, out DS4Color openRGBColor))
                {
                    color = openRGBColor;
                }

                // Connect hold override: fresh connect, pure custom or
                // profile main color, overriding battery/flash/charging/
                // rainbow intermediates, including any yellow step.
                if (isFreshConnect && !defaultLight && useLightRoutine)
                {
                    ConnectHoldUntil[deviceNum] = tickNow.AddSeconds(1); // final: 1 second, not 3
                    ConnectHoldColor[deviceNum] = lightModeInfo.useCustomLed
                        ? lightModeInfo.m_CustomLed : getMainColor(deviceNum);
                }
                if (ConnectHoldUntil[deviceNum] != DateTime.MinValue)
                {
                    if (DateTime.UtcNow < ConnectHoldUntil[deviceNum])
                    {
                        color = ConnectHoldColor[deviceNum];
                        useLightRoutine = true;
                        useForceLight = false;
                        forcedFlash[deviceNum] = 0;
                        if (device is DS4Windows.InputDevices.DualSenseDevice dsDevice2)
                        {
                            dsDevice2.ForceBatteryLightbarOwnership();
                        }
                    }
                    else
                    {
                        ConnectHoldUntil[deviceNum] = DateTime.MinValue;
                    }
                }

                // Battery hold override, after all other color logic.
                if (BatteryHoldUntil[deviceNum] != DateTime.MinValue)
                {
                    if (DateTime.UtcNow < BatteryHoldUntil[deviceNum])
                    {
                        batteryHoldWasActive[deviceNum] = true;
                        batteryFadingOut[deviceNum] = false;
                        color = BatteryHoldColor[deviceNum];
                        useLightRoutine = true;
                        useForceLight = false;
                        forcedFlash[deviceNum] = 0;
                        // Force over Steam/game native ownership every tick:
                        // a game that re-claims lightbar ownership each frame
                        // would otherwise win over the profile lightbar.
                        if (device is DS4Windows.InputDevices.DualSenseDevice dsDevice)
                        {
                            dsDevice.ForceBatteryLightbarOwnership();
                        }
                    }
                    else
                    {
                        BatteryHoldUntil[deviceNum] = DateTime.MinValue;
                    }
                }

                // Battery fade-out only: start once, on the first tick
                // after the hold ends, whether DS4LightBar or Mapping
                // cleared it. Target is the actual profile/custom color,
                // not the previous color, and a stale forced battery color
                // is cleared here so it cannot reassert the battery color.
                if (batteryHoldWasActive[deviceNum] &&
                    (BatteryHoldUntil[deviceNum] == DateTime.MinValue ||
                     DateTime.UtcNow >= BatteryHoldUntil[deviceNum]))
                {
                    batteryHoldWasActive[deviceNum] = false;
                    BatteryHoldUntil[deviceNum] = DateTime.MinValue;
                    DS4Color fadeOutTarget = lightModeInfo.useCustomLed
                        ? lightModeInfo.m_CustomLed : getMainColor(deviceNum);
                    DS4Color fadeOutFrom = lastSentValid[deviceNum]
                        ? lastSentColor[deviceNum] : BatteryHoldColor[deviceNum];
                    forcelight[deviceNum] = false;
                    if (fadeOutFrom.red != fadeOutTarget.red ||
                        fadeOutFrom.green != fadeOutTarget.green ||
                        fadeOutFrom.blue != fadeOutTarget.blue)
                    {
                        batteryFadeOutFrom[deviceNum] = fadeOutFrom;
                        batteryFadeOutTo[deviceNum] = fadeOutTarget;
                        batteryFadeOutStart[deviceNum] = tickNow;
                        batteryFadingOut[deviceNum] = true;
                        fading[deviceNum] = false;
                    }
                }
                if (batteryFadingOut[deviceNum] && !isFreshConnect)
                {
                    double fadeOutElapsedMs = (tickNow - batteryFadeOutStart[deviceNum]).TotalMilliseconds;
                    double fadeOutRatio = fadeOutElapsedMs / 420.0;
                    if (fadeOutRatio >= 1.0)
                    {
                        color = batteryFadeOutTo[deviceNum];
                        batteryFadingOut[deviceNum] = false;
                        fading[deviceNum] = false;
                        batteryFadeOutGraceUntil[deviceNum] = tickNow.AddMilliseconds(750);
                    }
                    else
                    {
                        if (fadeOutRatio < 0.0) fadeOutRatio = 0.0;
                        DS4Color fadeOutFromC = batteryFadeOutFrom[deviceNum];
                        DS4Color fadeOutToC = batteryFadeOutTo[deviceNum];
                        color = new DS4Color(
                            (byte)(fadeOutFromC.red + (fadeOutToC.red - fadeOutFromC.red) * fadeOutRatio),
                            (byte)(fadeOutFromC.green + (fadeOutToC.green - fadeOutFromC.green) * fadeOutRatio),
                            (byte)(fadeOutFromC.blue + (fadeOutToC.blue - fadeOutFromC.blue) * fadeOutRatio));
                    }
                    useLightRoutine = true;
                    useForceLight = false;
                    forcedFlash[deviceNum] = 0;
                    forcelight[deviceNum] = false;
                    if (device is DS4Windows.InputDevices.DualSenseDevice dsDeviceFadeOut)
                    {
                        dsDeviceFadeOut.ForceBatteryLightbarOwnership();
                    }
                    // Tell the generic fade below this color is already the
                    // interpolated fade-out color, so it does not start a
                    // competing fade on the same tick.
                    lastSentColor[deviceNum] = color;
                    lastSentValid[deviceNum] = true;
                }
                else if (batteryFadingOut[deviceNum] && isFreshConnect)
                {
                    batteryFadingOut[deviceNum] = false;
                }

                // Fade-out grace: after the fade-out ends, keep
                // queuing a physical output for 750 ms so the final
                // profile color is direct-sent, not left to the stalled
                // speaker clock. Normal profile logic already yields
                // that same color, so this changes transport only.
                if (!batteryFadingOut[deviceNum] &&
                    batteryFadeOutGraceUntil[deviceNum] != DateTime.MinValue)
                {
                    if (tickNow < batteryFadeOutGraceUntil[deviceNum])
                    {
                        if (device is DS4Windows.InputDevices.DualSenseDevice dsDeviceGrace)
                        {
                            dsDeviceGrace.ForceBatteryLightbarOwnership();
                        }
                    }
                    else
                    {
                        batteryFadeOutGraceUntil[deviceNum] = DateTime.MinValue;
                    }
                }

                // Fade in/out: smooth any color change over 600 ms.
                // Skip for rainbow, pulse flash, charging pulse/rainbow,
                // distance rumble, and forced picker preview, which already
                // animate or need live response, and skip on shutdown.
                bool skipFade = shuttingdown || useForceLight ||
                    lightModeInfo.rainbow > 0 ||
                    (lightModeInfo.flashType == 1 && device.getBattery() <= lightModeInfo.flashAt && !device.isCharging()) ||
                    (device.isCharging() && (lightModeInfo.chargingType == 1 || lightModeInfo.chargingType == 2)) ||
                    (DistanceProfiles[deviceNum] || tempprofileDistance[deviceNum]);
                if (!skipFade)
                {
                    if (isFreshConnect)
                    {
                        // 2026-11-03 connect fade fix, connect logic only:
                        // lastSentValid and fading are static and survive a
                        // controller disconnect in the same DS4 process, and
                        // the disconnect tick leaves a fade toward black
                        // behind. The old gate (isFreshConnect &&
                        // !lastSentValid) therefore skipped the connect fade
                        // on every reconnect after the first DS4 start, and
                        // the first connect tick could instead complete the
                        // stale disconnect fade to black. A profile switch,
                        // by contrast, changes color, so it still triggered
                        // the generic fade, imperfectly, from the stale
                        // last-sent color. Force a fresh fade on every fresh
                        // connect, from black to the connect-hold color.
                        // Final 2026-11-01: do NOT set DS4 blue on connect.
                        // Firmware blue fades out on its own, DS4 fades
                        // in from black only, then to profile/custom.
                        // Clear only stale battery fade-out transport state
                        // left from the previous session; battery check
                        // trigger, hold, fade-in, and fade-out code itself
                        // is untouched, and ConnectHold still makes this a
                        // direct-sent hold for transport.
                        batteryFadeOutGraceUntil[deviceNum] = DateTime.MinValue;
                        fadeFromColor[deviceNum] = new DS4Color(0, 0, 0);
                        fadeToColor[deviceNum] = color;
                        fadeStart[deviceNum] = tickNow;
                        fading[deviceNum] = true;
                        lastSentColor[deviceNum] = fadeFromColor[deviceNum];
                        lastSentValid[deviceNum] = true;
                    }
                    else if (lastSentValid[deviceNum] && (color.red != lastSentColor[deviceNum].red || color.green != lastSentColor[deviceNum].green || color.blue != lastSentColor[deviceNum].blue))
                    {
                        if (!fading[deviceNum] || fadeToColor[deviceNum].red != color.red || fadeToColor[deviceNum].green != color.green || fadeToColor[deviceNum].blue != color.blue)
                        {
                            fadeFromColor[deviceNum] = lastSentColor[deviceNum];
                            fadeToColor[deviceNum] = color;
                            fadeStart[deviceNum] = tickNow;
                            fading[deviceNum] = true;
                        }
                    }
                    if (fading[deviceNum])
                    {
                        double elapsedMs = (tickNow - fadeStart[deviceNum]).TotalMilliseconds;
                        // Fade in 600 ms, fade out 420 ms (0.7x), by direction.
                        int fadeFromSum = fadeFromColor[deviceNum].red + fadeFromColor[deviceNum].green + fadeFromColor[deviceNum].blue;
                        int fadeToSum = fadeToColor[deviceNum].red + fadeToColor[deviceNum].green + fadeToColor[deviceNum].blue;
                        double fadeDurationMs = fadeToSum >= fadeFromSum ? FadeDurationMs : 420.0;
                        double ratio = elapsedMs / fadeDurationMs;
                        if (ratio >= 1.0)
                        {
                            color = fadeToColor[deviceNum];
                            fading[deviceNum] = false;
                        }
                        else
                        {
                            if (ratio < 0.0) ratio = 0.0;
                            DS4Color fromC = fadeFromColor[deviceNum];
                            DS4Color toC = fadeToColor[deviceNum];
                            color = new DS4Color(
                                (byte)(fromC.red + (toC.red - fromC.red) * ratio),
                                (byte)(fromC.green + (toC.green - fromC.green) * ratio),
                                (byte)(fromC.blue + (toC.blue - fromC.blue) * ratio));
                        }
                    }
                    lastSentColor[deviceNum] = color;
                    lastSentValid[deviceNum] = true;
                }
                else
                {
                    lastSentColor[deviceNum] = color;
                    lastSentValid[deviceNum] = true;
                    fading[deviceNum] = false;
                }

                DS4LightbarState lightState = new DS4LightbarState
                {
                    LightBarColor = color,
                };

                if (lightState.IsLightBarSet())
                {
                    if (useForceLight && forcedFlash[deviceNum] > 0)
                    {
                        lightState.LightBarFlashDurationOff = lightState.LightBarFlashDurationOn = (byte)(25 - forcedFlash[deviceNum]);
                        lightState.LightBarExplicitlyOff = true;
                    }
                    else if (device.getBattery() <= lightModeInfo.flashAt && lightModeInfo.flashType == 0 && !defaultLight && !device.isCharging())
                    {
                        int level = device.getBattery() / 10;
                        if (level >= 10)
                            level = 10; // all values of >~100% are rendered the same

                        lightState.LightBarFlashDurationOn = BatteryIndicatorDurations[level, 0];
                        lightState.LightBarFlashDurationOff = BatteryIndicatorDurations[level, 1];
                    }
                    else if (distanceprofile && device.getLeftHeavySlowRumble() > 155) //also part of Distance
                    {
                        lightState.LightBarFlashDurationOff = lightState.LightBarFlashDurationOn = (byte)((-device.getLeftHeavySlowRumble() + 265));
                        lightState.LightBarExplicitlyOff = true;
                    }
                    else
                    {
                        //haptics.LightBarFlashDurationOff = haptics.LightBarFlashDurationOn = 1;
                        lightState.LightBarFlashDurationOff = lightState.LightBarFlashDurationOn = 0;
                        lightState.LightBarExplicitlyOff = true;
                    }
                }
                else
                {
                    lightState.LightBarExplicitlyOff = true;
                }

                byte tempLightBarOnDuration = device.getLightBarOnDuration();
                if (tempLightBarOnDuration != lightState.LightBarFlashDurationOn && tempLightBarOnDuration != 1 && lightState.LightBarFlashDurationOn == 0)
                    lightState.LightBarFlashDurationOff = lightState.LightBarFlashDurationOn = 1;

                device.SetLightbarState(ref lightState);
                //device.SetHapticState(ref haptics);
                //device.pushHapticState(ref haptics);
            }
        }

        public static bool defaultLight = false, shuttingdown = false;
      
        public static DS4Color HuetoRGB(float hue, byte sat)
        {
            byte C = sat;
            int X = (int)((C * (float)(1 - Math.Abs((hue / 60) % 2 - 1))));
            if (0 <= hue && hue < 60)
                return new DS4Color(C, (byte)X, 0);
            else if (60 <= hue && hue < 120)
                return new DS4Color((byte)X, C, 0);
            else if (120 <= hue && hue < 180)
                return new DS4Color(0, C, (byte)X);
            else if (180 <= hue && hue < 240)
                return new DS4Color(0, (byte)X, C);
            else if (240 <= hue && hue < 300)
                return new DS4Color((byte)X, 0, C);
            else if (300 <= hue && hue < 360)
                return new DS4Color(C, 0, (byte)X);
            else
                return new DS4Color(Color.Red);
        }
    }
}
