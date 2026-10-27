# Design system

One palette, one type pair, one set of radii and shadows. The product is not only the
Blazor app: a visitor signs in on Keycloak's pages, manages their password in Keycloak's
account console, and reads mail Keycloak sends. All four have to look like the same
product, and none of them can read another's stylesheet.

## The rule

**A style change is not finished until it is in every view that shows it: the app, the
Keycloak login pages, the Keycloak account console, and the mail.** There is no build step
that fans a token out, and nothing fails when one of them drifts; the app starts looking
newer than the sign-in page in front of it. Changing a colour, a font, a radius or
a shadow means editing every file in the table below that carries it.

## Expandable content

Use a triangle beside a stable clickable heading for small content disclosures: right
when closed, down when open. Use native `details`/`summary`, or `DisclosureToggle` for
controlled state with `aria-expanded` and `aria-controls`. Do not add View/Hide actions
for the same content, or switch the heading's wording when it expands.

Long receipts open through **Open receipts** in a dedicated dialog. Its itemized bill
is visible without another expansion, with one scrolling content area. Keep Edit, Read,
Download and Add actions separate. View/Open actions navigate to another view; triangle
controls expand content in place. Selection chips and Show all/Show fewer list controls
have their own purpose and are not section expanders.

## Where each copy lives

| File | Covers | Form |
| --- | --- | --- |
| [`GroupSplit.App.Shared/wwwroot/css/tokens.css`](../src/GroupSplit.App/GroupSplit.App.Shared/wwwroot/css/tokens.css) | The app's source of truth for the token names and values | `--gs-*` custom properties on `:root` and `:root[data-theme="dark"]` |
| [`GroupSplit.App.Shared/Services/ThemeService.cs`](../src/GroupSplit.App/GroupSplit.App.Shared/Services/ThemeService.cs) | Everything MudBlazor draws in the app | `PaletteLight` / `PaletteDark` / `Typography`, restating the same values |
| [`Assets/keycloak/themes/login/resources/css/group-split.css`](../src/GroupSplit.AppHost/Assets/keycloak/themes/login/resources/css/group-split.css) | Sign-in, password reset, email verification, identity-provider linking | The same `--gs-*` tokens, then PatternFly 5 globals mapped onto them |
| [`Assets/keycloak/themes/account/resources/css/group-split.css`](../src/GroupSplit.AppHost/Assets/keycloak/themes/account/resources/css/group-split.css) | The account console | As above |
| [`Assets/keycloak/themes/email/messages/messages_en.properties`](../src/GroupSplit.AppHost/Assets/keycloak/themes/email/messages/messages_en.properties) | Password reset, verification and required-action mail | Literal hex and pixel values inline, because a mail has no stylesheet |
| [`BrandMark.razor`](../src/GroupSplit.App/GroupSplit.App.Shared/Components/BrandMark.razor) and the two `logo.svg` under `Assets/keycloak/themes/*/resources/img/` | The three-arc mark | Literal `fill` attributes, one copy each |
| [`Resources/AppIcon/appiconfg.svg`](../src/GroupSplit.App/GroupSplit.App/Resources/AppIcon/appiconfg.svg) and [`Resources/Splash/splash.svg`](../src/GroupSplit.App/GroupSplit.App/Resources/Splash/splash.svg) | Native launcher and splash mark | The same three paths and fills as `wwwroot/favicon.svg`; native background and adaptive-icon scale live in the MAUI project |

The token names are identical across the CSS files, so a change is usually the same line
pasted into each. The two Keycloak stylesheets carry only the subset their pages
can use.

## Action buttons

All action buttons use `--gs-button-radius: 14px`, including filled, outlined, quiet,
destructive, glass, floating, and receipt-picker actions. MudBlazor buttons inherit this
from `.mud-button-root`; custom buttons reference the same token. Variants change colour
and emphasis, not corner shape. `gs-btn-pill` is a legacy class and no longer changes
geometry; new variants must not add their own radius or density overrides.

Text actions use the Slim density: a 32px minimum face, 6px vertical padding, an 18px
line height, and 13px labels at weight 600. Long labels wrap; never set a fixed height or
truncate action text. Their invisible hit area extends 6px above and below the face.
Wrapping action groups must reserve at least a 12px row gap so hit areas do not overlap.
Account actions share `gs-account-action` for spacing and content-sized widths. Filter chips,
segmented selectors, badges, and navigation retain their own shapes because they express
selection or location. Icon-only buttons retain their 44px touch size. The Keycloak themes
and email buttons mirror the 14px action radius.

## Both schemes, every time


Light and dark are equal citizens. The app flips on `data-theme="dark"`; both Keycloak
themes flip on `pf-v5-theme-dark`, the class Keycloak itself puts on `<html>`. Never key a
Keycloak override on `prefers-color-scheme`: PatternFly's own dark values sit under
`:where(.pf-v5-theme-dark)`, which is specificity-zero, so a media-query `:root` block
would darken our tokens on a page PatternFly still believed to be light. Each stylesheet
carries the reasoning in its header comment.

One colour legitimately differs between the app and Keycloak's dark scheme, for contrast
rather than taste: `--gs-teal-deep` is the *lighter* tint in dark mode, because hover has
to move away from the background rather than toward it.

## Checking a change

The app is the easy one: `aspire start` and look. For the Keycloak pages, the theme is
copied into the container when it starts (`WithContainerFiles` in
`KeycloakDevelopmentExtensions.cs`), so an edited stylesheet shows once Keycloak is
restarted, not on a page reload. Keycloak caches theme resources, so start it with
`KC_SPI_THEME_STATIC_MAX_AGE=-1`, `KC_SPI_THEME_CACHE_THEMES=false` and
`KC_SPI_THEME_CACHE_TEMPLATES=false` while iterating, or hard-reload. Check both schemes:
the OS setting is what drives them.

Mail goes to Mailpit in run mode, which renders the HTML body as a client would.
