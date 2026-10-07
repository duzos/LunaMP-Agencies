using LmpClient.Base;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using UnityEngine;

namespace LmpClient.Windows.Update
{
    /// <summary>
    /// Prompt for new agencies releases: update on exit, skip this version, later (and retry after a failed install).
    /// </summary>
    public partial class UpdateWindow : Window<UpdateWindow>
    {
        #region Fields & properties

        public static AgenciesReleaseInfo Info;
        public static string Failure;
        public static string Status = string.Empty;

        private static bool _display;
        public override bool Display
        {
            get => base.Display && _display && HighLogic.LoadedScene <= GameScenes.MAINMENU && SettingsSystem.CurrentSettings.DisclaimerAccepted;
            set => base.Display = _display = value;
        }

        private Vector2 _scrollPos;

        private const float WindowHeight = 330;
        private const float WindowWidth = 420;

        #endregion

        public static void Show(AgenciesReleaseInfo info, string failure)
        {
            Info = info;
            Failure = failure;
            Singleton.Display = true;
        }

        public static void ShowStatusOnly(string status)
        {
            Status = status;
            Singleton.Display = true;
        }

        public static void ClearFailure() => Failure = null;

        protected override void DrawGui()
        {
            WindowRect = FixWindowPos(GUILayout.Window(6724 + MainSystem.WindowOffset, WindowRect, DrawContent, "LMP Agencies update", LayoutOptions));
        }

        public override void SetStyles()
        {
            WindowRect = new Rect(Screen.width - (WindowWidth + 50), Screen.height / 2f - WindowHeight / 2f, WindowWidth, WindowHeight);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);

            LayoutOptions = new GUILayoutOption[4];
            LayoutOptions[0] = GUILayout.MinWidth(WindowWidth);
            LayoutOptions[1] = GUILayout.MaxWidth(WindowWidth);
            LayoutOptions[2] = GUILayout.MinHeight(WindowHeight);
            LayoutOptions[3] = GUILayout.MaxHeight(WindowHeight);

            TextAreaOptions = new GUILayoutOption[1];
            TextAreaOptions[0] = GUILayout.ExpandWidth(true);
        }
    }
}
