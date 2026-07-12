using System.Collections.Generic;
using Project_Jumpstart_Undertale_Mod_Manager.Reporting;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Xunit;

namespace MergeTests;

public class MergeReportTests
{
    [Fact]
    public void CleanMerge_saysClean()
    {
        var r = MergeResult.Ok(new List<ConflictLogEntry>(), new List<string>());
        string report = MergeReport.Format(r);
        Assert.Contains("Merge succeeded", report);
        Assert.Contains("clean merge", report);
    }

    [Fact]
    public void Warnings_areListed_andCounted()
    {
        var r = MergeResult.Ok(
            new List<ConflictLogEntry>(),
            new List<string> { "mod X skipped code foo", "mod Y degraded" });
        string report = MergeReport.Format(r);
        Assert.Contains("Warnings (2)", report);
        Assert.Contains("mod X skipped code foo", report);
        Assert.Contains("mod Y degraded", report);
    }

    [Fact]
    public void Conflicts_nameWinnerAndLosers()
    {
        var r = MergeResult.Ok(
            new List<ConflictLogEntry>
            {
                new("undertale//sprites:spr_x", "ModB", new[] { "ModA" })
            },
            new List<string>());
        string report = MergeReport.Format(r);
        Assert.Contains("Conflicts (1)", report);
        Assert.Contains("ModB won over ModA", report);
    }

    [Fact]
    public void Failure_namesModAndReason()
    {
        var r = MergeResult.Fail("SonicMod", "requires 'Base' which is not present");
        string report = MergeReport.Format(r);
        Assert.Contains("FAILED on 'SonicMod'", report);
        Assert.Contains("requires 'Base'", report);
    }
}