# Authority, trust boundaries and interop

Pass P11. Two questions for every changed feature: who is allowed to cause it, and who else touches the same state. The core asks them for any project. Packs add their own forms:
- multiplayer fairness and cross-plugin interop: [se-plugin multiplayer](../../packs/se-plugin/multiplayer.md), [harmony](../../packs/se-plugin/harmony.md);
- mod messages and other mods: [se-mod](../../packs/se-mod/mod-api.md#networking);
- IGC from other grids: [pb-script](../../packs/pb-script/checks.md#p9-arguments-and-igc);
- authorization, IDOR and per-user data: [dotnet-app web-security](../../packs/dotnet-app/web-security.md#authorization);
- callers on other threads and other versions: [dotnet-library](../../packs/dotnet-library/api-design.md#thread-safety-and-callbacks);
- other processes and sessions: [python](../../packs/python/checks.md#p11-other-processes-and-sessions), [powershell](../../packs/powershell/checks.md#p11-other-sessions-and-machine-state);
- other origins and frames: [web](../../packs/web/checks.md#p11-origins-and-frames).

## Authority

- **The enforcing check:** for each changed feature, name the check on the trusted side that enforces it. A client-side gate, a hidden button or a disabled control is not enforcement, because a modified client, a direct request or a script bypasses it.
- **Identity from the transport:** the caller's identity comes from what the transport proves (the network layer's sender ID, an authenticated session, a verified signature), never from a field in the payload.
- **Privilege flows down:** a lower level never gains a higher level's rights through the feature (MasterRules 23). Trace every path where a less trusted input reaches code that runs with more rights: an automated step, a macro, a helper running with the user's token.
- **Confused deputy:** a privileged component acting for a less trusted one. Real form here: a review or test tool runs with the user's credentials against a repository it doesn't trust, so nothing in that repository may choose what runs, where it writes, or what is sent. The GitHub token stays out of copies, and goes to children through an environment variable (MasterRules 30a).
- **Gates that must not be bypassed:** where a component is a gate (the lease guard, the push guard, the analyser gate, a consumer check), every route around it is a finding: a skip flag that still passes, a record field that can be forged, a cached result, a second code path that writes the protected file. The gate never passes on something it did not check.

## Information

- **What a caller learns:** compare with what they could learn without the feature. Other users' data, positions, inventories, admin data, file contents, other tenants' records, and details in error text or timing.
- **Data separation:** per-user state kept in a shared place (a singleton, a static, a cache keyed without the user) leaks between users ([dotnet-app hosting-di](../../packs/dotnet-app/hosting-di.md#di-lifetimes)).
- **Messages to others:** chat, notifications, Discord posts, logs other people read and pages must not leak other users' data, and must be rate limited ([errors.md](errors.md#checks), error spam).

## Interop

- **Others on the same state:** other plugins patching the same methods or using the same UI area, other mods with the same ids, other scripts on the same channel, other processes writing the same file, two instances of the same app. Trace each interaction. Real: Sort by Available Cargo PluginHub#190 called private methods that Unified Storage's patches didn't cover (L20).
- **Shared names:** ports, named pipes, mutex names, temp file names, container names, channel ids, environment variables and registry keys. A fixed name collides with another copy or another program.
- **Contracts between parts released separately:** a companion protocol (Quasar UI and its Magnetar companion), an MCP tool schema, a record format read by other sessions (gate.json, deep-review.json, stage1.json), a network message between client and server versions. Old and new versions meet in the field: the new side must accept the old, or refuse it clearly. Consumers ignore fields they don't know; producers never rename or remove one without a new schema version.
- **Ambiguous cases:** ask a short, concrete question (ReviewPolicy.md for plugins). Never present suspicion as a finding.

## How to confirm

- **Authority:** for each feature, write the path from the untrusted input to the enforcing check, with `file:line` for each step. Where there is none, try the bypass in a harness or scenario: a direct request, a forged payload field, a modified client message.
- **Interop:** search for the other parties (the hub's plugin sources with the se-dev-plugin skill, other mods, other scripts in the user's projects) and read their side.
