using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Server;

namespace Server.Agency
{
    /// <summary>
    /// Pushes existing ShareProgress* updates to all online members of an
    /// agency. Used when the server itself (e.g. an admin cheat or the
    /// owner-change flow) needs to mutate career state for every member.
    /// </summary>
    public static class AgencyFanout
    {
        public static void PushFundsToMembers(Agency agency, double funds, string reason)
        {
            if (agency == null) return;
            var msg = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
            msg.Funds = funds;
            msg.Reason = reason ?? "agency-sync";
            MessageQueuer.SendMessageToAgency<ShareProgressSrvMsg>(agency.Id, msg);
        }

        public static void PushScienceToMembers(Agency agency, float science)
        {
            if (agency == null) return;
            var msg = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressScienceMsgData>();
            msg.Science = science;
            msg.Reason = "agency-sync";
            MessageQueuer.SendMessageToAgency<ShareProgressSrvMsg>(agency.Id, msg);
        }

        public static void PushReputationToMembers(Agency agency, float reputation)
        {
            if (agency == null) return;
            var msg = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressReputationMsgData>();
            msg.Reputation = reputation;
            msg.Reason = "agency-sync";
            MessageQueuer.SendMessageToAgency<ShareProgressSrvMsg>(agency.Id, msg);
        }

        /// <summary>
        /// Sends funds/science/reputation snapshots to a single client as
        /// <c>ShareProgress*MsgData</c> messages. The client handlers then call
        /// <c>Funding.Instance.SetFunds(...)</c> etc at runtime, which is
        /// authoritative even when KSP has already initialised those scenarios
        /// from the local <c>persistent.sfs</c>.
        ///
        /// Called right after <see cref="System.ScenarioSystem.SendScenarioModules"/>
        /// so the user sees correct numbers as soon as KSP reaches the space
        /// center, regardless of whether the scenario-apply path succeeded.
        /// </summary>
        public static void PushCareerToClient(ClientStructure client, Agency agency)
        {
            if (client == null || agency == null) return;

            var funds = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
            funds.Funds = agency.Funds;
            funds.Reason = "agency-join-sync";
            MessageQueuer.SendToClient<ShareProgressSrvMsg>(client, funds);

            var sci = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressScienceMsgData>();
            sci.Science = agency.Science;
            sci.Reason = "agency-join-sync";
            MessageQueuer.SendToClient<ShareProgressSrvMsg>(client, sci);

            var rep = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressReputationMsgData>();
            rep.Reputation = agency.Reputation;
            rep.Reason = "agency-join-sync";
            MessageQueuer.SendToClient<ShareProgressSrvMsg>(client, rep);
        }
    }
}
