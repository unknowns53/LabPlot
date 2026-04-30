using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class PeakAssignmentTests
{
    [Fact]
    public void IsRange_TrueForBand_FalseForSingleValue()
    {
        var band = new PeakAssignment { Label = "C=O", MinWavenumber = 1680, MaxWavenumber = 1750 };
        var single = new PeakAssignment { Label = "Marker", MinWavenumber = 1234, MaxWavenumber = 1234 };

        Assert.True(band.IsRange);
        Assert.False(single.IsRange);
    }

    [Fact]
    public void CenterWavenumber_IsArithmeticMean()
    {
        var assignment = new PeakAssignment { Label = "C=O", MinWavenumber = 1680, MaxWavenumber = 1750 };
        Assert.Equal(1715, assignment.CenterWavenumber);
    }

    [Fact]
    public void DefaultTable_HasUniqueLabels()
    {
        var labels = IrPeakAssignmentTable.Default.Select(a => a.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void DefaultTable_AllEntriesHaveValidWavenumberRange()
    {
        foreach (var assignment in IrPeakAssignmentTable.Default)
        {
            Assert.True(assignment.MinWavenumber > 0, $"{assignment.Label}: MinWavenumber should be positive");
            Assert.True(assignment.MaxWavenumber >= assignment.MinWavenumber,
                $"{assignment.Label}: MaxWavenumber should be >= MinWavenumber");
            // IR fingerprint window comfortably fits within 400-4000 cm⁻¹.
            Assert.InRange(assignment.MinWavenumber, 400, 4000);
            Assert.InRange(assignment.MaxWavenumber, 400, 4000);
        }
    }

    [Fact]
    public void DefaultTable_AllEntriesHaveSevenDigitHexColor()
    {
        foreach (var assignment in IrPeakAssignmentTable.Default)
        {
            Assert.NotNull(assignment.ColorHex);
            Assert.Equal(7, assignment.ColorHex.Length);
            Assert.Equal('#', assignment.ColorHex[0]);
            foreach (var c in assignment.ColorHex.AsSpan(1))
            {
                Assert.True(Uri.IsHexDigit(c),
                    $"{assignment.Label}: '{c}' is not a hex digit in {assignment.ColorHex}");
            }
        }
    }

    [Fact]
    public void DefaultTable_IsNotEmpty()
    {
        Assert.NotEmpty(IrPeakAssignmentTable.Default);
    }
}
