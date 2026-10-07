using LmpClient.Systems.AgenciesUpdate;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using UnityEngine;

namespace LmpClient.Windows.Update
{
    public partial class UpdateWindow
    {
        protected override void DrawWindowContent(int windowId)
        {
            GUILayout.BeginVertical();
            GUI.DragWindow(MoveRect);

            var info = Info;
            if (info != null)
            {
                GUILayout.Label($"Current: agencies.{AgenciesBuild.Number}");
                GUILayout.Label($"Latest: agencies.{info.Build}", BoldGreenLabelStyle);

                if (!string.IsNullOrEmpty(Failure))
                    GUILayout.Label(Failure, BoldRedLabelStyle);

                GUILayout.Label("Changelog");
                _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Width(WindowWidth - 5), GUILayout.Height(WindowHeight - 190));
                GUILayout.Label(info.Changelog ?? string.Empty);
                GUILayout.EndScrollView();

                var settings = SettingsSystem.CurrentSettings;
                var auto = GUILayout.Toggle(settings.AgenciesAutoUpdate, "Always update automatically");
                var relaunch = GUILayout.Toggle(settings.AgenciesRelaunchAfterUpdate, "Relaunch KSP after updating");
                if (auto != settings.AgenciesAutoUpdate || relaunch != settings.AgenciesRelaunchAfterUpdate)
                {
                    settings.AgenciesAutoUpdate = auto;
                    settings.AgenciesRelaunchAfterUpdate = relaunch;
                    SettingsSystem.SaveSettings();
                }

                GUILayout.BeginHorizontal();
                if (!string.IsNullOrEmpty(Failure))
                {
                    if (GUILayout.Button("Retry"))
                        AgenciesUpdateClient.Retry(info);
                }
                else if (GUILayout.Button("Update on exit"))
                    AgenciesUpdateClient.StartDownload(info);

                if (GUILayout.Button("Skip this version"))
                {
                    settings.AgenciesSkippedBuild = info.Build;
                    SettingsSystem.SaveSettings();
                    Display = false;
                }
                if (GUILayout.Button("Later"))
                    Display = false;
                GUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("Close"))
                Display = false;

            GUILayout.Label($"Status: {Status}");

            GUILayout.EndVertical();
        }
    }
}
