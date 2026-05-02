using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabPlot.Core;

/// <summary>
/// Generic JSON store for any LabPlot app's <see cref="AnalysisSession"/>
/// subclass. <see cref="Save"/> writes pretty-printed UTF-8 JSON (with
/// BOM); <see cref="Load"/> deserialises directly into
/// <typeparamref name="TSession"/> so subclass-specific fields round-trip
/// without polymorphic JSON contracts.
/// </summary>
/// <remarks>
/// After deserialisation <see cref="Load"/> calls
/// <see cref="AnalysisSession.EnsureDefaults"/> so the subclass can
/// re-create any concrete <c>Datasets</c> / <c>Axes</c> / <c>Formatting</c>
/// containers that came back null from a partial JSON payload, and
/// normalise its formatting config in one place.
/// </remarks>
public class AnalysisSessionStore<TSession> where TSession : AnalysisSession, new()
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(TSession session, string filePath)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(filePath));
        }

        session.Version = AnalysisSession.CurrentVersion;
        session.SavedAt = DateTimeOffset.Now;
        var json = JsonSerializer.Serialize(session, JsonOptions);
        File.WriteAllText(filePath, json, Utf8WithBom);
    }

    public TSession Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Input file path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Session file was not found.", filePath);
        }

        var json = File.ReadAllText(filePath);
        var session = JsonSerializer.Deserialize<TSession>(json, JsonOptions)
            ?? throw new InvalidDataException("Session file is empty or invalid.");

        if (session.Version > AnalysisSession.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Session file uses version {session.Version}, which is newer than this application supports (version {AnalysisSession.CurrentVersion}).");
        }

        session.EnsureDefaults();
        return session;
    }
}
