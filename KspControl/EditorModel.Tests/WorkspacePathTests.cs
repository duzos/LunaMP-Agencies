using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

/// <summary>Workspace and thumbnail paths for the apply slice, including the P2.2 carry-forward: no tool path may reach KspControlData/control.</summary>
[TestClass]
public class WorkspacePathTests
{
    static string Root => OperatingSystem.IsWindows() ? @"C:\Games\KSP" : "/games/ksp";
    static CraftPaths Make(string save = "Sandbox", Func<string, bool>? reparse = null) => new(Root, save, reparse);

    [DataTestMethod]
    [DataRow("control")] [DataRow("Control")] [DataRow("CONTROL")]
    public void ASaveNamedControlGetsNoToolWorkspaceBecauseItWouldLiveInTheSuspensionDirectory(string save)
    {
        var p = Make(save);
        Assert.IsFalse(p.WorkspaceAllowed);
        foreach (var check in new[] { p.StagingPath("req_12345678"), p.UpgradedStagingPath("req_12345678"), p.RecoveryPath("snap0001"), p.RecoveryMetaPath("snap0001"), p.ThumbnailBackupPath("req_12345678") })
        { Assert.IsFalse(check.Ok); Assert.AreEqual("path_outside_save", check.ReasonCode); }
        Assert.IsNull(p.WorkspaceDirectory("staging")); Assert.IsNull(p.WorkspaceDirectory("recovery")); Assert.IsNull(p.WorkspaceDirectory("thumbs-backup"));
    }

    [TestMethod] public void ASaveNamedControlStillHasItsOwnShipsFolder()
    {
        var p = Make("control");
        var ships = p.ResolveNewShip("VAB", "My Rocket");
        Assert.IsTrue(ships.Ok, ships.ReasonCode);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Root, "saves", "control", "Ships", "VAB", "My Rocket.craft")), ships.FullPath);
    }

    [TestMethod] public void OtherSaveNamesThatMerelyContainControlAreFine()
    {
        foreach (var save in new[] { "control2", "my-control", "control.x", "controls" })
            Assert.IsTrue(Make(save).StagingPath("req_12345678").Ok, save);
    }

    [TestMethod] public void NoWorkspacePathEverFallsInsideTheControlDirectory()
    {
        var p = Make();
        var control = Path.GetFullPath(p.ControlDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var check in new[] { p.StagingPath("req_12345678"), p.RecoveryPath("snap0001"), p.RecoveryMetaPath("snap0001"), p.ThumbnailBackupPath("req_12345678") })
        { Assert.IsTrue(check.Ok); Assert.IsFalse(check.FullPath.StartsWith(control, StringComparison.OrdinalIgnoreCase), check.FullPath); }
        Assert.AreEqual(Path.Combine(Root, "KspControlData", "control", "suspensions.json"), p.SuspensionsFile);
    }

    [TestMethod] public void WorkspaceDirectoriesAreTheThreeKnownOnes()
    {
        var p = Make();
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Root, "KspControlData", "Sandbox", "staging")), p.WorkspaceDirectory("staging"));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Root, "KspControlData", "Sandbox", "recovery")), p.WorkspaceDirectory("recovery"));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Root, "KspControlData", "Sandbox", "thumbs-backup")), p.WorkspaceDirectory("thumbs-backup"));
        foreach (var other in new[] { "", "control", "..", "Ships", "staging/x", "STAGING" }) Assert.IsNull(p.WorkspaceDirectory(other), other);
    }

    [TestMethod] public void ReparsePointsAnywhereUnderTheWorkspaceDenyIt()
    {
        var staging = Path.GetFullPath(Path.Combine(Root, "KspControlData", "Sandbox", "staging"));
        var p = Make(reparse: path => string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), staging, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("reparse_point", p.StagingPath("req_12345678").ReasonCode);
        Assert.IsNull(p.WorkspaceDirectory("staging"));
        Assert.IsTrue(p.RecoveryPath("snap0001").Ok, "other directories are unaffected");
        var data = Path.GetFullPath(Path.Combine(Root, "KspControlData"));
        var q = Make(reparse: path => string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), data, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("reparse_point", q.RecoveryPath("snap0001").ReasonCode, "KspControlData itself being a link denies everything under it");
    }

    [TestMethod] public void SnapshotMetadataSitsBesideTheRecoveryCraftAndIdsAreStrict()
    {
        var p = Make();
        var craft = p.RecoveryPath("snap0001"); var meta = p.RecoveryMetaPath("snap0001");
        Assert.AreEqual("kc-snap-snap0001.craft", Path.GetFileName(craft.FullPath)); Assert.AreEqual("kc-snap-snap0001.json", Path.GetFileName(meta.FullPath));
        Assert.AreEqual(Path.GetDirectoryName(craft.FullPath), Path.GetDirectoryName(meta.FullPath));
        foreach (var bad in new[] { null, "", "short", "../../x/yyyy", "has space in it", "a/b/c/d/e/f/g/h" })
        { Assert.IsFalse(p.RecoveryMetaPath(bad!).Ok, bad); Assert.AreEqual("invalid_snapshot_id", p.RecoveryMetaPath(bad!).ReasonCode); }
    }

    [TestMethod] public void ThumbnailsLiveInTheKspThumbsFolderNamedBySaveFacilityAndStem()
    {
        var p = Make();
        Assert.AreEqual(Path.Combine(Root, "thumbs"), p.ThumbnailsDirectory);
        Assert.AreEqual("Sandbox_VAB_", p.ThumbnailPrefix("VAB"));
        Assert.AreEqual(Path.Combine(Root, "thumbs", "Sandbox_VAB_kc-req_12345678.png"), p.ThumbnailFile("VAB", "kc-req_12345678"));
        Assert.AreEqual(Path.Combine(Root, "thumbs", "Sandbox_SPH_My Rocket.png"), p.ThumbnailFile("SPH", "My Rocket"));
        foreach (var stem in new[] { null, "", "../x", "a/b", "a\\b", "..", "x..y" }) Assert.IsNull(p.ThumbnailFile("VAB", stem!), stem);
        Assert.IsNull(p.ThumbnailFile("LAUNCHPAD", "ok"));
    }
}
