using System.Text.Json;

namespace GpcAnalyzer.Core;

public sealed class StandardCurveFileReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public CalibrationCurveSet Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Standard curve file was not found.", filePath);
        }

        using var stream = File.OpenRead(filePath);
        var rawCurves = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CalibrationCurveCoefficients>>>(
            stream,
            JsonOptions);

        if (rawCurves is null || rawCurves.Count == 0)
        {
            throw new InvalidDataException("No calibration curves were found.");
        }

        var curves = new Dictionary<string, IReadOnlyDictionary<string, CalibrationCurve>>(StringComparer.OrdinalIgnoreCase);

        foreach (var solventEntry in rawCurves)
        {
            if (string.IsNullOrWhiteSpace(solventEntry.Key) || solventEntry.Value.Count == 0)
            {
                continue;
            }

            var detectorCurves = new Dictionary<string, CalibrationCurve>(StringComparer.OrdinalIgnoreCase);
            foreach (var detectorEntry in solventEntry.Value)
            {
                if (string.IsNullOrWhiteSpace(detectorEntry.Key))
                {
                    continue;
                }

                detectorCurves[detectorEntry.Key] = new CalibrationCurve
                {
                    Solvent = solventEntry.Key,
                    Detector = detectorEntry.Key,
                    Coefficients = detectorEntry.Value,
                };
            }

            if (detectorCurves.Count > 0)
            {
                curves[solventEntry.Key] = detectorCurves;
            }
        }

        if (curves.Count == 0)
        {
            throw new InvalidDataException("No usable calibration curves were found.");
        }

        return new CalibrationCurveSet(curves)
        {
            Solvents = curves.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }
}
