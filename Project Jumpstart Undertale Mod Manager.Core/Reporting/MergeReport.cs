using System.Linq;
using System.Text;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

namespace Project_Jumpstart_Undertale_Mod_Manager.Reporting;

// Turns a completed MergeResult into a human-readable report. Presentation-neutral
// so both the app (console/dialog/log) and tests can use it. Conflicts are
// informational (last-wins by design); warnings mean something was skipped.
public static class MergeReport
{
    public static string Format(MergeResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine(result.Success
            ? "Merge succeeded."
            : $"Merge FAILED on '{result.FailedMod}': {result.Reason}");

        if (result.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Conflicts ({result.Conflicts.Count}) — later mod in load order wins:");
            foreach (var c in result.Conflicts)
                sb.AppendLine($"  {c.AssetKey}: {c.WinningMod} won over {string.Join(", ", c.OverriddenMods)}");
        }

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Warnings ({result.Warnings.Count}) — something was skipped or degraded:");
            foreach (var w in result.Warnings)
                sb.AppendLine($"  {w}");
        }

        if (result.Success && result.Conflicts.Count == 0 && result.Warnings.Count == 0)
            sb.AppendLine("No conflicts, no warnings — clean merge.");

        return sb.ToString();
    }
}