#!/usr/bin/env python3
"""Check the shared CSS bundle for duplicate selectors.

The app entrypoint is an import manifest, so checking only app.css would miss
collisions between the files it imports. This deliberately small parser handles
the CSS constructs used by the bundle without adding a Node toolchain to the
repository.
"""

from __future__ import annotations

import re
import sys
from collections.abc import Iterator
from pathlib import Path


IMPORT_RE = re.compile(r"@import\s+(?:url\(\s*)?['\"]([^'\"]+)['\"]\s*\)?\s*;", re.IGNORECASE)
KEYFRAME_RE = re.compile(r"@(?:-webkit-)?keyframes\b", re.IGNORECASE)


class CssCheckError(RuntimeError):
    """A bundle cannot be checked because its import graph is invalid."""


def without_comments(source: str) -> str:
    """Replace comments with whitespace while preserving offsets and line numbers."""

    def keep_lines(match: re.Match[str]) -> str:
        return "".join("\n" if char == "\n" else " " for char in match.group())

    return re.sub(r"/\*.*?\*/", keep_lines, source, flags=re.DOTALL)


def imported_files(path: Path, stack: tuple[Path, ...] = ()) -> Iterator[tuple[Path, str]]:
    """Yield an entrypoint and every local stylesheet reachable from its imports."""

    path = path.resolve()
    if path in stack:
        cycle = " -> ".join(str(item) for item in (*stack, path))
        raise CssCheckError(f"cyclic CSS import: {cycle}")

    try:
        source = path.read_text(encoding="utf-8")
    except OSError as error:
        raise CssCheckError(f"cannot read {path}: {error}") from error

    yield path, source

    clean = without_comments(source)
    for match in IMPORT_RE.finditer(clean):
        href = match.group(1)
        if re.match(r"(?:[a-z]+:)?//", href, re.IGNORECASE) or href.startswith("data:"):
            continue

        child = (path.parent / href).resolve()
        yield from imported_files(child, (*stack, path))


def split_selector_list(prelude: str) -> list[str]:
    """Split commas that separate selectors, not commas in functions or attributes."""

    selectors: list[str] = []
    start = 0
    parentheses = 0
    brackets = 0
    quote: str | None = None
    escaped = False

    for index, char in enumerate(prelude):
        if quote is not None:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = None
            continue

        if char in "'\"":
            quote = char
        elif char == "(":
            parentheses += 1
        elif char == ")":
            parentheses = max(0, parentheses - 1)
        elif char == "[":
            brackets += 1
        elif char == "]":
            brackets = max(0, brackets - 1)
        elif char == "," and parentheses == 0 and brackets == 0:
            selectors.append(prelude[start:index])
            start = index + 1

    selectors.append(prelude[start:])
    return selectors


def normalize_selector(selector: str) -> str:
    """Normalize formatting differences that do not change selector identity."""

    selector = re.sub(r"\s+", " ", selector.strip())
    selector = re.sub(r"\s*([>+~])\s*", r" \1 ", selector)
    return selector


def rules(source: str) -> Iterator[tuple[tuple[str, ...], tuple[str, ...], int]]:
    """Yield selector, containing at-rules, and source line for each CSS rule."""

    clean = without_comments(source)
    stack: list[str] = []
    segment_start = 0
    index = 0

    while index < len(clean):
        char = clean[index]

        if char in "'\"":
            quote = char
            index += 1
            escaped = False
            while index < len(clean):
                current = clean[index]
                if escaped:
                    escaped = False
                elif current == "\\":
                    escaped = True
                elif current == quote:
                    index += 1
                    break
                index += 1
            continue

        if char == "{":
            prelude = clean[segment_start:index].strip()
            containing_at_rules = tuple(item for item in stack if item)
            if (
                prelude
                and not prelude.startswith("@")
                and not any(KEYFRAME_RE.match(item) for item in containing_at_rules)
            ):
                line = clean.count("\n", 0, index) + 1
                selector_list = tuple(
                    normalized
                    for normalized in (normalize_selector(selector) for selector in split_selector_list(prelude))
                    if normalized
                )
                if selector_list:
                    yield selector_list, containing_at_rules, line

            stack.append(prelude if prelude.startswith("@") else "")
            segment_start = index + 1
        elif char == "}":
            if stack:
                stack.pop()
            segment_start = index + 1
        elif char == ";" and not stack:
            segment_start = index + 1

        index += 1


def display_path(path: Path) -> str:
    try:
        return str(path.relative_to(Path.cwd()))
    except ValueError:
        return str(path)


def main() -> int:
    if len(sys.argv) != 2:
        print(f"usage: {Path(sys.argv[0]).name} ENTRYPOINT.css", file=sys.stderr)
        return 2

    entrypoint = Path(sys.argv[1])
    try:
        files = list(imported_files(entrypoint))
    except CssCheckError as error:
        print(f"CSS check failed: {error}", file=sys.stderr)
        return 2

    seen: dict[tuple[tuple[str, ...], tuple[str, ...]], tuple[Path, int]] = {}
    duplicates: list[tuple[str, Path, int, Path, int, tuple[str, ...]]] = []
    selector_count = 0

    for path, source in files:
        for selector_list, context, line in rules(source):
            selector_count += len(selector_list)
            if len(selector_list) != len(set(selector_list)):
                duplicates.append((", ".join(selector_list), path, line, path, line, context))
                continue

            selector = tuple(sorted(selector_list))
            key = context, selector
            previous = seen.get(key)
            if previous is not None:
                duplicates.append((", ".join(selector), previous[0], previous[1], path, line, context))
            else:
                seen[key] = path, line

    if duplicates:
        print("Duplicate CSS selectors found:", file=sys.stderr)
        for selector, first_path, first_line, path, line, context in duplicates:
            scope = " / ".join(context) if context else "document root"
            print(
                f"  {selector!r}: {display_path(first_path)}:{first_line} and "
                f"{display_path(path)}:{line} ({scope})",
                file=sys.stderr,
            )
        return 1

    print(f"Checked {len(files)} CSS files and {selector_count} selectors; no duplicates.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
