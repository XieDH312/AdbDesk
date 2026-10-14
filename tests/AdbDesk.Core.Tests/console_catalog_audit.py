"""Audit the exact embedded preset data, without launching ADB or .NET executables."""
import json
import re
import unittest
from pathlib import Path

CATALOG = Path(__file__).resolve().parents[2] / "src/AdbDesk.Core/Data/console-presets.json"


class CatalogAudit(unittest.TestCase):
    def setUp(self):
        self.items = json.loads(CATALOG.read_text(encoding="utf-8")) if CATALOG.exists() else []

    def test_useful_scope_and_identity(self):
        self.assertGreaterEqual(len(self.items), 100, "expanded catalog is missing")
        self.assertEqual(len({p["Category"] for p in self.items}), 10)
        self.assertEqual(len({p["Id"] for p in self.items}), len(self.items))
        self.assertEqual(len({p["Command"] for p in self.items}), len(self.items), "no duplicate padding")

    def test_parameters_are_complete(self):
        kinds = {"text", "package", "component", "permission", "endpoint", "port", "pid", "user", "coordinate", "duration", "density", "size", "code", "url", "remote-path", "file", "folder", "apk"}
        for p in self.items:
            with self.subTest(preset=p["Id"]):
                self.assertTrue(p["Title"] and p["Description"])
                self.assertNotRegex(p["Command"], r"[\r\n\x00]")
                parameters = p.get("Parameters", [])
                self.assertEqual({int(n) for n in re.findall(r"\{(\d+)\}", p["Command"])}, set(range(len(parameters))))
                self.assertLessEqual(len(parameters), 4)
                self.assertTrue(all(x["Kind"] in kinds for x in parameters))
                self.assertFalse(re.search(r"[;&|`]", p["Command"]), "presets are single operations")

    def test_mutations_and_requirements_are_visible(self):
        by_id = {p["Id"]: p for p in self.items}
        for key in ("app.clear", "app.uninstall", "app.disable", "file.delete", "adb.kill", "system.reboot", "permission.grant"):
            self.assertTrue(by_id.get(key, {}).get("Confirmation"), key)
        self.assertIn("userdebug", by_id.get("adb.root", {}).get("Requirements", ""))
        self.assertIn("调试", by_id.get("file.private", {}).get("Requirements", ""))
        for p in self.items:
            self.assertTrue(p.get("Requirements"), p["Id"])
            self.assertTrue(p.get("Source", "").startswith("https://"), p["Id"])

    def test_private_data_and_proxy_queries(self):
        by_id = {p["Id"]: p for p in self.items}
        private = by_id.get("file.private", {})
        self.assertIn("--user {1}", private.get("Command", ""), "run-as must explicitly target the chosen user")
        self.assertEqual(private["Parameters"][1]["Kind"], "user")
        for p in self.items:
            if p["Id"].startswith("network.proxy"):
                self.assertTrue(p["Command"].startswith("shell settings get global "), "proxy library entries are read-only")


if __name__ == "__main__":
    unittest.main()
