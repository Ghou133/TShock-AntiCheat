from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from check_repository import capability_table, evidence_table, load_capabilities, replace_block, safe_path


class RepositoryCheckTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "docs").mkdir()
        (self.root / "docs/ROADMAP.md").write_text("R3-01", encoding="utf-8")
        for name in ("source.cs", "tests.cs"):
            (self.root / name).write_text("// fixture", encoding="utf-8")
        self.entry = dict(id="test-feature", name="测试", implementation="仅观察", boundary="不处罚",
                          source="source.cs", tests="tests.cs", evidence="测试源码", next="R3-01")

    def write_data(self, entries: list[dict[str, str]]) -> None:
        (self.root / "docs/capabilities.json").write_text(json.dumps(
            dict(schemaVersion=1, reviewedSource="a" * 40, capabilities=entries)), encoding="utf-8")

    def test_valid_registry(self) -> None:
        self.write_data([self.entry])
        self.assertEqual(load_capabilities(self.root), [self.entry])

    def test_duplicate_id_rejected(self) -> None:
        self.write_data([self.entry, self.entry])
        with self.assertRaises(ValueError):
            load_capabilities(self.root)

    def test_missing_source_rejected(self) -> None:
        self.write_data([dict(self.entry, source="missing.cs")])
        with self.assertRaises(ValueError):
            load_capabilities(self.root)

    def test_invalid_status_task_and_table_content(self) -> None:
        for key, value in (("implementation", "全部安全"), ("next", "R9-99"), ("name", "a|b")):
            with self.subTest(key=key):
                self.write_data([dict(self.entry, **{key: value})])
                with self.assertRaises(ValueError):
                    load_capabilities(self.root)

    def test_path_escape_rejected(self) -> None:
        for value in ("../outside", "/absolute", "C:\\absolute", "src/../../outside", ""):
            with self.subTest(value=value), self.assertRaises(ValueError):
                safe_path(self.root, value)

    def test_relative_path_allowed(self) -> None:
        self.assertEqual(safe_path(self.root, "docs/ROADMAP.md"), self.root / "docs/ROADMAP.md")

    def test_generated_block_replacement_is_idempotent(self) -> None:
        original = "before\n<!-- capabilities:start -->\nstale\n<!-- capabilities:end -->\nafter\n"
        table = capability_table([self.entry])
        updated = replace_block(original, "capabilities", table)
        self.assertEqual(replace_block(updated, "capabilities", table), updated)
        self.assertTrue(updated.startswith("before\n"))
        self.assertTrue(updated.endswith("\nafter\n"))
        self.assertNotIn("stale", updated)

    def test_missing_duplicate_or_reversed_markers_fail(self) -> None:
        for text in ("none", "<!-- x:end --><!-- x:start -->",
                     "<!-- x:start --><!-- x:start --><!-- x:end -->"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                replace_block(text, "x", "table")

    def test_evidence_contains_traceable_paths_and_task(self) -> None:
        table = evidence_table([self.entry])
        self.assertIn("[源码](../source.cs)", table)
        self.assertIn("[测试](../tests.cs)", table)
        self.assertIn("R3-01", table)


if __name__ == "__main__":
    unittest.main()
