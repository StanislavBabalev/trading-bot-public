import math
import unittest

import numpy as np

from validation import stats
from validation.pbo import cscv


class StatisticsTests(unittest.TestCase):
    def test_psr_dsr_and_mintrl_match_holdout(self):
        from holdout.deflated import deflated_sharpe_ratio, minimum_track_record_length, probabilistic_sharpe_ratio

        rng = np.random.default_rng(3)
        for _ in range(50):
            sr, n = rng.uniform(0.01, 0.15), int(rng.integers(100, 3000))
            skew, kurt = rng.uniform(-1, 2), rng.uniform(3, 20)
            trials, var = int(rng.integers(2, 400)), rng.uniform(1e-5, 1e-3)
            self.assertAlmostEqual(stats.psr(sr, n, skew, kurt), probabilistic_sharpe_ratio(sr, n, skewness=skew, kurtosis=kurt), places=12)
            self.assertAlmostEqual(stats.deflated_sharpe(sr, n, skew, kurt, trials, var),
                                   deflated_sharpe_ratio(sr, n, n_trials=trials, trials_variance=var, skewness=skew, kurtosis=kurt), places=12)
            self.assertAlmostEqual(stats.min_track_record_length(sr, skew, kurt),
                                   minimum_track_record_length(sr, skewness=skew, kurtosis=kurt), places=6)

    def test_min_track_record_length_inverts_psr(self):
        sr, skew, kurt = 0.05, 0.4, 9.0
        n = stats.min_track_record_length(sr, skew, kurt, confidence=0.95)
        denom = 1 - skew * sr + (kurt - 1) / 4 * sr * sr
        from scipy.stats import norm
        self.assertAlmostEqual(norm.cdf(sr * math.sqrt(n - 1) / math.sqrt(denom)), 0.95, places=12)
        self.assertEqual(stats.min_track_record_length(-0.01, 0, 3), math.inf)

    def test_min_backtest_length_reproduces_the_paper_example(self):
        # Bailey et al. (2014): with 45 independent trials, about 5 years are needed before an annualized IS Sharpe of 1
        # stops being what luck alone delivers.
        self.assertAlmostEqual(stats.min_backtest_length_years(45, 1.0), 4.98, delta=0.05)
        self.assertEqual(stats.min_backtest_length_years(1, 1.0), 0.0)
        self.assertEqual(stats.min_backtest_length_years(10, 0.0), math.inf)

    def test_expected_maximum_grows_with_trials_and_matches_the_engine_constant(self):
        values = [stats.expected_max_z(n) for n in (1, 2, 10, 100, 1000)]
        self.assertEqual(values[0], 0.0)
        self.assertTrue(all(a < b for a, b in zip(values, values[1:])))
        # The hunt's threshold: 309 trials with an annualized cross-trial Sharpe SD of 0.3737 give 1.086.
        self.assertAlmostEqual(stats.expected_max_sharpe(309, (0.37372416769079375 ** 2) / 365) * math.sqrt(365), 1.086, places=3)

    def test_moments_follow_the_engine_conventions(self):
        x = np.array([0.01, -0.02, 0.03, 0.0, 0.05, -0.01])
        skew, kurt = stats.moments(x)
        d = x - x.mean()
        self.assertAlmostEqual(skew, np.mean(d ** 3) / np.mean(d ** 2) ** 1.5, places=14)
        self.assertAlmostEqual(kurt, np.mean(d ** 4) / np.mean(d ** 2) ** 2, places=14)
        self.assertEqual(stats.sharpe(np.zeros(10)), 0.0)


class PboTests(unittest.TestCase):
    def test_cscv_matches_holdout(self):
        from holdout.pbo import probability_of_backtest_overfitting

        for seed in range(4):
            x = np.random.default_rng(seed).normal(0.0002, 0.01, size=(640, 12))
            self.assertEqual(cscv(x, 16)["pbo"], probability_of_backtest_overfitting(x, n_blocks=16).pbo)

    def test_pure_noise_overfits_about_half_the_time_and_a_real_edge_does_not(self):
        rng = np.random.default_rng(11)
        noise = rng.normal(0, 0.01, size=(1600, 30))
        self.assertTrue(0.3 <= cscv(noise, 16)["pbo"] <= 0.7)
        edge = noise.copy()
        edge[:, 0] += 0.004
        result = cscv(edge, 16)
        self.assertLess(result["pbo"], 0.05)
        self.assertLess(result["probabilityOosLoss"], 0.05)

    def test_flat_configuration_scores_zero_instead_of_failing(self):
        x = np.random.default_rng(2).normal(0, 0.01, size=(320, 5))
        x[:200, 3] = 0.0
        self.assertTrue(0.0 <= cscv(x, 16)["pbo"] <= 1.0)


if __name__ == "__main__":
    unittest.main()
