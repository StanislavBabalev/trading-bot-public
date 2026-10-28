namespace Backtester.Core.Analytics;

/// <summary>
/// Probabilistic and Deflated Sharpe Ratio (Bailey &amp; López de Prado, 2014).
/// All Sharpe ratios here are per observation period (e.g. daily, not annualized).
/// </summary>
public static class DeflatedSharpe
{
    public const double EulerGamma = 0.5772156649015329;

    /// <summary>
    /// Expected maximum Sharpe ratio among <paramref name="trials"/> independent strategies whose true Sharpe is 0,
    /// given the variance of the Sharpe ratios observed across those trials.
    /// </summary>
    public static double ExpectedMaxSharpe(int trials, double trialSharpeVariance)
    {
        if (trials <= 1 || !(trialSharpeVariance > 0)) return 0;
        double sd = Math.Sqrt(trialSharpeVariance);
        return sd * ((1 - EulerGamma) * Statistics.NormalInv(1 - 1.0 / trials)
                     + EulerGamma * Statistics.NormalInv(1 - 1.0 / (trials * Math.E)));
    }

    /// <summary>P(true Sharpe &gt; <paramref name="benchmarkSharpe"/>) given an observed Sharpe over n returns.</summary>
    public static double Probabilistic(double sharpe, double benchmarkSharpe, int observations, double skewness, double kurtosis)
    {
        if (observations < 2 || double.IsNaN(sharpe)) return double.NaN;
        double denom = 1 - skewness * sharpe + (kurtosis - 1) / 4 * sharpe * sharpe;
        if (!(denom > 0)) return double.NaN;
        return Statistics.NormalCdf((sharpe - benchmarkSharpe) * Math.Sqrt(observations - 1) / Math.Sqrt(denom));
    }

    /// <summary>PSR against the Sharpe you would expect from the best of <paramref name="trials"/> lucky strategies.</summary>
    public static double Deflated(double sharpe, int observations, double skewness, double kurtosis, int trials, double trialSharpeVariance) =>
        Probabilistic(sharpe, ExpectedMaxSharpe(trials, trialSharpeVariance), observations, skewness, kurtosis);
}
