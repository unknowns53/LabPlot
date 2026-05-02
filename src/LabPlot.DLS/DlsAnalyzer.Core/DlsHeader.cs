namespace DlsAnalyzer.Core;

/// <summary>
/// Parsed Zetasizer column header. Headers follow the pattern
/// "&lt;DataType&gt; (&lt;unit&gt;) - &lt;SampleName&gt; [&lt;State&gt;]"
/// (e.g. "Size (d.nm) - 1-41_2_20 [Steady state]").
/// </summary>
public sealed record DlsHeader(DlsColumnKind Kind, string? SampleLabel, string? State, string Raw)
{
    public static DlsHeader Empty { get; } = new(DlsColumnKind.Unknown, null, null, string.Empty);

    public static DlsHeader Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;
        var trimmed = raw.Trim();
        return new DlsHeader(DetermineKind(trimmed), ExtractSampleLabel(trimmed), ExtractState(trimmed), trimmed);
    }

    private static DlsColumnKind DetermineKind(string raw)
    {
        if (Contains(raw, "Size") && Contains(raw, "d.nm")) return DlsColumnKind.SizeAxis;
        if (Contains(raw, "Number") && Contains(raw, "Percent")) return DlsColumnKind.NumberPercent;
        if (Contains(raw, "Intensity") && Contains(raw, "Percent")) return DlsColumnKind.IntensityPercent;
        if (Contains(raw, "Volume") && Contains(raw, "Percent")) return DlsColumnKind.VolumePercent;
        if (Contains(raw, "Correlation")) return DlsColumnKind.CorrelationG2Minus1;
        if (Contains(raw, "Time")) return DlsColumnKind.TimeAxis;
        return DlsColumnKind.Unknown;
    }

    private static bool Contains(string source, string token) =>
        source.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractSampleLabel(string raw)
    {
        var dashIndex = raw.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex < 0) return null;
        var afterDash = raw[(dashIndex + 3)..];
        var bracketIndex = afterDash.IndexOf('[');
        var name = (bracketIndex < 0 ? afterDash : afterDash[..bracketIndex]).Trim();
        return name.Length > 0 ? name : null;
    }

    private static string? ExtractState(string raw)
    {
        var openIndex = raw.LastIndexOf('[');
        if (openIndex < 0) return null;
        var closeIndex = raw.LastIndexOf(']');
        if (closeIndex <= openIndex) return null;
        var state = raw.Substring(openIndex + 1, closeIndex - openIndex - 1).Trim();
        return state.Length > 0 ? state : null;
    }
}
