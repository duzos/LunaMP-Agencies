using KSP.UI.Screens;
using KSP.UI.Screens.Mapview;
using LmpClient.Base;
using LmpClient.Systems.Agency;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Windows.Agency;

namespace LmpClient.Systems.Label
{
    public class LabelEvents : SubSystem<LabelSystem>
    {
        public void OnLabelProcessed(BaseLabel label)
        {
            if (label is VesselLabel vesselLabel)
            {
                var vessel = vesselLabel.vessel;
                var owner = LockSystem.LockQuery.GetControlLockOwner(vessel.id);

                if (!string.IsNullOrEmpty(owner))
                    label.text.text = $"{owner}\n{label.text.text}";

                // Stock ProcessLabel assigns the label colour on every call (this runs as its postfix), so the tint
                // is re-applied each frame and never restored. The stock target highlight (same test as stock) wins.
                if (!AgencyPresentation.TryGetVesselStyle(vessel, SettingsSystem.CurrentSettings.AgencyTintVessels, out var style) || !style.HasColour) return;
                var target = FlightGlobals.fetch ? FlightGlobals.fetch.VesselTarget : null;
                var targetVessel = target?.GetVessel();
                if (targetVessel != null && targetVessel == vessel) return;
                label.text.color = style.TextColour;
            }
        }

        public void OnMapLabelProcessed(Vessel vessel, MapNode.CaptionData label)
        {
            if (vessel == null) return;

            var owner = LockSystem.LockQuery.GetControlLockOwner(vessel.id);
            if (!string.IsNullOrEmpty(owner))
            {
                label.Header = $"{owner}\n{label.Header}";
            }

            // Agency line above the owner line; TmpPrefix is TMP-escaped and already ends with a newline.
            if (AgencyPresentation.TryGetVesselStyle(vessel, SettingsSystem.CurrentSettings.AgencyTintVessels, out var style))
                label.Header = style.TmpPrefix + label.Header;
        }

        public void OnMapWidgetTextProcessed(TrackingStationWidget widget)
        {
            // Owner prefix, agency tint and flag are all handled (and cached) by the per-widget decoration.
            TrackingWidgetDecoration.Process(widget);
        }
    }
}
