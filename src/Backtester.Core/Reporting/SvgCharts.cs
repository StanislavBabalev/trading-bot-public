using System.Globalization;
using System.Text;

namespace Backtester.Core.Reporting;

/// <summary>One line on a chart. <see cref="CssClass"/> picks its colour from the page stylesheet.</summary>
public sealed record ChartSeries(string Name, string CssClass, IReadOnlyList<(long T, double V)> Points, bool Area = false);

/// <summary>Static SVG charts (no JavaScript), so reports work offline and in any browser.</summary>
public static class SvgCharts
{
    private const int W = 1000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Line(IReadOnlyList<ChartSeries> series, Func<double, string> yFormat, string ariaLabel,
        int height = 280, bool includeZero = false, int maxPoints = 1400)
    {
        var all = series.SelectMany(s => s.Points).Where(p => double.IsFinite(p.V)).ToList();
        if (all.Count < 2) return "<p class=\"muted\">Not enough data to chart.</p>";

        const int padL = 70, padR = 14, padT = 14, padB = 30;
        long t0 = all.Min(p => p.T), t1 = all.Max(p => p.T);
        double lo = all.Min(p => p.V), hi = all.Max(p => p.V);
        if (includeZero) { lo = Math.Min(lo, 0); hi = Math.Max(hi, 0); }
        if (hi - lo < 1e-12) { hi += 1; lo -= 1; }
        var ticks = NiceTicks(lo, hi, 5);
        lo = Math.Min(lo, ticks[0]);
        hi = Math.Max(hi, ticks[^1]);

        double X(long t) => padL + (W - padL - padR) * (t - t0) / (double)Math.Max(1, t1 - t0);
        double Y(double v) => padT + (height - padT - padB) * (1 - (v - lo) / (hi - lo));

        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg class=\"chart\" viewBox=\"0 0 {W} {height}\" role=\"img\" aria-label=\"{Enc(ariaLabel)}\">");
        foreach (var v in ticks)
        {
            double y = Y(v);
            sb.Append(Inv, $"<line class=\"grid\" x1=\"{padL}\" x2=\"{W - padR}\" y1=\"{y:F1}\" y2=\"{y:F1}\"/>");
            sb.Append(Inv, $"<text class=\"axis\" x=\"{padL - 8}\" y=\"{y + 4:F1}\" text-anchor=\"end\">{Enc(yFormat(v))}</text>");
        }
        if (includeZero && lo < 0 && hi > 0)
            sb.Append(Inv, $"<line class=\"zero\" x1=\"{padL}\" x2=\"{W - padR}\" y1=\"{Y(0):F1}\" y2=\"{Y(0):F1}\"/>");
        foreach (var (t, label) in TimeTicks(t0, t1))
        {
            double x = X(t);
            sb.Append(Inv, $"<line class=\"grid\" x1=\"{x:F1}\" x2=\"{x:F1}\" y1=\"{padT}\" y2=\"{height - padB}\"/>");
            sb.Append(Inv, $"<text class=\"axis\" x=\"{x:F1}\" y=\"{height - 10}\" text-anchor=\"middle\">{label}</text>");
        }

        foreach (var s in series)
        {
            var pts = Decimate(s.Points.Where(p => double.IsFinite(p.V)).ToList(), maxPoints);
            if (pts.Count < 2) continue;
            var path = string.Join(" ", pts.Select(p => string.Create(Inv, $"{X(p.T):F1},{Y(p.V):F1}")));
            if (s.Area)
            {
                double baseY = Y(Math.Clamp(0, lo, hi));
                sb.Append(Inv, $"<polygon class=\"area {s.CssClass}\" points=\"{X(pts[0].T):F1},{baseY:F1} {path} {X(pts[^1].T):F1},{baseY:F1}\"/>");
            }
            sb.Append(Inv, $"<polyline class=\"series {s.CssClass}\" points=\"{path}\"><title>{Enc(s.Name)}</title></polyline>");
        }
        sb.Append("</svg>");

