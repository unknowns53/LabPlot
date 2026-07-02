namespace DataViewer.Core;

/// <summary>
/// Pure ordering logic behind the flat series list: moving one item within an
/// already-ordered sequence, and flattening several physically-grouped
/// sequences (e.g. "series nested under tables") into a single list ordered
/// by an explicit display-order key rather than by physical position.
/// </summary>
public static class SeriesOrderPlanner
{
    /// <summary>
    /// Returns a new list with the element at <paramref name="fromIndex"/>
    /// moved to <paramref name="toIndex"/>. A no-op (a plain copy of
    /// <paramref name="orderedItems"/>) when the indexes are equal or either
    /// is out of range; the source list is never mutated.
    /// </summary>
    public static List<T> Move<T>(IReadOnlyList<T> orderedItems, int fromIndex, int toIndex)
    {
        var result = new List<T>(orderedItems);

        if (fromIndex == toIndex
            || fromIndex < 0 || fromIndex >= result.Count
            || toIndex < 0 || toIndex >= result.Count)
        {
            return result;
        }

        var item = result[fromIndex];
        result.RemoveAt(fromIndex);
        result.Insert(toIndex, item);
        return result;
    }

    /// <summary>
    /// Enumerates <paramref name="groups"/> in their physical (group, then
    /// within-group) order, then stably sorts the resulting flat sequence by
    /// <paramref name="orderOf"/>. Because the sort is stable, items sharing
    /// the same key keep their relative physical position - this is what
    /// lets an unset (all-zero) display order fall back to the legacy
    /// "table then column" enumeration order.
    /// </summary>
    public static List<T> FlattenInDisplayOrder<T>(IEnumerable<IEnumerable<T>> groups, Func<T, int> orderOf)
    {
        return groups
            .SelectMany(static group => group)
            .OrderBy(orderOf)
            .ToList();
    }
}
