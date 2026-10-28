import unittest
from itertools import combinations

import numpy as np
import pandas as pd

from validation import cpcv


class PurgedSplitterTests(unittest.TestCase):
    def test_groups_splits_and_paths_have_the_cpcv_counts(self):
        sp = cpcv.splits(1095, 6, 2, horizon=3, embargo=10)
        self.assertEqual(len(sp), 15)
        self.assertEqual(cpcv.path_assignment(6, 2).shape, (5, 6))
        sizes = [len(g) for g in cpcv.groups(1095, 6)]
        self.assertEqual(sizes, [183, 183, 183, 182, 182, 182])
        for g in range(6):
            self.assertEqual(sum(g in s.test_groups for s in sp), 5)   # C(5, 1)

    def test_train_never_overlaps_test_and_purge_and_embargo_remove_the_neighbours(self):
        n, h, e = 600, 4, 7
        g = cpcv.groups(n, 6)
        for s in cpcv.splits(n, 6, 2, h, e):
            self.assertEqual(len(np.intersect1d(s.train, s.test)), 0)
            for a, b in cpcv.contiguous_blocks(s.test):
                near = np.arange(max(0, a - h + 1), min(n, b + h))
                after = np.arange(b + 1, min(n, b + 1 + e))
                self.assertEqual(len(np.intersect1d(s.train, near)), 0, "purge window leaked into training")
                self.assertEqual(len(np.intersect1d(s.train, after)), 0, "embargo window leaked into training")
                if a - h >= 0 and not np.isin(a - h, s.test):
                    self.assertIn(a - h, s.train)   # just outside the purge: kept
            kept = set(range(n)) - set(s.test.tolist())
            self.assertTrue(set(s.train.tolist()) <= kept)
        self.assertEqual(sum(len(x) for x in g), n)

    def test_splits_match_purgedcv(self):
        from purgedcv import CombinatorialPurgedCV

        n = 400
        t = pd.Series(pd.date_range("2021-01-01", periods=n, freq="D"))
        for h, e in ((1, 0), (3, 0), (3, 5), (8, 2)):
            ref = CombinatorialPurgedCV(6, 2, prediction_times=t, evaluation_times=t + pd.Timedelta(days=h), embargo_observations=e)
            ours = cpcv.splits(n, 6, 2, h, e)
            for (train, test), s in zip(ref.split(np.zeros((n, 1))), ours):
                np.testing.assert_array_equal(np.sort(test), s.test)
                np.testing.assert_array_equal(np.sort(train), s.train, err_msg=f"h={h} e={e} groups={s.test_groups}")

    def test_paths_match_purgedcv_reconstruction(self):
        from purgedcv import reconstruct_paths

        n = 120
        sp = cpcv.splits(n, 6, 2, 1, 0)
        paths = reconstruct_paths([np.full(len(s.test), float(i)) for i, s in enumerate(sp)], [s.test for s in sp], 6, 2, n)
        g = cpcv.groups(n, 6)
        for p, row in enumerate(cpcv.path_assignment(6, 2)):
            for grp, split in enumerate(row):
                self.assertTrue(np.all(paths[p, g[grp]] == split))

    def test_run_selects_the_best_training_configuration_per_split(self):
        rng = np.random.default_rng(5)
        n = 600
        days = np.arange(n) * 86_400_000
        good = rng.normal(0.003, 0.01, n)
        daily = pd.DataFrame({"day_ms": days, "c000": good, "c001": rng.normal(0, 0.01, n), "c002": rng.normal(-0.001, 0.01, n)})
        trades = pd.DataFrame({"config": ["c000"] * 4 + ["c001"] * 2, "entry_ms": days[[5, 150, 300, 500, 10, 20]],
                               "exit_ms": days[[6, 151, 301, 501, 11, 21]], "net_pnl": [10.0, -5.0, 7.0, 3.0, 1.0, -1.0]})
        r = cpcv.run(daily, trades, 6, 2, horizon=2, embargo=6)
        self.assertEqual(r["candidateSelectedInSplits"], 15)
        for p in r["paths"]:
            self.assertEqual(set(p["configsUsed"]), {"c000"})
            self.assertAlmostEqual(p["profitFactor"], 20.0 / 5.0)
        self.assertGreater(r["pathSharpe"]["min"], 0)


if __name__ == "__main__":
    unittest.main()
