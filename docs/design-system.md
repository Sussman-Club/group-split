# Design system

One palette, one type pair, one set of radii and shadows. The product is not only the
Blazor app: a visitor signs in on Keycloak's pages, manages their password in Keycloak's
account console, and reads mail Keycloak sends. All four have to look like the same
product, and none of them can read another's stylesheet.

## The rule

**A style change is not finished until it is in every view that shows it — the app, the
Keycloak login pages, the Keycloak account console, and the mail.** There is no build step
that fans a token out, and nothing fails when one of them drifts: the app simply starts
looking newer than the sign-in page in front of it. Changing a colour, a font, a radius or
a shadow means editing every file in the table below that carries it.

## Where each copy lives

| File | Covers | Form |
| --- | --- | --- |
| [`GroupSplit.App.Shared/wwwroot/app.css`](../src/GroupSplit.App/GroupSplit.App.Shared/wwwroot/app.css) | The app's own chrome, and the source of truth for the token names and values | `--gs-*` custom properties on `:root` and `:root[data-theme="dark"]` |
| [`GroupSplit.App.Shared/Services/ThemeService.cs`](../src/GroupSplit.App/GroupSplit.App.Shared/Services/ThemeService.cs) | Everything MudBlazor draws in the app | `PaletteLight` / `PaletteDark` / `Typography`, restating the same values |
| [`Assets/keycloak/themes/login/resources/css/group-split.css`](../src/GroupSplit.AppHost/Assets/keycloak/themes/login/resources/css/group-split.css) | Sign-in, password reset, email verification, identity-provider linking | The same `--gs-*` tokens, then PatternFly 5 globals mapped onto them |
| [`Assets/keycloak/themes/account/resources/css/group-split.css`](../src/GroupSplit.AppHost/Assets/keycloak/themes/account/resources/css/group-split.css) | The account console | As above |
| [`Assets/keycloak/themes/email/messages/messages_en.properties`](../src/GroupSplit.AppHost/Assets/keycloak/themes/email/messages/messages_en.properties) | Password reset, verification and required-action mail | Literal hex and pixel values inline, because a mail has no stylesheet |
| [`BrandMark.razor`](../src/GroupSplit.App/GroupSplit.App.Shared/Components/BrandMark.razor) and the two `logo.svg` under `Assets/keycloak/themes/*/resources/img/` | The three-arc mark | Literal `fill` attributes, one copy each |

The token names are deliberately identical across the CSS files, so a change is usually the
same line pasted into each. The two Keycloak stylesheets carry only the subset their pages
can use.

## Both schemes, every time

Light and dark are equal citizens. The app flips on `data-theme="dark"`; both Keycloak
themes flip on `pf-v5-theme-dark`, the class Keycloak itself puts on `<html>`. Never key a
Keycloak override on `prefers-color-scheme`: PatternFly's own dark values sit under
`:where(.pf-v5-theme-dark)`, which is specificity-zero, so a media-query `:root` block
would darken our tokens on a page PatternFly still believed to be light. Each stylesheet
carries the reasoning in its header comment.

Two colours legitimately differ between the app and Keycloak's dark scheme, for contrast
rather than taste: `--gs-teal-deep` is the *lighter* tint in dark mode, because hover has
to move away from the background rather than toward it.

## Checking a change

The app is the easy one — `aspire start` and look. For the Keycloak pages, the theme is
bind-mounted from the checkout in run mode, so a reload picks up an edited stylesheet
without rebuilding anything. Keycloak caches theme resources, so start it with
`KC_SPI_THEME_STATIC_MAX_AGE=-1`, `KC_SPI_THEME_CACHE_THEMES=false` and
`KC_SPI_THEME_CACHE_TEMPLATES=false` while iterating, or hard-reload. Check both schemes:
the OS setting is what drives them.

Mail goes to Mailpit in run mode, which renders the HTML body as a client would.
