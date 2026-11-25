# Pack: web (JavaScript, TypeScript, HTML)

**Loads when** gate.json's `languages` has JavaScript or TypeScript with `files` above 0, whatever the `projectKind`. Load it too for a site of plain HTML and CSS (the UpsideDown website), which the gate counts as markup and doesn't list as a language, and for the JavaScript side of a Blazor app's interop. Its checks apply to the scripts, pages and components on the P2 list.

**What it adds:** the event loop and async, browsers and Node, page and component lifetimes, DOM APIs, `fetch` error handling, browser storage, DOM XSS sinks, URL and code sinks, prototype pollution, CSP and Subresource Integrity, `postMessage` and framing, number precision and dates, site text against live features, accessibility, headless browser tests, and how to give verdicts to ESLint and TypeScript results.

**Stage 1:** the built-in `code-review` at high effort plus `security-review` ([Report.md](../../Report.md#the-stage-1-record)).

## Pass map

All checks are in [checks.md](checks.md), in sections by pass; ESLint and TypeScript triage is in [triage.md](triage.md).

| Pass | Section |
|---|---|
| P0 | target browsers and Node version, where the site is served, third-party scripts and embeds |
| P1 | [triage.md](triage.md) |
| P2 | entry points: event handlers, `fetch` callbacks, `message` listeners, timers, framework components and routes, build scripts |
| P3 | [The event loop and async](checks.md#p3-the-event-loop-and-async) |
| P4 | [Browsers and Node](checks.md#p4-browsers-and-node) |
| P5 | [Page and component lifetimes](checks.md#p5-page-and-component-lifetimes) |
| P6 | [DOM and browser APIs](checks.md#p6-dom-and-browser-apis) |
| P7 | [Errors and fetch](checks.md#p7-errors-and-fetch) |
| P8 | [Browser storage and caches](checks.md#p8-browser-storage-and-caches) |
| P9 | [Inputs](checks.md#p9-inputs) |
| P10 | [Security](checks.md#p10-security) |
| P11 | [Origins and frames](checks.md#p11-origins-and-frames) |
| P12 | [Performance](checks.md#p12-performance) |
| P13 | [Language traps](checks.md#p13-language-traps) |
| P14 | [Site text and release](checks.md#p14-site-text-and-release) |
| P15 | [Tests](checks.md#p15-tests) |
| P16 | Lessons rows with Pack `core` |
| P18 | a node script, or a headless browser run, in the scratchpad |

## Runtime tags

`Chrome`, `Firefox`, `Safari`, `Mobile`, `Node`, `All`.
