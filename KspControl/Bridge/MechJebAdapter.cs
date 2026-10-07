using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>Reflection helpers. Members are looked up by name per call and cached by (type, name) only: never an object.</summary>
    internal static class Reflect
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        private static readonly Dictionary<string, MemberInfo> Members = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        public static MemberInfo Member(Type type, string name)
        {
            var key = type.AssemblyQualifiedName + "|" + name;
            MemberInfo found;
            lock (Members)
            {
                if (Members.TryGetValue(key, out found)) return found;
                found = type.GetMember(name, Flags).FirstOrDefault(m => m is FieldInfo || (m is PropertyInfo && ((PropertyInfo)m).GetIndexParameters().Length == 0));
                Members[key] = found;
            }
            return found;
        }

        public static bool Has(Type type, string name) { return type != null && Member(type, name) != null; }

        /// <summary>The most derived method of that name and parameter count: a "new" method such as UserPool.Add hides the list's own.</summary>
        public static MethodInfo Method(Type type, string name, int parameters)
        {
            var key = type.AssemblyQualifiedName + "|" + name + "/" + parameters;
            MethodInfo found;
            lock (Methods)
            {
                if (Methods.TryGetValue(key, out found)) return found;
                found = type.GetMethods(Flags).Where(m => m.Name == name && !m.IsGenericMethodDefinition && m.GetParameters().Length == parameters)
                    .OrderByDescending(m => Depth(m.DeclaringType)).FirstOrDefault();
                Methods[key] = found;
            }
            return found;
        }

        public static bool HasMethod(Type type, string name, int parameters) { return type != null && Method(type, name, parameters) != null; }

        private static int Depth(Type type)
        {
            var depth = 0;
            for (var t = type; t != null; t = t.BaseType) depth++;
            return depth;
        }

        public static object Get(object target, string name)
        {
            var member = target == null ? null : Member(target.GetType(), name);
            if (member == null) throw new MechJebException(KspControl.Contracts.AutopilotReasons.MechJebModuleUnavailable, "missing member " + name);
            var field = member as FieldInfo;
            return field != null ? field.GetValue(target) : ((PropertyInfo)member).GetValue(target, null);
        }

        public static void Set(object target, string name, object value)
        {
            var member = target == null ? null : Member(target.GetType(), name);
            if (member == null) throw new MechJebException(KspControl.Contracts.AutopilotReasons.MechJebModuleUnavailable, "missing member " + name);
            var field = member as FieldInfo;
            if (field != null) field.SetValue(target, value); else ((PropertyInfo)member).SetValue(target, value, null);
        }

        public static object Call(object target, string name, params object[] args)
        {
            var method = target == null ? null : Method(target.GetType(), name, args.Length);
            if (method == null) throw new MechJebException(KspControl.Contracts.AutopilotReasons.MechJebModuleUnavailable, "missing method " + name);
            return method.Invoke(target, args);
        }

        public static object StaticProperty(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property != null) return property.GetValue(null, null);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return field?.GetValue(null);
        }

        public static double Number(object value) { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// The guarded reflection adapter over the installed MechJeb (2.15.x: MuMech.MechJebCore with Ascent, AscentSettings, Node, Landing, GetComputerModule).
    /// There is no compile-time reference. The types and members are resolved once and recorded as capability flags; each call then re-reads the live
    /// objects. Nothing here touches Unity types, so it is tested against look-alike classes.
    /// </summary>
    internal sealed class MechJebAdapter : IMechJebPort
    {
        private const string CoreTypeName = "MuMech.MechJebCore";
        private const string AtmosphereTypeName = "AtmosphereAutopilot.AtmosphereAutopilot";

        private readonly Func<object> coreSource;
        private readonly Func<object> vesselSource;
        private readonly Func<Type> coreTypeSource;
        private readonly Func<Type> atmosphereTypeSource;
        private readonly Func<Type, Version> versionSource;
        private MechJebCapabilities capabilities;
        private Type coreType, atmosphereType;
        private readonly Dictionary<string, Type> moduleTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

        /// <summary>Module name, how it is fetched from the core, the type name inside the MechJeb namespace, and the members the adapter needs.</summary>
        private sealed class ModuleSpec
        {
            public string Name, CoreMember, TypeName;
            public string[] Members;
            public bool ViaGeneric;
            /// <summary>An autopilot: engaged when enabled. Others count as engaged only with a foreign user.</summary>
            public bool Autopilot;
            /// <summary>Takes part in the competing-controller scan.</summary>
            public bool Scanned;
        }

        private static readonly ModuleSpec[] Specs =
        {
            new ModuleSpec { Name = "ascent", CoreMember = "Ascent", TypeName = "MechJebModuleAscentBaseAutopilot", Members = new[] { "Enabled", "Users", "Status" }, Autopilot = true, Scanned = true },
            new ModuleSpec { Name = "ascentSettings", CoreMember = "AscentSettings", TypeName = "MechJebModuleAscentSettings", Members = new[] { "DesiredOrbitAltitude", "DesiredInclination", "Autostage" } },
            new ModuleSpec { Name = "node", CoreMember = "Node", TypeName = "MechJebModuleNodeExecutor", Members = new[] { "Enabled", "Users", "State", "Autowarp", "ExecuteOneNode", "ExecuteAllNodes", "Abort" }, Autopilot = true, Scanned = true },
            new ModuleSpec { Name = "landing", CoreMember = "Landing", TypeName = "MechJebModuleLandingAutopilot", Members = new[] { "Enabled", "Users" }, Autopilot = true, Scanned = true },
            new ModuleSpec { Name = "rendezvous", TypeName = "MechJebModuleRendezvousAutopilot", Members = new[] { "Enabled", "Users" }, ViaGeneric = true, Autopilot = true, Scanned = true },
            new ModuleSpec { Name = "docking", TypeName = "MechJebModuleDockingAutopilot", Members = new[] { "Enabled", "Users" }, ViaGeneric = true, Autopilot = true, Scanned = true },
            new ModuleSpec { Name = "spaceplane", TypeName = "MechJebModuleSpaceplaneAutopilot", Members = new[] { "Enabled", "Users" }, ViaGeneric = true, Autopilot = true, Scanned = true },
            new ModuleSpec { Name = "airplane", CoreMember = "Airplane", TypeName = "MechJebModuleAirplaneAutopilot", Members = new[] { "Enabled", "Users" }, Autopilot = true, Scanned = true },
            new ModuleSpec { Name = "attitude", CoreMember = "Attitude", TypeName = "MechJebModuleAttitudeController", Members = new[] { "Enabled", "Users" }, Scanned = true },
            new ModuleSpec { Name = "rover", CoreMember = "Rover", TypeName = "MechJebModuleRoverController", Members = new[] { "Enabled", "Users" }, Scanned = true },
            new ModuleSpec { Name = "thrust", CoreMember = "Thrust", TypeName = "MechJebModuleThrustController", Members = new[] { "Enabled", "Users", "ThrustOff" } },
            new ModuleSpec { Name = "warp", CoreMember = "Warp", TypeName = "MechJebModuleWarpController", Members = new[] { "Enabled" } }
        };

        public MechJebAdapter(Func<object> coreSource, Func<object> vesselSource = null, Func<Type> coreTypeSource = null, Func<Type> atmosphereTypeSource = null, Func<Type, Version> versionSource = null)
        {
            this.coreSource = coreSource ?? throw new ArgumentNullException(nameof(coreSource));
            this.vesselSource = vesselSource ?? (() => null);
            this.coreTypeSource = coreTypeSource ?? (() => FindType(CoreTypeName));
            this.atmosphereTypeSource = atmosphereTypeSource ?? (() => FindType(AtmosphereTypeName));
            this.versionSource = versionSource ?? InstalledVersion;
        }

        /// <summary>
        /// The installed MechJeb2.dll has AssemblyVersion 2.5.1.0 but file version 2.15.2.0, so the file version attribute is the one that names the release.
        /// Falls back to the informational version, then the assembly version.
        /// </summary>
        internal static Version InstalledVersion(Type type)
        {
            var assembly = type.Assembly;
            try
            {
                var file = assembly.GetCustomAttributes(typeof(AssemblyFileVersionAttribute), false).OfType<AssemblyFileVersionAttribute>().FirstOrDefault();
                Version parsed;
                if (file != null && Version.TryParse(file.Version, out parsed)) return parsed;
                var info = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
                if (info != null)
                {
                    var text = info.InformationalVersion ?? "";
                    var cut = text.IndexOfAny(new[] { '+', '-', ' ' });
                    if (Version.TryParse(cut > 0 ? text.Substring(0, cut) : text, out parsed)) return parsed;
                }
            }
            catch (Exception) { /* fall through to the assembly version */ }
            return assembly.GetName().Version;
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var type = assembly.GetType(fullName, false); if (type != null) return type; }
                catch (Exception) { /* a dynamic or broken assembly: skip */ }
            }
            return null;
        }

        // ---------------------------------------------------------------- capabilities (resolved once)

        public MechJebCapabilities Capabilities
        {
            get
            {
                if (capabilities != null) return capabilities;
                var result = new MechJebCapabilities();
                try
                {
                    coreType = coreTypeSource();
                    atmosphereType = atmosphereTypeSource();
                    result.AtmosphereAutopilotInstalled = atmosphereType != null;
                    if (coreType == null) { result.Reason = KspControl.Contracts.AutopilotReasons.MechJebUnavailable; }
                    else
                    {
                        result.Installed = true;
                        var version = versionSource(coreType);
                        result.Version = version == null ? null : version.ToString();
                        result.VersionSupported = version != null && version.Major == 2 && version.Minor == 15;
                        if (!result.VersionSupported) result.Reason = KspControl.Contracts.AutopilotReasons.MechJebVersionUnsupported;
                        ResolveModules(result);
                    }
                }
                catch (Exception) { result.Reason = KspControl.Contracts.AutopilotReasons.MechJebUnavailable; result.Installed = false; }
                capabilities = result;
                return result;
            }
        }

        private void ResolveModules(MechJebCapabilities result)
        {
            var ns = coreType.Namespace;
            foreach (var spec in Specs)
            {
                var type = coreType.Assembly.GetType(ns + "." + spec.TypeName, false);
                moduleTypes[spec.Name] = type;
                var ok = type != null && spec.Members.All(m => Reflect.Has(type, m) || Reflect.Method(type, m, MethodArity(m)) != null);
                if (ok && spec.ViaGeneric) ok = coreType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == "GetComputerModule" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                else if (ok) ok = Reflect.Has(coreType, spec.CoreMember);
                result.Modules[spec.Name] = ok;
            }
            // The core itself must offer the lookups the adapter and the status rely on.
            if (!Reflect.Has(coreType, "MasterMechJeb")) result.Modules["ascent"] = false;
            var operations = coreType.Assembly.GetType(ns + ".Operation", false);
            if (operations != null)
                foreach (var name in new[] { "OperationCircularize", "OperationInterplanetaryTransfer", "OperationAdvancedTransfer", "OperationLambert" })
                    if (coreType.Assembly.GetType(ns + "." + name, false) != null) result.PlannerOperations.Add(name);
        }

        /// <summary>Methods in the member lists take no parameter, except the node executor's two which take the controller object.</summary>
        private static int MethodArity(string name) { return name == "ExecuteOneNode" || name == "ExecuteAllNodes" ? 1 : 0; }

        // ---------------------------------------------------------------- live objects (never cached)

        public bool HasCore() { try { return Core() != null; } catch (Exception) { return false; } }

        private object Core()
        {
            var raw = coreSource();
            if (raw == null) return null;
            // A vessel can carry several cores; the master is the one that drives.
            var master = Reflect.Has(raw.GetType(), "MasterMechJeb") ? Reflect.Get(raw, "MasterMechJeb") : null;
            return master ?? raw;
        }

        private object Module(object core, string name)
        {
            var spec = Specs.First(s => s.Name == name);
            Type type;
            if (!Capabilities.Has(name) || !moduleTypes.TryGetValue(name, out type) || type == null) return null;
            if (spec.ViaGeneric)
            {
                var generic = coreType.GetMethods(BindingFlags.Public | BindingFlags.Instance).First(m => m.Name == "GetComputerModule" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                return generic.MakeGenericMethod(type).Invoke(core, null);
            }
            return Reflect.Get(core, spec.CoreMember);
        }

        private object Require(string name)
        {
            if (!Capabilities.Usable) throw new MechJebException(Capabilities.Reason ?? KspControl.Contracts.AutopilotReasons.MechJebUnavailable);
            var core = Core();
            if (core == null) throw new MechJebException(KspControl.Contracts.AutopilotReasons.MechJebUnavailable, "the active vessel has no MechJeb core");
            var module = Module(core, name);
            if (module == null) throw new MechJebException(KspControl.Contracts.AutopilotReasons.MechJebModuleUnavailable, name);
            return module;
        }

        // ---------------------------------------------------------------- users

        private static void Users(object module, object own, out bool ownPresent, out int others)
        {
            ownPresent = false; others = 0;
            var list = Reflect.Get(module, "Users") as IEnumerable;
            if (list == null) return;
            foreach (var user in list) { if (ReferenceEquals(user, own)) ownPresent = true; else others++; }
        }

        private static bool Enabled(object module) { return (bool)Reflect.Get(module, "Enabled"); }

        private static void Engage(object module, object user)
        {
            Reflect.Call(Reflect.Get(module, "Users"), "Add", user);
            // Adding the first user enables the module in MechJeb; set it explicitly if that did not happen.
            if (!Enabled(module)) Reflect.Set(module, "Enabled", true);
            if (!Enabled(module)) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "the module did not enable");
        }

        private static void Disengage(object module, object user)
        {
            var errors = 0;
            try { Reflect.Call(Reflect.Get(module, "Users"), "Remove", user); } catch (Exception) { errors++; }
            try
            {
                bool own; int others; Users(module, user, out own, out others);
                if (!own && others == 0 && Enabled(module)) Reflect.Set(module, "Enabled", false);
            }
            catch (Exception) { errors++; }
            if (errors == 2) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "could not release the module");
        }

        // ---------------------------------------------------------------- competitors

        public List<string> FindCompetitors(object ownUser, bool autopilotsOnly)
        {
            var found = new List<string>();
            if (Capabilities.Installed)
            {
                try
                {
                    var core = Core();
                    if (core != null)
                        foreach (var spec in Specs.Where(s => s.Scanned && (!autopilotsOnly || s.Autopilot) && Capabilities.Has(s.Name)))
                        {
                            try
                            {
                                var module = Module(core, spec.Name);
                                if (module == null) continue;
                                bool own; int others; Users(module, ownUser, out own, out others);
                                var engaged = spec.Autopilot ? (Enabled(module) && (others > 0 || !own)) : others > 0;
                                if (engaged) found.Add("mechjeb." + spec.Name);
                            }
                            catch (Exception) { found.Add("mechjeb." + spec.Name + ":unreadable"); }
                        }
                }
                catch (Exception) { found.Add("mechjeb:unreadable"); }
            }
            AtmosphereAutopilot(found);
            return found;
        }

        private void AtmosphereAutopilot(List<string> found)
        {
            if (Capabilities.AtmosphereAutopilotInstalled == false) return;
            try
            {
                var instance = Reflect.StaticProperty(atmosphereType, "Instance");
                var vessel = vesselSource();
                if (instance == null || vessel == null) return;
                var modules = Reflect.Call(instance, "getVesselModules", vessel) as IDictionary;
                if (modules == null) return;
                foreach (var module in modules.Values)
                    if (module != null && Reflect.Has(module.GetType(), "Active") && (bool)Reflect.Get(module, "Active"))
                        found.Add("atmosphere_autopilot." + (Reflect.Has(module.GetType(), "ModuleName") ? Convert.ToString(Reflect.Get(module, "ModuleName"), CultureInfo.InvariantCulture) : module.GetType().Name));
            }
            catch (Exception) { found.Add("atmosphere_autopilot:unreadable"); }
        }

        // ---------------------------------------------------------------- ascent

        public AscentSettingsView ConfigureAscent(double altitudeMeters, double inclinationDegrees, bool autostage)
        {
            var settings = Require("ascentSettings");
            Reflect.Set(Reflect.Get(settings, "DesiredOrbitAltitude"), "Val", altitudeMeters);
            Reflect.Set(Reflect.Get(settings, "DesiredInclination"), "Val", inclinationDegrees);
            Reflect.Set(settings, "Autostage", autostage);
            // The bridge owns the end of the flight: never let the module skip the circularization it is being asked for.
            if (Reflect.Has(settings.GetType(), "SkipCircularization")) Reflect.Set(settings, "SkipCircularization", false);
            var view = ReadSettings(settings);
            if (Math.Abs(view.TargetAltitudeMeters.GetValueOrDefault(double.NaN) - altitudeMeters) > 0.5 || Math.Abs(view.InclinationDegrees.GetValueOrDefault(double.NaN) - inclinationDegrees) > 1e-6 || view.Autostage != autostage)
                throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "MechJeb did not keep the ascent settings");
            return view;
        }

        private static AscentSettingsView ReadSettings(object settings)
        {
            var view = new AscentSettingsView();
            try { view.TargetAltitudeMeters = Reflect.Number(Reflect.Get(Reflect.Get(settings, "DesiredOrbitAltitude"), "Val")); } catch (Exception) { }
            try { view.InclinationDegrees = Reflect.Number(Reflect.Get(Reflect.Get(settings, "DesiredInclination"), "Val")); } catch (Exception) { }
            try { view.Autostage = (bool)Reflect.Get(settings, "Autostage"); } catch (Exception) { }
            try { view.SkipCircularization = (bool)Reflect.Get(settings, "SkipCircularization"); } catch (Exception) { }
            try { view.AscentType = Convert.ToString(Reflect.Get(settings, "AscentType"), CultureInfo.InvariantCulture); } catch (Exception) { }
            return view;
        }

        public void EngageAscent(object user) { Engage(Require("ascent"), user); }
        public void DisengageAscent(object user) { Disengage(Require("ascent"), user); }

        public AscentReading ReadAscent(object user)
        {
            var module = Require("ascent");
            bool own; int others; Users(module, user, out own, out others);
            string status = null;
            try { status = Reflect.Get(module, "Status") as string; } catch (Exception) { }
            return new AscentReading { Enabled = Enabled(module), Status = status, OwnUserPresent = own, OtherUsers = others };
        }

        // ---------------------------------------------------------------- node executor

        public void EngageNode(object user, bool all)
        {
            var node = Require("node");
            // The agent tools never warp: the executor's own warping would bypass the bridge's warp policy.
            Reflect.Set(node, "Autowarp", false);
            if (all) Reflect.Call(node, "ExecuteAllNodes", user); else Reflect.Call(node, "ExecuteOneNode", user);
            if (!Enabled(node)) { try { Reflect.Set(node, "Enabled", true); } catch (Exception) { } }
            if (!Enabled(node)) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "the node executor did not enable");
        }

        public void DisengageNode(object user)
        {
            var node = Require("node");
            var errors = 0;
            try { Reflect.Call(node, "Abort"); } catch (Exception) { errors++; }
            try { Disengage(node, user); } catch (MechJebException) { errors++; }
            if (errors == 2) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "could not release the node executor");
        }

        public NodeReading ReadNode(object user)
        {
            var node = Require("node");
            bool own; int others; Users(node, user, out own, out others);
            var reading = new NodeReading { Enabled = Enabled(node), OwnUserPresent = own, OtherUsers = others };
            try { reading.State = Convert.ToString(Reflect.Get(node, "State"), CultureInfo.InvariantCulture); } catch (Exception) { }
            try { reading.Autowarp = (bool)Reflect.Get(node, "Autowarp"); } catch (Exception) { }
            return reading;
        }

        public void ThrustOff()
        {
            try { var thrust = Require("thrust"); Reflect.Call(thrust, "ThrustOff"); } catch (Exception) { /* the stock throttle is cut separately */ }
        }

        // ---------------------------------------------------------------- status

        public JObject ReadStatus()
        {
            var caps = Capabilities;
            var status = new JObject
            {
                ["mechjeb"] = caps.State, ["installed"] = caps.Installed, ["version"] = caps.Version == null ? JValue.CreateNull() : (JToken)caps.Version,
                ["versionSupported"] = caps.VersionSupported, ["reason"] = caps.Reason == null ? JValue.CreateNull() : (JToken)caps.Reason,
                ["plannerOperations"] = new JArray(caps.PlannerOperations),
                ["atmosphereAutopilotInstalled"] = caps.AtmosphereAutopilotInstalled
            };
            var modules = new JObject();
            foreach (var spec in Specs) modules[spec.Name] = new JObject { ["supported"] = caps.Has(spec.Name) };
            status["modules"] = modules;
            object core = null;
            try { core = caps.Installed ? Core() : null; } catch (Exception) { }
            status["vesselCore"] = core != null;
            if (core == null || !caps.Usable) { status["competingControllers"] = new JArray(FindCompetitors(null, false)); return status; }
            foreach (var spec in Specs.Where(s => caps.Has(s.Name)))
            {
                var entry = (JObject)modules[spec.Name];
                try
                {
                    var module = Module(core, spec.Name);
                    entry["ready"] = module != null;
                    if (module == null) continue;
                    if (Reflect.Has(module.GetType(), "Enabled")) entry["engaged"] = Enabled(module);
                    if (Reflect.Has(module.GetType(), "Users")) entry["users"] = (Reflect.Get(module, "Users") as ICollection)?.Count ?? 0;
                    if (spec.Name == "ascent")
                    {
                        string text = null; try { text = Reflect.Get(module, "Status") as string; } catch (Exception) { }
                        entry["status"] = text == null ? JValue.CreateNull() : (JToken)Clip(text);
                    }
                    if (spec.Name == "node") status["nodeExecutor"] = NodeStatus(module);
                    if (spec.Name == "ascentSettings")
                    {
                        var view = ReadSettings(module);
                        status["ascentSettings"] = new JObject
                        {
                            ["ascentType"] = view.AscentType, ["targetAltitudeMeters"] = view.TargetAltitudeMeters, ["inclinationDegrees"] = view.InclinationDegrees,
                            ["autostage"] = view.Autostage, ["skipCircularization"] = view.SkipCircularization
                        };
                    }
                }
                catch (Exception error) { entry["ready"] = false; entry["error"] = error is TargetInvocationException ? "read_failed" : error.GetType().Name; }
            }
            status["competingControllers"] = new JArray(FindCompetitors(null, false));
            return status;
        }

        private static JObject NodeStatus(object node)
        {
            var result = new JObject();
            try { result["state"] = Convert.ToString(Reflect.Get(node, "State"), CultureInfo.InvariantCulture); } catch (Exception) { result["state"] = null; }
            try { result["autowarp"] = (bool)Reflect.Get(node, "Autowarp"); } catch (Exception) { result["autowarp"] = null; }
            try { result["engaged"] = Enabled(node); } catch (Exception) { result["engaged"] = null; }
            try { var burn = Reflect.Call(node, "NextNodeBurnTime") as string; result["nextBurnTime"] = burn == null ? null : Clip(burn); } catch (Exception) { }
            return result;
        }

        private static string Clip(string text) { return text.Length <= 128 ? text : text.Substring(0, 128); }
    }
}
