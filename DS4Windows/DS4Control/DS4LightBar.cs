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
        // 2026-11-05 LED fix: true only for a battery-check hold, which snaps
        // to the battery color. A connect hold leaves this false, so its
        // 600 ms fade from black to the profile color is untouched.
        public static bool[] BatteryHoldSnap = new bool[Global.MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        // 2026-11-01 connect force: first tick after a gap (controller
        // just turned back on) forces pure custom/profile color for
        // 3 seconds, no battery/flash/charging/rainbow intermediate,
        // so no yellow step sticks on connect.
        public static DateTime[] ConnectHoldUntil = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        public static DS4Color[] ConnectHoldColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DateTime[] lastLightBarTick = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Device[] lastLightBarDevice = new DS4Device[Global.MAX_DS4_CONTROLLER_COUNT];
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
        // 2026-11-05 fade-in regression fix: explicit fade-in states.
        // Nov 5 snap (BatteryHoldSnap in skipFade) made a battery check
        // jump straight to green, and the generic fade can miss the
        // first tick on connect (lastSentValid false, or transport
        // deferred so the 600 ms software fade expires before the
        // first physical send). These states do not depend on the
        // generic fade trigger: they start from a known from-color
        // and interpolate to the target over 600 ms, every tick.
        private static DateTime[] batteryFadeInStart = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] batteryFadeInFrom = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] batteryFadeInTo = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool[] batteryFadingIn = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DateTime[] connectFadeInStart = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] connectFadeInFrom = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] connectFadeInTo = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool[] connectFadingIn = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool[] fadeInReplayed = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        // 2026-11-05 connect/battery fade fix: a software fade is only
        // visible if its first physical BT send succeeds. The fade clock
        // must not run while transport is still transitioning (the
        // 19:01 connect direct send was color=0,0,0 published=False, and
        // the 19:03 battery direct send was published=False). Freeze the
        // fade start until DualSenseDevice reports a successful publish,
        // and extend the hold while unpublished, so the visible hold is
        // 3s of actual green/profile, not 3s of failed sends.
        private static bool[] connectFadePublished = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool[] batteryFadePublished = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        // 2026-11-06 bounded publish grace. The freeze below used to push
        // both the fade clock and the 3s hold forward on every tick with no
        // limit, so a transport that never acknowledged a hold send froze
        // the fade and extended the hold forever: the battery check could
        // never fade out and the profile colour never came back. Measure the
        // freeze against the first tick of the hold and give up after
        // MaxHoldPublishGraceMs; the hold then runs its remaining 3s on
        // wall-clock time and the fade-out always executes. Mapping creates
        // battery holds directly, so hold entry is detected in updateLightBar
        // rather than at creation.
        private static DateTime[] batteryHoldEnteredAt = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private const double MaxHoldPublishGraceMs = 3000.0;
        private static bool[] ledDirectSendFailedLogged = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        public static void MarkConnectFadePublished(int deviceNum)
        {
            if (deviceNum >= 0 && deviceNum < Global.MAX_DS4_CONTROLLER_COUNT) connectFadePublished[deviceNum] = true;
        }
        public static void MarkBatteryFadePublished(int deviceNum)
        {
            if (deviceNum >= 0 && deviceNum < Global.MAX_DS4_CONTROLLER_COUNT) batteryFadePublished[deviceNum] = true;
        }
        // DualSenseDevice uses these to decide whether a hold still needs a
        // completion-aware (blocking) Bluetooth send. Once the first physical
        // publish is known, later hold ticks only need the ordinary
        // non-blocking cached publish, so the output worker is not stalled
        // waiting up to a second for an acknowledgement on every tick.
        public static bool IsBatteryFadePublished(int deviceNum) => deviceNum >= 0 && deviceNum < Global.MAX_DS4_CONTROLLER_COUNT && batteryFadePublished[deviceNum];
        public static bool IsConnectFadePublished(int deviceNum) => deviceNum >= 0 && deviceNum < Global.MAX_DS4_CONTROLLER_COUNT && connectFadePublished[deviceNum];
        public static bool IsLedDirectSendFailedLogged(int deviceNum) => deviceNum >= 0 && deviceNum < Global.MAX_DS4_CONTROLLER_COUNT && ledDirectSendFailedLogged[deviceNum];
        public static void SetLedDirectSendFailedLogged(int deviceNum, bool value) { if (deviceNum >= 0 && deviceNum < Global.MAX_DS4_CONTROLLER_COUNT) ledDirectSendFailedLogged[deviceNum] = value; }
        // Called from DualSenseDevice on the first direct BT send during
        // a hold. This used to replay a fade that expired before that
        // first physical send, so a late transport still showed a fade
        // instead of a snap. The replay was itself the flicker: the send
        // that triggers it already carries the final colour, so starting
        // over from black made the profile colour flash, then go dark,
        // then fade in a second time. The fade clock is now parked until
        // the first publish, so nothing expires unseen and this only
        // guards a path that publishes before the clock is parked.
        public static void RestartMissedFadeIn(int deviceNum)
        {
            if (deviceNum < 0 || deviceNum >= Global.MAX_DS4_CONTROLLER_COUNT) return;
            if (fadeInReplayed[deviceNum]) return;
            DateTime now = DateTime.UtcNow;
            bool holdActive = BatteryHoldUntil[deviceNum] != DateTime.MinValue && now < BatteryHoldUntil[deviceNum];
            if (!holdActive) return;
            // Never replay a fade whose colour has already physically
            // landed. A replay restarts from black, so the controller
            // shows the final colour, then goes dark, then fades in
            // again, which is the flicker Chuck reported. With the fade
            // clock parked until the first publish a replay no longer
            // has a reason to fire; this stays as the guard for any
            // path that publishes before the clock is parked.
            if (BatteryHoldSnap[deviceNum] && !batteryFadePublished[deviceNum] &&
                !batteryFadingIn[deviceNum] &&
                batteryFadeInStart[deviceNum] != DateTime.MinValue &&
                (now - batteryFadeInStart[deviceNum]).TotalMilliseconds >= 600.0)
            {
                batteryFadeInStart[deviceNum] = now;
                batteryFadingIn[deviceNum] = true;
                fadeInReplayed[deviceNum] = true;
            }
            else if (!BatteryHoldSnap[deviceNum] &&
                !connectFadePublished[deviceNum] &&
                connectFadeInStart[deviceNum] != DateTime.MinValue &&
                (now - connectFadeInStart[deviceNum]).TotalMilliseconds >= 600.0)
            {
                // Old code required !connectFadingIn here, so while a fade
                // was still running it could never restart, and after it
                // expired there was nothing left to restart. Restart even
                // if the flag is still set.
                connectFadeInStart[deviceNum] = now;
                connectFadingIn[deviceNum] = true;
                fadeInReplayed[deviceNum] = true;
            }
        }
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
        private static DateTime[] batteryFadeOutStart = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] batteryFadeOutFrom = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private static DS4Color[] batteryFadeOutTo = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        private const double FadeDurationMs = 600.0;
        public static byte[] forcedFlash = new byte[Global.MAX_DS4_CONTROLLER_COUNT];
        internal const int PULSE_FLASH_DURATION = 2000;
        internal const double PULSE_FLASH_SEGMENTS = PULSE_FLASH_DURATION / 40;
        internal const int PULSE_CHARGING_DURATION = 4000;
        internal const double PULSE_CHARGING_SEGMENTS = (PULSE_CHARGING_DURATION / 40) - 2;

        // 2026-11-06 smoother fades. Every fade used a straight linear
        // ramp, which starts and stops at full speed and reads as a
        // small jump at each end. Smoothstep keeps the same 600/420 ms
        // durations but eases both ends, so a fade rolls out of the
        // starting colour and settles into the target instead of
        // snapping into motion and stopping dead.
        private static double EaseFade(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        public static void updateLightBar(DS4Device device, int deviceNum)
        {
            DateTime tickNow = DateTime.UtcNow;
            // Fresh connect: first tick, a tick gap over 5 seconds,
            // or a new device object for the slot. A profile switch
            // never changes the device object and ticks keep coming,
            // so it is never a fresh connect here, and its fade is
            // untouched.
            bool deviceChanged = !ReferenceEquals(lastLightBarDevice[deviceNum], device);
            lastLightBarDevice[deviceNum] = device;
            bool isFreshConnect = lastLightBarTick[deviceNum] == DateTime.MinValue ||
                (tickNow - lastLightBarTick[deviceNum]) > TimeSpan.FromSeconds(5) ||
                deviceChanged;
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

                // Connect uses the SAME path as a battery check, no
                // separate connect fade. On a fresh connect, set a
                // battery hold to the pure custom/profile color for
                // 1 second, exactly like Mapping does for a battery
                // check, so the hold below, its direct BT send, its
                // generic fade, and its fade-out/grace machinery all
                // run for connect too. A real battery hold already
                // in progress is left alone. Resetting the last sent
                // color to black here makes the generic color-change
                // fade below (the same code a battery check uses)
                // interpolate black to the profile/custom color,
                // instead of skipping because the target equals a
                // stale last sent color. No DS4 blue is forced;
                // firmware blue fades out on its own first. A
                // profile switch is never a fresh connect, so its
                // perfect fade is untouched.
                if (isFreshConnect && !defaultLight && useLightRoutine &&
                    (BatteryHoldUntil[deviceNum] == DateTime.MinValue ||
                     DateTime.UtcNow >= BatteryHoldUntil[deviceNum]))
                {
                    DS4Color connectTarget = lightModeInfo.useCustomLed
                        ? lightModeInfo.m_CustomLed : getMainColor(deviceNum);
                    BatteryHoldColor[deviceNum] = connectTarget;
                    BatteryHoldUntil[deviceNum] = tickNow.AddSeconds(3);
                    BatteryHoldSnap[deviceNum] = false;
                    connectFadePublished[deviceNum] = false;
                    batteryFadePublished[deviceNum] = false;
                    ledDirectSendFailedLogged[deviceNum] = false;
                    // Explicit connect fade-in: black to profile over 600 ms.
                    connectFadeInFrom[deviceNum] = new DS4Color(0, 0, 0);
                    connectFadeInTo[deviceNum] = connectTarget;
                    connectFadeInStart[deviceNum] = tickNow;
                    connectFadingIn[deviceNum] = true;
                    batteryFadingIn[deviceNum] = false;
                    fadeInReplayed[deviceNum] = false;
                    forcedColor[deviceNum] = connectTarget;
                    forcelight[deviceNum] = true;
                    batteryFadingOut[deviceNum] = false;
                    batteryFadeOutGraceUntil[deviceNum] = DateTime.MinValue;
                    fading[deviceNum] = false;
                    lastSentColor[deviceNum] = new DS4Color(0, 0, 0);
                    lastSentValid[deviceNum] = true;
                }

                // Battery hold override, after all other color logic.
                if (BatteryHoldUntil[deviceNum] != DateTime.MinValue)
                {
                    if (DateTime.UtcNow < BatteryHoldUntil[deviceNum])
                    {
                        // The publish grace below is measured from the first
                        // tick of this hold, so record it once here. Mapping
                        // creates battery holds itself, which is why entry is
                        // detected here instead of at creation.
                        if (batteryHoldEnteredAt[deviceNum] == DateTime.MinValue)
                            batteryHoldEnteredAt[deviceNum] = tickNow;
                        // New battery-check hold: fade in from the color
                        // the controller is showing now (profile color)
                        // to the battery color over 600 ms, then hold.
                        // A connect hold (Snap false) keeps its own fade.
                        if (BatteryHoldSnap[deviceNum] && !batteryFadingIn[deviceNum] && !batteryHoldWasActive[deviceNum])
                        {
                            batteryFadeInFrom[deviceNum] = lastSentValid[deviceNum] ? lastSentColor[deviceNum] : getMainColor(deviceNum);
                            batteryFadeInTo[deviceNum] = BatteryHoldColor[deviceNum];
                            batteryFadeInStart[deviceNum] = tickNow;
                            batteryFadingIn[deviceNum] = true;
                            batteryFadePublished[deviceNum] = false;
                            ledDirectSendFailedLogged[deviceNum] = false;
                            connectFadingIn[deviceNum] = false;
                            fadeInReplayed[deviceNum] = false;
                        }
                        // Freeze the fade clock and extend the hold while
                        // no physical publish has succeeded yet. Otherwise
                        // the 3s hold expires in software while BT transport
                        // is still transitioning, and the controller only
                        // ever sees the final color, or nothing.
                        // 2026-11-06: the hold extension below is bounded.
                        // It used to run on every tick for as long as the
                        // send stayed unacknowledged, which pushed the
                        // hold out indefinitely, so the profile colour
                        // never came back. After MaxHoldPublishGraceMs
                        // with no publish the hold stops being extended
                        // and runs out on wall-clock time. The fade clock
                        // is a separate question and stays parked, see
                        // below.
                        double holdPublishGraceMs =
                            (tickNow - batteryHoldEnteredAt[deviceNum]).TotalMilliseconds;
                        bool freezeForPublish = holdPublishGraceMs >= 0.0 &&
                            holdPublishGraceMs < MaxHoldPublishGraceMs;
                        // The fade clock is parked with no time limit
                        // while nothing has physically landed yet. If it
                        // were allowed to expire first, the first
                        // successful send would carry the final colour
                        // and RestartMissedFadeIn would then replay the
                        // fade from black: that is the blink Chuck
                        // reported, the profile colour flashes, then the
                        // light goes dark and fades in a second time.
                        // Parked, the first send carries the fade's
                        // starting colour and the fade plays exactly
                        // once. Only the hold itself stays bounded by
                        // the grace above, so a transport that never
                        // acknowledges still lets the hold end and the
                        // profile colour come back.
                        if (BatteryHoldSnap[deviceNum] && !batteryFadePublished[deviceNum])
                        {
                            batteryFadeInStart[deviceNum] = tickNow;
                        }
                        else if (!BatteryHoldSnap[deviceNum] && !connectFadePublished[deviceNum])
                        {
                            connectFadeInStart[deviceNum] = tickNow;
                        }
                        if (freezeForPublish)
                        {
                            BatteryHoldUntil[deviceNum] = tickNow.AddSeconds(3);
                        }
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
                        BatteryHoldSnap[deviceNum] = false;
                        batteryHoldEnteredAt[deviceNum] = DateTime.MinValue;
                        batteryFadingIn[deviceNum] = false;
                        connectFadingIn[deviceNum] = false;
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
                    batteryHoldEnteredAt[deviceNum] = DateTime.MinValue;
                    DS4Color fadeOutTarget = lightModeInfo.useCustomLed
                        ? lightModeInfo.m_CustomLed : getMainColor(deviceNum);
                    DS4Color fadeOutFrom = BatteryHoldColor[deviceNum];
                    // Spec: fade out FROM the battery color into profile.
                    // lastSent may be an intermediate faded color if the
                    // hold ended mid-fade, or the profile color if the fade
                    // was frozen. Use the full battery color unless the
                    // last sent color already is the battery color.
                    if (lastSentValid[deviceNum] && lastSentColor[deviceNum].red == BatteryHoldColor[deviceNum].red && lastSentColor[deviceNum].green == BatteryHoldColor[deviceNum].green && lastSentColor[deviceNum].blue == BatteryHoldColor[deviceNum].blue)
                        fadeOutFrom = lastSentColor[deviceNum];
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
                        double fadeOutEase = EaseFade(fadeOutRatio);
                        DS4Color fadeOutFromC = batteryFadeOutFrom[deviceNum];
                        DS4Color fadeOutToC = batteryFadeOutTo[deviceNum];
                        color = new DS4Color(
                            (byte)(fadeOutFromC.red + (fadeOutToC.red - fadeOutFromC.red) * fadeOutEase),
                            (byte)(fadeOutFromC.green + (fadeOutToC.green - fadeOutFromC.green) * fadeOutEase),
                            (byte)(fadeOutFromC.blue + (fadeOutToC.blue - fadeOutFromC.blue) * fadeOutEase));
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

                // Explicit fade-ins (2026-11-05): battery check fades
                // profile -> battery color, connect fades black -> profile,
                // both over 600 ms. These run first and set lastSent to the
                // interpolated color, so the generic fade below does not
                // start a competing fade on the same tick.
                if (batteryFadingIn[deviceNum])
                {
                    double fadeInElapsedMs = (tickNow - batteryFadeInStart[deviceNum]).TotalMilliseconds;
                    double fadeInRatio = fadeInElapsedMs / 600.0;
                    if (fadeInRatio >= 1.0)
                    {
                        color = batteryFadeInTo[deviceNum];
                        batteryFadingIn[deviceNum] = false;
                    }
                    else
                    {
                        if (fadeInRatio < 0.0) fadeInRatio = 0.0;
                        double fadeInEase = EaseFade(fadeInRatio);
                        DS4Color fiFrom = batteryFadeInFrom[deviceNum];
                        DS4Color fiTo = batteryFadeInTo[deviceNum];
                        color = new DS4Color(
                            (byte)(fiFrom.red + (fiTo.red - fiFrom.red) * fadeInEase),
                            (byte)(fiFrom.green + (fiTo.green - fiFrom.green) * fadeInEase),
                            (byte)(fiFrom.blue + (fiTo.blue - fiFrom.blue) * fadeInEase));
                    }
                    lastSentColor[deviceNum] = color;
                    lastSentValid[deviceNum] = true;
                    fading[deviceNum] = false;
                }
                else if (connectFadingIn[deviceNum])
                {
                    double fadeInElapsedMs = (tickNow - connectFadeInStart[deviceNum]).TotalMilliseconds;
                    double fadeInRatio = fadeInElapsedMs / 600.0;
                    if (fadeInRatio >= 1.0)
                    {
                        color = connectFadeInTo[deviceNum];
                        connectFadingIn[deviceNum] = false;
                    }
                    else
                    {
                        if (fadeInRatio < 0.0) fadeInRatio = 0.0;
                        double connectEase = EaseFade(fadeInRatio);
                        DS4Color fiFrom = connectFadeInFrom[deviceNum];
                        DS4Color fiTo = connectFadeInTo[deviceNum];
                        color = new DS4Color(
                            (byte)(fiFrom.red + (fiTo.red - fiFrom.red) * connectEase),
                            (byte)(fiFrom.green + (fiTo.green - fiFrom.green) * connectEase),
                            (byte)(fiFrom.blue + (fiTo.blue - fiFrom.blue) * connectEase));
                    }
                    lastSentColor[deviceNum] = color;
                    lastSentValid[deviceNum] = true;
                    fading[deviceNum] = false;
                }

                // Fade in/out: smooth any color change over 600 ms.
                // Skip for rainbow, pulse flash, charging pulse/rainbow,
                // distance rumble, and forced picker preview, which already
                // animate or need live response, and skip on shutdown.
                // BatteryHoldSnap no longer skips the fade: a battery check
                // now fades in via the explicit state above, it must not
                // snap. That was the Nov 5 regression.
                bool skipFade = shuttingdown || useForceLight ||
                    lightModeInfo.rainbow > 0 ||
                    (lightModeInfo.flashType == 1 && device.getBattery() <= lightModeInfo.flashAt && !device.isCharging()) ||
                    (device.isCharging() && (lightModeInfo.chargingType == 1 || lightModeInfo.chargingType == 2)) ||
                    (DistanceProfiles[deviceNum] || tempprofileDistance[deviceNum]);
                if (!skipFade)
                {
                    // Generic color-change fade, the same code a battery
                    // check uses. On a fresh connect the last sent color
                    // was reset to black above, so this interpolates
                    // black to the profile/custom color. No special
                    // connect fade branch exists anymore.
                    // 2026-11-05: if lastSent was never set, treat it as
                    // black instead of skipping the fade and snapping.
                    if (!lastSentValid[deviceNum])
                    {
                        lastSentColor[deviceNum] = new DS4Color(0, 0, 0);
                        lastSentValid[deviceNum] = true;
                    }
                    if ((color.red != lastSentColor[deviceNum].red || color.green != lastSentColor[deviceNum].green || color.blue != lastSentColor[deviceNum].blue))
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
                            double genericEase = EaseFade(ratio);
                            DS4Color fromC = fadeFromColor[deviceNum];
                            DS4Color toC = fadeToColor[deviceNum];
                            color = new DS4Color(
                                (byte)(fromC.red + (toC.red - fromC.red) * genericEase),
                                (byte)(fromC.green + (toC.green - fromC.green) * genericEase),
                                (byte)(fromC.blue + (toC.blue - fromC.blue) * genericEase));
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
