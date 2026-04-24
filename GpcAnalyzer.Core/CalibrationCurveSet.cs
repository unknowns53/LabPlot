namespace GpcAnalyzer.Core;

public sealed class CalibrationCurveSet
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, CalibrationCurve>> _curves;

    public CalibrationCurveSet(IReadOnlyDictionary<string, IReadOnlyDictionary<string, CalibrationCurve>> curves)
    {
        _curves = curves;
    }

    public IReadOnlyList<string> Solvents { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> GetDetectors(string solvent)
    {
        return _curves.TryGetValue(solvent, out var detectors)
            ? detectors.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
    }

    public CalibrationCurve GetCurve(string solvent, string detector)
    {
        if (!_curves.TryGetValue(solvent, out var detectors))
        {
            throw new KeyNotFoundException($"Calibration solvent was not found: {solvent}");
        }

        if (!detectors.TryGetValue(detector, out var curve))
        {
            throw new KeyNotFoundException($"Calibration detector was not found: {solvent}/{detector}");
        }

        return curve;
    }
}
