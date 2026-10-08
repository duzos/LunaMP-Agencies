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

        private readonly Func<string, object> coreSource;
        private readonly Func<string, object> vesselSource;
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
            new ModuleSpec { Name = "ascentMenu", TypeName = "MechJebModuleAscentMenu", Members = new string[0], ViaGeneric = true },
            new ModuleSpec { Name = "attitude", CoreMember = "Attitude", TypeName = "MechJebModuleAttitudeController", Members = new[] { "Enabled", "Users" }, Scanned = true },
            // The same module, resolved again for the members the recovery job drives. Not scanned: the "attitude" entry above already is.
            new ModuleSpec { Name = "attitudeControl", CoreMember = "Attitude", TypeName = "MechJebModuleAttitudeController", Members = new[] { "Enabled", "Users", "attitudeTo", "attitudeAngleFromTarget" } },
            new ModuleSpec { Name = "rover", CoreMember = "Rover", TypeName = "MechJebModuleRoverController", Members = new[] { "Enabled", "Users" }, Scanned = true },
            new ModuleSpec { Name = "thrust", CoreMember = "Thrust", TypeName = "MechJebModuleThrustController", Members = new[] { "Enabled", "Users", "ThrustOff" }, Scanned = false }, // MechJeb's own limiters keep a standing user on it (live 2026-10-07): not a competitor
            new ModuleSpec { Name = "warp", CoreMember = "Warp", TypeName = "MechJebModuleWarpController", Members = new[] { "Enabled" } }
        };

        public MechJebAdapter(Func<string, object> coreSource, Func<string, object> vesselSource = null, Func<Type> coreTypeSource = null, Func<Type> atmosphereTypeSource = null, Func<Type, Version> versionSource = null)
        {
            this.coreSource = coreSource ?? throw new ArgumentNullException(nameof(coreSource));
            this.vesselSource = vesselSource ?? (id => null);
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
        private static int MethodArity(string name)
        {
            if (name == "attitudeTo") return 4; // attitudeTo(Vector3d direction, AttitudeReference reference, object controller, bool killRollRotation)
            return name == "ExecuteOneNode" || name == "ExecuteAllNodes" ? 1 : 0;
        }

        // ---------------------------------------------------------------- live objects (never cached)

        public bool HasCore(string vesselId) { try { return Core(vesselId) != null; } catch (Exception) { return false; } }

        /// <summary>The core of the named vessel (null: the active vessel). Never another vessel's.</summary>
        private object Core(string vesselId)
        {
            var raw = coreSource(vesselId);
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

        private object Require(string vesselId, string name)
        {
            if (!Capabilities.Usable) throw new MechJebException(Capabilities.Reason ?? KspControl.Contracts.AutopilotReasons.MechJebUnavailable);
            var core = Core(vesselId);
            if (core == null) throw new MechJebException(KspControl.Contracts.AutopilotReasons.MechJebUnavailable, "that vessel has no MechJeb core (or it is not loaded)");
            var module = Module(core, name);
            if (module == null) throw new MechJebException(KspControl.Contracts.AutopilotReasons.MechJebModuleUnavailable, name);
            return module;
        }

        /// <summary>The ascent window module, through which the bridge engages the ascent so MechJeb's own Disengage button stops it. Null when absent.</summary>
        private object Window(object core)
        {
            try { return core == null || !Capabilities.Has("ascentMenu") ? null : Module(core, "ascentMenu"); } catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- users

        private static bool Same(object a, object b) { return a != null && ReferenceEquals(a, b); }

        /// <summary>True when the user is one of ours, or a MechJeb module whose own user set (recursively, MechJeb's RecursiveUser) contains one of ours.</summary>
        private static bool IsOurs(object user, object own, object window, int depth, List<object> seen)
        {
            if (Same(user, own) || Same(user, window)) return true;
            if (user == null || depth >= 4 || seen.Any(x => ReferenceEquals(x, user))) return false;
            seen.Add(user);
            if (!Reflect.Has(user.GetType(), "Users")) return false;
            var list = Reflect.Get(user, "Users") as IEnumerable;
            if (list == null) return false;
            foreach (var inner in list) if (IsOurs(inner, own, window, depth + 1, seen)) return true;
            return false;
        }

        /// <summary>Counts the module's users: those that are directly ours, those that are ours by recursion (a MechJeb module acting for us), and the rest.</summary>
        private static void Users(object module, object own, object window, out bool ownDirect, out int ours, out int others)
        {
            ownDirect = false; ours = 0; others = 0;
            var list = Reflect.Get(module, "Users") as IEnumerable;
            if (list == null) return;
            foreach (var user in list)
            {
                if (Same(user, own) || Same(user, window)) { ownDirect = true; ours++; }
                else if (IsOurs(user, own, window, 0, new List<object>())) ours++;
                else others++;
            }
        }

        private static bool Enabled(object module) { return (bool)Reflect.Get(module, "Enabled"); }

        private static void Engage(object module, object user)
        {
            Reflect.Call(Reflect.Get(module, "Users"), "Add", user);
            // Adding the first user enables the module in MechJeb; set it explicitly if that did not happen.
            if (!Enabled(module)) Reflect.Set(module, "Enabled", true);
            if (!Enabled(module)) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "the module did not enable");
        }

        private static void RemoveUsers(object module, params object[] users)
        {
            var errors = 0; var tried = 0;
            foreach (var user in users)
            {
                if (user == null) continue;
                tried++;
                try { Reflect.Call(Reflect.Get(module, "Users"), "Remove", user); } catch (Exception) { errors++; }
            }
            if (tried > 0 && errors == tried) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "could not release the module");
        }

        /// <summary>Disables the module once nobody holds it. Best effort: the users were already removed.</summary>
        private static void DisableIfUnused(object module)
        {
            try { if (((Reflect.Get(module, "Users") as ICollection)?.Count ?? 0) == 0 && Enabled(module)) Reflect.Set(module, "Enabled", false); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- competitors

        public List<string> FindCompetitors(string vesselId, object ownUser, bool includeWindow)
        {
            var found = new List<string>();
            if (Capabilities.Installed)
            {
                try
                {
                    var core = Core(vesselId);
                    // The window module is ours only for a caller that has an identity; a new caller sees an engaged ascent as someone else's.
                    var window = ownUser != null && includeWindow ? Window(core) : null;
                    if (core != null)
                        foreach (var spec in Specs.Where(s => s.Scanned && Capabilities.Has(s.Name)))
                        {
                            try
                            {
                                var module = Module(core, spec.Name);
                                if (module == null) continue;
                                bool direct; int ours, others; Users(module, ownUser, window, out direct, out ours, out others);
                                // An autopilot that is enabled with no user of ours is someone else's; a support module only counts for a foreign user.
                                var engaged = spec.Autopilot ? (Enabled(module) && (others > 0 || ours == 0)) : others > 0;
                                if (engaged) found.Add("mechjeb." + spec.Name);
                            }
                            catch (Exception) { found.Add("mechjeb." + spec.Name + ":unreadable"); }
                        }
                }
                catch (Exception) { found.Add("mechjeb:unreadable"); }
            }
            AtmosphereAutopilot(vesselId, found);
            return found;
        }

        private void AtmosphereAutopilot(string vesselId, List<string> found)
        {
            if (Capabilities.AtmosphereAutopilotInstalled == false) return;
            try
            {
                var instance = Reflect.StaticProperty(atmosphereType, "Instance");
                var vessel = vesselSource(vesselId);
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

        public AscentSettingsView ConfigureAscent(string vesselId, double altitudeMeters, double inclinationDegrees, bool autostage)
        {
            var settings = Require(vesselId, "ascentSettings");
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

        public void EngageAscent(string vesselId, object user)
        {
            var ascent = Require(vesselId, "ascent");
            var window = Window(Core(vesselId));
            Engage(ascent, window ?? user);
        }

        public void DisengageAscent(string vesselId, object user)
        {
            var ascent = Require(vesselId, "ascent");
            var window = Window(Core(vesselId));
            RemoveUsers(ascent, user, window);
            DisableIfUnused(ascent);
        }

        public AscentReading ReadAscent(string vesselId, object user)
        {
            var ascent = Require(vesselId, "ascent");
            var window = Window(Core(vesselId));
            bool direct; int ours, others; Users(ascent, user, window, out direct, out ours, out others);
            string status = null;
            try { status = Reflect.Get(ascent, "Status") as string; } catch (Exception) { }
            return new AscentReading { Enabled = Enabled(ascent), Status = status, OwnUserPresent = direct, OtherUsers = others, ViaWindow = window != null };
        }

        // ---------------------------------------------------------------- node executor

        public bool EngageNode(string vesselId, object user, bool all)
        {
            var node = Require(vesselId, "node");
            var previous = (bool)Reflect.Get(node, "Autowarp");
            // The agent tools never warp: the executor's own warping would bypass the bridge's warp policy. The old value is restored on release.
            Reflect.Set(node, "Autowarp", false);
            if (all) Reflect.Call(node, "ExecuteAllNodes", user); else Reflect.Call(node, "ExecuteOneNode", user);
            if (!Enabled(node)) { try { Reflect.Set(node, "Enabled", true); } catch (Exception) { } }
            if (!Enabled(node)) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "the node executor did not enable");
            return previous;
        }

        public void DisengageNode(string vesselId, object user, bool? restoreAutowarp)
        {
            var node = Require(vesselId, "node");
            var errors = 0;
            try
            {
                bool direct; int ours, others; Users(node, user, null, out direct, out ours, out others);
                if (others > 0) RemoveUsers(node, user); // someone else is using the executor: leave their burn alone
                else
                {
                    try { Reflect.Call(node, "Abort"); } catch (Exception) { errors++; }
                    try { RemoveUsers(node, user); DisableIfUnused(node); } catch (MechJebException) { errors++; }
                }
            }
            catch (Exception) { errors += 2; }
            if (restoreAutowarp.HasValue) { try { Reflect.Set(node, "Autowarp", restoreAutowarp.Value); } catch (Exception) { } }
            if (errors >= 2) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "could not release the node executor");
        }

        public NodeReading ReadNode(string vesselId, object user)
        {
            var node = Require(vesselId, "node");
            bool direct; int ours, others; Users(node, user, null, out direct, out ours, out others);
            var reading = new NodeReading { Enabled = Enabled(node), OwnUserPresent = direct, OtherUsers = others };
            try { reading.State = Convert.ToString(Reflect.Get(node, "State"), CultureInfo.InvariantCulture); } catch (Exception) { }
            try { reading.Autowarp = (bool)Reflect.Get(node, "Autowarp"); } catch (Exception) { }
            return reading;
        }

        public void ThrustOff(string vesselId)
        {
            try { var thrust = Require(vesselId, "thrust"); Reflect.Call(thrust, "ThrustOff"); } catch (Exception) { /* the stock throttle is cut separately */ }
        }

        // ---------------------------------------------------------------- attitude (recovery)

        /// <summary>
        /// MechJeb 2.15.2 has three attitudeTo overloads (quaternion: 6 parameters, vector: 4, heading/pitch/roll: 8). The vector one is picked by its
        /// shape: a 3-component vector struct, the AttitudeReference enum, the controller object, a bool.
        /// </summary>
        private static MethodInfo AttitudeTo(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "attitudeTo") continue;
                var p = method.GetParameters();
                if (p.Length == 4 && p[0].ParameterType.IsValueType && p[1].ParameterType.IsEnum && p[2].ParameterType == typeof(object) && p[3].ParameterType == typeof(bool))
                    return method;
            }
            throw new MechJebException(KspControl.Contracts.AutopilotReasons.MechJebModuleUnavailable, "attitudeTo(Vector3d, AttitudeReference, object, bool) was not found");
        }

        public void HoldAttitude(string vesselId, object user, string direction)
        {
            var attitude = Require(vesselId, "attitudeControl");
            var method = AttitudeTo(attitude.GetType());
            var p = method.GetParameters();
            // Vector3d.back is (0, 0, -1): with the ORBIT reference that is retrograde, with SURFACE_VELOCITY surface retrograde (as MechJeb's SmartASS does).
            var back = Activator.CreateInstance(p[0].ParameterType, 0.0, 0.0, -1.0);
            var reference = Enum.Parse(p[1].ParameterType, direction == AttitudeDirections.SurfaceRetrograde ? "SURFACE_VELOCITY" : "ORBIT");
            method.Invoke(attitude, new[] { back, reference, user, (object)false });
            // UserPool.Add enables the module in 2.15.2; set it explicitly if that did not happen.
            if (!Enabled(attitude)) Reflect.Set(attitude, "Enabled", true);
            if (!Enabled(attitude)) throw new MechJebException(KspControl.Contracts.AutopilotReasons.EngageFailed, "the attitude controller did not enable");
        }

        public AttitudeReading ReadAttitude(string vesselId, object user)
        {
            var attitude = Require(vesselId, "attitudeControl");
            bool direct; int ours, others; Users(attitude, user, null, out direct, out ours, out others);
            var reading = new AttitudeReading { Enabled = Enabled(attitude), OwnUserPresent = direct, OtherUsers = others };
            try { reading.AngleFromTargetDegrees = Reflect.Number(Reflect.Call(attitude, "attitudeAngleFromTarget")); } catch (Exception) { reading.AngleFromTargetDegrees = double.NaN; }
            return reading;
        }

        public void ReleaseAttitude(string vesselId, object user)
        {
            var attitude = Require(vesselId, "attitudeControl");
            // Never attitudeDeactivate(): it clears every user, including a person's SmartASS hold.
            RemoveUsers(attitude, user);
            DisableIfUnused(attitude);
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
            try { core = caps.Installed ? Core(null) : null; } catch (Exception) { }
            status["vesselCore"] = core != null;
            if (core == null || !caps.Usable) { status["competingControllers"] = new JArray(FindCompetitors(null, null, false)); return status; }
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
            status["competingControllers"] = new JArray(FindCompetitors(null, null, false));
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
