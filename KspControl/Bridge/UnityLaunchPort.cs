using System;
using System.Reflection;

namespace KspControl.Bridge
{
    /// <summary>
    /// The Unity and KSP side of a launch. Everything agency-specific goes through the LunaMP control facade by reflection (the bridge has no
    /// reference to LmpClient), and each member fails closed: an absent or older facade means the launch is unavailable, never a guess.
    /// </summary>
    internal sealed class UnityLaunchPort : ILaunchPort
    {
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private static readonly Type Facade = Type.GetType("LmpClient.Systems.Agency.ControlObservation, LmpClient", false);

        private static object Property(string name)
        {
            try { return Facade == null ? null : Facade.GetProperty(name, Static)?.GetValue(null, null); }
            catch (Exception) { return null; }
        }

        private static double? Number(string name)
        {
            var value = Property(name);
            if (!(value is double)) return null;
            var number = (double)value;
            return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
        }

        public bool FacadeAvailable
        {
            get
            {
                try
                {
                    if (Facade == null) return false;
                    var version = (int?)Facade.GetField("ApiVersion", Static)?.GetRawConstantValue();
                    return version.HasValue && version.Value >= 2 && Facade.GetMethod("BeginLaunch", Static) != null && Facade.GetMethod("TryQuoteEditorCraft", Static) != null;
                }
                catch (Exception) { return false; }
            }
        }

        public string Scene { get { return HighLogic.LoadedScene.ToString().ToUpperInvariant(); } }

        private static Guid Agency
        {
            get { var value = Property("AgencyId"); return value is Guid ? (Guid)value : Guid.Empty; }
        }

        public bool Connected { get { return Agency != Guid.Empty; } }

        public LaunchQuote Quote()
        {
            try
            {
                var method = Facade == null ? null : Facade.GetMethod("TryQuoteEditorCraft", Static);
                if (method == null) return new LaunchQuote { Reason = "facade_unavailable" };
                var args = new object[5];
                var ok = (bool)method.Invoke(null, args);
                if (!ok) return new LaunchQuote { Success = false, Reason = args[4] as string ?? "quote_failed" };
                return new LaunchQuote { Success = true, LaunchCost = (double)args[0], ToolingCost = (double)args[1], AlreadyTooled = (bool)args[2], Fingerprint = args[3] as string };
            }
            catch (Exception error) { return new LaunchQuote { Reason = "quote_error:" + error.GetType().Name }; }
        }

        public bool Allowed(out string reason)
        {
            reason = null;
            try
            {
                var method = Facade == null ? null : Facade.GetMethod("EditorLaunchAllowance", Static);
                if (method == null) { reason = "facade_unavailable"; return false; }
                var args = new object[1];
                var ok = (bool)method.Invoke(null, args);
                reason = args[0] as string;
                return ok;
            }
            catch (Exception error) { reason = "allowance_error:" + error.GetType().Name; return false; }
        }

        public bool LaunchPending { get { var value = Property("LaunchPending"); return value is bool && (bool)value; } }

        public double? Funds { get { return Number("AgencyFunds"); } }

        public string LaunchStatus { get { return Property("LaunchStatus") as string; } }

        public long ChargeSerial { get { var value = Property("LaunchChargeSerial"); return value is long ? (long)value : 0; } }

        public double? LastCharge { get { return Number("LastLaunchCharge"); } }

        public bool BeginLaunch(string site, out string reason)
        {
            reason = null;
            var method = Facade == null ? null : Facade.GetMethod("BeginLaunch", Static);
            if (method == null) { reason = "facade_unavailable"; return false; }
            var args = new object[] { site, null };
            var ok = (bool)method.Invoke(null, args);
            reason = args[1] as string;
            return ok;
        }

        public bool CancelPendingLaunch()
        {
            try
            {
                var method = Facade == null ? null : Facade.GetMethod("CancelPendingLaunch", Static);
                return method != null && (bool)method.Invoke(null, null);
            }
            catch (Exception) { return false; }
        }

        public LaunchVessel ActiveVessel
        {
            get
            {
                if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return null;
                var vessel = FlightGlobals.ActiveVessel;
                if (vessel == null) return null;
                return new LaunchVessel { Id = vessel.id.ToString("D"), Name = vessel.vesselName, Prelaunch = vessel.situation == Vessel.Situations.PRELAUNCH };
            }
        }

        public bool VesselOwned(string vesselId)
        {
            Guid id;
            if (!Guid.TryParse(vesselId, out id)) return false;
            if (!Connected) return true; // a single-player save has no ownership records; the grant binding already says "offline"
            try
            {
                var method = Facade == null ? null : Facade.GetMethod("MayInspectActiveVessel", Static);
                return method != null && (bool)method.Invoke(null, new object[] { id });
            }
            catch (Exception) { return false; }
        }
    }
}
