import unittest

import numpy as np
import pandas as pd

from validation import bootstrap, regimes

DAY = 86_400_000


class BootstrapTests(unittest.TestCase):
    def test_indices_are_valid_with_the_requested_mean_block_length(self):
        rng = np.random.default_rng(1)
        idx = bootstrap.stationary_indices(5000, 48, 40, rng)
        self.assertEqual(idx.shape, (40, 5000))
        self.assertTrue(idx.min() >= 0 and idx.max() < 5000)
        breaks = np.sum(np.diff(idx, axis=1) % 5000 != 1)
        self.assertAlmostEqual(idx.size / (breaks + idx.shape[0]), 48, delta=48 * 0.1)

    def test_same_seed_same_result(self):
        x = np.random.default_rng(2).normal(0.0002, 0.004, 24 * 200)
        t = np.random.default_rng(3).normal(0.001, 0.01, 80)
        a = bootstrap.run(x, t, [24], 300, seed=9)
        b = bootstrap.run(x, t, [24], 300, seed=9)
        self.assertEqual(a, b)

    def test_percentiles_separate_an_edge_from_noise(self):
        rng = np.random.default_rng(4)
        edge = bootstrap.run(rng.normal(0.0004, 0.003, 24 * 400), rng.normal(0.004, 0.01, 200), [48], 500, seed=1)
        self.assertGreater(edge["worstSharpeP05"], 0)
        self.assertGreater(edge["worstProfitFactorP05"], 1)
        noise = bootstrap.run(rng.normal(0, 0.003, 24 * 400), rng.normal(0, 0.01, 200), [48], 500, seed=1)
        q = noise["byMeanBlockLength"][0]["sharpe"]
        self.assertLess(q["p05"], 0)
        self.assertGreater(q["p95"], 0)

    def test_bars_of_other_intervals_compound_into_days(self):
        # 4h returns: the median resampled Sharpe matches the series' own annualized daily Sharpe only when 6 bars make a
        # day; grouping 24 of them would measure 4-day returns and overstate it about twofold.
        x = np.random.default_rng(5).normal(0.0004, 0.006, 6 * 1000)
        daily = np.prod(1 + x.reshape(-1, 6), axis=1) - 1
        actual = daily.mean() / daily.std(ddof=1) * np.sqrt(365)
        out = bootstrap.run(x, np.array([0.01, -0.005]), [24], 400, seed=3, interval_minutes=240)
        self.assertAlmostEqual(out["byMeanBlockLength"][0]["sharpe"]["p50"], actual, delta=0.15 * actual)
        self.assertEqual(out["byMeanBlockLength"][0]["meanBlockHours"], 24)


class RegimeTests(unittest.TestCase):
    SETTINGS = {"smaDays": 200, "slopeDays": 20, "volDays": 30, "erDays": 30, "lookbackDays": 365, "minHistoryDays": 90, "volQuantile": 0.75}

    def closes(self, n=900, seed=0):
        r = np.random.default_rng(seed).normal(0.0005, 0.03, n)
        return pd.Series(20000 * np.exp(np.cumsum(r)), index=np.arange(n) * DAY)

    def test_labels_use_only_earlier_closes(self):
        base = self.closes()
        k = 700
        changed = base.copy()
        changed.iloc[k:] = changed.iloc[k:] * np.linspace(0.5, 2.0, len(base) - k)
        a = regimes.label_days(base, self.SETTINGS)
        b = regimes.label_days(changed, self.SETTINGS)
        cols = ["trend", "volatility", "efficiency"]
        pd.testing.assert_frame_equal(a[cols].iloc[: k + 1], b[cols].iloc[: k + 1])   # day k's label uses closes up to k - 1
        self.assertFalse(a[cols].iloc[k + 1:].equals(b[cols].iloc[k + 1:]))

    def test_daily_closes_skip_incomplete_days(self):
        t = np.arange(20, 24 * 3) * 3_600_000
        bars = pd.DataFrame({"open_time_ms": t, "close": np.arange(len(t), dtype=float)})
        c = regimes.daily_closes(bars)
        self.assertEqual(list(c.index), [DAY, 2 * DAY])
        self.assertEqual(c.iloc[0], 4 + 23)

    def test_gates_count_losing_regimes_and_detect_concentration(self):
        def row(partition, regime, trades, pnl, share):
            return {"partition": partition, "regime": regime, "trades": trades, "netPnl": pnl, "tradeShare": share}

        rows = [row("trend", "bull", 80, 300, 0.8), row("trend", "bear", 20, -50, 0.2),
                row("volatility", "high-vol", 25, -10, 0.25), row("volatility", "low-vol", 75, 260, 0.75),
                row("efficiency", "trending", 50, 120, 0.5), row("efficiency", "ranging", 50, 130, 0.5)]
        g = regimes.gates(rows, min_trades=20, max_losing=1, max_trade_share=0.3)
        self.assertEqual(g["losingRegimes"], ["bear", "high-vol"])
        self.assertFalse(g["lossesPassed"])
        self.assertTrue(g["concentrationPassed"])   # bull earns everything but holds 80% of trades
        rows[0]["tradeShare"], rows[1]["tradeShare"] = 0.2, 0.8
        rows[1]["trades"] = 19                       # below the trade floor: no longer counts as losing
        g = regimes.gates(rows, min_trades=20, max_losing=1, max_trade_share=0.3)
        self.assertEqual(g["concentratedIn"], ["bull"])
        self.assertTrue(g["lossesPassed"])


if __name__ == "__main__":
    unittest.main()
