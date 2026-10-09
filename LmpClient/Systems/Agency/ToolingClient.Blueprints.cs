using System;
using LmpCommon.Agency;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// The KSP-bound half of tooling blueprints (plan 40): editor capture, craft hashing, the missing-part check, the craft file write and the
    /// editor load. Not linked into LmpCommonTest; ToolingClientTestEnvironment stubs these members. Slice S3 owns the bodies.
    /// </summary>
    public static partial class ToolingClient
    {
        /// <summary>The open editor craft's bytes (SaveShip().Serialize()), facility and name. False when there is no craft or its fingerprint differs from the current manifest.</summary>
        private static bool CaptureEditorBlueprint(out byte[] bytes, out string editor, out string name)
        {
            throw new NotImplementedException("CaptureEditorBlueprint is implemented by plan 40 slice S3.");
        }

        /// <summary>SHA-256 (lowercase hex) of the open editor craft's blueprint bytes, for comparing with the saved blueprint.</summary>
        private static bool TryEditorBlueprintHash(out string hash)
        {
            throw new NotImplementedException("TryEditorBlueprintHash is implemented by plan 40 slice S3.");
        }

        /// <summary>Part names in the blueprint that this install does not have (PartLoader). Empty when everything can load.</summary>
        private static string[] MissingBlueprintParts(byte[] bytes)
        {
            throw new NotImplementedException("MissingBlueprintParts is implemented by plan 40 slice S3.");
        }

        /// <summary>Writes the fetched blueprint to the save's Ships library and returns the path.</summary>
        private static string WriteTooledCraftFile(ToolingBlueprintInfo info, byte[] bytes)
        {
            throw new NotImplementedException("WriteTooledCraftFile is implemented by plan 40 slice S3.");
        }

        /// <summary>Loads a craft file: in place in the editor, or into its facility from the Space Center.</summary>
        private static void LoadTooledCraftFile(string path, string editor)
        {
            throw new NotImplementedException("LoadTooledCraftFile is implemented by plan 40 slice S3.");
        }

        /// <summary>Per-frame blueprint work (load state machine, EditorBlueprintNeedsSave refresh).</summary>
        private static void TickBlueprints() { }

        /// <summary>Handles a FetchBlueprint result; true when the result was consumed.</summary>
        private static bool HandleBlueprintResult(EconomyResult result) => false;
    }
}
