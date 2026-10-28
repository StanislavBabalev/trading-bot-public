namespace Backtester.Core.Analytics;

public sealed record Fold(int Index, DateTime TrainFrom, DateTime TrainTo, DateTime TestFrom, DateTime TestTo);

public static class WalkForward
{
    /// <summary>
    /// Rolling folds: train on <paramref name="trainMonths"/>, test on the next <paramref name="testMonths"/>,
    /// then slide forward by <paramref name="stepMonths"/> (default: the test length). Every fold fits inside [from, to).
    /// </summary>
    public static IReadOnlyList<Fold> Generate(DateTime from, DateTime to, int trainMonths, int testMonths, int? stepMonths = null)
    {
        if (trainMonths < 1 || testMonths < 1) throw new ArgumentException("Train and test lengths must be at least one month.");
        int step = stepMonths ?? testMonths;
        if (step < 1) throw new ArgumentException("Step must be at least one month.");

        var folds = new List<Fold>();
        for (var trainFrom = from; ; trainFrom = trainFrom.AddMonths(step))
        {
            var trainTo = trainFrom.AddMonths(trainMonths);
            var testTo = trainTo.AddMonths(testMonths);
            if (testTo > to) break;
            folds.Add(new Fold(folds.Count + 1, trainFrom, trainTo, trainTo, testTo));
        }
        return folds;
    }
}
