using System;
using System.IO;
using System.Text;
using KspControl.Contracts;

namespace KspControl.Contracts.Fixtures
{
    /// <summary>
    /// Shared by the host and bridge test projects (linked, C# 7.3). The committed grant-fixture.json was produced by
    /// <see cref="GrantCodec.Encode"/> from <see cref="Payload"/> and the committed test key, so a verify on net472 and a
    /// re-encode on net10 prove both runtimes agree on the raw-byte MAC. The key is a public test key; never a real one.
    /// </summary>
    public static class GrantFixture
    {
        public const string RegenerateVariable = "KSPCONTROL_REGENERATE_GRANT_FIXTURE";
        public static string Directory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures");
        public static string EnvelopePath => Path.Combine(Directory, "grant-fixture.json");
        public static string KeyPath => Path.Combine(Directory, "grant-fixture.key");

        public static byte[] Key => Convert.FromBase64String(File.ReadAllText(KeyPath, Encoding.ASCII).Trim());
        public static string EnvelopeText => File.ReadAllText(EnvelopePath, Encoding.UTF8);

        public static GrantPayload Payload() => new GrantPayload
        {
            GrantId = "fixture-grant-0001",
            Generation = 7,
            IssuedUtc = "2026-01-01T00:00:00.000Z",
            ExpiresUtc = "2026-01-02T00:00:00.000Z",
            Binding = new GrantBindingInfo { InstallId = "0123456789abcdef", SaveFolder = "KspControlP2", Agency = "offline:KspControlP2" },
            Operations = new[] { "editor.replace_craft", "editor.restore_snapshot", "craft.write" },
            Facilities = new[] { "VAB" },
            UnsavedCraftPolicy = "refuse",
            MaxParts = 250,
            SpendLimitFunds = 0,
            Revoked = false
        };

        /// <summary>The same fixed instant the payload was issued at plus one hour: inside the validity window.</summary>
        public static readonly DateTime InsideWindowUtc = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
    }
}
