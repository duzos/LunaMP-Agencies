namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// Decides when an IMGUI cache may rebuild: only on a Layout event, at most once per frame (Time.frameCount), and only when it is due
    /// (the interval passed or it was invalidated). Every Layout and event pass of one frame, in every window, then reads the same data, so
    /// the control count never changes between a Layout and the event that follows it. Pure (no Unity calls) so LmpCommonTest can link it.
    /// </summary>
    internal sealed class FrameRefreshGate
    {
        private readonly float interval;
        private int lastFrame = int.MinValue;
        private float nextDue = float.MinValue;
        private bool invalidated = true;

        internal FrameRefreshGate(float interval) { this.interval = interval; }

        /// <summary>Asks for a rebuild at the next allowed Layout event; never rebuilds in the current frame if it already did.</summary>
        internal void Invalidate() => invalidated = true;

        /// <summary>True when the caller should rebuild now. Consumes the rebuild, so a second call in the same frame returns false.</summary>
        internal bool TryBegin(int frame, bool isLayout, float now)
        {
            if (!isLayout || frame == lastFrame) return false;
            if (!invalidated && now < nextDue) return false;
            lastFrame = frame;
            nextDue = now + interval;
            invalidated = false;
            return true;
        }
    }
}
