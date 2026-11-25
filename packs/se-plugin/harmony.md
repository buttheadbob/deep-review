# Harmony and patching

Passes P5, P6, P10 and P11. A patch changes code the plugin doesn't own, in a process every other plugin shares. These checks were spread over the old lifecycle, game API, security and multiplayer files; they are gathered here. Torch plugins may patch through Torch's own `PatchManager` instead of Harmony directly: read its unpatch and ordering rules with se-dev-torch, and apply the same checks.

## Lifecycle (P5)

- **Unpatch exactly what this plugin patched:** `UnpatchSelf`, or unpatch each original with this plugin's id. Never `UnpatchAll()`, with or without an id. (DR013. Real: DLSS PluginHub#177 called `harmony.UnpatchAll(Name)` in `Dispose`, L42.)
- **Ids:** Harmony ids must be unique and namespaced (DR013 flags an id with no `.`).
- **All or nothing:** patches are applied at Init with failure handling. When one patch fails, the plugin either unpatches the rest and turns the feature off, or says plainly which feature is off. Half the patches applied is a state nobody tested.
- **`PatchAll` with no unpatch:** a `PatchAll` with no `Unpatch*` on unload leaves the patches in place after a plugin reload, so the next load patches twice.

## Binding to the game (P6)

- **Parameter names:** Harmony binds patch parameters by name, so a parameter rename in a game update breaks the binding silently. The same holds for injected fields (`___fieldName`) and `__instance`/`__result`. Decompile the target and compare.
- **Overloads:** a target picked by name alone becomes ambiguous when a game update adds an overload, and patching throws at Init. Give argument types.
- **Transpilers:** an IL pattern that no longer matches after an update must fail closed (no patch, a clear log line), never emit a half-changed method.
- **Prefixes that return false** skip the original method and its side effects. Say which side effects are lost, as for any workaround ([game-api.md](game-api.md#reflection-into-game-privates), Workarounds).
- **Finalizers** that swallow exceptions hide the game's own failures. Each one needs a reason and a log.
- **Redundant patches:** a patch the game has since made redundant should be removed. Decompile the patched method and its callers and compare field by field (REVIEW.md).
- **Unconditional patches:** a patch that only matters on some hardware or setups (a GPU vendor, an optional dependency) is applied only there, and fails closed when its assumptions don't hold. Real: DLSS PluginHub#177 patched on every GPU and relied on undocumented driver internals (L40).

<a id="patches-on-checks"></a>
## Patches on checks (P10)

Patches that change permissions, ownership, safe zones or damage are reviewed as authority changes ([multiplayer.md](multiplayer.md)). Name what the original check enforced, and what the patched version lets through.

<a id="other-plugins-on-the-same-target"></a>
## Other plugins on the same target (P11)

- **Ordering:** patches must not assume they run first or last. Check for other plugins' prefixes on the same target.
- **Private calls around other patches:** calling private game methods by reflection can skip another plugin's patch on the public path. Real: Sort by Available Cargo PluginHub#190 called private methods that Unified Storage's prefixes didn't cover, which corrupted the unified view (L20).
- **Search the hub:** for each changed Harmony target, private method call or UI injection point, search the hub for plugins touching the same members (se-dev-plugin skill: download and search plugin sources). Trace the interaction ([multiplayer.md](multiplayer.md), Cross-plugin interop).

## How to confirm

- **Decompile every target** at the game version the hub serves, with se-dev-server-code or se-dev-game-code, and quote the signature you bind to.
- **Reload:** a PluginCheck scenario that loads, unloads and loads again shows doubled patches in the log.
