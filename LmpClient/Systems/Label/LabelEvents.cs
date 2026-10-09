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
        }

        public void OnMapWidgetTextProcessed(TrackingStationWidget widget)
        {
            // Owner prefix, agency tint and flag are all handled (and cached) by the per-widget decoration.
            TrackingWidgetDecoration.Process(widget);
        }
    }
}
