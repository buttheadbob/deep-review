# Web checks

Read the core file for each pass first. ESLint and TypeScript results get their verdicts through [triage.md](triage.md).

<a id="p3-the-event-loop-and-async"></a>
## P3: the event loop and async

- **One thread:** long synchronous work blocks input and rendering. Split it up, or move it to a Worker.
- **Floating promises:** a promise nobody awaits turns its error into an unhandled rejection. `array.forEach(async ...)` doesn't wait for anything; use `for...of` with `await`, or `Promise.all`.
- **Out-of-order responses:** a slow earlier request that finishes after a newer one overwrites the newer state (search as you type, switching tabs or items). Abort the old request (`AbortController`), or check a request id before applying a result.
- **`Promise.all`** rejects on the first failure and leaves the others running. `Promise.allSettled` when each result matters.
- **Double actions:** a double click submits twice. Disable the control while the request runs, or make the action idempotent on the server.
- **Timers:** `setInterval` callbacks pile up when async work takes longer than the interval; chain `setTimeout` instead. Background tabs throttle timers to a second or more.

<a id="p4-browsers-and-node"></a>
## P4: browsers and Node

- **Target browsers:** list them in P0 (browserslist, or what the site says it supports). Features beyond them need a fallback or a build step. Safari usually lags; check newer APIs and CSS (`:has()`, container queries, `structuredClone`) against it.
- **Node:** the version in `engines` or `.nvmrc` for tooling, ES modules versus CommonJS, and `fetch` (built in from Node 18).
- **Phones:** layout at phone width, touch targets large enough, and no control that only works on hover.
- **Visitors' clocks and locales:** dates sent and stored in UTC ISO 8601, and shown with `Intl.DateTimeFormat` in the viewer's zone.

<a id="p5-page-and-component-lifetimes"></a>
## P5: page and component lifetimes

