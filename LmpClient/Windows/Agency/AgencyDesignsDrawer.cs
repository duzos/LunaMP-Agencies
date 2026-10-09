using LmpClient.Systems.Agency;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        /// <summary>The Tooled designs tab (plan 40): build stock, search and sort, and load a design into the editor. Slice S4 owns the body.</summary>
        private static void DrawDesignsTab()
        {
            GUILayout.Label(ToolingClient.Enabled ? "Tooled designs are coming soon." : "Tooling is off on this server.");
        }
    }
}
