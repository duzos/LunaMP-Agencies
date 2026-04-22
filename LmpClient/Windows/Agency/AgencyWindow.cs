using LmpClient.Base;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// In-game management window for agencies. Tabs:
    ///   - Mine:   current agency details, resources, member list, owner
    ///             actions if the local player is the owner.
    ///   - Browse: list of non-solo agencies with a "Request to join" button.
    ///   - Create: input a new agency name and submit.
    ///
    /// The window is server-driven: it only displays what
    /// <see cref="LmpClient.Systems.Agency.AgencySystem"/> has cached from the
    /// server, and every user action is a fire-and-forget send. The server's
    /// AgencyReplyMsgData responses show up in a small status banner.
    /// </summary>
    public partial class AgencyWindow : Window<AgencyWindow>
    {
        private const float WindowWidth = 420;
        private const float WindowHeight = 500;
        private static readonly string Title = "LMP - Agencies";

        public override bool Display
        {
            get => _display
                   && SettingsSystem.CurrentSettings.DisclaimerAccepted
                   && MainSystem.ToolbarShowGui
                   && MainSystem.NetworkState >= ClientState.Running
                   && HighLogic.LoadedScene >= GameScenes.SPACECENTER;
            // Route the base Window<T>'s setter (called by the close button)
            // through our local toggle field so "X" actually closes the window.
            set => _display = value;
        }

        private bool _display;

        /// <summary>
        /// Bound to the toolbar toggle button. Mirrors <see cref="Display"/>'s
        /// setter so both the toolbar and the X-button close are in sync.
        /// </summary>
        public bool DisplayToggle
        {
            get => _display;
            set => _display = value;
        }

        protected override void OnCloseButton()
        {
            _display = false;
        }

        public override void SetStyles()
        {
            WindowRect = new Rect(Screen.width * 0.5f - WindowWidth * 0.5f,
                                  Screen.height * 0.5f - WindowHeight * 0.5f,
                                  WindowWidth, WindowHeight);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);

            LayoutOptions = new GUILayoutOption[4]
            {
                GUILayout.MinWidth(WindowWidth),
                GUILayout.MaxWidth(WindowWidth),
                GUILayout.MinHeight(WindowHeight),
                GUILayout.MaxHeight(WindowHeight),
            };
        }

        protected override void DrawGui()
        {
            WindowRect = FixWindowPos(GUILayout.Window(6811 + MainSystem.WindowOffset,
                WindowRect, DrawContent, Title, LayoutOptions));
        }
    }
}
