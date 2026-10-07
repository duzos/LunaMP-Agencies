using System;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// Serves control.* operations on the loopback worker thread. It only talks to <see cref="IControlAuthority"/>,
    /// so it can never touch Unity objects, and it never enqueues or waits on the main thread.
    /// </summary>
    internal sealed class ControlDispatcher
    {
        private readonly IControlAuthority authority;
        public ControlDispatcher(IControlAuthority authority)
        { this.authority = authority ?? throw new ArgumentNullException(nameof(authority)); }

        public BridgeResponse Handle(BridgeRequest request)
        {
            try
            {
                switch (request.Operation)
                {
                    case ControlOperations.Status: return Done(request, JObject.FromObject(authority.Status()));
                    case ControlOperations.Acquire: return Acquire(request);
                    case ControlOperations.Renew: return Renew(request);
                    case ControlOperations.Release: return Release(request);
                    case ControlOperations.Heartbeat: return Heartbeat(request);
                    default: return Fail(request, ControlReasons.OperationUnavailable);
                }
            }
            catch (ArgumentException) { return Fail(request, ControlReasons.InvalidArgument); }
            catch (InvalidOperationException error) { return Fail(request, SafeCode(error.Message)); }
            catch (Exception) { return Fail(request, "authority_unavailable"); }
        }

        private BridgeResponse Acquire(BridgeRequest request)
        {
            string purpose; int seconds;
            var problem = ReadPurpose(request.Arguments, out purpose);
            if (problem == null) problem = ReadSeconds(request.Arguments, out seconds);
            else seconds = 0;
            if (problem != null) return Invalid(request, problem);
            var id = authority.AcquireLease(seconds * 1000L, purpose);
            var details = authority.DescribeLease(id);
            if (details == null) return Fail(request, ControlReasons.AuthorityRevoked);
            return Done(request, new JObject
            {
                ["leaseId"] = details.LeaseId, ["purpose"] = details.Purpose, ["expiresInSeconds"] = details.ExpiresInSeconds,
                ["grantId"] = details.GrantId, ["generation"] = details.Generation, ["epoch"] = details.Epoch, ["entity"] = details.Entity
            });
        }

        private BridgeResponse Renew(BridgeRequest request)
        {
            string id; int seconds;
            var problem = ReadLeaseId(request, out id);
            if (problem == null) problem = ReadSeconds(request.Arguments, out seconds);
            else seconds = 0;
            if (problem != null) return Invalid(request, problem);
            authority.RenewLease(id, seconds * 1000L);
            var details = authority.DescribeLease(id);
            if (details == null) return Fail(request, authority.LeaseFailureReason(id));
            return Done(request, new JObject { ["expiresInSeconds"] = details.ExpiresInSeconds });
        }

        private BridgeResponse Release(BridgeRequest request)
        {
            string id;
            var problem = ReadLeaseId(request, out id);
            if (problem != null) return Invalid(request, problem);
            if (!authority.ReleaseLease(id)) return Fail(request, authority.LeaseFailureReason(id));
            return Done(request, new JObject { ["released"] = true });
        }

        private BridgeResponse Heartbeat(BridgeRequest request)
        {
            string id;
            var problem = ReadLeaseId(request, out id);
            if (problem != null) return Invalid(request, problem);
            if (!authority.Heartbeat(id)) return Fail(request, authority.LeaseFailureReason(id));
            return Done(request, new JObject { ["alive"] = true });
        }

        private static string ReadLeaseId(BridgeRequest request, out string id)
        {
            id = request.LeaseId;
            if (id == null && request.Arguments != null && request.Arguments["leaseId"]?.Type == JTokenType.String) id = (string)request.Arguments["leaseId"];
            if (!ControlLimits.IsLeaseId(id)) return "leaseId must be " + ControlLimits.LeaseIdLength + " hex characters";
            id = id.ToLowerInvariant();
            return null;
        }

        private static string ReadPurpose(JObject args, out string purpose)
        {
            purpose = null;
            var token = args?["purpose"];
            if (token == null || token.Type != JTokenType.String) return "purpose is required";
            var text = ((string)token).Trim();
            if (text.Length < ControlLimits.PurposeMin || text.Length > ControlLimits.PurposeMax) return "purpose must be " + ControlLimits.PurposeMin + ".." + ControlLimits.PurposeMax + " characters";
            if (text.Any(char.IsControl)) return "purpose must not contain control characters";
            purpose = text; return null;
        }

        private static string ReadSeconds(JObject args, out int seconds)
        {
            seconds = 0;
            var token = args?["durationSeconds"];
            if (token == null || token.Type != JTokenType.Integer) return "durationSeconds is required";
            var value = (long)token;
            if (value < ControlLimits.DurationMinSeconds || value > ControlLimits.DurationMaxSeconds)
                return "durationSeconds must be " + ControlLimits.DurationMinSeconds + ".." + ControlLimits.DurationMaxSeconds;
            seconds = (int)value; return null;
        }

        private BridgeResponse Done(BridgeRequest request, JObject data) => new BridgeResponse
        { RequestId = request.RequestId, Status = "completed", WorldEpoch = authority.PublishedEpoch, Revision = authority.PublishedRevision, Data = data };

        private BridgeResponse Fail(BridgeRequest request, string code) => new BridgeResponse
        { RequestId = request.RequestId, Status = "failed", ReasonCode = code, WorldEpoch = authority.PublishedEpoch, Revision = authority.PublishedRevision };

        private BridgeResponse Invalid(BridgeRequest request, string detail)
        { var response = Fail(request, ControlReasons.InvalidArgument); response.Data = new JObject { ["detail"] = detail }; return response; }

        /// <summary>Exception messages in this assembly are reason codes. Anything else is hidden.</summary>
        private static string SafeCode(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length > 64) return "authority_unavailable";
            foreach (var c in message) if (!((c >= 'a' && c <= 'z') || c == '_')) return "authority_unavailable";
            return message;
        }
    }
}
