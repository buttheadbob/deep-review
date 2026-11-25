# Accessibility and dynamic UI

Passes P6, P9, P14 and P15. Read with [host-api.md](host-api.md), [inputs.md](inputs.md), [docs-release.md](docs-release.md) and [tests.md](tests.md). Packs add the host's DOM and component model: [dotnet-app](../../packs/dotnet-app/blazor-signalr.md), [web](../../packs/web/checks.md#p6-dom-and-browser-apis).

This file covers the parts of a UI that are not visible in a markup diff: the accessible name, the description, live-region timing, DOM identity, focus and the visual cue that carries the meaning.

## Names, roles and descriptions

- **Every interactive control has an accessible name.** A button needs its visible label, an `aria-label`, or `aria-labelledby`. An icon-only button with no name is a finding.
- **Do not name the same thing twice.** A dialog with `aria-label="Delete backup?"` and a visible `<h2>Delete backup?</h2>` can be announced as the name and then again as a heading. Prefer `aria-labelledby` to the visible heading, or suppress one of the two. Check the same for cards, panels and sections.
- **`aria-describedby` points at a real, non-empty element.** An id that resolves to nothing, an empty element, or an element removed by a later render is a finding. Empty descriptions should not be referenced at all.
- **The description exists when focus enters.** A dialog description rendered only after an async load is not the description the user hears on open. Either render it with the dialog, or observe the content and update `aria-describedby`; document and test the chosen contract.
- **Late content is tested.** If the description can appear, be replaced, become empty, or be removed, test each path. A one-shot `querySelector` at open is not enough for a dialog whose body is rendered by a component.
- **Observation is scoped and cleaned up.** A `MutationObserver` or equivalent is disconnected when the dialog closes or is removed. It observes only what it needs; a subtree observer on a live modeless panel is a per-render cost to check in P12.
- **Live regions match urgency.** `role="status"` is polite; `role="alert"` interrupts. Warning and error notices may use `alert`; success and informational notices use `status`. Do not use `alert` for content that is present at page load unless it genuinely needs immediate announcement.
- **`aria-atomic` is intentional.** A notice that announces only changed text needs the right atomic value; a whole-message announcement needs `true`.
- **Accessible names and descriptions survive re-renders.** A Blazor or framework re-render that replaces the element must not leave `aria-describedby` pointing at the old node.

## DOM identity and attribute ownership

- **Generated ids are unique.** A host that assigns an id must check the document for a collision. A counter alone is not enough when the page can already contain a matching id.
- **Caller-supplied ids are preserved.** Do not reassign an id that the caller owns; that breaks the caller's own `aria-*` references.
- **Host-owned attributes are explicit.** A reusable component that forwards `AdditionalAttributes` must keep `class`, `style`, `role` and `aria-atomic` under component control when those carry the component's contract. Document which attributes are caller-owned.
- **A namespace is part of the API.** A public component should not share a namespace with host-internal components; a future same-named type becomes ambiguous for every consumer.

## Keyboard, focus and dismissal

- **Keyboard operation:** every action reachable by pointer is reachable by keyboard, with a visible focus indicator.
- **Focus return:** closing a dialog returns focus to the control that opened it. A dialog that moves focus but never restores it is a finding.
- **Escape and backdrop:** the dismissal policy matches the contract, and a disabled dismissal does not trap the user without a visible close path.
- **Focus trap:** a modal keeps focus inside; a modeless panel does not steal focus from the page.
- **Disabled and busy states:** a disabled control is not focusable or is announced as disabled; a busy control exposes its state.

## Visual cues

- **Meaning is not carried by colour alone.** Severity needs text, an icon, a label, or another non-colour cue. A border accent that disappears under a variant is a regression when it was the only severity cue.
- **Computed style is the contract.** A class name in markup is not proof that the visual rule applied. A browser test reads the computed property (`border-left-color`, `white-space`, `background`) for the states the change claims.
- **Shorthand order:** `border-color`, `background`, `font` and similar shorthands reset longhands. A shorthand added after a specific border or background can silently erase the accent. Check the computed value, not only the declaration.
- **Plain text keeps its shape.** A plain-text message rendered in normal white-space collapses newlines. Use `pre-line` or `pre-wrap`, or state that the value is single-line; test a multi-line value.
- **Contrast and size:** text, focus rings and severity colours meet the project's contrast rules at the shipped sizes.

## Tests

- **Assert the accessible contract.** Check the accessible name, `role`, `aria-describedby` target and its text, not only that a class or element exists.
- **Test the dynamic paths:** description absent, inserted, replaced, emptied and removed; message empty and multi-line; duplicate id; caller-supplied id; caller-supplied `aria-describedby`.
- **Use stable locators.** A browser test must identify the target by a stable attribute (title, id, test id), not a positional `.first()` that can resolve to a previous dialog or a stale element.
- **Test the visual contract in a browser.** A bUnit or unit test can prove markup; only a browser can prove `white-space`, computed border colours and layout.
- **Test keyboard and focus return.** A pointer-only test does not prove the dialog is usable.

## How to confirm

- **Read the rendered DOM.** For a dialog or notice, write down the accessible name, the description, the role, and which element owns each id.
- **Run the late-content path.** Open the component, then insert or replace the description and check `aria-describedby` again.
- **Run the visual path.** Read the computed style in a headless browser for each variant and severity.
- **Run the keyboard path.** Tab to the control, activate it, close the dialog, and confirm focus returns.
- **State what is not tested.** No screen reader, no Firefox or Safari, or no mobile run is a named gap, not a pass.
