using LmpClient.Base;
using LmpClient.Localization;
using LmpClient.Systems.Admin;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Windows.Admin
{
    public partial class AdminWindow : SystemWindow<AdminWindow, AdminSystem>
    {
        #region Fields

        private const float WindowHeight = 600;
        private const float WindowWidth = 720;
        private const float ConfirmationWindowHeight = 50;
        private const float ConfirmationWindowWidth = 350;

        private static Rect _confirmationWindowRect;
        private static GUILayoutOption[] _confirmationLayoutOptions;

        private static string _selectedPlayer;
        private static bool _banMode;
        private static string _reason = string.Empty;

        private static bool _display;
        public override bool Display
        {
            get => base.Display && _display && MainSystem.NetworkState >= ClientState.Running && HighLogic.LoadedScene >= GameScenes.SPACECENTER && SettingsSystem.ServerSettings.AllowAdmin;
            set => base.Display = _display = value;
        }

        #endregion

        protected override void DrawGui()
        {
            if (Display)
            {
                WindowRect = FixWindowPos(GUILayout.Window(6723 + MainSystem.WindowOffset, WindowRect, DrawContent, LocalizationContainer.AdminWindowText.Title, LayoutOptions));
                if (!string.IsNullOrEmpty(_selectedPlayer))
                {
                    _confirmationWindowRect = FixWindowPos(GUILayout.Window(6724 + MainSystem.WindowOffset,
                        _confirmationWindowRect, DrawConfirmationDialog, LocalizationContainer.AdminWindowText.ConfirmDialogTitle, _confirmationLayoutOptions));
                }
            }
            else
            {
                _reason = string.Empty;    //FIXME: State being changed when this window isn't likely to even be called. (after we're done with the refactor)
                _selectedPlayer = null;
            }
        }

        public override void SetStyles()
        {
            var width = Mathf.Min(WindowWidth, Mathf.Max(320, Screen.width - 32));
            var height = Mathf.Min(WindowHeight, Mathf.Max(260, Screen.height - 32));
            WindowRect = new Rect(Screen.width / 2f - width / 2f, Screen.height / 2f - height / 2f, width, height);
            MoveRect = new Rect(0, 0, 10000, 40);

            LayoutOptions = new GUILayoutOption[4];
            LayoutOptions[0] = GUILayout.MinWidth(width);
            LayoutOptions[1] = GUILayout.MaxWidth(width);
            LayoutOptions[2] = GUILayout.MinHeight(height);
            LayoutOptions[3] = GUILayout.MaxHeight(height);

            _confirmationLayoutOptions = new GUILayoutOption[4];
            _confirmationLayoutOptions[0] = GUILayout.MinWidth(ConfirmationWindowWidth);
            _confirmationLayoutOptions[1] = GUILayout.MaxWidth(ConfirmationWindowWidth);
            _confirmationLayoutOptions[2] = GUILayout.MinHeight(ConfirmationWindowHeight);
            _confirmationLayoutOptions[3] = GUILayout.MaxHeight(ConfirmationWindowHeight);

            ScrollPos = new Vector2();
        }

        public override void RemoveWindowLock()
        {
            if (IsWindowLocked)
            {
                IsWindowLocked = false;
                InputLockManager.RemoveControlLock("LMP_AdminLock");
            }
        }

        public override void CheckWindowLock()
        {
            if (Display)
            {
                if (MainSystem.NetworkState < ClientState.Running || HighLogic.LoadedSceneIsFlight)
                {
                    RemoveWindowLock();
                    return;
                }

                Vector2 mousePos = Input.mousePosition;
                mousePos.y = Screen.height - mousePos.y;

                var shouldLock = WindowRect.Contains(mousePos)
                                 || !string.IsNullOrEmpty(_selectedPlayer) && _confirmationWindowRect.Contains(mousePos);

                if (shouldLock && !IsWindowLocked)
                {
                    InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, "LMP_AdminLock");
                    IsWindowLocked = true;
                }
                if (!shouldLock && IsWindowLocked)
                    RemoveWindowLock();
            }

            if (!Display && IsWindowLocked)
                RemoveWindowLock();
        }
    }
}
