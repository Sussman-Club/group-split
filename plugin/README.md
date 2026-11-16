# Group Split MCP plugin

This skills-only plugin guides Group Split operations, inbox reconciliation, household summaries and read-only inbox reviews using an independently configured Group Split MCP connection. It does not include an endpoint or credentials because deployments use different origins. Connect an approved server at its actual HTTPS `/mcp` URL in your host, authenticate through the host, and grant `mcp:read`. See [MCP setup](../docs/mcp.md). Write actions additionally require `mcp:write` and the API's own permissions.

The portable package consists of `plugin.json` and `skills/`. The Claude Code compatibility manifest is in `.claude-plugin/`. Register this folder with the host's supported plugin installation flow, connect the MCP server separately, and confirm that the skills and tools are discoverable. Try “Summarize our household spending for March in New York” or “Review my Group Split inbox.” Do not embed access tokens in plugin files.

Existing CLI installations and Keycloak clients on deployed realms are not removed automatically by this source change. Operators should plan migration and revoke obsolete access separately. This change does not deploy or install the plugin.

For Claude Code, after this branch is merged and available at the repository's default branch:

```bash
claude plugin marketplace add Sussman-Club/group-split
claude plugin install groupsplit@group-split
```

The plugin supplies instructions only; configure the authenticated MCP connection through your host separately. Existing installations must update the plugin to receive these skills.
