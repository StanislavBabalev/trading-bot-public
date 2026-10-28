import json
import re
import shutil
import socket
import tempfile
import unittest
from pathlib import Path

import pandas as pd

from validation.config import REPO_ROOT, dev_window_ms, load_config, strip_json_comments
from validation.data import HoldoutLocked, assert_dev_only, load_bundle, load_dev_bars
from validation.fingerprint import validator_fingerprint
from validation.netguard import NetworkBlocked, block_network

CFG = load_config()
DEV_FROM, DEV_TO = dev_window_ms(CFG)


class HoldoutLockTests(unittest.TestCase):
    def test_timestamps_after_dev_are_refused(self):
        assert_dev_only(CFG, [DEV_FROM, DEV_TO - 1], "opens", inclusive_end=False)
        assert_dev_only(CFG, [DEV_TO], "closes")   # the last dev bar closes at devTo
        with self.assertRaises(HoldoutLocked):
            assert_dev_only(CFG, [DEV_TO], "opens", inclusive_end=False)
        with self.assertRaises(HoldoutLocked):
            assert_dev_only(CFG, [DEV_TO + 1], "closes")

    @unittest.skipUnless((REPO_ROOT / "data" / "bybit" / "BTCUSDT_60.csv").exists(), "research data not present")
    def test_dev_bars_end_before_the_holdout(self):
        bars = load_dev_bars(CFG)
        self.assertLess(bars["open_time_ms"].max(), DEV_TO)
        self.assertEqual(bars["open_time_ms"].max(), DEV_TO - 3_600_000)

    def test_a_bundle_reaching_into_the_holdout_is_refused(self):
        src = next((p.parent for p in (REPO_ROOT / "artifacts" / "validation").glob("*/*/meta.json")), None)
        if src is None or not (src / "equity.csv").exists():
            self.skipTest("no exported bundle")
        tmp = Path(tempfile.mkdtemp())
        try:
            dst = tmp / "bundle"
            shutil.copytree(src, dst)
            load_bundle(CFG, dst)   # as exported: fine
            eq = pd.read_csv(dst / "equity.csv")
            eq.loc[len(eq)] = [DEV_TO + 3_600_000, eq["equity"].iloc[-1], 0.0]
            eq.to_csv(dst / "equity.csv", index=False)
            with self.assertRaises(HoldoutLocked):
                load_bundle(CFG, dst)
        finally:
            shutil.rmtree(tmp)

    def test_config_comments_are_stripped_outside_strings(self):
        text = '{ // note\n "a": "x // not a comment", /* block */ "b": 1 }'
        self.assertEqual(json.loads(strip_json_comments(text)), {"a": "x // not a comment", "b": 1})


class NetworkTests(unittest.TestCase):
    def test_network_is_blocked(self):
        block_network()
        with self.assertRaises(NetworkBlocked):
            socket.create_connection(("127.0.0.1", 9))
        with self.assertRaises(NetworkBlocked):
            socket.getaddrinfo("example.org", 443)
        with self.assertRaises(NetworkBlocked):
            socket.socket().connect(("127.0.0.1", 9))

    def test_no_network_code_in_the_validator(self):
        # Patterns are assembled so this file does not match itself.
        forbidden = re.compile("|".join([
            r"^\s*(import|from)\s+(" + "|".join(["urllib", "http", "requests", "aiohttp", "httpx", "websocket", "ftplib", "smtplib"]) + r")\b",
            "http" + "s?://",
            r"socket\." + "(socket|create_connection)\\(",
        ]), re.M)
        offenders = []
        for p in (REPO_ROOT / "validation").rglob("*.py"):
            text = p.read_text(encoding="utf-8")
            if p.name == "netguard.py":
                text = text.replace("socket.socket.connect", "").replace("socket.create_connection = ", "")
            if p == Path(__file__).resolve():
                text = text[: text.index("class NetworkTests")]   # the guard test above opens sockets on purpose
            for m in forbidden.finditer(text):
                offenders.append(f"{p.relative_to(REPO_ROOT)}: {m.group(0).strip()}")
        self.assertEqual(offenders, [])


class FingerprintTests(unittest.TestCase):
    def test_fingerprint_of_a_fixed_tree_matches_the_csharp_vector(self):
        # Same tree and value as ValidationTests.Validator_fingerprint_matches_the_python_vector in the C# tests.
        tmp = Path(tempfile.mkdtemp())
        try:
            (tmp / "validation" / "sub").mkdir(parents=True)
            (tmp / "validation" / "tests").mkdir()
            (tmp / "validation" / "__pycache__").mkdir()
            (tmp / "validation" / "a.py").write_bytes(b"x = 1\n")
            (tmp / "validation" / "sub" / "b.py").write_bytes(b"y = 2\n")
            (tmp / "validation" / "tests" / "t.py").write_bytes(b"ignored\n")
            (tmp / "validation" / "__pycache__" / "c.py").write_bytes(b"ignored\n")
            self.assertEqual(validator_fingerprint(tmp), "c82e5801b91a26aa07003dcef2a8da4ac1fb3645af7787f85b8e8d9ad30cdb96")
            (tmp / "validation" / "a.py").write_bytes(b"x = 1\r\n")   # a CRLF checkout hashes the same
            self.assertEqual(validator_fingerprint(tmp), "c82e5801b91a26aa07003dcef2a8da4ac1fb3645af7787f85b8e8d9ad30cdb96")
            (tmp / "validation" / "a.py").write_bytes(b"x = 2\n")
            self.assertNotEqual(validator_fingerprint(tmp), "c82e5801b91a26aa07003dcef2a8da4ac1fb3645af7787f85b8e8d9ad30cdb96")
        finally:
            shutil.rmtree(tmp)


if __name__ == "__main__":
    unittest.main()
