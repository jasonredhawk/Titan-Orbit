"""Strip agent debug regions and helper calls from C# sources. Run from repo root: python tools/_strip_debug_instrumentation.py"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

CALL_PATTERNS = [
    re.compile(
        r"^[ \t]*(?:TitanOrbit\.Diagnostics\.)?F38c7dDebugLog\.Write\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*DebugNdjson065367\.Write\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*(?:TitanOrbit\.Core\.)?DebugSessionLog\.Write\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*Be1131SessionLog\.Write\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*NetworkGameManager\.DebugSessionE2a466Log\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*NetworkGameManager\.DebugSessionE695ffLog\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*DebugSessionE2a466Log\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*DebugSession65b1a1Log\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*DebugSessionE695ffLog\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*AgentDebugLog\([^;]*\);\s*\n",
        re.MULTILINE,
    ),
]

MULTI = [
    re.compile(
        r"^[ \t]*(?:TitanOrbit\.Diagnostics\.)?F38c7dDebugLog\.Write\(\s*\n(?:[^;]|\n)*?\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*Be1131SessionLog\.Write\(\s*\n(?:[^;]|\n)*?\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*(?:TitanOrbit\.Core\.)?DebugSessionLog\.Write\(\s*\n(?:[^;]|\n)*?\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*AgentDebugLog\(\s*\n(?:[^;]|\n)*?\);\s*\n",
        re.MULTILINE,
    ),
    re.compile(
        r"^[ \t]*NetworkGameManager\.DebugSessionE2a466Log\(\s*\n(?:[^;]|\n)*?\);\s*\n",
        re.MULTILINE,
    ),
]


def strip_agent_regions(text: str) -> str:
    while True:
        start = text.find("// #region agent")
        if start < 0:
            break
        depth = 1
        line_start = text.rfind("\n", 0, start) + 1
        i = text.find("\n", start)
        if i < 0:
            text = text[:line_start] + text[start + len("// #region agent") :]
            continue
        i += 1
        removed = False
        while i < len(text) and depth > 0:
            j = text.find("\n", i)
            if j < 0:
                break
            line = text[i : j + 1]
            if re.match(r"^[ \t]*//\s*#region agent", line):
                depth += 1
            elif re.match(r"^[ \t]*//\s*#endregion", line):
                depth -= 1
                if depth == 0:
                    text = text[:line_start] + text[j + 1 :]
                    removed = True
                    break
            i = j + 1
        if not removed:
            text = text[:start] + text[start + 1 :]
    return text


def strip_file(path: Path) -> bool:
    raw = path.read_text(encoding="utf-8")
    text = raw
    text = strip_agent_regions(text)
    changed = text != raw
    for _ in range(12):
        prev = text
        for pat in CALL_PATTERNS:
            text = pat.sub("", text)
        for pat in MULTI:
            text = pat.sub("", text)
        if text == prev:
            break
        changed = True
    if text != raw:
        path.write_text(text, encoding="utf-8", newline="\n")
    return changed


def main() -> int:
    changed = []
    for path in ROOT.glob("Assets/**/*.cs"):
        if strip_file(path):
            changed.append(path.relative_to(ROOT).as_posix())
    for rel in sorted(changed):
        print("stripped:", rel)
    return 0


if __name__ == "__main__":
    sys.exit(main())
