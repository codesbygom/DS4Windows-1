# Release Candidate 4.6.1 Hotfix — Dial In Your Aim, Safer HidHide

RC4.6.1 adds a simple way to dial in flick-stick aiming, cleans up trigger settings, prevents the startup-task failures reported in #98 and #103 from aborting setup, and protects against the confirmed HidHide configuration-client crash caused by Windows app-execution aliases.

## Dial in a full 360° turn

- Open **Axis Config → LS or RS → Output Mode: Flick Stick** and choose a **360° test button** beside Real World Calibration.
- Save the profile, face a recognizable landmark in-game, and tap your chosen button. Increase Real World Calibration if the turn falls short; decrease it if it goes too far.
- Each tap requests one approximately one-second turn. Holding the button does not repeat, and extra taps during a turn do not build a queue.
- The test button's normal mapping is reserved while configured. Choose **Not assigned** when finished to restore it; your original binding is never deleted. A trigger assignment reserves both its soft and full-pull outputs.
- Changes to the profile, stick mode, assigned button, or controller connection cancel unfinished turns. Flick Stick also safely handles a controller disappearing during mapping.

This is a manual calibration aid for games that accept **mouse look**, not automatic camera tracking. Use a spare button and keep other aiming inputs still for a repeatable result. The setting is in **Axis Config**, not button remapping or Special Actions.

## Cleaner trigger settings

- Removed the legacy **Trigger Effect**, **Trigger Start**, and **Trigger Strength** controls from Axis Config. **Trigger Lab** remains the place to configure adaptive effects.
- Normal trigger dead zones, curves and sensitivity remain available. Existing saved trigger effects, Trigger Lab restoration and native game-provided feedback are preserved.

## Safer HidHide configuration

- Prevent DS4Windows from registering Windows app-execution aliases that can crash HidHide's Applications list.
- Check the configuration before opening HidHide through DS4Windows, with clear feedback when it cannot be read safely.
- If unsafe aliases are found, offer a **confirmation-based, backed-up repair** that removes only those entries. Other applications, hidden-device rules and hiding settings are preserved. Unsafe or changed configurations block the repair instead of guessing.

This addresses the confirmed alias-related crash, not every possible HidHide failure. An external tool can still add an unsafe entry; launch HidHide through DS4Windows to use the guard.

## Startup-task recovery (#98 and #103)

- Setup keeps the original **RunVIIPER** and **RunDS4Windows** names. Existing definitions at those two reserved root names are backed up before replacement, including manually created or incomplete tasks. No alternate task names are introduced.
- Registration, verification or Task Scheduler access failures produce a startup warning instead of canceling installation. Setup can start the verified executables directly; automatic logon startup may need a later Repair.
- Verification diagnostics identify the mismatched fields instead of reporting only a generic failure. The #98 log proves verification failed, but does not contain the task definition needed to establish which field failed on that PC.
- Launch and uninstall still check the task's contents. An unsuccessful repair never authorizes running an arbitrary task just because its name matches.

Backups are stored under **%ProgramData%\\DS4Windows\\Installer\\task-backups**. Tasks outside the two reserved root names are untouched. Windows can still refuse registration; this release makes that failure non-fatal, not invisible. Driver integrity, pending-reboot, USB/IP compatibility and VIIPER API checks remain required.

## Packages and updates

- Release/update tag: **VIIPERRC4.6.1**. Windows application, MSI and installer version: **5.0.7.0**, advanced for reliable upgrade ordering.
- Complete offline x64 installer and portable ZIP, including **VIIPER 0.1.5-rc4.6**, the required Xbox output identity, and matching source/notices. Portable users should extract the entire folder.
- **DS4Updater 2.0.6** remains compatible; no separate updater or broker upgrade is required for this hotfix. USB/IP remains **0.9.7.7**.
- Audio, haptics, adaptive-trigger transport, and Nintendo rumble tuning are unchanged from RC4.6.

## Validation

This is an **unsigned release candidate**. The complete local x64 suite passed **5,991 tests with zero failures**, with 11 explicitly gated tests skipped. Calibration uses the canonical mapper and recording test outputs; no input is injected into the desktop by the tests. Calibration light/dark UI layouts were inspected at normal and 150% scale. Existing profile, trigger restoration, native feedback and Nintendo regression checks remain enabled.

In-game calibration still depends on mouse sensitivity and acceleration. Hardware/gameplay acceptance is not replaced by automated tests, and this release makes no new claim about the previously unattributed rare movement interruption or issue #81 gameplay acceptance.

Source evidence: [calibration and trigger UI](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/validation/2026-09-14-axis-calibration-and-trigger-ui.md), [calibration guide](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/flick-stick-calibration.md), [startup-task collision](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/validation/2026-09-14-issue103-startup-task-collision.md), and [HidHide safeguard](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6.1/docs/validation/2026-09-14-hidhide-client-alias-guard.md).
