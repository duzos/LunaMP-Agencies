using System;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using LmpClient.Systems.Agency;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// Plan 41 S1: per-<see cref="TrackingStationWidget"/> agency tint, flag and owner prefix cache. Added to a
    /// widget the first time its <c>Update</c> postfix fires. A frame costs a string compare and a few field
    /// compares; the visibility / style re-evaluation runs when the vessel, identity version or display name
    /// changes and otherwise every <see cref="EvaluateInterval"/> seconds.
    /// </summary>
    internal sealed class TrackingWidgetDecoration : MonoBehaviour
    {
        private const float EvaluateInterval = 0.5f;
        private const float FlagWidth = 24f, FlagHeight = 15f, FlagInset = 2f, MarginShift = 28f;
        private static readonly FieldInfo IconImageField = AccessTools.Field(typeof(VesselIconSprite), "image");

        private TrackingStationWidget widget;
        private TextMeshProUGUI nameText;
        private Image icon;
        private Color originalNameColour, originalIconColour;
        private Vector4 originalMargin;
        private bool initialised;
        private bool tinted, flagShown;
        private GameObject flagObject;
        private RawImage flagImage;
        private Guid lastVessel;
        private int lastVersion = int.MinValue;
        private float nextEvaluate;
        private string rawName, owner, composed;

        /// <summary>Called from <c>LabelEvents.OnMapWidgetTextProcessed</c> after every stock widget Update.</summary>
        internal static void Process(TrackingStationWidget widget)
        {
            if (!widget || !widget.textName) return;
            var decoration = widget.GetComponent<TrackingWidgetDecoration>();
            if (!decoration) decoration = widget.gameObject.AddComponent<TrackingWidgetDecoration>();
            decoration.Tick(widget);
        }

        private void Tick(TrackingStationWidget source)
        {
            widget = source;
            if (!initialised) Initialise();
            var vessel = widget.vessel;
            if (!vessel)
            {
                // Pooled / unassigned widget: leave stock visuals untouched.
                ClearStyle();
                lastVessel = Guid.Empty;
                composed = null;
                owner = null;
                rawName = null;
                return;
            }

            var displayName = vessel.DiscoveryInfo.displayName.Value;
            var version = AgencyIdentityClient.Version;
            var now = Time.unscaledTime;
            if (vessel.id != lastVessel || version != lastVersion || now >= nextEvaluate || displayName != rawName)
                Evaluate(vessel, displayName, version, now);

            // Stock Update resets textName to the display name whenever it differs, so the composed text is
            // assigned every frame (a string compare, no allocation when it is already applied).
            if (composed != null && nameText.text != composed) nameText.text = composed;
        }

        private void Initialise()
        {
            initialised = true;
            nameText = widget.textName;
            originalNameColour = nameText.color;
            originalMargin = nameText.margin;
            var sprite = widget.iconSprite;
            if (sprite)
                icon = (IconImageField?.GetValue(sprite) as Image) ?? sprite.GetComponentInChildren<Image>(true);
            if (icon) originalIconColour = icon.color;
        }

        private void Evaluate(Vessel vessel, string displayName, int version, float now)
        {
            lastVessel = vessel.id;
            lastVersion = version;
            nextEvaluate = now + EvaluateInterval;

            var lockOwner = LockSystem.LockQuery.GetControlLockOwner(vessel.id);
            if (string.IsNullOrEmpty(lockOwner)) lockOwner = null;
            if (lockOwner != owner || displayName != rawName)
            {
                owner = lockOwner;
                composed = owner == null ? null : "(" + owner + ") " + displayName;
            }
            rawName = displayName;

            var settings = SettingsSystem.CurrentSettings;
            if (!AgencyPresentation.TryGetVesselStyle(vessel, out var style))
            {
                ClearStyle();
                return;
            }

            if (settings.AgencyTintVessels && style.HasColour)
            {
                nameText.color = style.TextColour;
                if (icon) icon.color = style.Colour;
                tinted = true;
            }
            else RestoreTint();

            if (settings.AgencyTrackingListFlags && style.Flag) ShowFlag(style.Flag);
            else HideFlag();
        }

        private void ClearStyle()
        {
            if (!initialised) return;
            RestoreTint();
            HideFlag();
        }

        private void RestoreTint()
        {
            if (!tinted) return;
            tinted = false;
            if (nameText) nameText.color = originalNameColour;
            if (icon) icon.color = originalIconColour;
        }

        private void ShowFlag(Texture2D texture)
        {
            if (!flagObject) CreateFlag();
            if (flagImage.texture != texture) flagImage.texture = texture;
            if (flagShown) return;
            flagShown = true;
            flagObject.SetActive(true);
            var margin = originalMargin;
            margin.x += MarginShift;
            nameText.margin = margin;
        }

        private void HideFlag()
        {
            if (!flagShown) return;
            flagShown = false;
            if (flagObject) flagObject.SetActive(false);
            if (nameText) nameText.margin = originalMargin;
        }

        private void CreateFlag()
        {
            // Child of the name text so it anchors to the left edge of the very rect whose margin is shifted.
            flagObject = new GameObject("LmpAgencyFlag", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage), typeof(LayoutElement));
            var rect = (RectTransform)flagObject.transform;
            rect.SetParent(nameText.rectTransform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.sizeDelta = new Vector2(FlagWidth, FlagHeight);
            rect.anchoredPosition = new Vector2(FlagInset, 0f);
            flagObject.GetComponent<LayoutElement>().ignoreLayout = true;
            flagImage = flagObject.GetComponent<RawImage>();
            flagImage.raycastTarget = false;
            flagObject.SetActive(false);
        }
    }
}
