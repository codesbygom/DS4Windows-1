# Release Candidate 4.6.2 — Game Mods, Safer Updates & Startup

RC4.6.2 adds optional DSX game-mod support for DualSense triggers and lights, fixes custom-named portable updates, and improves startup recovery and shifted profile settings.

## Let supported game mods control triggers and lights

- New **Settings → Game mod support (DSX)** option for physical **DualSense and DualSense Edge** controllers. It starts **off**; enable it only when using a compatible mod.
- Local mods can control adaptive triggers, lightbar, player lights and the microphone light through the DSX UDP protocol. The default address is **127.0.0.1:6969**; connection details and **Apply / Retry** make a busy port easier to resolve.
- **Trigger Lab takes priority on each enabled trigger.** A mod can control one trigger without blocking the other, and temporary mod settings do not overwrite your profile.
- Resetting the mod, disabling support, stopping DS4Windows or removing the controller releases its overrides and restores the underlying state.
- Native trigger commands retain their order and exact effect data. Repeated trigger-command flags, stale duplicate tracking after a reset, and old callbacks affecting replacement controllers are covered by new regression tests.

This is **local trigger/light compatibility**, not full DSX replacement or audio/PCM haptics support. Some legacy effects and input-threshold instructions remain unsupported rather than sending guessed commands. Mods should send reset when exiting; disabling the option also releases a held override. See the [setup and compatibility guide](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.2/docs/DSX-game-mod-support.md).

## Portable updates keep your custom name (#99)

- **DS4Updater 2.0.7** preserves the selected application EXE name without adding another canonical `DS4Windows.exe` to a compatible target installation.
- Required DLLs and runtime files retain their correct names. An old default EXE is removed only when the package owns it; unrelated files and profiles are preserved.
- Renaming, clearing a saved name, rollback, and protected setup staging use matching application identities. Unsafe collisions stop before file replacement.
- The new custom-only layout requires **RC4.6.2 / Windows version 5.0.8.0 or newer**. The updater checks the verified target version; it is not locked to one release tag.
- RC4.6.2 requests updater **2.0.7 or newer**, including future verified versions. Portable users should always extract the complete ZIP when updating manually.

## Clearer startup recovery (#77)

- Keep your requested startup preference separate from tasks temporarily disabled while setup needs repair or a reboot.
- Show clearer requested-versus-active startup status and the appropriate next action.
- Stage a protected, narrowly scoped continuation for the next logon after a required reboot, with account, boot, package and hash checks before continuing setup.
- Preserve the original **RunDS4Windows** and **RunVIIPER** task names and explicit startup opt-outs. Driver and broker readiness checks remain required.

Windows can still deny permissions or require manual repair. Automated recovery tests pass; a complete physical reboot/logon/UAC acceptance test for this change remains outstanding.

## Profile reliability and regression coverage (#46)

- Extras-only shifted bindings keep their activation button through save/load and repeated saves.
- Shift extras can activate without replacing the button's normal action, and release correctly when the source or modifier is released.
- Add non-default settings, culture-independent date, macro value-band and historical profile-migration tests.
- Thanks to **Gabarsolon** for the DSX feature contribution and **anagnorisis2peripeteia / Cameron Beeley** for settings, macro and migration contributions.

## Packages and validation

- Tag: **VIIPERRC4.6.2**. Windows application/MSI/installer version: **5.0.8.0**, advanced for correct upgrade ordering.
- Complete offline x64 installer and portable ZIP, including unchanged **VIIPER 0.1.5-rc4.6**, **USB/IP 0.9.7.7**, the required Xbox output identity, matching sources and notices. No separate broker upgrade is needed.
- Companion updater: **DS4Updater 2.0.7**. Earlier release assets remain unchanged.
- The release candidate passed **6,367 tests with zero failures**, with 11 explicitly gated skips. The warmed native-shadow check measures **zero allocations**; the allocation assertion was not relaxed.
- This is an **unsigned release candidate**. Automated tests, hosted MSI lifecycle checks and package verification do not replace physical controller/game-mod acceptance. Nintendo rumble gain/cadence and PCM audio encoding are not retuned in this release.

Technical evidence: [PR review](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.2/docs/validation/2026-09-14-pull-request-review.md) and [startup, updater and profile fixes](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.2/docs/validation/2026-09-14-issues-99-77-46.md).
