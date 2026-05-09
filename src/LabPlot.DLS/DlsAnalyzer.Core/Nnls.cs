namespace DlsAnalyzer.Core;

/// <summary>
/// Lawson-Hanson non-negative least-squares (NNLS) solver for the
/// dense system min ||A·x − b||₂  s.t.  x ≥ 0.
/// </summary>
/// <remarks>
/// Reference: Lawson &amp; Hanson, "Solving Least Squares Problems"
/// (1974), Algorithm 23.10. The implementation is the classical
/// active-set scheme:
///
///   - keep two index sets: the *passive* set P (variables free to be
///     positive) and the *active* set Z (variables fixed at 0)
///   - per outer iteration, take the gradient w = Aᵀ(b − Ax) and pick
///     the most violated active constraint (largest w[j], j ∈ Z); if
///     none is positive, the KKT conditions are satisfied and we exit
///   - per inner iteration, solve the unconstrained least-squares
///     problem on the columns of A indexed by P; if the resulting
///     candidate keeps every passive variable strictly positive it is
///     accepted, otherwise we walk along the line to keep feasibility
///     and migrate the variables that hit zero back into Z
///
/// The unconstrained sub-problem is solved through the normal
/// equations Aᵖᵀ Aᵖ s = Aᵖᵀ b with Gaussian elimination + partial
/// pivoting on the small dynamically-sized system. Cholesky would be
/// faster for very large P but the dense Gauss elimination is robust
/// for the |P| ≤ 60 grids used by the DLS size-distribution inverter.
/// </remarks>
public static class Nnls
{
    public sealed record Outcome
    {
        public required bool Converged { get; init; }
        public required double[] X { get; init; }
        public required int OuterIterations { get; init; }
        public required int InnerIterations { get; init; }
        public required double ResidualNormSquared { get; init; }
    }

    /// <summary>
    /// Solve min ||A·x − b||₂ subject to x ≥ 0.
    /// </summary>
    /// <param name="a">Design matrix (m rows × n cols), copied internally so the caller may mutate it later.</param>
    /// <param name="b">Right-hand side (length m).</param>
    /// <param name="maxOuterIterations">Cap on the active-set outer loop. Default 3·n.</param>
    /// <param name="tolerance">Numerical zero used for "is this gradient component still positive?" / "did this passive variable bottom out?". Default 1e-10 · ||A||_F · ||b||_∞.</param>
    public static Outcome Solve(
        double[,] a,
        double[] b,
        int? maxOuterIterations = null,
        double? tolerance = null)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (b is null) throw new ArgumentNullException(nameof(b));
        int m = a.GetLength(0);
        int n = a.GetLength(1);
        if (b.Length != m)
            throw new ArgumentException($"b length {b.Length} does not match A row count {m}", nameof(b));

        var x = new double[n];
        if (n == 0)
            return new Outcome { Converged = true, X = x, OuterIterations = 0, InnerIterations = 0, ResidualNormSquared = SumSquares(b) };

        var inP = new bool[n];               // passive set membership
        var w = new double[n];               // gradient = Aᵀ (b − A x)
        var residual = new double[m];

        var resolvedTolerance = tolerance ?? DefaultTolerance(a, b);
        var outerCap = maxOuterIterations ?? 3 * n;

        int outerIter = 0;
        int innerIter = 0;

        while (outerIter < outerCap)
        {
            outerIter++;

            // residual = b − A x
            for (int i = 0; i < m; i++)
            {
                double sum = b[i];
                for (int j = 0; j < n; j++) sum -= a[i, j] * x[j];
                residual[i] = sum;
            }
            // w = Aᵀ residual
            for (int j = 0; j < n; j++)
            {
                double sum = 0;
                for (int i = 0; i < m; i++) sum += a[i, j] * residual[i];
                w[j] = sum;
            }

            // KKT: among active variables, the gradient must be ≤ 0; otherwise
            // adding the most-violating one to the passive set will reduce
            // the residual.
            int t = -1;
            double maxW = resolvedTolerance;
            for (int j = 0; j < n; j++)
            {
                if (inP[j]) continue;
                if (w[j] > maxW) { maxW = w[j]; t = j; }
            }
            if (t < 0) break;

            inP[t] = true;

            // Inner loop: solve the unconstrained problem on the current
            // passive set, walk along the line if any component goes
            // negative, retire the offending variables to Z, repeat.
            while (true)
            {
                innerIter++;
                var pIndices = PassiveIndices(inP);
                var s = new double[n];
                if (!SolvePassiveSubproblem(a, b, pIndices, s))
                {
                    // Sub-problem singular — back off the latest move and
                    // bail. The active set is left consistent because the
                    // passive variable that triggered this branch keeps
                    // its current x[j] (possibly 0) and the loop exits.
                    inP[t] = false;
                    return new Outcome
                    {
                        Converged = false,
                        X = x,
                        OuterIterations = outerIter,
                        InnerIterations = innerIter,
                        ResidualNormSquared = SumSquares(residual),
                    };
                }

                // Check feasibility of the candidate s on the passive set.
                bool allPositive = true;
                for (int k = 0; k < pIndices.Length; k++)
                {
                    if (s[pIndices[k]] <= resolvedTolerance) { allPositive = false; break; }
                }

                if (allPositive)
                {
                    for (int k = 0; k < pIndices.Length; k++)
                        x[pIndices[k]] = s[pIndices[k]];
                    break;
                }

                // Walk along the line x → x + α(s − x) until the first
                // passive variable hits zero. α ∈ (0, 1] by construction.
                double alpha = double.PositiveInfinity;
                for (int k = 0; k < pIndices.Length; k++)
                {
                    var idx = pIndices[k];
                    if (s[idx] > resolvedTolerance) continue;
                    var denom = x[idx] - s[idx];
                    if (denom <= 0) continue;
                    var ratio = x[idx] / denom;
                    if (ratio < alpha) alpha = ratio;
                }
                if (!double.IsFinite(alpha) || alpha <= 0)
                {
                    // Degenerate case: no positive ratio found although
                    // some s[j] ≤ 0. Force-retire the violators and let
                    // the next outer iteration restart the descent.
                    for (int k = 0; k < pIndices.Length; k++)
                    {
                        var idx = pIndices[k];
                        if (s[idx] <= resolvedTolerance)
                        {
                            inP[idx] = false;
                            x[idx] = 0;
                        }
                    }
                    break;
                }

                for (int k = 0; k < pIndices.Length; k++)
                {
                    var idx = pIndices[k];
                    x[idx] = x[idx] + alpha * (s[idx] - x[idx]);
                    if (x[idx] <= resolvedTolerance)
                    {
                        x[idx] = 0;
                        inP[idx] = false;
                    }
                }
            }
        }

