# Agent skills

Two skills that teach an AI agent to drive the `groupsplit` CLI:

| Skill | |
| --- | --- |
| [`groupsplit`](groupsplit/SKILL.md) | The protocol: read the schema first, JSON output, the exit-code table, the exit-4 confirmation gate, token auth with no browser. References cover the command surface, setup and the machine contract in full. |
| [`groupsplit-inbox`](groupsplit-inbox/SKILL.md) | Turning imported bank rows into expenses without recording the same money twice. |

They live here rather than in a repository of their own so that a change to the CLI and the
change to what an agent is told about it are the same commit. What they deliberately do
**not** contain is a copy of the flag list: `groupsplit schema --json` prints that from the
parser itself, and is right for whichever version is installed. The skills teach the
protocol around it.

## Installing them

Pick whichever suits the agent. All three read the same files.

### Any agent

[`npx skills`](https://github.com/vercel-labs/skills) installs into whatever agents it finds
-- Claude Code, Codex, Cursor, opencode, Gemini CLI and others -- writing to
`.agents/skills/` plus each agent's own directory.

```bash
npx skills add Sussman-Club/group-split                       # both
npx skills add Sussman-Club/group-split --skill groupsplit    # just the one
```

`--global` installs to `~/.agents/skills` instead of the current project.

### Claude Code, as a plugin

```bash
claude plugin marketplace add Sussman-Club/group-split
claude plugin install groupsplit@group-split
```

### By hand

Copy either directory into `.claude/skills/` or `.agents/skills/` of the project that needs
it. A skill is a directory with a `SKILL.md` in it; nothing here is generated or compiled.

## Changing them

The rule that matters: **do not write anything into a skill that
`groupsplit schema --json` already says.** A hand-maintained flag list is a second source of
truth that nothing in the build checks, and an agent told about a flag that no longer exists
will keep passing it until the CLI refuses.

The command table in
[`groupsplit/references/commands.md`](groupsplit/references/commands.md) is the one
concession, as a map for planning. It carries the `jq` line that regenerated it from a built
CLI, and the file says outright that the schema wins when the two disagree.
