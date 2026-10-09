using System;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// Plan 41 S3: flight nameplates (agency flag + agency name) over visible rival craft. Called every
    /// MainSystem.OnGUI by AgencyPresentationPatches.
    /// <para>
    /// Two paths, both inside Repaint only. The refresh (4 Hz) walks <see cref="FlightGlobals.VesselsLoaded"/>,
    /// applies <see cref="NameplatePolicy"/> and keeps the nearest <see cref="NameplatePolicy.ClampCount"/> in a
    /// fixed array; it is the only place that calls the locking <see cref="VisibilityClient.CanSee(Vessel)"/>.
    /// The draw projects each kept plate every frame from cached data: no LINQ, no strings, no heap allocation.
    /// </para>
    /// <para>
    /// Privacy: plates exist only for loaded (physics-range) craft that pass the same full-visibility bar as
    /// stock labels, never for unowned craft. A craft that loses visibility keeps its plate for at most one
    /// refresh (250 ms); a stale plate is also never drawn after a pause longer than that, because the refresh
    /// runs before the draw whenever it is due.
    /// </para>
    /// </summary>
    internal static class AgencyNameplateOverlay
    {
        private const float RefreshSeconds = 0.25f;
        private const float FlagWidth = 24f, FlagHeight = 15f, FlagGap = 4f;
        private const float PlateHeight = 18f, LiftPixels = 34f;

        private sealed class Plate
        {
            internal Vessel Vessel;
            internal Guid Agency;
            internal string MeasuredText;
            internal float TextWidth;
        }

        private static readonly Plate[] Plates = CreatePlates();
        private static readonly double[] Distances = new double[NameplatePolicy.ClampCount];
        private static int count;
        private static float nextRefresh = float.MinValue;
        private static GUIStyle textStyle, shadowStyle;

        private static Plate[] CreatePlates()
        {
            var plates = new Plate[NameplatePolicy.ClampCount];
            for (var i = 0; i < plates.Length; i++) plates[i] = new Plate();
            return plates;
        }

        internal static void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (MainSystem.NetworkState < ClientState.Running || !HighLogic.LoadedSceneIsFlight || !SettingsSystem.CurrentSettings.AgencyNameplates)
            {
                Reset();
                return;
            }
            if (!MainSystem.ToolbarShowGui || !FlightGlobals.ready || MapView.MapIsEnabled) return;
            if (StockUiOverlayGate.Covered()) return; // F2 / pause menu
            var cameraManager = CameraManager.Instance;
            if (cameraManager != null && (cameraManager.currentCameraMode == CameraManager.CameraMode.IVA || cameraManager.currentCameraMode == CameraManager.CameraMode.Internal)) return;
            var active = FlightGlobals.ActiveVessel;
            if (!active)
            {
                Reset();
                return;
            }
            var flightCamera = FlightCamera.fetch;
            var camera = flightCamera ? flightCamera.mainCamera : null;
            if (!camera) return;

            EnsureStyles();
            var maxKm = AgencyPresentationPolicy.ClampNameplateRangeKm(SettingsSystem.CurrentSettings.AgencyNameplateRangeKm);
            var now = Time.unscaledTime;
            if (now >= nextRefresh)
            {
                nextRefresh = now + RefreshSeconds;
                Refresh(active, maxKm);
            }
            if (count == 0) return;
            DrawPlates(camera, active, maxKm * 1000d);
        }

        private static void Reset()
        {
            if (count == 0) return;
            for (var i = 0; i < count; i++) Plates[i].Vessel = null;
            count = 0;
            nextRefresh = float.MinValue;
        }

        private static void EnsureStyles()
        {
            if (textStyle != null) return;
            textStyle = new GUIStyle(GUI.skin.label)
            {
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Overflow,
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            shadowStyle = new GUIStyle(textStyle);
        }

        private static void Refresh(Vessel active, double maxKm)
        {
            for (var i = 0; i < count; i++) Plates[i].Vessel = null;
            count = 0;
            var agencies = AgencySystem.Singleton;
            if (agencies == null) return;
            var mine = agencies.MyAgencyId;
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return;
            var activePosition = active.transform.position;
            for (var i = 0; i < loaded.Count; i++)
            {
                var vessel = loaded[i];
                if (!vessel || vessel == active) continue;
                var owner = agencies.GetVesselAgency(vessel.id);
                double distSq = (vessel.transform.position - activePosition).sqrMagnitude;
                if (!NameplatePolicy.IsCandidate(true, owner, mine, false, (VesselTypeCode)(int)vessel.vesselType, distSq, maxKm)) continue;
                var slot = NameplatePolicy.InsertSlot(Distances, count, distSq);
                if (slot < 0) continue;
                // Visibility last: it takes a lock, and only craft that would make the cut pay for it.
                // IsCandidate already passed, so this completes NameplatePolicy.ShouldShow: classified craft never get a plate.
                if (!VisibilityClient.CanSee(vessel)) continue;
                if (!AgencyPresentation.TryGetAgencyStyle(owner, out var style)) continue;

                var spare = Plates[Math.Min(count, Plates.Length - 1)];
                NameplatePolicy.ShiftRight(Plates, count, slot);
                NameplatePolicy.ShiftRight(Distances, count, slot);
                Plates[slot] = spare;
                Distances[slot] = distSq;
                spare.Vessel = vessel;
                spare.Agency = owner;
                var text = style.NameGuiContent.text;
                if (!ReferenceEquals(spare.MeasuredText, text))
                {
                    spare.MeasuredText = text;
                    spare.TextWidth = Mathf.Ceil(textStyle.CalcSize(style.NameGuiContent).x) + 2f;
                }
                if (count < Plates.Length) count++;
            }
        }

        private static void DrawPlates(Camera camera, Vessel active, double maxMetres)
        {
            var previousColour = GUI.color;
            try
            {
                var activePosition = active.transform.position;
                var screenWidth = Screen.width;
                var screenHeight = Screen.height;
                // Farthest first so the nearest plate ends up on top.
                for (var i = count - 1; i >= 0; i--)
                {
                    var plate = Plates[i];
                    var vessel = plate.Vessel;
                    if (!vessel || !vessel.loaded || vessel == active) continue;
                    if (!AgencyPresentation.TryGetAgencyStyle(plate.Agency, out var style)) continue;
                    var world = vessel.transform.position;
                    double distance = (world - activePosition).magnitude;
                    if (distance > maxMetres) continue;
                    var screen = camera.WorldToScreenPoint(world);
                    if (screen.z <= 0f || float.IsNaN(screen.x) || float.IsNaN(screen.y)) continue;

                    var flag = style.Flag;
                    var flagSpace = flag ? FlagWidth + FlagGap : 0f;
                    var width = flagSpace + plate.TextWidth;
                    var left = screen.x - width * 0.5f;
                    var top = screenHeight - screen.y - LiftPixels - PlateHeight * 0.5f;
                    if (left > screenWidth || left + width < 0f || top > screenHeight || top + PlateHeight < 0f) continue;

                    var alpha = NameplatePolicy.Alpha(distance, maxMetres);
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    if (flag) GUI.DrawTexture(new Rect(left, top + (PlateHeight - FlagHeight) * 0.5f, FlagWidth, FlagHeight), flag, ScaleMode.ScaleToFit);

                    var textRect = new Rect(left + flagSpace, top, plate.TextWidth, PlateHeight);
                    GUI.color = Color.white;
                    shadowStyle.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.85f);
                    GUI.Label(new Rect(textRect.x + 1f, textRect.y + 1f, textRect.width, textRect.height), style.NameGuiContent, shadowStyle);
                    var textColour = style.TextColour;
                    textColour.a = alpha;
                    textStyle.normal.textColor = textColour;
                    GUI.Label(textRect, style.NameGuiContent, textStyle);
                }
            }
            finally { GUI.color = previousColour; }
        }
    }
}
