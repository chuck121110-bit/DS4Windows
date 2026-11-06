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

namespace DS4Windows.DS4Control
{
    public abstract class VirtualKBMMapping
    {
        public uint MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4,
            MOUSEEVENTF_RIGHTDOWN = 8, MOUSEEVENTF_RIGHTUP = 16,
            MOUSEEVENTF_MIDDLEDOWN = 32, MOUSEEVENTF_MIDDLEUP = 64,
            MOUSEEVENTF_XBUTTONDOWN = 128, MOUSEEVENTF_XBUTTONUP = 256,
            MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;

        public uint KEY_TAB = 0x09, KEY_LALT = 0x12;
        // Arrow keys, handler-specific values set in PopulateConstants. Base defaults are VK codes, correct for SendInput. FakerInput overrides with KeyboardKey.LeftArrow/RightArrow, because FakerInput PerformKeyPress expects a KeyboardKey value, NOT a VK code. Passing raw VK 0x25/0x27 to FakerInput was the bug that made D-pad arrows do nothing in Alt-Tab: KeyboardKey(0x25) is not LeftArrow.
        public uint KEY_LEFT = 0x25, KEY_RIGHT = 0x27;
        public int WHEEL_TICK_DOWN = -120, WHEEL_TICK_UP = 120;
        public bool macroKeyTranslate = false;

        public abstract void PopulateConstants();
        public abstract void PopulateMappings();
        public abstract uint GetRealEventKey(uint winVkKey);
    }
}