- **Clean up:** event listeners, intervals, observers (`ResizeObserver`, `IntersectionObserver`, `MutationObserver`), WebSockets and `EventSource` connections are removed when their element or component goes away. Otherwise they leak and fire on detached nodes, once more for every visit in a single-page app.
- **Back/forward cache:** an `unload` handler disables it and doesn't fire reliably. Save state on `pagehide` or `visibilitychange`. A page restored from the cache refreshes stale data (`pageshow` with `persisted`).
- **Saving on leave:** `navigator.sendBeacon` or `fetch(..., { keepalive: true })`. A `beforeunload` prompt only when there are unsaved changes.
- **Blazor interop modules** are disposed with their components ([dotnet-app blazor-signalr](../dotnet-app/blazor-signalr.md#component-lifetime)).

<a id="p6-dom-and-browser-apis"></a>
## P6: DOM and browser APIs

- **Missing elements:** `querySelector` and `getElementById` return null, including when the script runs before the element exists (script placement, `defer`, `DOMContentLoaded`).
- **DOM identity:** generated ids must be unique against the whole document, and a caller-supplied id must not be reassigned. `aria-describedby`, label `for`, and focus targets must resolve to the current element, not one replaced by a later render.
- **Late accessibility content:** an `aria-describedby` target that is inserted, replaced, emptied or removed after open needs an observer or an explicit re-wire. A one-shot query at open is a finding when the content is asynchronous. See [core accessibility](../../checks/core/accessibility.md#names-roles-and-descriptions).
- **Form values are strings,** whatever the input type.
- **Permission APIs** (clipboard, notifications, geolocation, camera) need a secure context (HTTPS or localhost), may prompt, and may be denied. Handle the denial.
- **`localStorage`** can throw (private modes, a full quota). Wrap access in try/catch.
- **History:** `pushState` routes survive a reload and work with the back button.
- **Forms handled in JavaScript** prevent the default submit, or the page reloads.
- **Third-party embeds** (Discord widgets, video, analytics) are disclosed, load lazily, and send no more than they must (`referrerpolicy`).

<a id="p7-errors-and-fetch"></a>
## P7: errors and fetch

- **`fetch` resolves on HTTP errors:** a 404 or 500 is a resolved promise. Check `response.ok` or the status before reading the body.
- **`response.json()` throws** on a body that isn't JSON (an HTML error page from a proxy). Check `Content-Type`, or read text and parse in try/catch.
- **Rejections:** network failures reject with a `TypeError`, aborts with an `AbortError`. Tell an abort the code asked for from a failure.
- **No default timeout:** use `AbortSignal.timeout(ms)` or an `AbortController` with a timer.
- **Retries** with backoff, for idempotent requests only. A POST that may have gone through is never retried without an idempotency key.
- **The user sees failures:** an error state, never a silent empty list or a spinner that never stops.
- **Global handlers** (`window.onerror`, `unhandledrejection`) are for logging, not error handling.
- **`JSON.parse`** of stored or received data is wrapped in try/catch.

<a id="p8-browser-storage-and-caches"></a>
## P8: browser storage and caches

- **`localStorage` and `sessionStorage`** hold strings, per origin, readable by any script on that origin. An XSS bug reads them, so auth tokens don't go there; use `HttpOnly` cookies.
- **Versioned data:** stored values get a version and a migration, like files ([core persistence](../../checks/core/persistence.md#formats-and-migration)).
- **IndexedDB** upgrades run in `onupgradeneeded`, and are blocked while another tab holds the old version open.
- **HTTP caching:** hashed file names with long caching for static assets; HTML cached briefly. A cached `index.html` pointing at bundles a deploy deleted breaks the site.
- **Service workers** need an update path; a broken worker keeps users on an old version.

<a id="p9-inputs"></a>
## P9: inputs

- **What counts:** URL parameters, the hash, `postMessage` data, `document.referrer`, `window.name`, form fields, and responses from other origins.
- **Client checks are convenience;** the server validates again ([core authority-interop](../../checks/core/authority-interop.md#authority)).
- **Numbers:** `Number("")` is 0, `parseFloat("1,5")` is 1 (decimal commas), `parseInt` needs a radix, and `NaN` is tested with `Number.isNaN`.
- **64-bit ids** (Steam IDs, Discord snowflakes) are larger than `Number.MAX_SAFE_INTEGER`, and `JSON.parse` rounds them silently. They travel as strings.
- **Text into the page** goes through `textContent` or the framework's escaping ([P10](#p10-security)).

<a id="p10-security"></a>
## P10: security

- **DOM XSS sinks:** `innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`, parsed HTML inserted into the page, jQuery `.html()`, `$(htmlString)` and `.append(string)`, React's `dangerouslySetInnerHTML`, Vue's `v-html`. Trace each one to its input. Where HTML is really needed, sanitize it with a maintained sanitizer.
- **URL sinks:** `location = input`, `a.href = input`, `iframe.src`, `window.open(input)`. A `javascript:` URL runs code; allow `http` and `https` only.
- **Code sinks:** `eval`, `new Function`, and `setTimeout` or `setInterval` with a string.
- **Prototype pollution:** merging parsed JSON into objects copies `__proto__` and `constructor` keys onto every object. Use `Map` or `Object.create(null)`, and reject those keys.
- **Content-Security-Policy:** a policy without `unsafe-inline` or `unsafe-eval` for scripts, with `object-src 'none'`, a `base-uri`, and `frame-ancestors`. Inline scripts and `onclick=""` attributes need nonces or must go. Start existing sites in report-only mode.
- **Third-party scripts** come from a pinned version with Subresource Integrity (`integrity` and `crossorigin`). Each one runs with the page's full rights.
- **`target="_blank"`:** `rel="noopener noreferrer"`, for older browsers and to keep the referrer private.
- **Mixed content:** no HTTP resources on HTTPS pages.
- **Secrets in client code are public:** API keys, webhook URLs and tokens in a bundle or a page, including build-time variables that end up in the bundle (`VITE_*`, `NEXT_PUBLIC_*`).
- **Dependencies:** a committed lock file, `npm ci` in builds, `npm audit` results with verdicts, install scripts of new packages read, and package names checked for typosquats ([core security](../../checks/core/security.md#supply-chain)). deep_tools' own linters are pinned and never fetched at run time.

<a id="p11-origins-and-frames"></a>
## P11: origins and frames

- **`postMessage`:** the receiver checks `event.origin` against an exact list and validates `event.data`. The sender names the target origin, never `"*"` for anything private.
- **Being framed:** `frame-ancestors` in the CSP, or `X-Frame-Options`, against clickjacking.
- **CORS** on the APIs the site calls: exact origins, credentials only where needed ([dotnet-app web-security](../dotnet-app/web-security.md#cross-site-requests)).
- **Cookies:** a `Domain` attribute sends them to every subdomain.
- **Embedded frames** get a `sandbox` attribute with the fewest permissions that work.

<a id="p12-performance"></a>
## P12: performance

- **Bundle size:** code splitting, no whole library for one function.
- **Images:** sized for their display (`srcset`), modern formats, and `width`/`height` set so the layout doesn't jump.
- **Blocking resources:** scripts with `defer`, `async` or `type="module"`; fonts with `font-display`.
- **Layout thrashing:** reading layout (`offsetHeight`, `getBoundingClientRect`) between writes inside a loop.
- **Scroll and resize handlers:** throttled, and `passive: true` where they don't cancel.
- **Long lists** rendered at once; virtualize them.

<a id="p13-language-traps"></a>
## P13: language traps

- **`==` coerces** (`"0" == false`, `null == undefined`). Use `===`.
- **Types:** `typeof null` is `"object"`; arrays are objects (`Array.isArray`).
- **Numbers:** `0.1 + 0.2`, integers past 2^53 ([P9](#p9-inputs)), and `toFixed`, which returns a string and rounds unexpectedly.
- **Dates:** months count from 0. `new Date("2026-11-15")` is midnight UTC, but `new Date("2026-11-15T10:00")` is local time. `Date.parse` of other formats depends on the browser.
- **Sorting:** `sort()` with no comparator sorts as strings (`[10, 9, 1]` becomes `[1, 10, 9]`). A comparator must be consistent ([core correctness](../../checks/core/correctness.md#ordering)).
- **Loops:** `for...in` over an array gives string keys and inherited properties; use `for...of`. `var` in loops shares one binding between closures.
- **`this`** in callbacks: arrow functions keep it, plain functions don't.
- **Optional chaining** can hide missing data that should be an error.
- **TypeScript:** `as` casts and `!` assert without checking. `JSON.parse` and `response.json()` return `any`; validate the shape where the data enters.
- **CSS shorthand order:** `border-color`, `background`, `font` and similar shorthands reset longhands. A shorthand after a specific `border-left` or `background` can erase an accent or surface. Read the final computed value, not only the declaration order.
- **Whitespace contracts:** default `white-space: normal` collapses newlines. A plain-text message, log excerpt or user note needs `pre-line`/`pre-wrap`, or the single-line behavior must be explicit and tested.
- **Specificity and custom properties:** a later rule at equal specificity wins; a custom property that is unset falls back silently. Check the variant state (`Filled`, `Text`, `Dense`, hover, disabled) that the change claims.

<a id="p14-site-text-and-release"></a>
## P14: site text and release

- **Site text against live features:** the UpsideDown website describes live server features (KOTH, events, quantum storage). Check each changed claim against the server's plugins and config, and against what the user says is live. A page describing a feature that changed or is gone is a finding.
- **Links and content:** internal and external links work, anchors resolve, no placeholder text, dates and prices current, no links to staging hosts.
- **Accessibility:** `alt` text on images that carry meaning, labels on inputs, visible keyboard focus, enough contrast, headings in order, `lang` on `<html>`. Check the accessible name and description of dialogs and notices: no duplicate name from `aria-label` plus a visible heading, no empty `aria-describedby`, and late content is described or the first-render contract is explicit and tested.
- **Deployment:** the published files are what was reviewed; no source maps or config files that expose more than intended.

<a id="p15-tests"></a>
## P15: tests

- **Unit tests** for logic run headless in Node.
- **Browser tests run headless** (a headless browser engine), never in a visible window on the user's desktop (MasterRules 33a). Screenshots come from the headless browser's own render.
- **What to cover:** each changed page renders with no console errors, links resolve, forms submit and show their errors, and the layout holds at phone and desktop widths. For a UI change, also cover the empty, late, duplicate-id and cancellation-order paths, and assert the accessible name/description and computed style rather than only a class name.
- **Locator identity:** use a stable locator (title, id, test id), not a positional `.first()` that can resolve to the previous dialog or a stale element. Reusing one locator across sequential dialogs is a finding.
- **Computed style:** when the contract is visual, read `getComputedStyle` for the property (`white-space`, `border-left-color`, `background`) in the browser. A class assertion is not evidence that the rule applied.
- **A plain HTML site with no tests:** at least a link check and a headless render of each changed page, recorded as the live test.
