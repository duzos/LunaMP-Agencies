using KSP.UI.Screens.Flight;
using LmpClient.Extensions;
using LmpClient.Systems.VesselPositionSys;
using System;
using Object = UnityEngine.Object;

namespace LmpClient.VesselUtilities
{
    public class VesselLoader
    {
        /// <summary>
        /// Loads/Reloads a vessel into game
        /// </summary>
        public static bool LoadVessel(ProtoVessel vesselProto, bool forceReload)
        {
            try
            {
                return vesselProto.Validate(true) && LoadVesselIntoGame(vesselProto, forceReload);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Error loading vessel: {e}");
                return false;
            }
        }

        #region Private methods

        /// <summary>
        /// Loads the vessel proto into the current game
        /// </summary>
        private static bool LoadVesselIntoGame(ProtoVessel vesselProto, bool forceReload)
        {
            if (HighLogic.CurrentGame?.flightState == null)
                return false;

            var reloadingOwnVessel = FlightGlobals.ActiveVessel && vesselProto.vesselID == FlightGlobals.ActiveVessel.id;

            //In case the vessel exists, silently remove them from unity and recreate it again
            var existingVessel = FlightGlobals.FindVessel(vesselProto.vesselID);
            if (existingVessel != null)
            {
                if (!forceReload && existingVessel.Parts.Count == vesselProto.protoPartSnapshots.Count &&
                    existingVessel.GetCrewCount() == vesselProto.GetVesselCrew().Count)
                {
                    return true;
                }

                LunaLog.Log($"[LMP]: Reloading vessel {vesselProto.vesselID}");
                if (reloadingOwnVessel)
                    existingVessel.RemoveAllCrew();

                FlightGlobals.RemoveVessel(existingVessel);
                // Disable immediately so Unity stops calling FixedUpdate on this vessel before
                // Object.Destroy is processed — same deferred-destroy race that causes
                // Vessel.UpdateCaches() NullReferenceExceptions (see VesselRemoveSystem.KillVessel).
                existingVessel.gameObject.SetActive(false);
                foreach (var part in existingVessel.parts)
                {
                    Object.Destroy(part.gameObject);
                }
                Object.Destroy(existingVessel.gameObject);
            }
            else
            {
                LunaLog.Log($"[LMP]: Loading vessel {vesselProto.vesselID}");
            }

            try
            {
                vesselProto.Load(HighLogic.CurrentGame.flightState);
            }
            catch (Exception loadEx)
            {
                // KSP may have created the Vessel GameObject before the exception (e.g. OrbitSnapshot.Load
                // throws when the vessel's referenceBody index is out of range because the server has extra
                // celestial bodies from a mod the client doesn't have).  Without cleanup the zombie vessel
                // stays in FlightGlobals and causes NullReferenceExceptions in Vessel.UpdateCaches() on
                // every physics tick.
                LunaLog.LogError($"[LMP]: Vessel {vesselProto.vesselID} threw during ProtoVessel.Load — removing to prevent zombie vessel. Error: {loadEx.Message}");
                if (vesselProto.vesselRef != null)
                {
                    FlightGlobals.RemoveVessel(vesselProto.vesselRef);
                    foreach (var part in vesselProto.vesselRef.parts)
                        Object.Destroy(part.gameObject);
                    Object.Destroy(vesselProto.vesselRef.gameObject);
                }
                HighLogic.CurrentGame.flightState.protoVessels.Remove(vesselProto);
                return false;
            }

            if (vesselProto.vesselRef == null)
            {
                LunaLog.Log($"[LMP]: Protovessel {vesselProto.vesselID} failed to create a vessel!");
                return false;
            }

            // Safety-net: verify the ProtoVessel can be saved before keeping it in the flight state.
            // If ProtoVessel.Save() throws (e.g. from a null resource definition left by a server mod),
            // GamePersistence.SaveGame() would also throw, causing the UI to freeze on any menu close.
            try
            {
                vesselProto.Save(new ConfigNode());
            }
            catch (Exception saveEx)
            {
                LunaLog.LogError($"[LMP]: Vessel {vesselProto.vesselID} ({vesselProto.vesselName}) cannot be saved — removing to prevent UI freezes. Error: {saveEx.Message}");
                FlightGlobals.RemoveVessel(vesselProto.vesselRef);
                foreach (var part in vesselProto.vesselRef.parts)
                    Object.Destroy(part.gameObject);
                Object.Destroy(vesselProto.vesselRef.gameObject);
                HighLogic.CurrentGame.flightState.protoVessels.Remove(vesselProto);
                return false;
            }

            VesselPositionSystem.Singleton.ForceUpdateVesselPosition(vesselProto.vesselRef.id);

            vesselProto.vesselRef.protoVessel = vesselProto;
            if (vesselProto.vesselRef.isEVA)
            {
                var evaModule = vesselProto.vesselRef.FindPartModuleImplementing<KerbalEVA>();
                if (evaModule != null && evaModule.fsm != null && !evaModule.fsm.Started)
                {
                    evaModule.fsm?.StartFSM("Idle (Grounded)");
                }
                vesselProto.vesselRef.GoOnRails();
            }

            if (vesselProto.vesselRef.situation > Vessel.Situations.PRELAUNCH)
            {
                vesselProto.vesselRef.orbitDriver.updateFromParameters();
            }

            if (double.IsNaN(vesselProto.vesselRef.orbitDriver.pos.x))
            {
                LunaLog.Log($"[LMP]: Protovessel {vesselProto.vesselID} has an invalid orbit");
                return false;
            }

            if (reloadingOwnVessel)
            {
                vesselProto.vesselRef.Load();
                vesselProto.vesselRef.RebuildCrewList();

                //Do not do the setting of the active vessel manually, too many systems are dependant of the events triggered by KSP
                FlightGlobals.ForceSetActiveVessel(vesselProto.vesselRef);

                vesselProto.vesselRef.SpawnCrew();
                foreach (var crew in vesselProto.vesselRef.GetVesselCrew())
                {
                    ProtoCrewMember._Spawn(crew);
                    if (crew.KerbalRef)
                        crew.KerbalRef.state = Kerbal.States.ALIVE;
                }

                if (KerbalPortraitGallery.Instance.ActiveCrewItems.Count != vesselProto.vesselRef.GetCrewCount())
                {
                    KerbalPortraitGallery.Instance.StartReset(FlightGlobals.ActiveVessel);
                }
            }

            return true;
        }

        #endregion
    }
}
