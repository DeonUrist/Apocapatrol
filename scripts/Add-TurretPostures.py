"""Add missing sitting defaults to vehicle/trailer JSON without rewriting existing fields."""
import argparse
import json
from pathlib import Path
import re
import shutil

KEYS = ("riderStanding", "riderStanding2", "riderStanding3", "riderStanding4")


def migrate(text):
    original = json.loads(text)
    spans, stack = [], []
    quoted = escaped = False
    for index, char in enumerate(text):
        if quoted:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                quoted = False
        elif char == '"':
            quoted = True
        elif char == "{":
            stack.append(index)
        elif char == "}":
            spans.append((stack.pop(), index + 1))
    insertions = []
    newline = "\r\n" if "\r\n" in text else "\n"
    for start, end in spans:
        obj = json.loads(text[start:end])
        if not isinstance(obj.get("body"), str):
            continue
        missing = [key for key in KEYS if key not in obj]
        if not missing:
            continue
        indent = re.match(r"\s*\n([ \t]*)\"", text[start + 1:end])
        prefix = indent.group(1) if indent else "  "
        addition = newline + newline.join(prefix + json.dumps(key) + ": false," for key in missing)
        insertions.append((start + 1, addition))
    for index, addition in sorted(insertions, reverse=True):
        text = text[:index] + addition + text[index:]
    updated = json.loads(text)

    def strip_added(before, after):
        if isinstance(before, dict):
            for key in KEYS:
                if key not in before:
                    after.pop(key, None)
            for key, value in before.items():
                strip_added(value, after[key])
        elif isinstance(before, list):
            for left, right in zip(before, after):
                strip_added(left, right)

    strip_added(original, updated)
    if original != updated:
        raise ValueError("Migration changed an existing template value")
    return text, len(insertions)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("roots", nargs="+", type=Path)
    parser.add_argument("--backup", type=Path)
    args = parser.parse_args()
    plans = []
    for root_index, root in enumerate(args.roots):
        for folder in ("BaseTemplates", "CarTemplates", "PlayerTemplates"):
            for path in sorted((root / folder).glob("*.json")):
                raw = path.read_bytes()
                updated, count = migrate(raw.decode("utf-8-sig"))
                if count:
                    encoded = updated.encode("utf-8")
                    if raw.startswith(b"\xef\xbb\xbf"):
                        encoded = b"\xef\xbb\xbf" + encoded
                    plans.append((root_index, root, path, encoded, count))
    # Validate every file before writing any changes.
    for root_index, root, path, encoded, count in plans:
        if args.backup:
            backup = args.backup / str(root_index) / path.relative_to(root)
            backup.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, backup)
        path.write_bytes(encoded)
    print(f"Updated {len(plans)} template files / {sum(p[4] for p in plans)} vehicle and trailer definitions; existing values preserved.")


if __name__ == "__main__":
    main()
