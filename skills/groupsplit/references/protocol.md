# The machine contract

What a program can rely on. Everything here is deliberate; none of it is a side effect of
how the CLI happens to be written today.

## stdout and stderr

- **stdout carries the result and nothing else.** Exit code 0 means stdout is safe to parse.
- **stderr carries everything else** -- progress, warnings, errors.

Capture them separately. A warning folded into stdout will break a JSON parse, and
redirecting stdout to a file is meant to leave you with a clean file and the warnings still
on screen.

## Choosing the rendering

| stdout is | Default |
| --- | --- |
| a terminal | text: tables, colour, sized to the window |
| anything else | JSON |

So no flag has to be remembered when piping. Pass `--json` anyway when you are the one
running the command: you are not always holding the pipe, and a table parsed as JSON fails
in a confusing way.

`--fields id,name` narrows JSON output to the named top-level fields, of an object or of
every element of an array.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. stdout is trustworthy. |
| `1` | A general failure. |
| `2` | Authentication: not signed in, expired, or refused. |
| `3` | The invocation was wrong: bad usage, a rejected value, or no server configured. |
| `4` | A mutation needs confirming. stdout carries the confirmation envelope. |

The distinction worth relying on is between 1 and 3: **3 means retrying will not help until
something changes.** A parse error is 3, and so is a value the server rejected.

Adding a code is a compatible change. Changing what an existing one means is not, so these
five will not be redefined underneath you.

## The error envelope

On stderr, whenever a command fails:

```json
{
  "error": "No group with that id.",
  "code": "GROUP_NOT_FOUND",
  "remediation": "Check the id. List what you can see with: groupsplit groups list",
  "traceId": "00-a1b2c3-d4e5f6-01"
}
```

- `code` is stable. **Branch on it.**
- `error` is prose for a person and may be reworded. Do not match on it.
- `remediation` is written to be acted on, and often names the command that would help.
- `traceId` is the API's own and matches its log line. Quote it when reporting a problem.

### Codes the CLI raises itself

Prefixed `CLI_`, which is how you tell them apart: a code with the prefix means the request
was never sent.

| Code | Exit | Meaning |
| --- | --- | --- |
| `CLI_AUTH_REQUIRED` | 2 | No credentials at all. |
| `CLI_AUTH_EXPIRED` | 2 | Credentials existed but could not be refreshed. |
| `CLI_AUTH_FAILED` | 2 | The identity server refused to issue a token. |
| `CLI_INVALID_INPUT` | 3 | Arguments rejected before any request. |
| `CLI_USAGE` | 3 | The command line did not parse. |
| `CLI_SERVER_NOT_CONFIGURED` | 3 | No server URL by flag, environment or config file. |
| `CLI_SERVER_UNREACHABLE` | 1 | DNS, TLS or connection failure. Nothing was sent. |
| `CLI_SERVER_ERROR` | 1 | The server answered with nothing interpretable. |
| `CLI_CONFIG_ERROR` | 1 | The config file is unreadable. |
| `CLI_CANCELLED` | 1 | Interrupted, or a confirmation declined. |
| `CLI_INTERNAL_ERROR` | 1 | **Always a defect in the CLI**, not a usage mistake. |

### Codes from the server

Anything without the prefix is the API's own code, passed through verbatim -- the CLI does
not invent a second vocabulary for conditions the API already names. The full catalog is in
[docs/errors.md](https://github.com/Sussman-Club/group-split/blob/dev/docs/errors.md). The
ones worth handling by name:

| Code | What it means for a caller |
| --- | --- |
| `GROUP_NOT_FOUND`, `TRANSACTION_NOT_FOUND`, `CATEGORY_NOT_FOUND` | The id is wrong or is not yours to see. Re-list. |
| `POSSIBLE_DUPLICATE_EXPENSE` | Filing was refused because this row looks like an expense already recorded. See the **groupsplit-inbox** skill. |
| `SPLITS_DO_NOT_SUM_TO_AMOUNT` | The `--split` figures do not add up to the amount. |
| `SPLIT_USER_NOT_IN_GROUP`, `RULE_USERS_NOT_IN_GROUP`, `TRANSACTION_PAYER_NOT_IN_GROUP` | A user id names somebody who is not in that group. |
| `GROUP_MEMBER_NOT_SETTLED`, `ACCOUNT_NOT_SETTLED` | Leaving or deleting is blocked until balances are square. |
| `CURRENCY_MISMATCH` | The amount is not in the group's currency. |
| `CATEGORY_IN_USE`, `SPLIT_RULE_IN_USE` | Something still points at it. Move those first. |
| `SPLIT_ON_A_PERSONAL_EXPENSE` | An expense with no group cannot be divided. |

## The confirmation envelope

Written to stdout with exit code 4, in place of the result:

```json
{
  "confirmationRequired": true,
  "action": "groups.leave",
  "summary": "Leave the group 'Trip to Lisbon'?",
  "changes": ["You are removed from 'Trip to Lisbon' (4 members)."],
  "confirmCommand": "groupsplit groups leave 3f25... --yes"
}
```

`action` is a stable slug you can branch on. `confirmCommand` is the exact string that
performs the action, so a caller with no terminal does not have to reconstruct the
invocation from its own history.

Nothing ever blocks on a prompt that nobody can answer. That is the reason exit code 4
exists, and the reason `--yes` should be something a user asked for rather than something
you added to keep the pipeline moving.

## The schema

`groupsplit schema --json` returns:

```json
{
  "name": "groupsplit",
  "version": "...",
  "exitCodes": { "success": 0, "error": 1, "authRequired": 2, "invalidInput": 3, "confirmationRequired": 4 },
  "command": { "name": "groupsplit", "description": "...", "aliases": [], "arguments": [], "options": [], "subcommands": [] }
}
```

`command` nests to the leaves. Each argument carries `name`, `type`, `arity` (`"min..max"`,
with `*` for unbounded) and `required`; each option carries `name`, `aliases`, `type`,
`required` and `recursive`. Types are rendered as what they look like on a command line --
`string`, `uuid`, `integer`, `number`, `boolean`, `date-time`, `string[]` -- and an enum as
its values joined by `|`, e.g. `asc|desc`.

Hidden commands and options are omitted, so what the schema lists is what is supported.

## Debugging

`GROUPSPLIT_DEBUG=1` attaches a stack trace to the envelope of an unexpected error.
`groupsplit --version` prints the version and the commit it was built from, so an installed
tool can always be traced back to what produced it.
