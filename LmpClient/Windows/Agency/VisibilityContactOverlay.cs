using System;
using LmpCommon.Agency;
using LmpClient.Systems.Agency;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    internal static class VisibilityContactOverlay
    {
        private static GUIStyle label;
        internal static string Caption(ContactIdentificationTier tier, string contact, ContactSnapshot snapshot,
            double progress, bool detected, double age, double expiry, double observationSeconds)
        {
            var title = tier == ContactIdentificationTier.Anonymous ? contact :
                tier == ContactIdentificationTier.Classified ? snapshot.AgencyName + " | " + snapshot.Size : snapshot.VesselName;
            if (!detected) return title + "\nLast seen " + TimeText(age) + " ago | expires in " + TimeText(Math.Max(0, expiry - age));
            if (tier == ContactIdentificationTier.Anonymous)
                return title + "\nIdentifying " + (int)(progress * 100) + "% | Observed " + TimeText(progress * observationSeconds) + " / " + TimeText(observationSeconds);
            return title + "\nApproach within " + (LmpClient.Systems.SettingsSys.SettingsSystem.ServerSettings.AgencyContactIdentificationDistance / 1000).ToString("0.##") + " km to identify craft";
        }
        private static string TimeText(double seconds) => ((int)Math.Max(0, seconds) / 60) + ":" + ((int)Math.Max(0, seconds) % 60).ToString("00");
        internal static void Draw()
        {
            if (!VisibilityClient.Enabled || !MapView.MapIsEnabled || !MainSystem.ToolbarShowGui || Event.current.type != EventType.Repaint) return;
            var camera = PlanetariumCamera.Camera;
            if (!camera) return;
            if (label == null) label = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, fontSize = 12, richText = false };
            var oldColour = GUI.color; var oldMatrix = GUI.matrix;
            try
            {
                foreach (var view in VisibilityContacts.Views())
                {
                    if (view.IsDetected && view.Tier == ContactIdentificationTier.Identified) continue;
                    var snapshot = view.Snapshot;
                    if (snapshot == null || !snapshot.Body) continue;
                    var alpha = (float)view.Fade;
                    GUI.color = new Color(0.85f, 0.9f, 1, alpha);
                    var points = snapshot.OrbitPoints;
                    if (points != null)
                        for (var i = 1; i < points.Length; i++)
                            if (Project(camera, snapshot.Body.position + points[i - 1], out var a) && Project(camera, snapshot.Body.position + points[i], out var b)) DrawLine(a, b);
                    if (!Project(camera, snapshot.Body.position + snapshot.Position, out var marker)) continue;
                    GUI.DrawTexture(new Rect(marker.x - 3, marker.y - 3, 6, 6), Texture2D.whiteTexture);
                    var text = Caption(view.Tier, "Contact " + view.ContactId.ToString("N").Substring(0, 6), snapshot, view.Progress, view.IsDetected,
                        view.AgeSeconds, VisibilityContacts.ExpirySeconds, VisibilityContacts.ClassificationSeconds);
                    var width = 290f;
                    var rect = new Rect(Mathf.Clamp(marker.x + 9, 0, Math.Max(0, Screen.width - width)), Mathf.Clamp(marker.y + 5, 0, Math.Max(0, Screen.height - 48)), width, 42);
                    GUI.Label(rect, text, label);
                    if (view.IsDetected && view.Tier == ContactIdentificationTier.Anonymous)
                        GUI.DrawTexture(new Rect(rect.x, rect.yMax, width * (float)view.Progress, 3), Texture2D.whiteTexture);
                }
            }
            finally { GUI.color = oldColour; GUI.matrix = oldMatrix; }
        }
        private static bool Project(Camera camera, Vector3d world, out Vector2 screen)
        {
            var point = camera.WorldToScreenPoint((Vector3)ScaledSpace.LocalToScaledSpace(world));
            screen = new Vector2(point.x, Screen.height - point.y);
            return !float.IsNaN(point.x) && !float.IsInfinity(point.x) && !float.IsNaN(point.y) && !float.IsInfinity(point.y) &&
                point.z > 0 && screen.x >= 0 && screen.x <= Screen.width && screen.y >= 0 && screen.y <= Screen.height;
        }
        private static void DrawLine(Vector2 a, Vector2 b)
        {
            var delta = b - a;
            if (delta.sqrMagnitude < 1 || delta.sqrMagnitude > Screen.width * Screen.width + Screen.height * Screen.height) return;
            var matrix = GUI.matrix;
            try { GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, a); GUI.DrawTexture(new Rect(a.x, a.y, delta.magnitude, 1), Texture2D.whiteTexture); }
            finally { GUI.matrix = matrix; }
        }
    }
}
