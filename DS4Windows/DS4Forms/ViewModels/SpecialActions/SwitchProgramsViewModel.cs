using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels.Util;

namespace DS4WinWPF.DS4Forms.ViewModels.SpecialActions
{
    public class SwitchProgramsViewModel : NotifyDataErrorBase
    {
        public void LoadAction(SpecialAction action)
        {
        }

        public void SaveAction(SpecialAction action, bool edit = false)
        {
            Global.SaveAction(action.name, action.controls, 11, "", edit);
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
