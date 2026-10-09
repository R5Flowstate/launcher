namespace R5Flowstate.Contracts;

/// <summary>
/// One ordering for every mod version comparison: dot-separated parts, each compared by its
/// leading number first and then as text, so "1.0.10" beats "1.0.9" and "1.0" equals "1.0.0".
/// </summary>
public static class ModVersion
{
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(current))
            return false;
        return Compare(candidate, current) > 0;
    }

    public static int Compare(string? a, string? b)
    {
        var pa = (a ?? string.Empty).Trim().Split('.');
        var pb = (b ?? string.Empty).Trim().Split('.');
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            var c = LeadingNumber(sa).CompareTo(LeadingNumber(sb));
            if (c != 0)
                return c;
            c = string.Compare(StripLeadingNumber(sa), StripLeadingNumber(sb), StringComparison.OrdinalIgnoreCase);
            if (c != 0)
                return c;
        }

        return 0;
    }

    static long LeadingNumber(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
            i++;
        return i == 0 ? 0 : long.TryParse(s.AsSpan(0, Math.Min(i, 18)), out var n) ? n : 0;
    }

    static string StripLeadingNumber(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
            i++;
        return s[i..];
    }
}
