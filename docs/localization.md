# Languages

The initial supported languages are English (`en`), Spanish (`es`), and Portuguese
(`pt`). Regional browser tags such as `es-MX` and `pt-BR` resolve to their neutral
language. Unsupported browser languages fall back to English. Shared pages, dialogs,
navigation, receipts, inbox, settlements, validation, domain errors, and web reconnect
screens use the resource catalogs.

## Choosing a language

Settings contains a language picker. `PUT /users/me/language` stores the choice on
the signed-in account; `GET /users/me` returns `preferredLanguage`. Null means the
person has not chosen a language. The API accepts only the supported neutral tags.
It does not localize responses, so CLI output remains unchanged.

The web host resolves the account preference before rendering HTML, using a custom
ASP.NET request culture provider after authentication. If no account preference is
available, the culture cookie wins, then Accept-Language, then English. The root
component writes the resolved culture cookie before the response starts, so
WebAssembly starts with the same answer. Its bootstrap reads that cookie or
`navigator.language`. Native hosts initialize culture from device preferences at
startup, falling back to the supported device language. A shared synchronization
component persists a newly loaded account preference and reloads if it differs from
startup. Saving reloads the app so existing components and date pickers are recreated
with the chosen culture. Culture is never changed inside render-tree construction.

Number and date formatting use the active culture. Money always retains its supplied
currency; legacy amounts without a currency remain USD. Group names, merchants,
category names, and other user content are not translated. Machine-readable values
and API/CSV serialization keep their existing formats.

## Translating UI

Resources live in `GroupSplit.App.Shared/Localization/AppStrings.resx`,
`AppStrings.es.resx`, and `AppStrings.pt.resx`. Inject
`IStringLocalizer<AppStrings>` and use the English sentence as the key, for example:

```razor
@using GroupSplit.App.Shared.Localization
@using Microsoft.Extensions.Localization
@inject IStringLocalizer<AppStrings> Loc

<h1>@Loc["Settings"]</h1>
<p>@Loc["You owe {0} {1}", name, amount.ToMoney(currency)]</p>
```

Add each key to all three catalogs. Use numbered placeholders for values; never
concatenate translated sentence fragments or use user content as a resource key.
English sentence keys ensure a missing UI entry remains readable English. Changing
an English sentence requires updating the key and every translation together.
Resource keys must also be unique ignoring case: the .NET resource compiler ignores
case-only duplicates. Context-specific variants use distinct `Copy.*` keys, with the
intended English copy stored as their neutral resource value.

MudBlazor keys use the names in its versioned resource catalog and go through
`AppMudLocalizer`; absent control keys fall back to MudBlazor's English resources.
The catalog includes calendar navigation, paging, dismissal, and number/date
conversion errors. Domain errors are resolved by stable API code and then localized.
Known server validation templates are localized without changing field names or
unknown server messages. Request attributes use shared `ValidationMessages` resources;
custom validation attributes must call `FormatErrorMessage` to honor them.

Razor views inherit `Loc` from `_Imports.razor`. Static C# copy helpers use `UiText`
to access the same resources when dependency injection is unavailable. Resource
lookups are evaluated for the current culture, never cached in static translated fields.
JavaScript widgets receive localized labels from their Razor component.

`LanguageResourcesTest` checks exact key parity and nonempty translations without
resource fallback. `LanguagePickerTest` checks selection, failed saves, regional
resolution, fallback, and account preference precedence during rendering. The API
tests check persistence, validation, authentication, and account isolation.
Run `python tools/localization/audit.py` to check literal source references, catalog
parity, placeholder preservation, and case-insensitive key uniqueness. Rendered
component tests also verify Spanish and Portuguese copy and unchanged user names.

## Adding another language

1. Add its neutral tag to `AppLanguages.Supported` and its native name to the picker.
2. Copy the English catalog to `AppStrings.<tag>.resx` and translate every value,
   preserving format placeholders. Add the tag to the resource parity test.
3. Add the tag to the web host's supported cultures and the browser helper's supported
   language list. WASM loads all globalization data.
4. Add it to `supportedLocales` in the Keycloak realm and `locales` in the login theme.
   Translate the login overrides into `messages_<tag>.properties`. The realm worker
   reconciles language settings on existing realms; theme changes require the normal
   Keycloak image rollout. Web and native sign-in requests pass `ui_locales`.
5. Run the app/web, API language, and realm reconciliation tests and review the UI
   with long translated text. Localized emails remain a separate follow-up.

Validation catalogs live in `GroupSplit.Shared/Localization`; add translations there
when adding an annotated request field. User-entered group, category, rule, merchant,
and person names remain unchanged. Language selection changes display formatting,
never the expense's currency or stored wire values.
