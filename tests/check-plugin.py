"""Check plugin skills refer to real, appropriately scoped MCP tools."""
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
    "inbox-review": ("bank_inbox_summary", "bank_inbox", "list_receipt_inbox", "get_bank_row", "review_receipt_inbox_item", "search_receipt_bank_rows"),
    "groupsplit": ("current_user", "list_groups", "group_balances", "user_position", "list_group_expenses", "list_expenses", "get_expense", "expense_shares", "preview_expense", "settlement_plan"),
    "groupsplit-inbox": ("get_bank_row", "bank_row_matches", "review_receipt_inbox_item", "search_receipt_bank_rows"),
}.items():
    text = (plugin / "skills" / skill / "SKILL.md").read_text(encoding="utf-8")
    assert text.startswith("---\n") and re.search(rf"(?m)^name: {skill}$", text)
    assert re.search(r"(?m)^description: .+", text)
    for name in required:
        assert f"`{name}`" in text
        assert re.search(rf'McpServerTool\(Name = "{name}", ReadOnly = true', tools), name
for skill, names in {
    "groupsplit": ("create_expense",),
    "groupsplit-inbox": ("link_bank_row", "file_bank_row", "dismiss_bank_match", "set_bank_row_ignored", "file_receipt_inbox"),
}.items():
    text = (plugin / "skills" / skill / "SKILL.md").read_text(encoding="utf-8")
    for name in names:
        assert f"`{name}`" in text
        assert re.search(rf'\[Authorize\(Policy = "mcp:write"\)\]\s*\[McpServerTool\(Name = "{name}", ReadOnly = false', tools), name
reconciliation = (plugin / "skills/groupsplit-inbox/SKILL.md").read_text(encoding="utf-8")
for safety in ("fileAnyway", "POSSIBLE_DUPLICATE_EXPENSE", "Do not perform both creation paths", "not an atomic operation"):
    assert safety in reconciliation
print("Plugin manifest, skills, MCP tool permissions and reconciliation guidance verified")
