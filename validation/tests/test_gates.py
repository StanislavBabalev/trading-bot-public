import copy
import re
import unittest

from validation.config import REPO_ROOT
from validation.gates import REQUIRED_GATES, evaluate, summarize

THRESHOLDS = {
    "minDeflatedSharpe": 0.95, "maxPbo": 0.20, "minPlateauScore": 0.60, "plateauMinProfitFactor": 1.05, "plateauMinSharpe": 0.30,
    "regimeMinTrades": 20, "maxLosingRegimes": 1, "regimeConcentrationMaxTradeShare": 0.30, "bootstrapMinSharpeP05": 0.0,
    "bootstrapMinProfitFactorP05": 1.0, "stressMultiplier": 2, "minTrackRecordConfidence": 0.95, "minTradesPerYear": 50,
}

PASSING = {
    "lookAheadAudit": {"verdict": "pass", "comparisons": 8, "tradesBeforeCut": 400, "problems": []},
    "multipleTesting": {"dsrGate": 0.97, "pboSearch": {"pbo": 0.10}},
    "plateau": {"score": 0.8, "onEdgeOf": []},
    "regimes": {"gates": {"losingRegimes": ["bear"], "lossesPassed": True, "concentratedIn": [], "concentrationPassed": True}},
    "bootstrap": {"worstSharpeP05": 0.2, "worstProfitFactorP05": 1.1},
    "stress": {"slippageAtMultiplier": {"netProfitPct": 5.0}, "commissionAtMultiplier": {"netProfitPct": 3.0}},
    "trackRecord": {"devDays": 1095, "minTrackRecordDays": 700},
    "baseline": {"tradesPerYear": 80},
}


def failing(path, value):
    r = copy.deepcopy(PASSING)
    node = r
    for key in path[:-1]:
        node = node[key]
    node[path[-1]] = value
    return r


class GateTests(unittest.TestCase):
    def test_all_pass_and_names_are_the_required_ones(self):
        s = summarize(evaluate(PASSING, THRESHOLDS))
        self.assertEqual([g["name"] for g in s["gates"]], list(REQUIRED_GATES))
        self.assertTrue(s["allHardGatesPassed"])
        self.assertEqual(s["verdict"], "PASS")

    def test_each_hard_gate_rejects_on_its_own(self):
        cases = {
            "lookahead_audit": (("lookAheadAudit", "verdict"), "inconclusive"),
            "deflated_sharpe": (("multipleTesting", "dsrGate"), 0.9499),
            "pbo": (("multipleTesting", "pboSearch", "pbo"), 0.2001),
            "plateau_score": (("plateau", "score"), 0.59),
            "plateau_edge": (("plateau", "onEdgeOf"), ["holdBars"]),
            "regime_losses": (("regimes", "gates", "lossesPassed"), False),
            "regime_concentration": (("regimes", "gates", "concentrationPassed"), False),
            "bootstrap_sharpe_p05": (("bootstrap", "worstSharpeP05"), -0.01),
            "bootstrap_pf_p05": (("bootstrap", "worstProfitFactorP05"), 0.99),
            "stress_slippage_2x": (("stress", "slippageAtMultiplier", "netProfitPct"), 0.0),
            "stress_commission_2x": (("stress", "commissionAtMultiplier", "netProfitPct"), -1.0),
            "min_track_record": (("trackRecord", "minTrackRecordDays"), 1096),
            "trades_per_year": (("baseline", "tradesPerYear"), 49.9),
        }
        self.assertEqual(set(cases), set(REQUIRED_GATES))
        for name, (path, value) in cases.items():
            s = summarize(evaluate(failing(path, value), THRESHOLDS))
            self.assertEqual(s["failedHardGates"], [name], name)
            self.assertEqual(s["verdict"], "REJECT")

    def test_thresholds_are_inclusive_where_the_rules_say_so(self):
        at = failing(("multipleTesting", "dsrGate"), 0.95)
        at["multipleTesting"]["pboSearch"]["pbo"] = 0.20
        at["plateau"]["score"] = 0.60
        at["bootstrap"]["worstProfitFactorP05"] = 1.0
        at["trackRecord"]["minTrackRecordDays"] = 1095
        self.assertTrue(summarize(evaluate(at, THRESHOLDS))["allHardGatesPassed"])

    def test_missing_values_fail_rather_than_pass(self):
        s = summarize(evaluate(failing(("multipleTesting", "dsrGate"), None), THRESHOLDS))
        self.assertEqual(s["failedHardGates"], ["deflated_sharpe"])

    def test_required_gates_match_the_csharp_holdout_gate(self):
        src = (REPO_ROOT / "src" / "Backtester.Core" / "Validation" / "HoldoutGate.cs").read_text(encoding="utf-8")
        block = re.search(r"RequiredGates\s*=\s*\[(.*?)\];", src, re.S).group(1)
        self.assertEqual(tuple(re.findall(r'"([a-z0-9_]+)"', block)), REQUIRED_GATES)


if __name__ == "__main__":
    unittest.main()