        // Final residual.
        for (int i = 0; i < m; i++)
        {
            double sum = b[i];
            for (int j = 0; j < n; j++) sum -= a[i, j] * x[j];
            residual[i] = sum;
        }

        return new Outcome
        {
            Converged = outerIter < outerCap,
            X = x,
            OuterIterations = outerIter,
            InnerIterations = innerIter,
            ResidualNormSquared = SumSquares(residual),
        };
    }

    private static int[] PassiveIndices(bool[] inP)
    {
        int count = 0;
        for (int j = 0; j < inP.Length; j++) if (inP[j]) count++;
        var arr = new int[count];
        int k = 0;
        for (int j = 0; j < inP.Length; j++) if (inP[j]) arr[k++] = j;
        return arr;
    }

    /// <summary>
    /// Solve A_P^T A_P · s_P = A_P^T b on the columns indexed by
    /// <paramref name="passive"/>, writing the result into the
    /// corresponding slots of <paramref name="s"/>. Returns false if
    /// the normal equations are singular.
    /// </summary>
    private static bool SolvePassiveSubproblem(double[,] a, double[] b, int[] passive, double[] s)
    {
        int m = a.GetLength(0);
        int p = passive.Length;
        if (p == 0) return true;

        var ata = new double[p, p + 1]; // last column = Aᵀb (augmented)
        for (int i = 0; i < p; i++)
        {
            int colI = passive[i];
            for (int j = 0; j <= i; j++)
            {
                int colJ = passive[j];
                double sum = 0;
                for (int r = 0; r < m; r++) sum += a[r, colI] * a[r, colJ];
                ata[i, j] = sum;
                ata[j, i] = sum;
            }
            double rhs = 0;
            for (int r = 0; r < m; r++) rhs += a[r, colI] * b[r];
            ata[i, p] = rhs;
        }

        // Gauss elimination with partial pivoting.
        for (int k = 0; k < p; k++)
        {
            int pivot = k;
            double pivotValue = Math.Abs(ata[k, k]);
            for (int i = k + 1; i < p; i++)
            {
                if (Math.Abs(ata[i, k]) > pivotValue)
                {
                    pivot = i;
                    pivotValue = Math.Abs(ata[i, k]);
                }
            }
            if (pivotValue < 1e-30) return false;

            if (pivot != k)
            {
                for (int j = 0; j <= p; j++)
                    (ata[k, j], ata[pivot, j]) = (ata[pivot, j], ata[k, j]);
            }
            for (int i = k + 1; i < p; i++)
            {
                var factor = ata[i, k] / ata[k, k];
                for (int j = k; j <= p; j++) ata[i, j] -= factor * ata[k, j];
            }
        }

        for (int i = p - 1; i >= 0; i--)
        {
            double sum = ata[i, p];
            for (int j = i + 1; j < p; j++) sum -= ata[i, j] * s[passive[j]];
            s[passive[i]] = sum / ata[i, i];
            if (!double.IsFinite(s[passive[i]])) return false;
        }
        return true;
    }

    private static double SumSquares(double[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
        return sum;
    }

    private static double DefaultTolerance(double[,] a, double[] b)
    {
        double normA = 0;
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++) normA += a[i, j] * a[i, j];
        normA = Math.Sqrt(normA);
        double normB = 0;
        for (int i = 0; i < b.Length; i++)
        {
            var v = Math.Abs(b[i]);
            if (v > normB) normB = v;
        }
        return Math.Max(1e-12, 1e-10 * normA * Math.Max(normB, 1.0));
    }
}
