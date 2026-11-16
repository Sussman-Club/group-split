"""Check the repository plugin is self-contained and names real read-only MCP tools."""
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[1]
plugin = root / "plugin"
manifest = json.loads((plugin / "plugin.json").read_text(encoding="utf-8"))
assert manifest["$schema"] == "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json"
assert manifest["name"] == "groupsplit"
compat = json.loads((plugin / ".claude-plugin/plugin.json").read_text(encoding="utf-8"))
assert (compat["name"], compat["version"]) == (manifest["name"], manifest["version"])
market = json.loads((root / ".claude-plugin/marketplace.json").read_text(encoding="utf-8"))
assert market["plugins"][0]["source"] == "./plugin"
tools = "\n".join(p.read_text(encoding="utf-8-sig") for p in (root / "src/GroupSplit.Mcp/Tools").glob("*.cs"))
for skill, required in {
    "household-summary": ("list_groups", "household_summary"),
    "inbox-review": ("bank_inbox_summary", "bank_inbox", "list_receipt_inbox", "get_bank_row", "review_receipt_inbox_item"),
}.items():
    text = (plugin / "skills" / skill / "SKILL.md").read_text(encoding="utf-8")
    assert text.startswith("---\n") and re.search(rf"(?m)^name: {skill}$", text)
    assert re.search(r"(?m)^description: .+", text)
    for name in required:
        assert f"`{name}`" in text
        assert re.search(rf'McpServerTool\(Name = "{name}", ReadOnly = true', tools), name
print("Plugin manifest, skills, and read-only tool names verified")
