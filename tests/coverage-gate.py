#!/usr/bin/env python3
"""Fails the build when merged coverage drops below the recorded floor.

The floor is a ratchet, not a target: it is set to whatever was measured when the
gate went in, so the build breaks on a regression rather than on being imperfect.
Raise it when coverage improves; never lower it to turn a red build green.

Usage:
    python tests/coverage-gate.py <merged-cobertura.xml> <line-floor> <branch-floor>
"""

import sys
import xml.etree.ElementTree as ET


def main(argv: list[str]) -> int:
    if len(argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2

    report, line_floor, branch_floor = argv[1], float(argv[2]), float(argv[3])

    root = ET.parse(report).getroot()
    line = float(root.get("line-rate", 0)) * 100
    branch = float(root.get("branch-rate", 0)) * 100

    print(f"line   {line:5.1f}%  (floor {line_floor:.0f}%)")
    print(f"branch {branch:5.1f}%  (floor {branch_floor:.0f}%)")

    failures = []
    if line < line_floor:
        failures.append(f"line coverage {line:.1f}% is below the {line_floor:.0f}% floor")
    if branch < branch_floor:
        failures.append(f"branch coverage {branch:.1f}% is below the {branch_floor:.0f}% floor")

    for failure in failures:
        print(f"::error::{failure}", file=sys.stderr)

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
