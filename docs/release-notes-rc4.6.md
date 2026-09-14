# Release Candidate 4.6 Hotfix — Clearer Audio, Reliable Feedback & Disconnects

RC4.6 brings together the fixes since RC4.5.9: more reliable controller audio, safer feedback delivery under load, and cleaner Bluetooth disconnects. The faster DualSense feedback path from RC4.5.9 stays in place.

## Controller audio that starts and stays on the right source

- **DualSense speaker recovery:** restarting or switching audio sources no longer stalls speaker warmup because an earlier session's counters were reused.
- **Sonar compatibility:** physical DualSense USB and Bluetooth capture use the established Sonar-compatible endpoint capture policy, including audio from an emulated DualShock 4 headset.
- **Keep the source quality:** app and external-endpoint capture for DualSense uses 48 kHz floating-point audio. The virtual DualShock 4 headset endpoint remains at its native 32 kHz; physical DualShock 4 Bluetooth audio keeps its existing codec and rate.
- **Correct USB playback:** different source rates are converted to the physical DualSense output rate instead of being copied frame-for-frame. Large callbacks are no longer truncated, and small callbacks are processed without waiting to fill an extra block.
- **Safer source changes:** old callbacks and buffered samples cannot leak into a replacement source. Capture shutdown and shared-app capture retirement no longer hold the manager lock while waiting on callbacks.

## Feedback delivery beyond DualSense

- **Physical DualShock 4:** fresh motor changes and explicit stops can pass immediately during Bluetooth audio instead of waiting behind the unchanged-state maintenance interval.
- **Physical DualShock 3:** a failed output write remains pending for retry; a newer stop can replace the failed active effect.
- **Switch 2 Pro and Joy-Con 2:** eligible identical commands at an unclaimed pending queue tail can share an entry. Changed effects and stops, A-to-B-to-A sequences, PCM and timed feedback are not folded together. This is a delivery change, not a rumble gain, encoding or cadence retune.
- **Virtual DualShock 4 / VIIPER:** the V3 speaker-output path reserves room for a motor-stop command, preserves ordered changes, and reports a full distinct-command queue instead of silently accepting and dropping its contents. Reconnecting feedback readers cannot let an old connection retire the new owner's state.

## Disconnect and profile safeguards

- **No stale controller row after manual Bluetooth disconnect:** the controller-list and tray commands now request complete removal, rather than depending on a later HID read failure after transport cleanup has already finished.
- **Reject stale Game Bar activation:** delayed compatibility activation rechecks the current profile policy before allocating or rerouting output. Switching to Xbox output, disabling compatibility or selecting input-only mode prevents an old activation request from taking over.

## Packages and updating

- DS4Windows **VIIPERRC4.6**, Windows file/installer version **5.0.6.0**.
- Bundled **VIIPER 0.1.5-rc4.6**, with matching source and notices. USB/IP remains **0.9.7.7**.
- Complete offline installer and portable ZIP, including VIIPER and the required Xbox output identity. Portable users should extract the entire folder.
- The published **DS4Updater 2.0.6** remains compatible; no replacement updater is needed for this version.

## Validation and limits

This is an **unsigned release candidate**. The rebuilt local x64 suite passed **5,717 tests with zero failures**, including eight isolated DS4Windows-to-VIIPER process tests; three live-audio tests remain explicitly gated. Regression coverage exercises speaker priming, sample-rate continuity and channel placement, source retirement, feedback order and stop admission, reconnect ownership, manual removal, and Game Bar activation policy. Allocation-sensitive assertions remain enabled; a traced GC-counter measurement issue was corrected in the test harness without raising the zero-byte limit or changing production behavior.

Software tests are not a promise of unlimited controller output rates or zero physical latency. Final audio listening, disconnect behavior and gameplay feel still need tester confirmation. The rare once-in-a-day movement interruption remains unattributed; the Game Bar safeguard is not being presented as proof that incident is solved. Genuine disconnect and shutdown neutral-report safety remains intact.

Detailed source evidence: [cross-controller feedback](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/validation/2026-09-12-cross-controller-feedback.md), [audio and Sonar](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/validation/2026-09-12-sonar-ds4-dualsense-audio.md), [manual disconnect](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/validation/2026-09-14-dualsense-manual-disconnect.md), and [rare input investigation](https://github.com/hbashton/DS4Windows/blob/VIIPERRC4.6/docs/validation/2026-09-14-rare-input-neutral-investigation.md).
