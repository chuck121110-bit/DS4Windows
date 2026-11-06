using System;
using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels.Util;

namespace DS4WinWPF.DS4Forms.ViewModels.SpecialActions
{
    public class OnScreenKeyboardViewModel : NotifyDataErrorBase
    {
        private bool useTouchpadCursor;
        public bool UseTouchpadCursor { get => useTouchpadCursor; set => useTouchpadCursor = value; }

        public void LoadAction(SpecialAction action)
        {
            useTouchpadCursor = !string.IsNullOrEmpty(action.details) &&
                action.details.IndexOf("TouchpadCursor", StringComparison.Ordinal) >= 0;
        }

        public void SaveAction(SpecialAction action, bool edit = false)
        {
            Global.SaveAction(action.name, action.controls, 12,
                useTouchpadCursor ? "TouchpadCursor" : "", edit);
        }

        public override bool IsValid(SpecialAction action)
        {
            ClearOldErrors();
            return true;
        }

        public override void ClearOldErrors()
        {
            if (errors.Count > 0)
            {
                errors.Clear();
            }
        }
    }
}
