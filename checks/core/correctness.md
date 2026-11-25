# Correctness details

Pass P13. These are the small defects reviewers find by reading carefully, and the ones a first review skims past. Go through every changed line with these in mind. Then read every unchanged line of each changed member as well: a change can break the line next to it.

Packs add their languages' traps: truthiness, `bool` as `int` and mutable defaults ([python](../../packs/python/checks.md#p13-language-traps)), `$null` comparisons, array unrolling and output pollution ([powershell](../../packs/powershell/checks.md#p13-language-traps)), equality, number precision and dates ([web](../../packs/web/checks.md#p13-language-traps)), and SBC and MES definitions ([se-mod definitions](../../packs/se-mod/definitions.md)).

## Logic

- **`break` versus `continue`:** a `break` on an element-level condition ends the loop for every later element. Real: Sort by Available Cargo PluginHub#190, where `GetTotalAvailableSpace` under-counted after a null inventory slot (L22). (DR027)
- **Inverted conditions and De Morgan mistakes:** `!a || !b` versus `!(a || b)`. A "not found" result treated as "does not have it" (REVIEW.md).
- **Precedence:** `??` versus `?:`, `&` versus `&&`, a cast versus member access, string concatenation with `+` and numbers.
- **Off-by-one:** bounds (`<` versus `<=`), inclusive and exclusive ranges, `Substring` lengths, the last element, empty collections.
- **Edge cases when swapping one API for another:** two calls that look the same can differ at the edges. Real: PAM swapped LINQ `Skip` for `GetRange(_routeIndex, ...)`, which throws where `Skip` returned empty once the index passed the cleared list (L47).
- **Short-circuit side effects:** a call inside a condition that only runs sometimes.
- **Switches:** a missing `default`. A new enum member not handled where every member must be.
- **Enum defaults:** the default `0` of an enum is a real value. A config missing a field gets member 0, which may be the destructive one. Zero meaning "unset" versus "none" is the same trap (L13).
- **Equality:**
  - `Equals`/`GetHashCode` consistency, and reference versus value equality on records and classes.
  - `==` on boxed values.
  - Float equality. Use a tolerance, but never in a comparer ([below](#ordering)).
- **Captured variables:** a `for` loop variable captured by a lambda (all closures see the final value; `foreach` is safe since C# 5). A LINQ query enumerated later, after the state it reads has changed.
- **Mutable structs:** copied out of a collection or readonly field, then mutated. The mutation is lost.
- **Checks that sample instead of covering:** a guard that tests one representative of a group passes when another member is wrong. Real: GridGenerator's guard checked one cell per room, and a block placed in that cell hid an opened room (L49).

## Ordering

- **Comparers must be a strict weak ordering:**
  - Epsilon-based or subtraction-based comparisons (`a - b`, which overflows) are not transitive.
  - `List.Sort` can then throw "IComparer.Compare() method returns inconsistent results", or give a different order on each run.
  - Real: PluginHub#190 (L23). (DR026)
- **Sort stability:** `List.Sort` is unstable, and `OrderBy` is stable. A tie-break that relies on input order needs `OrderBy`/`ThenBy`.
- **Order of side effects:**
  - Remove before add loses the item if the add throws (DR028. Real: PluginHub#187, L44).
  - Delay before the work versus after it (quasar-hub#18, L06).
  - Advancing a step before or after running it, when the step can reset the same state (magnetar-hub#172, Low, L02).
- **"First match" over `Dictionary`/`HashSet`:** the iteration order is not guaranteed, and differs by runtime.
- **First completion wins:** when a button, cancellation, timeout, navigation and host unmount can all finish the same operation, the first recorded result must win unless the contract says otherwise. A later path must not overwrite a result the user already chose. Test both orders. (Real: Quasar PR2's message box discarded a recorded Yes when the token fired during the native-close round trip.)
- **Shorthand after longhand:** a CSS shorthand (`border-color`, `background`, `font`) resets every side of the longhand it replaces. A later shorthand can erase an accent, border or background the component contract depends on; verify the computed style, not only the declaration.

## Numbers and units

- **Integer division and truncation;** `int` overflow in sums, products and tick counts. `checked` where the value comes from users or config.
- **Units:** metres versus kilometres, ticks versus milliseconds versus seconds, kg versus tonnes, bytes versus KiB versus KB, host units versus display units. The game's tick assumptions are in [se-plugin game-api](../../packs/se-plugin/game-api.md#timing).
- **Rounding:** `Math.Round` uses banker's rounding by default on both runtimes. Casting to `int` truncates toward zero.
- **Money:** `float`/`double` for currency loses cents. Use `long` units or `decimal`.
- **Time:**
  - `DateTime.Now` for durations or schedules jumps at DST (DR020).
  - Schedules across midnight, month ends and leap days.
  - `DateTime.Kind` mismatches in comparisons.
  - `TimeSpan.Parse` (DR019).

## Text and behaviour agree

- **Settings, help and docs:**
  - Setting names, tooltips, command help, README lines, changelog entries, manifest and package descriptions and PR text each describe behaviour. Compare each one that the change touches with the code.
  - Real: Atomic.FM's "Mute out of range" only muted when the listener position was unknown (L32). Ore to Auto GPS's description lagged its key-press gate. APBL's log line named both settings when only one was changed (L08).
- **Code comments that the change made false:** report them when they could mislead a maintainer (ReviewPolicy.md).
- **Replies and logs:** these must report what really happened, not what was attempted ([inputs.md](inputs.md)).

## Idempotency and repetition

- **Running twice:** what happens when a command, migration, save, init or script runs twice? Real: APBL's `ReplaceDamage` was checked to be a no-op on its second call.
- **Retries:** must not duplicate side effects such as payments, messages, spawns or posts.
- **Re-entrancy of handlers:** a handler that triggers the event it handles.
- **Remedies that don't end their trigger:** an action taken because a condition holds must end that condition, or it repeats on every check. Real: APBL's Damage punishment left the script running, so it repeated on every PB run (L11).

<a id="dr029"></a>
## Debug-only code (DR029)

- **Never ships:** code under `#if DEBUG`, `#if LOCAL_BUILD` or `#if TRACE` never runs in a release build, and code only in the `#else` branch is never run in a debug build. Say what differs in each region. The hub loaders compile with no `DEBUG` and no `LOCAL_BUILD` ([se-plugin loader-compile](../../packs/se-plugin/loader-compile.md#preprocessor-and-build-settings)).
- **Behaviour must match:** behaviour that differs between the two branches needs testing on the release build ([runtime-differences.md](runtime-differences.md), Preprocessor and build settings).

<a id="dr030"></a>
## Nullable annotations in the loader compile (DR030)

A plugin rule: the hub loaders compile with nullable disabled, so `?` with no `#nullable` directive gives CS8632 in the hub's log, and `!` does nothing there. Full check and fix: [se-plugin loader-compile](../../packs/se-plugin/loader-compile.md#nullable-in-the-loader-compile-dr030). Real case: PluginHub#190 (L21).

<a id="dr031"></a>
## Reflection by name (DR031)

- **Resolve at start-up:** resolve every member at start-up, check BindingFlags against the real member, and fail loudly when it is missing. No `!` on the result.
- See [host-api.md](host-api.md#contracts-you-dont-own) and, for the game, [se-plugin game-api](../../packs/se-plugin/game-api.md#reflection-into-game-privates).

<a id="dr032"></a>
## Entity lookups off the game thread (DR032)

A game rule: `MyEntities.GetEntityById`/`TryGetEntityById` from a background thread, or after an `await`, can return null for entities that exist. Look entities up on the game thread, and pass IDs or snapshots to background work. Full check: [se-plugin game-api](../../packs/se-plugin/game-api.md#threading).
