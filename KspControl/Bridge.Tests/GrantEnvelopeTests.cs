using System;
using System.Linq;
using System.Text;
using KspControl.Contracts;
using KspControl.Contracts.Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class GrantEnvelopeTests
    {
        private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        private static readonly DateTime Now = GrantFixture.InsideWindowUtc;
        private static GrantBindingInfo Current() => GrantFixture.Payload().Binding;
        private static GrantStatusInfo Evaluate(string envelope, GrantBindingInfo binding = null, DateTime? now = null, Func<string, long, bool> suspended = null, long highest = 0, byte[] key = null)
            => GrantEvaluator.Evaluate(GrantCodec.Verify(envelope, key ?? Key), now ?? Now, binding ?? Current(), suspended, highest);
        private static string Encode(Action<GrantPayload> change = null)
        { var payload = GrantFixture.Payload(); change?.Invoke(payload); return GrantCodec.Encode(payload, Key); }

        [TestMethod] public void ValidGrantVerifiesAndEvaluatesValid()
        {
            var status = Evaluate(Encode());
            Assert.AreEqual(GrantStates.Valid, status.State); Assert.AreEqual("fixture-grant-0001", status.Id); Assert.AreEqual(7L, status.Generation);
        }
        [TestMethod] public void TamperingWithOnePayloadByteIsInvalidMac()
        {
            var envelope = JObject.Parse(Encode());
            var bytes = Convert.FromBase64String((string)envelope["payload"]); bytes[bytes.Length / 2] ^= 0x01;
            envelope["payload"] = Convert.ToBase64String(bytes);
            Assert.AreEqual(GrantStates.InvalidMac, Evaluate(envelope.ToString()).State);
        }
        [TestMethod] public void TamperingWithTheMacIsInvalidMac()
        {
            var envelope = JObject.Parse(Encode());
            var mac = Convert.FromBase64String((string)envelope["mac"]); mac[0] ^= 0x80; envelope["mac"] = Convert.ToBase64String(mac);
            Assert.AreEqual(GrantStates.InvalidMac, Evaluate(envelope.ToString()).State);
        }
        [TestMethod] public void WrongKeyIsInvalidMac()
            => Assert.AreEqual(GrantStates.InvalidMac, Evaluate(Encode(), key: Enumerable.Repeat((byte)7, 32).ToArray()).State);
        [TestMethod] public void ValidMacOverMalformedJsonIsMalformed()
        {
            foreach (var text in new[] { "{not json", "[]", "null", "{\"version\":1}", "\"x\"" })
            {
                var status = Evaluate(GrantCodec.EncodeRaw(Encoding.UTF8.GetBytes(text), Key));
                Assert.AreEqual(GrantStates.Malformed, status.State, text);
            }
        }
        [TestMethod] public void ValidMacOverInvalidUtf8OrFieldsIsMalformed()
        {
            Assert.AreEqual(GrantStates.Malformed, Evaluate(GrantCodec.EncodeRaw(new byte[] { 0xff, 0xfe, 0xfd }, Key)).State);
            Assert.AreEqual(GrantStates.Malformed, Evaluate(Encode(p => p.Version = 2)).State);
            Assert.AreEqual(GrantStates.Malformed, Evaluate(Encode(p => p.Generation = 0)).State);
            Assert.AreEqual(GrantStates.Malformed, Evaluate(Encode(p => p.UnsavedCraftPolicy = "anything")).State);
            Assert.AreEqual(GrantStates.Malformed, Evaluate(Encode(p => p.Facilities = new string[0])).State);
            Assert.AreEqual(GrantStates.Malformed, Evaluate(Encode(p => p.ExpiresUtc = "tomorrow")).State);
            Assert.AreEqual(GrantStates.Malformed, Evaluate(Encode(p => p.ExpiresUtc = p.IssuedUtc)).State);
            var noBinding = JObject.Parse(Encoding.UTF8.GetString(GrantCodec.SerializePayload(GrantFixture.Payload()))); noBinding.Remove("binding");
            Assert.AreEqual(GrantStates.Malformed, Evaluate(GrantCodec.EncodeRaw(Encoding.UTF8.GetBytes(noBinding.ToString()), Key)).State, "missing required field");
            var nullField = JObject.Parse(Encoding.UTF8.GetString(GrantCodec.SerializePayload(GrantFixture.Payload()))); nullField["grantId"] = null;
            Assert.AreEqual(GrantStates.Malformed, Evaluate(GrantCodec.EncodeRaw(Encoding.UTF8.GetBytes(nullField.ToString()), Key)).State, "null required field");
        }
        [TestMethod] public void InvalidMacWinsOverMalformedPayloadSoPayloadIsNeverParsedUnverified()
        {
            var forged = JObject.Parse(GrantCodec.EncodeRaw(Encoding.UTF8.GetBytes("{not json"), Key));
            forged["mac"] = Convert.ToBase64String(new byte[32]);
            Assert.AreEqual(GrantStates.InvalidMac, Evaluate(forged.ToString()).State);
        }
        [TestMethod] public void EnvelopeShapeProblemsAreMalformedAndNeverThrow()
        {
            foreach (var text in new[] { "", "   ", "not json", "[1]", "{}", "{\"payload\":1,\"mac\":2}", "{\"payload\":\"!!!\",\"mac\":\"!!!\"}", "{\"payload\":\"\",\"mac\":\"\"}" })
                Assert.AreEqual(GrantStates.Malformed, Evaluate(text).State, text);
            Assert.AreEqual(GrantStates.Malformed, Evaluate(new string('a', GrantCodec.MaxEnvelopeCharacters + 1)).State);
        }
        [TestMethod] public void MissingKeyIsMissing()
            => Assert.AreEqual(GrantStates.Missing, GrantCodec.Verify(Encode(), new byte[5]).State);
        [TestMethod] public void ExpiryBoundary()
        {
            var envelope = Encode();
            Assert.AreEqual(GrantStates.Valid, Evaluate(envelope, now: GrantFixture.Payload().ExpiresAt.AddMilliseconds(-1)).State);
            Assert.AreEqual(GrantStates.Expired, Evaluate(envelope, now: GrantFixture.Payload().ExpiresAt).State);
        }
        [TestMethod] public void NotYetApplicableWhenIssuedInTheFuture()
        {
            var envelope = Encode();
            Assert.AreEqual(GrantStates.NotYetApplicable, Evaluate(envelope, now: GrantFixture.Payload().IssuedAt.AddMinutes(-6)).State);
            Assert.AreEqual(GrantStates.Valid, Evaluate(envelope, now: GrantFixture.Payload().IssuedAt.AddMinutes(-4)).State, "small clock skew is tolerated");
        }
        [TestMethod] public void RevokedAndSuspendedAndRegressedOrdering()
        {
            Assert.AreEqual(GrantStates.Revoked, Evaluate(Encode(p => p.Revoked = true)).State);
            Assert.AreEqual(GrantStates.Suspended, Evaluate(Encode(), suspended: (id, gen) => id == "fixture-grant-0001" && gen == 7).State);
            Assert.AreEqual(GrantStates.Valid, Evaluate(Encode(), suspended: (id, gen) => gen == 6).State);
            var regressed = Evaluate(Encode(), highest: 8);
            Assert.AreEqual(GrantStates.Revoked, regressed.State); Assert.AreEqual("generation_regressed", regressed.Detail);
            Assert.AreEqual(GrantStates.Valid, Evaluate(Encode(), highest: 7).State);
            Assert.AreEqual(GrantStates.Revoked, Evaluate(Encode(p => { p.Revoked = true; p.Binding.SaveFolder = "x"; })).State, "revoked outranks binding");
        }
        [TestMethod] public void BindingMismatchOnEveryComponent()
        {
            var envelope = Encode();
            Assert.AreEqual(GrantStates.BindingMismatch, Evaluate(envelope, binding: new GrantBindingInfo { InstallId = "other", SaveFolder = "KspControlP2", Agency = "offline:KspControlP2" }).State);
            Assert.AreEqual(GrantStates.BindingMismatch, Evaluate(envelope, binding: new GrantBindingInfo { InstallId = "0123456789abcdef", SaveFolder = "Other", Agency = "offline:KspControlP2" }).State);
            Assert.AreEqual(GrantStates.BindingMismatch, Evaluate(envelope, binding: new GrantBindingInfo { InstallId = "0123456789abcdef", SaveFolder = "KspControlP2", Agency = Guid.NewGuid().ToString("D") }).State);
            Assert.AreEqual(GrantStates.BindingMismatch, GrantEvaluator.Evaluate(GrantCodec.Verify(envelope, Key), Now, null, null, 0).State);
        }
        [TestMethod] public void SentinelAgencyIsNeverApplicableWhileConnected()
        {
            var guid = Guid.NewGuid();
            Assert.AreEqual("offline:KspControlP2", GrantBindingKey.AgencyKey(Guid.Empty, "KspControlP2"));
            Assert.AreEqual(guid.ToString("D"), GrantBindingKey.AgencyKey(guid, "KspControlP2"));
            var sentinel = Encode();
            Assert.AreEqual(GrantStates.Valid, Evaluate(sentinel, binding: new GrantBindingInfo { InstallId = "0123456789abcdef", SaveFolder = "KspControlP2", Agency = GrantBindingKey.AgencyKey(Guid.Empty, "KspControlP2") }).State);
            Assert.AreEqual(GrantStates.BindingMismatch, Evaluate(sentinel, binding: new GrantBindingInfo { InstallId = "0123456789abcdef", SaveFolder = "KspControlP2", Agency = GrantBindingKey.AgencyKey(guid, "KspControlP2") }).State);
            var agencyGrant = Encode(p => p.Binding.Agency = guid.ToString("D"));
            Assert.AreEqual(GrantStates.Valid, Evaluate(agencyGrant, binding: new GrantBindingInfo { InstallId = "0123456789abcdef", SaveFolder = "KspControlP2", Agency = guid.ToString("D").ToUpperInvariant() }).State, "GUID comparison ignores case");
            Assert.AreEqual(GrantStates.BindingMismatch, Evaluate(agencyGrant, binding: new GrantBindingInfo { InstallId = "0123456789abcdef", SaveFolder = "KspControlP2", Agency = "offline:KspControlP2" }).State);
        }
        [TestMethod] public void InstallIdIsStableCaseInsensitiveAndSixteenHexCharacters()
        {
            var a = GrantBindingKey.InstallId(@"C:\Games\KSP"); var b = GrantBindingKey.InstallId(@"c:\games\ksp\");
            Assert.AreEqual(a, b); Assert.AreEqual(16, a.Length); Assert.IsTrue(a.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
            Assert.AreNotEqual(a, GrantBindingKey.InstallId(@"C:\Games\KSP2"));
        }
        [TestMethod] public void EncodingIsDeterministicAndEnvelopeShapeIsExact()
        {
            Assert.AreEqual(Encode(), Encode());
            var parsed = JObject.Parse(Encode());
            CollectionAssert.AreEqual(new[] { "payload", "mac" }, parsed.Properties().Select(p => p.Name).ToArray());
            Assert.IsTrue(GrantCodec.FixedTimeEquals(GrantCodec.Mac(Key, Convert.FromBase64String((string)parsed["payload"])), Convert.FromBase64String((string)parsed["mac"])));
        }

        // ---- cross-runtime fixture: committed file, verified on whichever runtime runs this test ----

        [TestMethod] public void CommittedFixtureVerifiesOnThisRuntime()
        {
            var verification = GrantCodec.Verify(GrantFixture.EnvelopeText, GrantFixture.Key);
            Assert.IsTrue(verification.Ok, verification.State + " " + verification.Detail);
            Assert.AreEqual("fixture-grant-0001", verification.Payload.GrantId); Assert.AreEqual(7L, verification.Payload.Generation);
            Assert.AreEqual(GrantStates.Valid, GrantEvaluator.Evaluate(verification, GrantFixture.InsideWindowUtc, GrantFixture.Payload().Binding, null, 0).State);
        }
        [TestMethod] public void CommittedFixtureReEncodesToIdenticalBytesOnThisRuntime()
        {
            var committed = JObject.Parse(GrantFixture.EnvelopeText);
            var again = JObject.Parse(GrantCodec.Encode(GrantFixture.Payload(), GrantFixture.Key));
            Assert.AreEqual((string)committed["payload"], (string)again["payload"]);
            Assert.AreEqual((string)committed["mac"], (string)again["mac"]);
        }
        [TestMethod] public void CommittedFixtureTamperIsDetectedOnThisRuntime()
        {
            var envelope = JObject.Parse(GrantFixture.EnvelopeText);
            var bytes = Convert.FromBase64String((string)envelope["payload"]); bytes[10] ^= 0x01; envelope["payload"] = Convert.ToBase64String(bytes);
            Assert.AreEqual(GrantStates.InvalidMac, GrantCodec.Verify(envelope.ToString(), GrantFixture.Key).State);
        }
    }
}
