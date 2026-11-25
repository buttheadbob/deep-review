# ESLint and TypeScript triage

Pass P1. deep_tools runs ESLint with `@eslint/js` recommended and typescript-eslint `strictTypeChecked`, from the central `tools\analyzers\deep-eslint.config.mjs`, with pinned linters in `tools\linters\js` that are never fetched at run time. The repository's own ESLint config is never used, and inline configuration is off, so `eslint-disable` comments hide nothing and each one is a suppression issue. `@ts-ignore`, `@ts-expect-error` and `@ts-nocheck` are suppressions too.

Every lead in scope gets a verdict (`real`, `intended`, `not a problem`, `unable to verify`) with the line that proves it. A rule that is wrong for every project is turned off in the central config's "off" block with a `// reason:`, which the tools own.

## Rules that find real defects

Read each lead individually:
- **Promises:** `no-floating-promises`, `no-misused-promises` (an async function where a void callback or a condition is expected), `await-thenable`, `prefer-promise-reject-errors`, `only-throw-error` ([checks.md](checks.md#p3-the-event-loop-and-async)).
- **`any` leaking:** `no-unsafe-assignment`, `no-unsafe-call`, `no-unsafe-member-access`, `no-unsafe-return`, `no-unsafe-argument`. Most start at `JSON.parse` or `response.json()`; the fix is validation at that boundary, not a cast.
- **Strings and numbers:** `restrict-template-expressions`, `restrict-plus-operands`, `no-base-to-string` (an object printed as `[object Object]`).
- **Dead or wrong checks:** `no-unnecessary-condition` (a condition the types say is always true or false: either dead code or a wrong type), `switch-exhaustiveness-check`, `no-constant-condition`, `no-self-compare`, `no-cond-assign`.
- **Plain JavaScript:** `eqeqeq` where configured, `no-fallthrough`, `no-prototype-builtins`, `no-unused-vars` (dead code goes, MasterRules 19), `no-undef`.

## TypeScript settings

- **Type-checked rules need a project:** files outside any `tsconfig` get the untyped rules only. A JavaScript-only site gets far fewer checks, so the review reads it more carefully.
- **Compiler options:** `strict: true`; `noUncheckedIndexedAccess` and `exactOptionalPropertyTypes` catch more. `skipLibCheck` is fine for dependencies. `allowJs` without `checkJs` leaves JavaScript files unchecked.

## Style rules

Naming, ordering and `prefer-*` rules can be grouped by rule, with one verdict per rule and file. Bundler warnings (circular imports, oversized chunks) are leads for P12.
