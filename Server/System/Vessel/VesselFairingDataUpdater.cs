using LmpCommon.Message.Data.Vessel;
using System.Threading.Tasks;

namespace Server.System.Vessel
{
    /// <summary>
    /// We try to avoid working with protovessels as much as possible as they can be huge files.
    /// This class patches the vessel file with the information messages we receive about a position and other vessel properties.
    /// This way we send the whole vessel definition only when there are parts that have changed 
    /// </summary>
    public partial class VesselDataUpdater
    {

        /// <summary>
        /// We received a fairing change from a player
        /// Then we rewrite the vesselproto with that last information so players that connect later receive an updated vesselproto
        /// </summary>
        public static void WriteFairingDataToFile(VesselBaseMsgData message)
        {
            var ownershipEpoch=global::Server.Agency.AgencyVesselMap.CaptureEpoch();
            if (!(message is VesselFairingMsgData msgData)) return;
            if (VesselContext.RemovedVessels.ContainsKey(msgData.VesselId)) return;

            //Sync fairings ALWAYS and ignore the rate they arrive
            _ = Task.Run(() =>
            {
                lock (global::Server.Agency.AgencyVesselMap.TransactionGate)
                lock (Semaphore.GetOrAdd(msgData.VesselId, new object()))
                {
                    if (!global::Server.Agency.AgencyVesselMap.CanApplyEpoch(msgData.VesselId,ownershipEpoch) || !VesselStoreSystem.CurrentVessels.TryGetValue(msgData.VesselId, out var vessel)) return;

                    var part = vessel.GetPart(msgData.PartFlightId);
                    var module = part?.GetSingleModule("ModuleProceduralFairing");
                    if (module != null)
                    {
                        module.Values.Update("fsm", "st_flight_deployed");
                        module.Nodes.Remove("XSECTION");
                    }
                }
            });
        }
    }
}
