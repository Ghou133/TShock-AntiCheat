#!/usr/bin/env python3
"""Validate public navigation, generated capability tables and project boundaries.

Uses the standard library only. It checks declared structure, not gameplay
correctness, production qualification, native compatibility or absence of secrets.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from urllib.parse import unquote, urlsplit

PUBLIC_DOCS = (
    "README.md", "START_HERE.md", "AGENTS.md", "CONTRIBUTING.md",
    "SECURITY.md", "CHANGELOG.md", "THIRD_PARTY_NOTICES.md",
    "docs/ARCHITECTURE.md", "docs/BUILDING.md", "docs/CONFIGURATION.md",
    "docs/STATUS.md", "docs/ROADMAP.md", "docs/DEVELOPMENT.md",
    "docs/REVIEW-2026-09.md",
)
DEPENDENCIES = {
    "AntiCheat.Core": set(),
    "AntiCheat.Progression": set(),
    "AntiCheat.Persistence": {"AntiCheat.Core"},
    "AntiCheat.Rules": {"AntiCheat.Core", "AntiCheat.Progression"},
    "AntiCheat.Plugin.TShock": {
        "AntiCheat.Core", "AntiCheat.Rules", "AntiCheat.Persistence"
    },
}
STATES = {"已实现", "部分实现", "仅观察", "实验性", "待实现", "冻结", "退出"}


def safe_path(root: Path, value: str) -> Path:
    """Resolve a repository-relative path, rejecting escapes and absolute paths."""
    path = PurePosixPath(value.replace("\\", "/"))
    if not value or path.is_absolute() or ".." in path.parts or ":" in value:
        raise ValueError(f"Not a repository-relative path: {value!r}")
    resolved = root.joinpath(*path.parts).resolve()
    if not resolved.is_relative_to(root.resolve()):
        raise ValueError(f"Path escapes repository: {value!r}")
    return resolved


def load_capabilities(root: Path) -> list[dict[str, str]]:
    data = json.loads((root / "docs/capabilities.json").read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1 or not re.fullmatch(
        r"[0-9a-f]{40}", str(data.get("reviewedSource", ""))
    ):
        raise ValueError("Capability schema/versioned source is missing or invalid")
    entries = data.get("capabilities")
    if not isinstance(entries, list) or not entries:
        raise ValueError("Capability list is empty or invalid")
    roadmap = (root / "docs/ROADMAP.md").read_text(encoding="utf-8")
    tasks = set(re.findall(r"\bR[0-9]+-[0-9]+\b", roadmap))
    seen: set[str] = set()
    fields = ("id", "name", "implementation", "boundary", "source", "tests", "evidence", "next")
    for entry in entries:
        if not isinstance(entry, dict) or any(
            not isinstance(entry.get(key), str) or not entry[key].strip()
            or any(char in entry[key] for char in "\r\n|") for key in fields
        ):
            raise ValueError("Each capability needs nonempty, single-line table-safe fields")
        if not re.fullmatch(r"[a-z][a-z0-9-]*", entry["id"]) or entry["id"] in seen:
            raise ValueError(f"Invalid/duplicate capability ID: {entry['id']}")
        seen.add(entry["id"])
        if entry["implementation"] not in STATES or entry["next"] not in tasks:
            raise ValueError(f"Invalid state or missing roadmap task: {entry['id']}")
        for key in ("source", "tests"):
            if not safe_path(root, entry[key]).is_file():
                raise ValueError(f"Missing {key} path for {entry['id']}: {entry[key]}")
    return entries


def capability_table(entries: list[dict[str, str]]) -> str:
    lines = ["| 模块 | 实现状态 | 当前边界 |", "| --- | --- | --- |"]
    lines += [f"| {e['name']} | {e['implementation']} | {e['boundary']} |" for e in entries]
    return "\n".join(lines)


def evidence_table(entries: list[dict[str, str]]) -> str:
    lines = ["| 模块 | 入口 / 测试 | 验证边界 | 下一任务 |", "| --- | --- | --- | --- |"]
    lines += [f"| {e['name']} | [源码](../{e['source']}) / [测试](../{e['tests']}) | "
              f"{e['evidence']} | {e['next']} |" for e in entries]
    return "\n".join(lines)


def replace_block(text: str, name: str, table: str) -> str:
    start, end = f"<!-- {name}:start -->", f"<!-- {name}:end -->"
    if text.count(start) != 1 or text.count(end) != 1 or text.index(start) >= text.index(end):
        raise ValueError(f"Expected one ordered {name} marker pair")
    before, tail = text.split(start, 1)
    _, after = tail.split(end, 1)
    return before + start + "\n" + table + "\n" + end + after


def check_documents(root: Path) -> None:
    for name in PUBLIC_DOCS:
        path = root / name
        text = path.read_text(encoding="utf-8")
        for example in re.findall(r"```json\s*\n(.*?)\n```", text, re.S):
            json.loads(example)
        prose = re.sub(r"```.*?```", "", text, flags=re.S)
        for target in re.findall(r"(?<!!)\[[^\]]*\]\(([^\s)]+)\)", prose):
            url = urlsplit(target)
            if url.scheme or url.netloc or not url.path:
                continue
            resolved = (path.parent / unquote(url.path)).resolve()
            if not resolved.is_relative_to(root.resolve()) or not resolved.exists():
                raise ValueError(f"Broken public link in {name}: {target}")


def check_projects(root: Path) -> None:
    for name, allowed in DEPENDENCIES.items():
        project = root / "src" / name / f"{name}.csproj"
        tree = ET.parse(project)
        references: set[str] = set()
        for item in tree.findall(".//ProjectReference"):
            value = item.attrib["Include"].replace("\\", "/")
            dependency = (project.parent / value).resolve()
            if not dependency.is_relative_to((root / "src").resolve()) or not dependency.is_file():
                raise ValueError(f"Invalid source dependency: {name} -> {value}")
            references.add(dependency.stem)
        if references != allowed:
            raise ValueError(f"Review architecture change: {name} references {sorted(references)}, "
                             f"expected {sorted(allowed)}")
    filtered = json.loads((root / "AntiCheat.Public.slnf").read_text(encoding="utf-8"))["solution"]
    solution = safe_path(root, filtered["path"]).read_text(encoding="utf-8-sig")
    declared = {v.replace("\\", "/") for v in re.findall(r'"([^"\n]+\.csproj)"', solution)}
    for value in filtered["projects"]:
        if not safe_path(root, value).is_file() or value.replace("\\", "/") not in declared:
            raise ValueError(f"Public solution project missing from source/solution: {value}")


def run(root: Path, write: bool = False) -> None:
    entries = load_capabilities(root)
    tables = capability_table(entries), evidence_table(entries)
    for name in ("README.md", "docs/STATUS.md"):
        path = root / name
        original = path.read_text(encoding="utf-8")
        updated = replace_block(original, "capabilities", tables[0])
        if name == "docs/STATUS.md":
            updated = replace_block(updated, "evidence", tables[1])
        if updated != original:
            if not write:
                raise ValueError(f"Stale generated table: {name}; run with --write")
            path.write_text(updated, encoding="utf-8", newline="\n")
    check_documents(root)
    check_projects(root)
    print(f"Public repository checks passed: {len(entries)} capabilities, {len(PUBLIC_DOCS)} guides.")
    sizes = sorted(((len(p.read_text(encoding="utf-8-sig").splitlines()), p)
                    for p in (root / "src").rglob("*.cs")
                    if not {"bin", "obj"}.intersection(p.relative_to(root).parts)), reverse=True)
    print(f"Source inventory: {len(sizes)} C# files, {sum(n for n, _ in sizes)} lines (including blanks/comments).")
    print("Largest source files (maintenance indicators, not security findings):")
    for count, path in sizes[:5]:
        print(f"  {count:5d}  {path.relative_to(root).as_posix()}")
    print("Historical reports, gameplay semantics and native runtime qualification are not validated here.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="Regenerate public capability tables")
    args = parser.parse_args()
    try:
        run(Path(__file__).resolve().parents[2], args.write)
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError) as error:
        print(f"Repository check failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
