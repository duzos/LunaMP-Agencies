using System;
using System.Globalization;
using KspControl.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// The parsed arguments of flight.node_create, flight.node_update, flight.node_delete and flight.warp_to. Pure: the bridge checks every bound again
    /// (the host checked them first), and <see cref="Canonical"/> is what the request fingerprint covers.
    /// </summary>
    internal sealed class NavigationRequest
    {
        public string TimeReference;
        public double? TimeSeconds;
        public double? Prograde, Normal, Radial;
        public int NodeIndex = -1;
        public bool All;
        public string WarpTarget;
        public double LeadSeconds = NavigationLimits.DefaultLeadSeconds;
        public JObject Requested = new JObject();
        public string Canonical { get { return Requested.ToString(Formatting.None); } }

        private static readonly string[] CreateNames = { "requestId", "timeReference", "timeSeconds", "prograde", "normal", "radial" };
        private static readonly string[] UpdateNames = { "requestId", "nodeIndex", "timeReference", "timeSeconds", "prograde", "normal", "radial" };
        private static readonly string[] DeleteNames = { "requestId", "nodeIndex", "all" };
        private static readonly string[] WarpNames = { "requestId", "target", "timeSeconds", "leadSeconds", "nodeIndex" };

        /// <summary>Returns the invalid_argument detail, or null with <paramref name="parsed"/> set.</summary>
        public static string Parse(AutopilotKind kind, JObject args, out NavigationRequest parsed)
        {
            parsed = null;
            var r = new NavigationRequest();
            string[] allowed;
            switch (kind)
            {
                case AutopilotKind.NodeCreate: allowed = CreateNames; break;
                case AutopilotKind.NodeUpdate: allowed = UpdateNames; break;
                case AutopilotKind.NodeDelete: allowed = DeleteNames; break;
                case AutopilotKind.WarpTo: allowed = WarpNames; break;
                default: return "unsupported operation";
            }
            foreach (var property in args.Properties())
                if (Array.IndexOf(allowed, property.Name) < 0 && property.Value.Type != JTokenType.Null) return property.Name + " is not an argument of this operation";
            string error;
            switch (kind)
            {
                case AutopilotKind.NodeCreate:
                    if ((error = ReadTime(args, r, true)) != null) return error;
                    if ((error = ReadDeltaV(args, r)) != null) return error;
                    error = NavigationLimits.CheckDeltaV(r.Prograde ?? 0, r.Normal ?? 0, r.Radial ?? 0);
                    if (error != null) return error;
                    break;
                case AutopilotKind.NodeUpdate:
                    if ((error = ReadIndex(args, r, true)) != null) return error;
                    if ((error = ReadTime(args, r, false)) != null) return error;
                    if ((error = ReadDeltaV(args, r)) != null) return error;
                    if (r.TimeReference == null && !r.Prograde.HasValue && !r.Normal.HasValue && !r.Radial.HasValue) return "give timeReference or at least one of prograde, normal, radial";
                    break;
                case AutopilotKind.NodeDelete:
                {
                    var all = args["all"];
                    if (all != null && all.Type != JTokenType.Null)
                    {
                        if (all.Type != JTokenType.Boolean) return "all must be true or false";
                        r.All = (bool)all;
                    }
                    var hasIndex = args["nodeIndex"] != null && args["nodeIndex"].Type != JTokenType.Null;
                    if (r.All == hasIndex) return "give exactly one of nodeIndex and all=true";
                    if (hasIndex && (error = ReadIndex(args, r, true)) != null) return error;
                    r.Requested["all"] = r.All;
                    break;
                }
                default:
                {
                    var target = args["target"];
                    if (target == null || target.Type != JTokenType.String || !NavigationLimits.IsWarpTarget((string)target)) return "target must be one of " + string.Join(", ", NavigationLimits.WarpTargets);
                    r.WarpTarget = (string)target; r.Requested["target"] = r.WarpTarget;
                    double? number;
                    if ((error = Number(args, "timeSeconds", out number)) != null) return error;
                    r.TimeSeconds = number;
                    if (r.WarpTarget == "node" || r.WarpTarget == "soi")
                    {
                        if (r.TimeSeconds.HasValue) return "timeSeconds is not used with target " + r.WarpTarget;
                    }
                    else
                    {
                        if ((error = NavigationLimits.CheckTime(r.WarpTarget, r.TimeSeconds)) != null) return error;
                        if (r.TimeSeconds.HasValue) r.Requested["timeSeconds"] = r.TimeSeconds.Value;
                    }
                    if ((error = Number(args, "leadSeconds", out number)) != null) return error;
                    if (number.HasValue)
                    {
                        if (number.Value < 0 || number.Value > NavigationLimits.MaxLeadSeconds) return "leadSeconds must be 0.." + NavigationLimits.MaxLeadSeconds;
                        r.LeadSeconds = number.Value;
                    }
                    r.Requested["leadSeconds"] = r.LeadSeconds;
                    var hasIndex = args["nodeIndex"] != null && args["nodeIndex"].Type != JTokenType.Null;
                    if (hasIndex && r.WarpTarget != "node") return "nodeIndex is only used with target node";
                    if (r.WarpTarget == "node")
                    {
                        if (hasIndex) { if ((error = ReadIndex(args, r, true)) != null) return error; }
                        else { r.NodeIndex = 0; r.Requested["nodeIndex"] = 0; }
                    }
                    break;
                }
            }
            parsed = r;
            return null;
        }

        private static string ReadTime(JObject args, NavigationRequest r, bool required)
        {
            var reference = args["timeReference"];
            double? seconds;
            var error = Number(args, "timeSeconds", out seconds);
            if (error != null) return error;
            if (reference == null || reference.Type == JTokenType.Null)
            {
                if (required) return "timeReference is required: one of " + string.Join(", ", NavigationLimits.TimeReferences);
                return seconds.HasValue ? "timeSeconds needs timeReference" : null;
            }
            if (reference.Type != JTokenType.String || !NavigationLimits.IsTimeReference((string)reference)) return "timeReference must be one of " + string.Join(", ", NavigationLimits.TimeReferences);
            r.TimeReference = (string)reference; r.TimeSeconds = seconds;
            if ((error = NavigationLimits.CheckTime(r.TimeReference, r.TimeSeconds)) != null) return error;
            r.Requested["timeReference"] = r.TimeReference;
            if (seconds.HasValue) r.Requested["timeSeconds"] = seconds.Value;
            return null;
        }

        private static string ReadDeltaV(JObject args, NavigationRequest r)
        {
            string error; double? value;
            if ((error = Component(args, "prograde", out value)) != null) return error; r.Prograde = value;
            if ((error = Component(args, "normal", out value)) != null) return error; r.Normal = value;
            if ((error = Component(args, "radial", out value)) != null) return error; r.Radial = value;
            if (r.Prograde.HasValue) r.Requested["prograde"] = r.Prograde.Value;
            if (r.Normal.HasValue) r.Requested["normal"] = r.Normal.Value;
            if (r.Radial.HasValue) r.Requested["radial"] = r.Radial.Value;
            return null;
        }

        private static string Component(JObject args, string name, out double? value)
        {
            var error = Number(args, name, out value);
            if (error != null) return error;
            if (value.HasValue && Math.Abs(value.Value) > NavigationLimits.MaxDeltaVMetersPerSecond)
                return name + " must be -" + NavigationLimits.MaxDeltaVMetersPerSecond.ToString(CultureInfo.InvariantCulture) + ".." + NavigationLimits.MaxDeltaVMetersPerSecond.ToString(CultureInfo.InvariantCulture) + " m/s";
            return null;
        }

        private static string ReadIndex(JObject args, NavigationRequest r, bool required)
        {
            var token = args["nodeIndex"];
            if (token == null || token.Type == JTokenType.Null) return required ? "nodeIndex is required" : null;
            if (token.Type != JTokenType.Integer) return "nodeIndex must be an integer";
            var index = (long)token;
            if (index < 0 || index > NavigationLimits.MaxNodeIndex) return "nodeIndex must be 0.." + NavigationLimits.MaxNodeIndex;
            r.NodeIndex = (int)index; r.Requested["nodeIndex"] = r.NodeIndex;
            return null;
        }

        private static string Number(JObject args, string name, out double? value)
        {
            value = null;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return name + " must be a number";
            var number = (double)token;
            if (!NavigationLimits.IsFinite(number)) return name + " must be a finite number";
            value = number;
            return null;
        }
    }
}
