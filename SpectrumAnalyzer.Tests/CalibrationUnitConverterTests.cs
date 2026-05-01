using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class CalibrationUnitConverterTests
{
    [Fact]
    public void ToMolar_MolarUnits_ApplyExpectedFactors()
    {
        Assert.Equal(2.0, CalibrationUnitConverter.ToMolar(2.0, CalibrationConcentrationUnit.MolPerLiter, null));
        Assert.Equal(2e-3, CalibrationUnitConverter.ToMolar(2.0, CalibrationConcentrationUnit.MillimolPerLiter, null));
        Assert.Equal(2e-6, CalibrationUnitConverter.ToMolar(2.0, CalibrationConcentrationUnit.MicromolPerLiter, null));
        Assert.Equal(2e-9, CalibrationUnitConverter.ToMolar(2.0, CalibrationConcentrationUnit.NanomolPerLiter, null));
    }

    [Fact]
    public void ToMolar_MassBasedUnits_RequireMolarMass()
    {
        // BSA-like: 1 mg/mL ÷ 66,500 g/mol ≈ 1.504e-5 mol/L
        var asMgPerMl = CalibrationUnitConverter.ToMolar(
            1.0, CalibrationConcentrationUnit.MilligramPerMilliliter, 66_500.0);
        Assert.NotNull(asMgPerMl);
        Assert.Equal(1.5037593984962406e-5, asMgPerMl.Value, precision: 12);

        // 1 g/L ÷ 66,500 g/mol = same as 1 mg/mL since the units are equivalent
        var asGPerL = CalibrationUnitConverter.ToMolar(
            1.0, CalibrationConcentrationUnit.GramPerLiter, 66_500.0);
        Assert.Equal(asMgPerMl.Value, asGPerL!.Value, precision: 12);
    }

    [Fact]
    public void ToMolar_MassBasedUnit_NoMolarMass_ReturnsNull()
    {
        Assert.Null(CalibrationUnitConverter.ToMolar(
            1.0, CalibrationConcentrationUnit.MilligramPerMilliliter, null));
        Assert.Null(CalibrationUnitConverter.ToMolar(
            1.0, CalibrationConcentrationUnit.GramPerLiter, molarMassGramsPerMol: 0));
    }

    [Fact]
    public void ToMolar_NonFiniteValue_ReturnsNull()
    {
        Assert.Null(CalibrationUnitConverter.ToMolar(
            double.NaN, CalibrationConcentrationUnit.MolPerLiter, null));
    }

    [Fact]
    public void RequiresMolarMass_OnlyMassBased()
    {
        Assert.False(CalibrationUnitConverter.RequiresMolarMass(CalibrationConcentrationUnit.MolPerLiter));
        Assert.False(CalibrationUnitConverter.RequiresMolarMass(CalibrationConcentrationUnit.MicromolPerLiter));
        Assert.True(CalibrationUnitConverter.RequiresMolarMass(CalibrationConcentrationUnit.MilligramPerMilliliter));
        Assert.True(CalibrationUnitConverter.RequiresMolarMass(CalibrationConcentrationUnit.GramPerLiter));
    }

    [Fact]
    public void Parse_AcceptsSymbolsAndNames()
    {
        Assert.Equal(CalibrationConcentrationUnit.MicromolPerLiter, CalibrationUnitConverter.Parse("μM"));
        Assert.Equal(CalibrationConcentrationUnit.MicromolPerLiter, CalibrationUnitConverter.Parse("uM"));
        Assert.Equal(CalibrationConcentrationUnit.MicromolPerLiter, CalibrationUnitConverter.Parse("MicromolPerLiter"));
        Assert.Equal(CalibrationConcentrationUnit.MilligramPerMilliliter, CalibrationUnitConverter.Parse("mg/mL"));
        Assert.Null(CalibrationUnitConverter.Parse("bogus"));
        Assert.Null(CalibrationUnitConverter.Parse(null));
    }
}