        if (series.Count > 1)
        {
            sb.Append("<div class=\"legend\">");
            foreach (var s in series) sb.Append($"<span><i class=\"key {s.CssClass}\"></i>{Enc(s.Name)}</span>");
            sb.Append("</div>");
        }
        return sb.ToString();
    }

    /// <summary>Vertical bars coloured by sign; labels under each bar (thinned when crowded).</summary>
    public static string Bars(IReadOnlyList<(string Label, double Value)> bars, Func<double, string> format, string ariaLabel,
        int height = 220, bool showValues = true, Func<int, string>? barClass = null)
    {
        var vals = bars.Where(b => double.IsFinite(b.Value)).ToList();
        if (vals.Count == 0) return "<p class=\"muted\">No data to chart.</p>";

        const int padL = 70, padR = 14, padT = 18, padB = 34;
        double lo = Math.Min(0, vals.Min(b => b.Value)), hi = Math.Max(0, vals.Max(b => b.Value));
        if (hi - lo < 1e-12) hi = lo + 1;
        var ticks = NiceTicks(lo, hi, 4);
        lo = Math.Min(lo, ticks[0]);
        hi = Math.Max(hi, ticks[^1]);
        double Y(double v) => padT + (height - padT - padB) * (1 - (v - lo) / (hi - lo));
        double slot = (W - padL - padR) / (double)bars.Count;
        double bw = Math.Max(1, slot * 0.72);
        int labelEvery = Math.Max(1, (int)Math.Ceiling(bars.Count / 14.0));

        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg class=\"chart\" viewBox=\"0 0 {W} {height}\" role=\"img\" aria-label=\"{Enc(ariaLabel)}\">");
        foreach (var v in ticks)
        {
            sb.Append(Inv, $"<line class=\"grid\" x1=\"{padL}\" x2=\"{W - padR}\" y1=\"{Y(v):F1}\" y2=\"{Y(v):F1}\"/>");
            sb.Append(Inv, $"<text class=\"axis\" x=\"{padL - 8}\" y=\"{Y(v) + 4:F1}\" text-anchor=\"end\">{Enc(format(v))}</text>");
        }
        sb.Append(Inv, $"<line class=\"zero\" x1=\"{padL}\" x2=\"{W - padR}\" y1=\"{Y(0):F1}\" y2=\"{Y(0):F1}\"/>");
        for (int i = 0; i < bars.Count; i++)
        {
            var (label, v) = bars[i];
            double cx = padL + slot * (i + 0.5);
            if (double.IsFinite(v))
            {
                double y0 = Y(0), y1 = Y(v);
                sb.Append(Inv, $"<rect class=\"bar {barClass?.Invoke(i) ?? (v >= 0 ? "pos" : "neg")}\" x=\"{cx - bw / 2:F1}\" y=\"{Math.Min(y0, y1):F1}\" width=\"{bw:F1}\" height=\"{Math.Max(1, Math.Abs(y1 - y0)):F1}\">" +
                                $"<title>{Enc(label)}: {Enc(format(v))}</title></rect>");
                if (showValues && bars.Count <= 16)
                    sb.Append(Inv, $"<text class=\"value\" x=\"{cx:F1}\" y=\"{(v >= 0 ? y1 - 5 : y1 + 13):F1}\" text-anchor=\"middle\">{Enc(format(v))}</text>");
            }
            if (i % labelEvery == 0)
                sb.Append(Inv, $"<text class=\"axis\" x=\"{cx:F1}\" y=\"{height - 12}\" text-anchor=\"middle\">{Enc(label)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Keeps the min and max of each bucket so peaks and drawdowns survive downsampling.</summary>
    public static List<(long T, double V)> Decimate(IReadOnlyList<(long T, double V)> pts, int maxPoints)
    {
        if (pts.Count <= maxPoints) return pts.ToList();
        int buckets = Math.Max(1, maxPoints / 2);
        double size = pts.Count / (double)buckets;
        var outPts = new List<(long, double)>(maxPoints + 2) { pts[0] };
        for (int b = 0; b < buckets; b++)
        {
            int s = (int)(b * size), e = Math.Min(pts.Count, (int)((b + 1) * size));
            if (e <= s) continue;
            int iMin = s, iMax = s;
            for (int i = s; i < e; i++)
            {
                if (pts[i].V < pts[iMin].V) iMin = i;
                if (pts[i].V > pts[iMax].V) iMax = i;
            }
            if (iMin < iMax) { outPts.Add(pts[iMin]); outPts.Add(pts[iMax]); }
            else if (iMin > iMax) { outPts.Add(pts[iMax]); outPts.Add(pts[iMin]); }
            else outPts.Add(pts[iMin]);
        }
        outPts.Add(pts[^1]);
        return outPts;
    }

    public static double[] NiceTicks(double lo, double hi, int count)
    {
        double raw = (hi - lo) / Math.Max(1, count - 1);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= raw);
        double start = Math.Floor(lo / step) * step, end = Math.Ceiling(hi / step) * step;
        var ticks = new List<double>();
        for (double v = start; v <= end + step * 1e-9; v += step) ticks.Add(Math.Round(v, 10));
        return ticks.ToArray();
    }

    private static IEnumerable<(long T, string Label)> TimeTicks(long t0, long t1)
    {
        var a = TimeUtil.FromMs(t0);
        double days = (t1 - t0) / 86_400_000.0;
        (int months, string fmt) = days > 1100 ? (12, "yyyy") : days > 400 ? (6, "yyyy-MM") : days > 120 ? (1, "yyyy-MM") : (0, "MM-dd");
        if (months == 0)
        {
            var d = a.Date.AddDays(1);
            int stepDays = Math.Max(1, (int)Math.Ceiling(days / 8));
            for (; TimeUtil.ToMs(d) < t1; d = d.AddDays(stepDays)) yield return (TimeUtil.ToMs(d), d.ToString(fmt, Inv));
            yield break;
        }
        var m = new DateTime(a.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int every = months == 1 && days > 240 ? 2 : months;
        for (; TimeUtil.ToMs(m) < t1; m = m.AddMonths(every))
            if (TimeUtil.ToMs(m) > t0) yield return (TimeUtil.ToMs(m), m.ToString(fmt, Inv));
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
