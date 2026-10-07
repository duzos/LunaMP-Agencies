using System;
using System.Collections.Generic;
using LmpCommon.Agency;
using CommNet;

namespace LmpClient.Systems.Agency
{
    internal static class VisibilitySensors
    {
        internal static double TotalPower(Vessel vessel)
        {
            var values=new List<double>();
            try
            {
                if(vessel.loaded)
                {
                    foreach(var part in vessel.parts)
                        foreach(PartModule module in part.Modules)
                            if(module is ICommAntenna antenna && antenna.CanComm()) values.Add(antenna.CommPower);
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
                                if(saved!=null && antenna.CanCommUnloaded(saved))values.Add(antenna.CommPowerUnloaded(saved));
                            }
                    }
                }
                var modifier=HighLogic.CurrentGame?.Parameters.CustomParams<CommNetParams>().rangeModifier??1;
                var total=VisibilityPolicy.SumPower(values)*modifier;
                return double.IsNaN(total)||double.IsInfinity(total)?0:Math.Min(VisibilityPolicy.MaxPower,Math.Max(0,total));
            }
            catch(Exception e)
            {
                Diagnostics.PlaytestDiagnostics.Write("client.visibility.sensor-unavailable",()=> $"vessel={vessel.id} reason={e.GetType().Name}");
                return 0;
            }
        }
    }
}
