namespace Backtester.Core.Analytics;

public static class Statistics
{
    public static double Mean(IReadOnlyList<double> x) => x.Count == 0 ? double.NaN : x.Average();

    public static double StdDev(IReadOnlyList<double> x)
    {
        if (x.Count < 2) return double.NaN;
        double m = x.Average();
        return Math.Sqrt(x.Sum(v => (v - m) * (v - m)) / (x.Count - 1));
    }

    public static double Variance(IReadOnlyList<double> x)
    {
        double sd = StdDev(x);
        return sd * sd;
    }

    /// <summary>Sample skewness (population moments, g1).</summary>
    public static double Skewness(IReadOnlyList<double> x)
    {
        if (x.Count < 3) return double.NaN;
        double m = x.Average();
        double m2 = x.Sum(v => Math.Pow(v - m, 2)) / x.Count;
        double m3 = x.Sum(v => Math.Pow(v - m, 3)) / x.Count;
        return m2 == 0 ? 0 : m3 / Math.Pow(m2, 1.5);
    }

    /// <summary>Kurtosis (not excess): 3 for a normal distribution.</summary>
    public static double Kurtosis(IReadOnlyList<double> x)
    {
        if (x.Count < 4) return double.NaN;
        double m = x.Average();
        double m2 = x.Sum(v => Math.Pow(v - m, 2)) / x.Count;
        double m4 = x.Sum(v => Math.Pow(v - m, 4)) / x.Count;
        return m2 == 0 ? 3 : m4 / (m2 * m2);
    }

    /// <summary>Standard normal CDF via erfc (Numerical Recipes erfcc, |relative error| &lt; 1.2e-7).</summary>
    public static double NormalCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2));

    private static double Erfc(double x)
    {
        double z = Math.Abs(x);
        double t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                   t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
                   t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2 - r;
    }

    /// <summary>Inverse standard normal CDF (Acklam's rational approximation plus one Newton step).</summary>
    public static double NormalInv(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;

        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double low = 0.02425, high = 1 - low;

        double x;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        else if (p <= high)
        {
            double q = p - 0.5, r = q * q;
            x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        else
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        double e = NormalCdf(x) - p;
        double u = e * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
        return x - u / (1 + x * u / 2);
    }
}
