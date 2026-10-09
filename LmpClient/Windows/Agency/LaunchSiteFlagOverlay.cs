using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// Assigned agency flags over launch sites (plan 41 S4): the KSC scene, the tracking station and the flight
    /// map. Called every MainSystem.OnGUI by AgencyPresentationPatches; draws on Repaint only and allocates nothing
    /// per frame.
    /// </summary>
    internal static class LaunchSiteFlagOverlay
    {
        private const float FlagWidth = 32f, FlagHeight = 20f, BarHeight = 2f, ScreenLift = 14f;
        private const double WorldLift = 30;
        private static readonly double[] Site = new double[3], Centre = new double[3], Eye = new double[3];
        private static GUIStyle label;

        internal static void Draw()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint || !MainSystem.ToolbarShowGui) return;
            if (MainSystem.NetworkState < LmpCommon.Enums.ClientState.Handshaking) return;
            var scene = HighLogic.LoadedScene;
            var map = scene == GameScenes.TRACKSTATION || (scene == GameScenes.FLIGHT && MapView.MapIsEnabled);
            if (!map && scene != GameScenes.SPACECENTER) return;
            var snapshot = AgencySystem.Singleton?.LaunchSitesSnapshot;
            var perAgency = SettingsSystem.ServerSettings.AgencyLaunchSitesPerAgency;
            var featureOn = SettingsSystem.CurrentSettings.AgencySiteFlags;
            if (snapshot == null || !featureOn || !perAgency || !snapshot.Ready || snapshot.Assignments.Count == 0) return;

            var camera = map ? PlanetariumCamera.Camera : Camera.main;
            if (!camera) return;
            var marks = LaunchSiteLocator.GetMarks();
            var mouse = Event.current.mousePosition;
            for (var i = 0; i < marks.Count; i++)
            {
                var mark = marks[i];
                if (!LaunchSiteFlagPolicy.ShouldShow(featureOn, perAgency, snapshot.Ready, mark.Agency, mark.Hidden) || !mark.Body) continue;
                if (!AgencyPresentation.TryGetAgencyStyle(mark.Agency, out var style) || (!style.Flag && !style.HasColour)) continue;
                if (!TryProject(camera, mark, map, out var screen)) continue;
                DrawMark(style, screen, mouse);
            }
        }

        private static bool TryProject(Camera camera, SiteMark mark, bool map, out Vector2 screen)
        {
            screen = default;
            var world = mark.Body.GetWorldSurfacePosition(mark.Lat, mark.Lon, mark.Alt + WorldLift);
            Vector3 point;
            if (map)
            {
                var scaled = (Vector3)ScaledSpace.LocalToScaledSpace(world);
                var body = mark.Body.scaledBody;
                if (!body) return false;
                var bodyPos = body.transform.position;
                var cam = camera.transform.position;
                Fill(Site, scaled); Fill(Centre, bodyPos); Fill(Eye, cam);
                if (!LaunchSiteFlagPolicy.AboveHorizon(Site, Centre, mark.Body.Radius * ScaledSpace.InverseScaleFactor, Eye)) return false;
                point = camera.WorldToScreenPoint(scaled);
            }
            else
            {
                var cam = camera.transform.position;
                var delta = world - (Vector3d)cam;
                if (!LaunchSiteFlagPolicy.WithinKscRange(delta.sqrMagnitude)) return false;
                var centre = mark.Body.position;
                Site[0] = world.x; Site[1] = world.y; Site[2] = world.z;
                Centre[0] = centre.x; Centre[1] = centre.y; Centre[2] = centre.z;
                Eye[0] = cam.x; Eye[1] = cam.y; Eye[2] = cam.z;
                if (!LaunchSiteFlagPolicy.AboveHorizon(Site, Centre, mark.Body.Radius, Eye)) return false;
                point = camera.WorldToScreenPoint((Vector3)world);
            }
            if (point.z <= 0 || float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.x) || float.IsInfinity(point.y)) return false;
            screen = new Vector2(point.x, Screen.height - point.y);
            return screen.x >= 0 && screen.x <= Screen.width && screen.y >= 0 && screen.y <= Screen.height;
        }

        private static void Fill(double[] target, Vector3 value) { target[0] = value.x; target[1] = value.y; target[2] = value.z; }

        private static void DrawMark(AgencyStyle style, Vector2 screen, Vector2 mouse)
        {
            var flag = new Rect(screen.x - FlagWidth / 2, screen.y - ScreenLift - FlagHeight, FlagWidth, FlagHeight);
            var oldColour = GUI.color;
            try
            {
                GUI.color = Color.white;
                if (style.Flag) GUI.DrawTexture(flag, style.Flag, ScaleMode.StretchToFill);
                var bar = new Rect(flag.x, flag.yMax, FlagWidth, BarHeight);
                if (style.HasColour)
                {
                    GUI.color = style.Colour;
                    GUI.DrawTexture(bar, Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
                if (!flag.Contains(mouse) && !bar.Contains(mouse)) return;
                if (label == null) label = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 12, richText = false, wordWrap = false };
                var size = label.CalcSize(style.NameGuiContent);
                GUI.Label(new Rect(Mathf.Clamp(flag.center.x - size.x / 2 - 4, 0, Mathf.Max(0, Screen.width - size.x - 8)), flag.y - size.y - 6, size.x + 8, size.y + 4), style.NameGuiContent, label);
            }
            finally { GUI.color = oldColour; }
        }
    }
}
