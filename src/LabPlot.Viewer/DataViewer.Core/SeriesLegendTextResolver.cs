namespace DataViewer.Core;

/// <summary>
/// Resolves the legend label shown for one plotted series. Extracted so the
/// exact same rule can be reused outside <c>MainWindow</c> (e.g. by a future
/// flat series list) without re-implementing it.
/// </summary>
public static class SeriesLegendTextResolver
{
    /// <summary>
    /// A trimmed <paramref name="legendName"/> wins if it is non-blank.
    /// Otherwise falls back to the column name alone when only one table is
    /// loaded, or to "{tableDisplayName}: {columnName}" once several tables
    /// share the plot (so series from different sources stay distinguishable
    /// in the legend).
    /// </summary>
    public static string Resolve(string? legendName, string columnName, string tableDisplayName, bool multipleTablesLoaded)
    {
        if (!string.IsNullOrWhiteSpace(legendName))
        {
            return legendName.Trim();
        }

        return multipleTablesLoaded
            ? $"{tableDisplayName}: {columnName}"
            : columnName;
    }
}
