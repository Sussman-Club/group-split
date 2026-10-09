#!/usr/bin/env python3
"""Keep the existing RustFS account when publishing onto a persistent volume."""

import json
from pathlib import Path
import sys


STORAGE_KEYS = ("STORAGE_ACCESSKEY", "STORAGE_SECRETKEY")


def credential_lines(environment):
    credentials = {}
    for line in environment.splitlines():
        key, separator, value = line.partition("=")
        if key not in STORAGE_KEYS or not separator:
            continue
        if key in credentials or not value.strip().strip("\"'"):
            raise ValueError(f"Invalid or duplicate {key} assignment")
        credentials[key] = line
    if credentials and len(credentials) != len(STORAGE_KEYS):
        raise ValueError("The previous storage account is incomplete")
    return credentials


def preserve(environment, previous):
    credentials = credential_lines(previous)
    generated = credential_lines(environment)
    if len(generated) != len(STORAGE_KEYS):
        raise ValueError("The generated environment is missing the storage account")
    if not credentials:
        return environment
    return "\n".join(
        credentials.get(line.partition("=")[0], line)
        for line in environment.splitlines()
    ) + "\n"


def main():
    previous_path, environment_path = map(Path, sys.argv[1:])
    previous = json.loads(previous_path.read_text())["config"].get("environment")
    if previous is None:
        previous = ""
    if not isinstance(previous, str):
        raise ValueError("The previous stack environment is not a string")
    environment_path.write_text(preserve(environment_path.read_text(), previous))


if __name__ == "__main__":
    main()
