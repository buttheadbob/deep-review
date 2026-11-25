# Definitions, framework config and Workshop release

Passes P8, P11, P13 and P14. Much of a mod is not C#: SBC definition files, framework config written into them (MES SpawnGroups and profiles, WeaponCore parts), models, textures and sounds. UD Encounters is mostly MES config with a slim script. Review these files line by line like code.

## SBC files

- **Well-formed XML:** every file parses, with the expected root and `xsi:type` names. A broken file stops the game from loading the mod's definitions.
- **Ids:** every definition's `TypeId` and `SubtypeId` pair is unique within the mod. A pair that matches the game's or another mod's definition overrides it by load order. Say whether that is meant.
- **References resolve:** every model (`.mwm`), icon and texture (`.dds`), sound file (`.xwm`, listed in the audio definitions), blueprint, component and category a definition names exists in the mod or the game. Real case to check against: UD Encounters moved its audio into `Audio\`, so every sound path had to follow.
- **Case in paths:** a dedicated server on Linux (Magnetar Interim) reads paths case-sensitively. A path whose case differs from the file works on Windows only (core runtime-differences).
- **Mod storage keys:** every GUID the scripts use for `Entity.Storage` is declared in `EntityComponents.sbc` ([mod-api.md](mod-api.md#storage-and-config)).
- **Numbers and text agree:** balance values (costs, ranges, timers, damage) match the description, the in-game text and the server's intent.

<a id="definitions-are-save-keys"></a>
## Definitions are save keys (P8)

- **Renamed or removed ids:** worlds store placed blocks and items by `TypeId` and `SubtypeId`. A renamed or removed id leaves existing worlds unable to load what they have. Treat a rename as a migration: keep the old id, or say plainly in the release notes what players lose.
- **Changed components or sizes:** existing blocks keep their old build state; check what happens to a half-built block when its component list changes.

## Framework config

- **MES:** SpawnGroup and profile settings live as tags in definition descriptions. Check every tag name, value type and default against se-dev-mes, every profile and SpawnGroup a tag names exists, and conditions agree with the encounter's intent (spawn types, zones, threat, caps). A misspelled tag doesn't do what it says; find out from se-dev-mes whether MES ignores or logs it, and say which.
- **WeaponCore:** part definitions (`CoreParts`) checked against se-dev-weaponcore: what 0 or an unset field really does, ammo links, and server overrides. Reviews only; no WeaponCore fixes.
- **Which framework:** servers differ (the UD Encounters server is Vanilla+; other mods differ, some on WeaponCore). Ask which framework a mod targets when the files don't say.

<a id="workshop-release"></a>
## Workshop release (P14)

- **The user uploads.** Nothing goes to the Workshop from a session.
- **Metadata:** the mod info file, thumbnail and description match the release. The description says what the mod does by default, what admins must configure, and any data it stores or sends.
- **Dependencies:** framework mods the mod needs are listed as Workshop dependencies (MES 1521905890, Rich HUD Master 1965654081, WeaponCore 3154371364 where used).
- **Licence:** the mod's own terms (CaptureZones is private and paid, all rights reserved, owned by Erebus Alpha), plus the notices of anything embedded (the Rich HUD client's MIT notice).
- **Beta versus live:** a beta folder or item (CaptureZones_Beta) and the live one must not share mod storage keys, channel ids or world storage names unless that is meant.
