# Error contract

Every non-2xx response from the API is an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)
problem details document, `application/problem+json`, in every environment. The client
branches on one member of it, `code`, and shows people a message of its own choosing; the
`title` and `detail` the API writes are for developers and may change at any time.

```json
{
  "type": "https://groupsplit.app/errors/group-member-not-settled",
  "title": "Member has an outstanding balance",
  "status": 409,
  "detail": "The member has to settle up before leaving the group.",
  "instance": "/groups/8f1.../members/2ab...",
  "code": "GROUP_MEMBER_NOT_SETTLED",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "balance": -50
}
```

| Member | Always present | Meaning |
| --- | --- | --- |
| `type` | yes | `https://groupsplit.app/errors/` + the code in kebab-case. An opaque, stable identifier; it does not resolve to a page. |
| `title` | yes | A short phrase for developers. |
| `status` | yes | The HTTP status, repeated. |
| `detail` | usually | A sentence for developers about this occurrence. Never an exception message, stack trace or SQL. |
| `instance` | yes | The request path. |
| `code` | yes | The stable identifier from the [catalog](#the-code-catalog). **This is what clients branch on.** |
| `traceId` | yes | The W3C trace id of the request. The same id is on the server's log line, so a screenshot of an error is enough to find the log. |
| `errors` | on `VALIDATION_FAILED` | Field name to messages, as the framework's validation problem has it. |
| anything else | per code | Extension members named in the catalog, such as `outstandingBalances`. |

## Which status means what

| Status | Category | Thrown in the API as | Meaning for the caller |
| --- | --- | --- | --- |
| 400 | Validation | `ValidationException`, or the framework's own validation | The request itself is wrong. Fix the input and retry. |
| 401 | Unauthenticated | the bearer handler | Sign in again. |
| 403 | Forbidden | `ForbiddenException` | You are known and may not do this. |
| 404 | Not found | `NotFoundException`, or `Problems.NotFound(...)` from an endpoint | What the URL names, or what the body refers to, does not exist for you. A resource in someone else's group answers this way too: whether it exists is not yours to learn. |
| 409 | Conflict | `ConflictException`, or `Problems.Conflict(...)` from an endpoint | The request is fine, the current state refuses it. Something has to change first. |
| 500 | Bug | anything else | Our fault. The body carries only the trace id. |

## The code catalog

Declared once in [`ErrorCodes`](../src/GroupSplit.Shared/Errors/ErrorCodes.cs), which both the
API and the clients compile against. Codes are strings, as RFC 9457 practice has it, so a code
the API adds later reaches an old client as a string it does not recognise and can fall back
on, rather than as a value it cannot deserialize.

### Generic

Used when the response was produced by something that knows nothing about the domain:
routing, authentication, model binding, an unhandled exception.

| Code | Status | When |
| --- | --- | --- |
| `BAD_REQUEST` | 400 | The body or a parameter could not be read. |
| `VALIDATION_FAILED` | 400 | A model annotation failed. Carries `errors`. |
| `UNAUTHENTICATED` | 401 | No token, or an expired one. |
| `FORBIDDEN` | 403 | An authorization policy refused. |
| `NOT_FOUND` | 404 | No such route. |
| `CONFLICT` | 409 | Reserved; nothing produces it today. |
| `INTERNAL_ERROR` | 500 | An unhandled exception. Carries only `traceId`. |

### Not found (404)

| Code | When |
| --- | --- |
| `GROUP_NOT_FOUND` | The group does not exist, or the caller is not a member. |
| `USER_NOT_FOUND` | The user named in the request does not exist, or is not someone the caller shares a group with. |
| `ACCOUNT_NOT_FOUND` | The account to delete does not exist. |
| `TRANSACTION_NOT_FOUND` | The transaction does not exist, or is in a group the caller is not in. |
| `RULE_NOT_FOUND` | The rule does not exist, has no current version, or is in a group the caller is not in. |
| `RULE_VERSION_NOT_FOUND` | The rule version named in a transaction does not exist for the caller. |

### Forbidden (403)

| Code | When |
| --- | --- |
| `GROUP_CANNOT_REMOVE_SELF` | The member being removed is the caller. |

### Conflict (409)

| Code | When | Extra members |
| --- | --- | --- |
| `GROUP_MEMBER_NOT_SETTLED` | The member being removed still has a balance in the group. | `balance`: their net balance, negative when they owe. |
| `ACCOUNT_NOT_SETTLED` | The account being deleted still has a balance in one or more groups. Nothing was changed. | `outstandingBalances`: an array of `{ groupId, groupName, balance }`. |
| `GROUP_HAS_NO_RULE` | A transaction names a group that has no rule to record it against. | |
| `RULE_CATEGORY_TAKEN` | The group already has a live rule with that category. | |
| `RULE_NOT_EDITABLE` | The rule is a system rule and cannot be edited. | |
| `RULE_NOT_DELETABLE` | The rule is a system rule and cannot be deleted. | |
| `RULE_NO_USER_TRANSACTIONS` | The rule (a settlement rule, say) does not accept transactions entered by members. | |
| `RULE_VERSION_HAS_REMOVED_MEMBER` | The rule version splits with someone who has left the group. Update the rule first. | |
| `TRANSACTION_PAYER_NOT_IN_GROUP` | The person named as having paid is not a member of the rule's group. | |

### Validation (400)

Domain rules the model annotations cannot express. Unlike `VALIDATION_FAILED` these carry
no `errors` member; the code is the whole message.

| Code | When |
| --- | --- |
| `TRANSACTION_RULE_REQUIRED` | The group has rules to pick from and the transaction picked none. |
| `TRANSACTION_PAYER_REQUIRES_RULE` | A transaction paid by someone other than the caller named no rule version. |
| `RULE_PERCENTAGES_INVALID` | The percentages of a percent rule do not add up to 100. |
| `RULE_USERS_NOT_IN_GROUP` | A percent or shares rule names a user who is not a member of the group. |
| `RULE_SHARES_EMPTY` | A shares rule gives nobody a share. |

## Producing an error in the API

Everything lives under [`src/GroupSplit.API/Errors`](../src/GroupSplit.API/Errors).

- **In a service**, throw one of the four typed exceptions with a code and a sentence for
  `detail`: `throw new ConflictException(ErrorCodes.RuleNotEditable, "Rule is not editable.")`.
  Attach extension members with `.WithExtension("balance", userBalance)`. Nothing catches these
  in the service layer; the exception handler does.
- **In an endpoint** that already has the answer in hand, return a problem rather than throw:
  `Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.")`, or
  `Problems.Conflict(code, detail, extensions)`. Never `Results.NotFound()` on its own: it
  would still become problem details, but with the generic `NOT_FOUND` code.
- **Declare what the endpoint can return**: `.ProducesProblem(StatusCodes.Status404NotFound)`,
  `.ProducesProblem(StatusCodes.Status409Conflict)`, `.ProducesValidationProblem()` for a
  body the framework validates. The 401 and 500 every endpoint shares are declared once per
  route group by `ProducesStandardProblems()`. These declarations are the generated client's
  only knowledge of the contract: a declared status arrives in the client as a typed
  `ApiException<ProblemDetails>`; an undeclared one arrives as a plain `ApiException` with the
  body as a string, which the client still parses, but do not rely on that.
- **Anything else that is thrown is a bug.** `InvalidOperationException` for a broken
  invariant stays as it is; the caller gets a 500 with a trace id and nothing else, and the
  log gets the exception at `Error` with the same trace id.

How it fits together:

| Piece | Job |
| --- | --- |
| `Problems` | The one factory. Builds a problem from a status, code and detail; `Enrich` stamps `code`, `type`, `instance` and `traceId` on every problem the framework produces, whoever produced it. |
| `DomainExceptionHandler` | `IExceptionHandler` that maps the typed exceptions. Logs at `Information` with the code, without a stack trace. |
| `UnhandledExceptionHandler` | The last `IExceptionHandler`. Logs at `Error` with the exception and the trace id, marks the activity as failed, answers `INTERNAL_ERROR`. |
| `AddApiErrorHandling()` / `UseApiErrorHandling()` | The wiring: `AddProblemDetails` with `Enrich` as the customization, the two handlers, `UseExceptionHandler()` and `UseStatusCodePages()` first in the pipeline. `Program.cs` and the endpoint test host both call these, so they cannot drift. |

## Consuming an error in the clients

Everything lives under
[`src/GroupSplit.App/GroupSplit.App.Shared/Services/Errors`](../src/GroupSplit.App/GroupSplit.App.Shared/Services/Errors).

- **`ApiErrors.Read(exception)`** turns anything a call can throw into an `ApiError`: a
  `Kind` to decide what to do, a `Message` a person can read, and the `Code`, `Status`,
  `TraceId` and `Problem` for the few places that branch on them.
- **`ErrorMessages`** is the code-to-message table, and the only place user-facing wording
  for an error lives. A code it does not know falls back to a message for the status, so a
  newer API never produces a blank. A test checks every code in the catalog has an entry.
- **`ApiErrorPresenter`** owns what happens next. `TryAsync(call, "Could not save the expense.")`
  runs the call and, on failure, shows the message as an error snackbar and returns `false`.
  `CaptureAsync(call)` returns the `ApiError` without showing it, for a dialog that wants
  the message inline and to stay open (see `ManageRulesDialog`). Anything that is not an API
  failure is rethrown: a bug belongs to the `ErrorBoundary` in `MainLayout`, not to a snackbar.
- The page state services run every write through the presenter and return `Task<bool>`;
  the pages need only the answer.

The three failures that are not domain errors each have one defined behaviour:

| What happened | `Kind` | What the person sees |
| --- | --- | --- |
| 401 | `Unauthenticated` | Sent to sign in again, returning to the page they were on. |
| No connection, timeout | `Network` | "We could not reach the server. Check your connection and try again." |
| Any 5xx | `Server` | "Something went wrong on our side. Please try again in a moment. (Reference `traceId`)" |

Branching on a code, the one place it happens today:

```csharp
catch (Exception exception) when (ApiErrors.IsApiFailure(exception))
{
    var error = ApiErrors.Read(exception);

    if (error.Code == ErrorCodes.AccountNotSettled)
        _blocking = error.Extension<List<OutstandingBalance>>(ProblemDetails.OutstandingBalancesExtension) ?? [];
    else
        _error = error.Message;
}
```

## Adding a code

1. Add the constant to `ErrorCodes`, in the section for its status.
2. Add a title to `Problems.Titles` (the API's short phrase for developers).
3. Add a message to `ErrorMessages` (what people see). The catalog test fails until you do.
4. Throw it from the service, or return it from the endpoint, and declare the status with
   `ProducesProblem` if the endpoint did not already.
5. Add a row to the table above.
6. Cover it in `ProblemResponseTest` if it is a new category or carries an extension member.

## Decisions

- **Exceptions, not `Result<T>`, in the service layer.** The smaller step from where the code
  was; the handler makes the mapping a single place either way.
- **Strings, not an enum, for `code`.** Forward compatibility with clients that have not been
  rebuilt, and the convention the RFC and the framework guidance follow.
- **`type` is opaque.** It is derived from the code and stable, but does not resolve. Making
  it resolve to this document is a later, additive change.
- **`traceId` is the W3C `traceparent` form** (`00-<trace>-<span>-<flags>`), the same value
  the OpenTelemetry pipeline attaches to the log line and the Aspire dashboard searches by.
