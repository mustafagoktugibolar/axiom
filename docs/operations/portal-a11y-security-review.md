# Portal accessibility and security review (plan item 10.10)

Scope: `src/Axiom.Portal` (Vue 3 + TypeScript: `App.vue`, `auth.ts`, `api.ts`, `style.css`, `views/*.vue`) and the API's static hosting of it (`src/Axiom.Api/Program.cs`).

**This is a static source review. No browser, screen reader, or automated accessibility engine (axe, Lighthouse) was run. Nothing here claims WCAG 2.2 AA conformance.** The criteria below are those the source was checked against; "fixed" means the source was changed, not that behaviour was observed in a browser.

## Method

- Read every view, the shell, the stylesheet and the auth code. Grepped for `v-html`, `innerHTML`, `localStorage`, `sessionStorage`, `clipboard`.
- Contrast ratios computed with the WCAG relative-luminance formula from the literal hex values in `style.css` and the SVG fills (table below).
- Read the API's static-file and header setup; added a CSP and related headers.
- Added automated guards (`tests/Axiom.UnitTests/Portal/PortalStaticGuardTests.cs`, runs in the existing `dotnet test` CI step) and header tests (`SecurityHeadersTests.cs`).

## Contrast (computed)

| Pair | Ratio | Result |
|---|---|---|
| light: fg / muted / accent / warn / bad / ok on #fff | 16.56 / 6.11 / 6.67 / 4.87 / 6.57 / 5.08 | pass (>= 4.5) |
| dark: fg / muted / accent / warn / bad / ok on #0d1117 | 16.02 / 6.50 / 6.11 / 7.50 / 5.65 / 7.45 | pass |
| Control borders, was `--line` #d0d7de on #fff (1.4.11 needs 3:1) | 1.45 | fail, fixed with `--control` #6e7781 (4.55 light, 4.16 dark) |
| White 12px text on graph node #bf8700 (`api` kind) | 3.14 | fail, fixed: #8a6100 (5.54) |
| White text on other node colours #8250df / #0969da / #1a7f37 / #cf222e / #6e7781 / #bc4c00 | 5.05 / 5.19 / 5.08 / 5.36 / 4.55 / 5.03 | pass |
| Node fills against dark page (#0d1117), 1.4.11 | 3.53 to 4.16 | pass (>= 3) |
| Selected-node stroke #1f2328 on dark page | 1.2 | fail, fixed: stroke now follows `--fg` |

Not computed: the 35% opacity "dimmed" graph nodes and semi-transparent `pre` background (decorative or supplementary state).

## Findings

| ID | Severity | Criterion / category | File | Finding | Status |
|---|---|---|---|---|---|
| A-1 | Medium | WCAG 2.4.1 Bypass blocks | App.vue, style.css | No skip link. | Fixed (skip link to `#main-content`) |
| A-2 | Medium | 2.4.3 Focus order, 4.1.3 Status | App.vue | View changes did not move focus or update the title; screen reader users get no cue. | Fixed (focus moves to view `h1`, `document.title` updated). Behaviour not verified in a browser |
| A-3 | Medium | 1.1.1, 4.1.2 | Graph.vue | `<svg role="img">` hides its focusable children (nodes) from assistive tech; no text alternative for edges. | Fixed (role `group`; relations table with caption added; Space key and `aria-pressed` on nodes) |
| A-4 | Low | 2.1.1 | Graph.vue | Nodes activated by Enter only. | Fixed (Space too) |
| A-5 | Medium | 1.4.11 Non-text contrast | style.css | Input/button borders 1.45:1. | Fixed |
| A-6 | Medium | 1.4.3 | Graph.vue | White on #bf8700 is 3.14:1. | Fixed |
| A-7 | Low | 1.4.11 | DecisionGraph.vue | Selected-node stroke invisible in dark mode. | Fixed |
| A-8 | Low | 1.3.1 | Evaluations, Explorer, Reviews, RecordDetail, DecisionGraph | Tables without caption; `<th>` without `scope`. | Fixed; guarded by test |
| A-9 | Low | 1.4.1 Use of colour | style.css | `button.link` distinguished by colour only inside tables. | Fixed (underline) |
| A-10 | Low | 2.3.3 / user preference | style.css | No `prefers-reduced-motion` handling (the portal has no animation today). | Fixed (defensive rule) |
| A-11 | Low | 1.4.10 Reflow | style.css | Nav could overflow; wide `size=` inputs overflow at 320px. | Fixed (nav wraps, controls `max-width:100%`, `overflow-wrap:anywhere`). Not verified at 320px in a browser |
| A-12 | Low | 3.3.1 / 4.1.3 | App.vue, Start.vue, Graph.vue | Sign-in error not associated with the input; async results not announced. | Fixed (`aria-describedby`, `aria-live` on result cards; errors already use `role="alert"`) |
| A-13 | Info | 1.3.1, 2.4.6 | all views | Landmarks (`header`, `nav aria-label`, `main`) and h1/h2/h3 order reviewed: one `h1` per view, no skipped levels found. Form controls all have a label or `aria-label`. `<html lang="en">` present. | OK; guarded by tests |
| A-14 | Medium | 2.1.1, 1.4.13, 4.1.2 | Graph.vue, DecisionGraph.vue | SVG graphs: keyboard access depends on `tabindex=0` on every node (long tab sequence for big graphs), no arrow-key navigation, no skip past the graph. The tables are the accessible equivalent. | Open |
| A-15 | Low | 1.4.10 Reflow | tables | Wide data tables scroll with the page at 320px rather than reflowing. Data tables are generally exempt (2D content) but usability is poor. | Open |
| A-16 | Low | 2.4.2 / 4.1.2 | views | Row "link" buttons are `<button>`, not links, so there are no URLs per record (no deep linking, back button leaves the app). The app has no router. | Open (design) |
| A-17 | Low | 1.4.3 | Graph.vue, DecisionGraph.vue | Dimmed (opacity .15 to .35) edges/nodes are intentionally low contrast. | Open (state is also conveyed by the table and `aria-pressed`) |
| A-18 | Low | 2.5.8 Target size (minimum) | style.css | Not measured: `button.link` text targets in tables may be under 24x24 CSS px without spacing. | Open, needs browser |
| S-1 | High if present | XSS | all | `v-html`, `innerHTML`, `eval`, `document.write`: none found. Finding messages, candidate statements, draft markdown are rendered through Vue text interpolation (escaped) and `<pre>`. | OK; guarded by test |
| S-2 | Info | Token storage | auth.ts | Access token in `sessionStorage` only (per tab, not shared, cleared with the tab); never `localStorage`, never in a URL. XSS would still read it; mitigated by S-1 and the CSP below. Sent as `Authorization: Bearer` same-origin only. | OK; localStorage guarded by test |
| S-3 | Medium | Missing security headers | Program.cs | No CSP, X-Frame-Options, nosniff or Referrer-Policy on the SPA or API. | Fixed: `SecurityHeaders.cs` (CSP `default-src 'none'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'`, `connect-src 'self'` + the OIDC authority origin, nosniff, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, Permissions-Policy, COOP/CORP). Tests in `SecurityHeadersTests` |
| S-4 | Low | Clickjacking | Program.cs | No framing protection. | Fixed (`frame-ancestors 'none'` + `X-Frame-Options`) |
| S-5 | Low | OIDC discovery trust | auth.ts | Endpoints from the discovery document were used without checking scheme; a downgrade or hostile document could receive the code and verifier. | Fixed: https required (http only for loopback) |
| S-6 | Low | OIDC error handling | auth.ts | IdP error redirect (`?error=...&state=...`) left in the URL and was ignored silently. | Fixed: query cleared, fixed message shown (raw IdP description never rendered) |
| S-7 | Info | OIDC PKCE | auth.ts | Verified: S256 challenge from a 48-byte `crypto.getRandomValues` verifier, 32-byte random `state` checked before the code is exchanged, pending data removed on first read and the code stripped from the URL with `replaceState`; `redirect_uri` is built from `location.origin + pathname` (never from query input), so no open redirect. No `nonce` is sent because the portal uses the access token only and does not validate an ID token. | OK |
| S-8 | Info | Dev login exposure | PortalAuthEndpoints.cs, AxiomAuth.cs | `/v1/auth/dev-login` is mapped only when `IsDevelopment()`, and configuration validation refuses `DevelopmentSigningKey` outside Development. The dev token is signed with a shared secret key in config, so Development must never be exposed. | OK |
| S-9 | Low | Token in clipboard | Start.vue | "Copy my access token" puts a bearer token on the clipboard (clipboard managers and history can retain it). Deliberate feature, user-initiated, with expiry noted in the UI. | Open (accepted; consider a short-lived agent token instead) |
| S-10 | Low | Error message leakage | api.ts | UI shows the server `detail`/`title` of ProblemDetails (and `error.message` for MCP-style bodies). The API's `AxiomExceptionHandler` controls that text; stack traces are not shown by the portal. Not re-audited for every server message. | Open (verify handler text) |
| S-11 | Low | CSP gaps | SecurityHeaders.cs | `style-src-attr 'unsafe-inline'` is needed for Vue style bindings. `connect-src` allows only the authority origin; an IdP whose token endpoint is on a different origin (e.g. some hosted IdPs) needs that origin added. No HSTS (left to the ingress/TLS terminator). No `Cache-Control: no-store` for the SPA shell. | Open |
| S-12 | Info | Dependencies | package.json | `npm audit` not run (no network assumed). Dependencies are Vue, Vite, vue-tsc only. | Open |

## Automated guards

`PortalStaticGuardTests` (unit tests, run by CI's `dotnet test`) fail on: `v-html` / `innerHTML` / `outerHTML` / `insertAdjacentHTML` / `document.write` / `eval` / `new Function` in `.vue`/`.ts`; `localStorage`; `<img>` without `alt`; `input`/`textarea`/`select` without `aria-label` or an enclosing `<label>`; `<table>` without `<caption>`; `<th>` without `scope`; `index.html` without `lang` or with zoom blocked. They are regex heuristics, not a parser: a control labelled via `for`/`id` would need an `aria-label` added or the heuristic extended. No CI workflow change was needed.

## Could NOT be verified without a browser or screen reader

- Actual announcements (NVDA, JAWS, VoiceOver) for focus-on-view-change, `aria-live` regions, graph nodes and `aria-current`.
- Visible focus appearance everywhere, the skip link position, tab order across the SVG graphs, 200% zoom and 320px reflow, and target sizes.
- Contrast of any state not listed above (hover, disabled, `rgba` backgrounds, high-contrast/forced-colours mode).
- That the CSP does not block anything legitimate (inline Vite polyfill, IdP token endpoint on another origin, favicon) in a real build served by the API.
- Real IdP behaviour for the PKCE flow.
- Cognitive criteria (plain language, error suggestions, consistent help) beyond what is evident in the source.

A follow-up should run axe/Lighthouse and a manual keyboard and screen reader pass against a built portal before any conformance statement is made.
