using System;
using CommNet;

namespace LmpClient.Systems.Agency
{
    internal static class VisibilitySensors
    {
        internal static double StrongestPower(Vessel vessel)
        {
            var power=0d;
            try
            {
                if(vessel.loaded)
                {
                    foreach(var part in vessel.parts)
                        foreach(PartModule module in part.Modules)
                            if(module is ICommAntenna antenna && antenna.CanComm()) power=Maximum(power,antenna.CommPower);
                }
                else if(vessel.protoVessel!=null)
                {
                    foreach(var part in vessel.protoVessel.protoPartSnapshots)
                    {
                        var prefab=part.partInfo?.partPrefab;if(!prefab)continue;
                        for(var i=0;i<prefab.Modules.Count;i++)
                            if(prefab.Modules[i] is ICommAntenna antenna)
                            {
                                var saved=part.FindModule(prefab.Modules[i],i);
                                if(saved!=null && antenna.CanCommUnloaded(saved))power=Maximum(power,antenna.CommPowerUnloaded(saved));
                            }
                    }
                }
                var modifier=HighLogic.CurrentGame?.Parameters.CustomParams<CommNetParams>().rangeModifier??1;
                return Maximum(0,power*modifier);
            }
            catch(Exception e)
            {
                Diagnostics.PlaytestDiagnostics.Write("client.visibility.sensor-unavailable",()=> $"vessel={vessel.id} reason={e.GetType().Name}");
                return 0;
            }
        }
        private static double Maximum(double current,double value)=>double.IsNaN(value)||double.IsInfinity(value)||value<0||value>1e100?current:Math.Max(current,value);
    }
}
