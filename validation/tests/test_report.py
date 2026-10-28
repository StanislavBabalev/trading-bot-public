import copy
import json
import math
import unittest

import jsonschema

from validation.config import REPO_ROOT, load_config, output_dir
from validation.gates import REQUIRED_GATES
from validation.report import SCHEMA_PATH, sanitize, validate_schema

CFG = load_config()


class ReportTests(unittest.TestCase):
    def test_sanitize_writes_strict_json(self):
        out = sanitize({"a": math.nan, "b": math.inf, "c": [-math.inf, 1.5]})
        self.assertEqual(out, {"a": None, "b": "Infinity", "c": ["-Infinity", 1.5]})
        json.dumps(out, allow_nan=False)

    def test_schema_is_valid(self):
        jsonschema.Draft202012Validator.check_schema(json.loads(SCHEMA_PATH.read_text(encoding="utf-8")))

    def reports(self):
        return sorted(output_dir(CFG).glob("*/*/validation_report.json"))

    def test_written_reports_follow_the_schema_and_hold_every_gate(self):
        paths = self.reports()
        if not paths:
            self.skipTest("no validation reports; run python -m validation run")
        for p in paths:
            r = json.loads(p.read_text(encoding="utf-8"))
            validate_schema(r)
            self.assertEqual([g["name"] for g in r["gates"]], list(REQUIRED_GATES))
            self.assertEqual(r["allHardGatesPassed"], all(g["passed"] for g in r["gates"] if g["hard"]))
            self.assertEqual(r["verdict"], "PASS" if r["allHardGatesPassed"] else "REJECT")

    def test_schema_refuses_a_report_without_a_gate(self):
        paths = self.reports()
        if not paths:
            self.skipTest("no validation reports; run python -m validation run")
        r = json.loads(paths[0].read_text(encoding="utf-8"))
        broken = copy.deepcopy(r)
        broken["gates"] = [g for g in broken["gates"] if g["name"] != "pbo"]
        with self.assertRaises(jsonschema.ValidationError):
            validate_schema(broken)


if __name__ == "__main__":
    unittest.main()
