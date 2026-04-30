using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpcAnalyzer.Core;

public sealed class AnalysisSessionStore
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(AnalysisSession session, string filePath)
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

    public AnalysisSession Load(string filePath)
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
        var session = JsonSerializer.Deserialize<AnalysisSession>(json, JsonOptions)
            ?? throw new InvalidDataException("Session file is empty or invalid.");

        if (session.Version > AnalysisSession.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Session file uses version {session.Version}, which is newer than this application supports (version {AnalysisSession.CurrentVersion}).");
        }

        session.Datasets ??= new List<AnalysisSessionDataset>();
        session.MolecularWeight ??= new AnalysisSessionMolecularWeight();
        session.Axes ??= new AnalysisSessionAxes();
        session.Labels ??= new AnalysisSessionLabels();
        session.Formatting?.Normalize();
        return session;
    }
}
