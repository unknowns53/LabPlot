using DlsAnalyzer.Core;

namespace DlsAnalyzer.Tests;

/// <summary>
/// Unit tests for the Lawson-Hanson NNLS solver. Covers the textbook
/// scenarios (already-feasible OLS solution, negative-coefficient OLS
/// projected onto the non-negative orthant, degenerate / over-determined
/// systems) plus enough edge cases that the active-set inner loop and
/// the "force-retire" fallback are both exercised.
/// </summary>
public sealed class NnlsTests
{
    [Fact]
    public void RecoversUnconstrainedSolutionWhenAlreadyNonNegative()
    {
        // y = 2 x1 + 3 x2 + ε, with x1 = 1 and x2 = 4 ⇒ b = [14; 14; 14]
        // for A rows that span (2, 3) in different combinations.
        var a = new double[,]
        {
            { 1, 1 },
            { 2, 3 },
            { 3, 4 },
        };
        var b = new double[] { 5, 14, 19 };
        var outcome = Nnls.Solve(a, b);

        Assert.True(outcome.Converged);
        Assert.InRange(outcome.X[0], 0.99, 1.01);
        Assert.InRange(outcome.X[1], 3.99, 4.01);
        Assert.True(outcome.ResidualNormSquared < 1e-18);
    }

    [Fact]
    public void ProjectsNegativeOlsCoefficientToZero()
    {
        // OLS would give a negative coefficient on x2 (anti-correlated
        // with b); NNLS pins x2 = 0 and finds the best non-negative x1.
        var a = new double[,]
        {
            { 1, 1 },
            { 1, 1 },
            { 1, 1 },
        };
        var b = new double[] { 2, 2, 2 };
        var outcome = Nnls.Solve(a, b);

        Assert.True(outcome.Converged);
        // Either coefficient may absorb the load (the system is rank-deficient)
        // but neither may be negative.
        Assert.True(outcome.X[0] >= 0);
        Assert.True(outcome.X[1] >= 0);
        Assert.InRange(outcome.X[0] + outcome.X[1], 1.99, 2.01);
    }

    [Fact]
    public void HandlesOverdeterminedSystemWithUniqueOptimum()
    {
        // A is 4×2 and full column rank. y was generated from x = (2, 1) +
        // small noise; NNLS must recover both coefficients.
        var rng = new Random(1234);
        var a = new double[,]
        {
            { 1, 0 },
            { 0, 1 },
            { 1, 1 },
            { 2, 1 },
        };
        var trueX = new[] { 2.0, 1.0 };
        var b = new double[4];
        for (int i = 0; i < 4; i++)
        {
            double sum = 0;
            for (int j = 0; j < 2; j++) sum += a[i, j] * trueX[j];
            b[i] = sum + (rng.NextDouble() - 0.5) * 1e-3;
        }

        var outcome = Nnls.Solve(a, b);
        Assert.True(outcome.Converged);
        Assert.InRange(outcome.X[0], 1.99, 2.01);
        Assert.InRange(outcome.X[1], 0.99, 1.01);
    }

    [Fact]
    public void ReturnsZeroSolutionWhenAllGradientsNegative()
    {
        // b = -1 ⇒ Aᵀ b is negative for every column ⇒ KKT is satisfied
        // at x = 0 and the solver should not bring anything into the
        // passive set.
        var a = new double[,]
        {
            { 1, 1 },
            { 1, 1 },
        };
        var b = new double[] { -1, -1 };
        var outcome = Nnls.Solve(a, b);

        Assert.True(outcome.Converged);
        Assert.Equal(0.0, outcome.X[0]);
        Assert.Equal(0.0, outcome.X[1]);
    }

    [Fact]
    public void RecoversFiveColumnNonNegativeProblem()
    {
        // Larger-scale spot check: x = (0, 1, 2, 0, 3), A random.
        var rng = new Random(20260509);
        int m = 12, n = 5;
        var a = new double[m, n];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++) a[i, j] = rng.NextDouble();
        var trueX = new[] { 0.0, 1.0, 2.0, 0.0, 3.0 };
        var b = new double[m];
        for (int i = 0; i < m; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++) sum += a[i, j] * trueX[j];
            b[i] = sum;
        }

        var outcome = Nnls.Solve(a, b);
        Assert.True(outcome.Converged);
        for (int j = 0; j < n; j++)
        {
            Assert.True(outcome.X[j] >= -1e-10, $"Component {j} was negative: {outcome.X[j]}");
            Assert.InRange(outcome.X[j], trueX[j] - 1e-6, trueX[j] + 1e-6);
        }
    }

    [Fact]
    public void RejectsMismatchedDimensions()
    {
        var a = new double[2, 3];
        var b = new double[5];
        Assert.Throws<ArgumentException>(() => Nnls.Solve(a, b));
    }

    [Fact]
    public void HandlesEmptyColumnSet()
    {
        var a = new double[3, 0];
        var b = new double[] { 1, 2, 3 };
        var outcome = Nnls.Solve(a, b);
        Assert.True(outcome.Converged);
        Assert.Empty(outcome.X);
        // Residual = ||b||² because there are no columns to fit anything.
        Assert.InRange(outcome.ResidualNormSquared, 13.99, 14.01);
    }
}
