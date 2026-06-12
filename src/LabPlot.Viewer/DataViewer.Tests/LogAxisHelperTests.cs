using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class LogAxisHelperTests
{
    [Fact]
    public void ToLog10_TransformsPositiveValues()
    {
        var result = LogAxisHelper.ToLog10(new[] { 1.0, 10.0, 100.0 });

        Assert.Equal(new[] { 0.0, 1.0, 2.0 }, result);
    }

    [Fact]
    public void ToLog10_NonPositiveAndNonFinite_BecomeNaN()
    {
        var result = LogAxisHelper.ToLog10(new[] { 0.0, -5.0, double.NaN, double.PositiveInfinity });

        Assert.All(result, static value => Assert.True(double.IsNaN(value)));
    }

    [Fact]
    public void GetDecadeExponentRange_CoversDataRange()
    {
        var (minExponent, maxExponent) = LogAxisHelper.GetDecadeExponentRange(
            Math.Log10(0.5), Math.Log10(123));

        // floor(log10 0.5) = -1, ceil(log10 123) = 3
        Assert.Equal(-1, minExponent);
        Assert.Equal(3, maxExponent);
    }

    [Fact]
    public void GetDecadeExponentRange_DegenerateInputs_FallBackToOneDecade()
    {
        Assert.Equal((0, 1), LogAxisHelper.GetDecadeExponentRange(double.NaN, double.NaN));
        Assert.Equal((0, 1), LogAxisHelper.GetDecadeExponentRange(5, 1));
    }

    [Fact]
    public void GetDecadeExponentRange_SingleDecadeData_SpansAtLeastOneDecade()
    {
        // log10(2)〜log10(5) は同一 decade 内 → 0..1 に広げる
        var (minExponent, maxExponent) = LogAxisHelper.GetDecadeExponentRange(
            Math.Log10(2), Math.Log10(5));

        Assert.Equal(0, minExponent);
        Assert.Equal(1, maxExponent);
    }

    [Fact]
    public void CreateDecadeTicks_BuildsGeneratorWithoutThrowing()
    {
        // NumericManual.Ticks は描画時の Regenerate まで空なので、ここでは
        // 生成が例外なく完了することだけ確認する (内容は exponent range 側で担保)
        var generator = LogAxisHelper.CreateDecadeTicks(Math.Log10(0.5), Math.Log10(123));

        Assert.NotNull(generator);
    }
}
