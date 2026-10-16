using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

public sealed record VersionRule(string Operator, int[] Version)
{
    private static readonly string[] Operators = [">=", "<=", ">", "<", "="];

    public static bool IsAny(string? rule) => string.IsNullOrWhiteSpace(rule) || rule.Trim() == "*";

    public static bool TryParse(string? rule, [NotNullWhen(true)] out VersionRule? parsed)
    {
        parsed = null;
        string text = (rule ?? "").Trim();
        string op = "=";
        foreach (string candidate in Operators)
        {
            if (!text.StartsWith(candidate, StringComparison.Ordinal)) continue;
            op = candidate;
            text = text[candidate.Length..].Trim();
            break;
        }

        if (!TryParseVersion(text, out int[]? version)) return false;
        parsed = new VersionRule(op, version);
        return true;
    }

    public static bool TryParseVersion(string? text, [NotNullWhen(true)] out int[]? version)
    {
        version = null;
        string[] parts = (text ?? "").Trim().Split('.');
        if (parts.Length > 4) return false;

        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;

        version = numbers;
        return true;
    }

    public bool Allows(int[] installed)
    {
        int order = Compare(installed, Version);
        return Operator switch
        {
            ">=" => order >= 0,
            "<=" => order <= 0,
            ">" => order > 0,
            "<" => order < 0,
            _ => order == 0
        };
    }

    private static int Compare(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int left = i < a.Length ? a[i] : 0;
            int right = i < b.Length ? b[i] : 0;
            if (left != right) return left.CompareTo(right);
        }
        return 0;
    }
}
