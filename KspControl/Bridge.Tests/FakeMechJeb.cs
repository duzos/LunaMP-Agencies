using System;
using System.Collections.Generic;
using System.Linq;

// Look-alike classes with the member names of MechJeb 2.15.2 (see docs/handoff/plans/32-mechjeb-api.txt and the reflection notes in the P3b report).
// The adapter finds types by name next to MechJebCore, so this namespace plays the part of MuMech. Nothing here references the game.
namespace FakeMechJeb
{
    public enum AscentType { CLASSIC, PVG }
    public enum States { WARPALIGN, LEAD, BURN, IDLE }

    public class EditableDoubleMult { public virtual double Val { get; set; } }
    public class EditableDouble : EditableDoubleMult { }
    /// <summary>Ignores writes: simulates MechJeb refusing or clamping a setting.</summary>
    public class FrozenDouble : EditableDoubleMult { public override double Val { get { return 5; } set { } } }

    public class UserPool : List<object>
    {
        public ComputerModule Module;
        /// <summary>MechJeb's real pool may or may not enable the module on Add; the adapter must cope with both.</summary>
        public static bool AddEnablesModule = true;
        public new void Add(object user)
        {
            if (Contains(user)) return;
            base.Add(user);
            if (AddEnablesModule && Module != null) Module.Enabled = true;
        }
        public new void Remove(object user)
        {
            base.Remove(user);
            if (Count == 0 && Module != null) Module.Enabled = false;
        }
    }

    public class ComputerModule
    {
        public UserPool Users;
        public bool Enabled { get; set; }
        public ComputerModule() { Users = new UserPool { Module = this }; }
    }

    public class MechJebModuleAscentBaseAutopilot : ComputerModule { public string Status = "Pre-launch"; }

    public class MechJebModuleAscentSettings : ComputerModule
    {
        public EditableDoubleMult DesiredOrbitAltitude = new EditableDoubleMult { Val = 100000 };
        public EditableDouble DesiredInclination = new EditableDouble();
        public bool Autostage { get; set; }
        public bool SkipCircularization = true;
        public AscentType AscentType { get; set; }
    }

    public class MechJebModuleNodeExecutor : ComputerModule
    {
        public bool Autowarp = true;
        public States State = States.IDLE;
        public int ExecuteOne, ExecuteAll, Aborts;
        public void ExecuteOneNode(object controller) { ExecuteOne++; State = States.WARPALIGN; Users.Add(controller); }
        public void ExecuteAllNodes(object controller) { ExecuteAll++; State = States.WARPALIGN; Users.Add(controller); }
        public void Abort() { Aborts++; Users.Clear(); Enabled = false; State = States.IDLE; }
        public string NextNodeBurnTime() { return "00:01:30"; }
    }

    public class MechJebModuleAscentMenu : ComputerModule { }
    public class MechJebModuleLandingAutopilot : ComputerModule { }
    public class MechJebModuleRendezvousAutopilot : ComputerModule { }
    public class MechJebModuleDockingAutopilot : ComputerModule { }
    public class MechJebModuleSpaceplaneAutopilot : ComputerModule { }
    public class MechJebModuleAirplaneAutopilot : ComputerModule { }
    public class MechJebModuleAttitudeController : ComputerModule { }
    public class MechJebModuleRoverController : ComputerModule { }
    public class MechJebModuleWarpController : ComputerModule { }
    public class MechJebModuleThrustController : ComputerModule { public int Off; public void ThrustOff() { Off++; } }
    public class Operation { }
    public class OperationCircularize : Operation { }
    public class OperationInterplanetaryTransfer : Operation { }

    public class MechJebCore
    {
        public string version = "2.15.2.0";
        public MechJebCore MasterMechJeb { get; set; }
        public MechJebModuleAscentBaseAutopilot Ascent { get { return ascent; } }
        private readonly MechJebModuleAscentBaseAutopilot ascent = new MechJebModuleAscentBaseAutopilot();
        public MechJebModuleAscentSettings AscentSettings = new MechJebModuleAscentSettings();
        public MechJebModuleNodeExecutor Node = new MechJebModuleNodeExecutor();
        public MechJebModuleLandingAutopilot Landing = new MechJebModuleLandingAutopilot();
        public MechJebModuleAirplaneAutopilot Airplane = new MechJebModuleAirplaneAutopilot();
        public MechJebModuleAttitudeController Attitude = new MechJebModuleAttitudeController();
        public MechJebModuleRoverController Rover = new MechJebModuleRoverController();
        public MechJebModuleThrustController Thrust = new MechJebModuleThrustController();
        public MechJebModuleWarpController Warp = new MechJebModuleWarpController();
        public MechJebModuleRendezvousAutopilot Rendezvous = new MechJebModuleRendezvousAutopilot();
        public MechJebModuleDockingAutopilot Docking = new MechJebModuleDockingAutopilot();
        public MechJebModuleSpaceplaneAutopilot Spaceplane = new MechJebModuleSpaceplaneAutopilot();
        public MechJebModuleAscentMenu AscentMenu = new MechJebModuleAscentMenu();
        /// <summary>False models an install whose ascent window module cannot be found.</summary>
        public bool HasMenu = true;
        public MechJebCore() { MasterMechJeb = this; }
        /// <summary>What MechJeb does when the ascent reaches its circularization: the node executor and the attitude controller are used with the ascent module as the user.</summary>
        public void HandOffToNode() { Node.Users.Add(Ascent); Node.Enabled = true; Attitude.Users.Add(Ascent); Attitude.Enabled = true; }
        public T GetComputerModule<T>() where T : class
        {
            return new ComputerModule[] { Rendezvous, Docking, Spaceplane, Landing, HasMenu ? AscentMenu : null }.OfType<T>().FirstOrDefault();
        }
    }
}

namespace FakeAtmosphere
{
    public class AutopilotModule { public bool Active { get; set; } public string ModuleName { get; set; } }

    public class AtmosphereAutopilot
    {
        public static AtmosphereAutopilot Instance { get; set; }
        public Dictionary<Type, AutopilotModule> Modules = new Dictionary<Type, AutopilotModule>();
        public bool Throw;
        public Dictionary<Type, AutopilotModule> getVesselModules(object vessel)
        {
            if (Throw) throw new InvalidOperationException("boom");
            return Modules;
        }
    }
}
