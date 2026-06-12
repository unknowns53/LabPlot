using System.Globalization;

namespace DataViewer.Core;

/// <summary>
/// Lenient numeric cell parsing shared by all viewer readers. Same policy
/// as the GPC CSV reader: strict dot-decimal invariant parse first, then a
/// "comma → dot" fallback for European-style decimals, with an IsFinite
/// gate so NaN / ±Infinity strings never enter a table as data.
/// </summary>
internal static class NumericParsing
{
    public static bool TryParseDouble(string? text, out double value)
    {
        if (text is null)
        {
            value = 0;
            return false;
        }

        return TryParseDouble(text.AsSpan(), out value);
    }

    public static bool TryParseDouble(ReadOnlySpan<char> text, out double value)
    {
        value = 0;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value))
        {
            return true;
        }

        if (text.IndexOf(',') >= 0 && text.IndexOf('.') < 0)
        {
            Span<char> buffer = text.Length <= 64
                ? stackalloc char[64]
                : new char[text.Length];
            buffer = buffer[..text.Length];
            text.CopyTo(buffer);
            for (var i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == ',')
                {
                    buffer[i] = '.';
                }
            }

            if (double.TryParse(buffer, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && double.IsFinite(value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}
