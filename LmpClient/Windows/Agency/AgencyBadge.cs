using System;
using LmpClient.Systems.Agency;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// IMGUI agency flag helpers (plan 41 S0). Every read comes from <see cref="AgencyPresentation"/>;
    /// nothing allocates per call. Callers gate their own feature toggle.
    /// </summary>
    internal static class AgencyBadge
    {
        /// <summary>
        /// Draws the agency flag in the current GUILayout group at <paramref name="width"/> x <paramref name="height"/>.
        /// No-op (no space taken) when the agency has no flag. Returns true when a flag slot was laid out.
        /// </summary>
        internal static bool DrawFlag(Guid agency, float width, float height) => DrawFlag(agency, width, height, false);

        /// <summary>
        /// As <see cref="DrawFlag(Guid, float, float)"/>; with <paramref name="reserveSpace"/> the slot is laid
        /// out even without a flag so rows stay aligned.
        /// </summary>
        internal static bool DrawFlag(Guid agency, float width, float height, bool reserveSpace) => DrawFlag(agency, width, height, reserveSpace, null);

        /// <summary>
        /// As <see cref="DrawFlag(Guid, float, float, bool)"/>; with <paramref name="alignTo"/> the slot takes that
        /// style's vertical margin and one-line height and the flag is centred in it, so it lines up with the first
        /// line of a label drawn with <paramref name="alignTo"/> in the same horizontal group.
        /// </summary>
        internal static bool DrawFlag(Guid agency, float width, float height, bool reserveSpace, GUIStyle alignTo)
        {
            // Frozen per frame so Layout and Repaint always lay out the same rects even if the flag loads mid-frame.
            var flag = GetFrameFlag(agency);
            if (!flag && !reserveSpace) return false;
            Rect rect;
            if (alignTo == null) rect = GUILayoutUtility.GetRect(width, width, height, height);
            else
            {
                if (slotStyle == null) slotStyle = new GUIStyle { margin = new RectOffset(0, 4, 0, 0), padding = new RectOffset(0, 0, 0, 0) };
                slotStyle.margin.top = alignTo.margin.top;
                slotStyle.margin.bottom = alignTo.margin.bottom;
                var line = alignTo.fixedHeight > 0 ? alignTo.fixedHeight : alignTo.lineHeight + alignTo.padding.vertical;
                var slotHeight = Mathf.Max(height, line);
                rect = GUILayoutUtility.GetRect(width, width, slotHeight, slotHeight, slotStyle);
                rect = new Rect(rect.x, rect.y + (rect.height - height) * 0.5f, width, height);
            }
            if (flag && Event.current.type == EventType.Repaint) GUI.DrawTexture(rect, flag, ScaleMode.ScaleToFit);
            return true;
        }

        private static GUIStyle slotStyle;

        /// <summary>Draws the agency flag into <paramref name="rect"/> (Repaint only). Returns true when the agency has a flag.</summary>
        internal static bool DrawFlag(Rect rect, Guid agency)
        {
            var flag = GetFlag(agency);
            if (!flag) return false;
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(rect, flag, ScaleMode.ScaleToFit);
            return true;
        }

        /// <summary>Fills <paramref name="rect"/> with the agency colour (Repaint only). Returns true when the agency has a colour.</summary>
        internal static bool DrawColourBar(Rect rect, Guid agency)
        {
            if (!AgencyPresentation.TryGetAgencyStyle(agency, out var style) || !style.HasColour) return false;
            if (Event.current.type != EventType.Repaint) return true;
            var previous = GUI.color;
            GUI.color = style.Colour;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
            return true;
        }

        private static Texture2D GetFlag(Guid agency) =>
            AgencyPresentation.TryGetAgencyStyle(agency, out var style) ? style.Flag : null;

        private static Texture2D GetFrameFlag(Guid agency)
        {
            // The out style is the cache entry even for a not-yet-known agency, so the freeze also covers
            // an agency becoming known between Layout and Repaint.
            var known = AgencyPresentation.TryGetAgencyStyle(agency, out var style);
            if (style == null) return null;
            var frame = Time.frameCount;
            if (style.LayoutFrame != frame) { style.LayoutFrame = frame; style.LayoutFlag = known ? style.Flag : null; }
            return style.LayoutFlag;
        }
    }
}
