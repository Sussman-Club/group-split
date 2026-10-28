"""Read-only resource audit. Run `python tools/localization/audit.py` from the repo root."""
from pathlib import Path
import collections
import json
import re
import sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8")


def string_key(source, quote):
    """Read C# strings, replacing interpolation expressions with numbered placeholders."""
    interpolated = quote > 0 and source[quote - 1] == "$"
    index, argument, result = quote + 1, 0, ""
    while index < len(source):
        char = source[index]
        if char == "\\":
            escape = source[index:index + 2]
            result += {'\\"': '"', '\\n': '\n', '\\t': '\t', '\\\\': '\\'}.get(escape, escape)
            index += 2
            continue
        if char == '"':
            return index + 1, result
        if interpolated and source[index:index + 2] in ("{{", "}}"):
            result += source[index:index + 2]
            index += 2
            continue
        if interpolated and char == "{":
            start, depth = index + 1, 1
            index += 1
            while index < len(source) and depth:
                if source[index] == '"':
                    index, _ = string_key(source, index)
                    continue
                if source[index] == "{": depth += 1
                if source[index] == "}": depth -= 1
                index += 1
            body, nesting, spec = source[start:index - 1], 0, ""
            for position, token in enumerate(body):
                if token in "([": nesting += 1
                if token in ")]": nesting -= 1
                if nesting == 0 and token in ":,":
                    spec = body[position:]
                    break
            result += "{" + str(argument) + spec + "}"
            argument += 1
            continue
        result += char
        index += 1
    raise ValueError("Unterminated string")


def catalog(path):
    entries = [(node.get("name"), node.findtext("value", ""))
               for node in ET.parse(path).getroot().findall("data")]
    duplicates = [key for key, count in collections.Counter(key.casefold() for key, _ in entries).items() if count > 1]
    assert not duplicates, f"{path}: .NET ignores case-colliding keys: {duplicates}"
    assert all(value.strip() for _, value in entries), f"{path}: empty translation"
    return dict(entries)


def placeholders(value):
    return sorted(re.findall(r"\{\d+(?:[^}]*)\}", value))


ui = Path("src/GroupSplit.App/GroupSplit.App.Shared/Localization/AppStrings")
validation = Path("src/GroupSplit.Shared/Localization/ValidationMessages")
for base in (ui, validation):
    english = catalog(base.with_suffix(".resx"))
    for language in ("es", "pt"):
        translated = catalog(Path(str(base) + f".{language}.resx"))
        assert english.keys() == translated.keys(), f"{base}: {language} key mismatch"
        for key in english:
            assert placeholders(english[key]) == placeholders(translated[key]), f"{language}: placeholders differ for {key}"
    print(f"{base.name}: {len(english)} entries; translations, placeholders, and key uniqueness verified")

keys = set()
for path in Path("src/GroupSplit.App").rglob("*"):
    if path.suffix not in (".cs", ".razor") or any(part in ("bin", "obj") for part in path.parts): continue
    source = path.read_text(encoding="utf-8-sig")
    for match in re.finditer(r'UiText\.(?:Get|Format)\(\s*\$?"|\bLoc\[\s*"', source):
        _, key = string_key(source, match.end() - 1)
        keys.add(key)
    if path.name == "ErrorMessages.cs":
        source = re.sub(r"(?m)^\s*//[^\n]*", "", source)
        constants = {match[1]: "".join(re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"', match[2]))
                     for match in re.finditer(r"const string (\w+)\s*=([^;]+);", source)}
        for match in re.finditer(r"\[ErrorCodes\.\w+\]\s*=\s*([^;]+?)(?=,\s*\[ErrorCodes\.|\n\s*\};)", source):
            parts = re.findall(r'"(?:\\.|[^"\\])*"|\b(?:ServerPrefix|RateLimitedPrefix)\b', match[1])
            keys.add("".join(json.loads(value) if value.startswith('"') else constants[value] for value in parts))

missing = keys - catalog(ui.with_suffix(".resx")).keys()
assert not missing, "Missing resource keys:\n" + "\n".join(sorted(missing))
print(f"{len(keys)} literal UI/error references have all three translations")
