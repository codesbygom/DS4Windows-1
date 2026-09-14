# Pull request review — 2026-09-14

Scope: integrate PR #86 with current runtime behavior, then review all open PRs,
retaining useful work from `anagnorisis2peripeteia` and closing stale or superseded
proposals with evidence. No released tag or asset is replaced by this source work.

## Review decisions (not a record of completed GitHub actions)

| PR | Assessment |
| --- | --- |
| #86 | Useful DSX mod compatibility feature; original patch requires protocol, lifecycle, reversible output ownership, and settings corrections before integration. |
| #41 | Retain the authored non-default AppSettings deserialization test, without merging unrelated history from its stale branch. |
| #42 | Production invariant-date parsing/formatting is already superseded by stronger current code. Retain the remaining culture-independent expected-date assertion. |
| #43 | Retain macro value-band, boundary, idempotence, and output-name tests; assert the expected count too. |
| #44 | Do not merge: removed gyro fields are now used by reference in active mouse mapping. |
| #45 | Retain profile migration v1–v5 coverage. |
| #47 | Superseded by current independent RGB channel parsing and invalid/missing/precedence tests. |
| #48 | Do not merge stale instructions that exclude repaired serialization tests or claim CI x86 coverage not present in the current workflow. |
| #49 | Superseded by current shifted-extras serialization and stronger extras-only activation/release handling. Its proposed save fixture omits the required positive shift trigger. |
| #7 / #8 | Do not blindly upgrade MSTest to v4: the repository still uses hundreds of `Assert.ThrowsException` calls removed by v4. Framework-only upgrading also mismatches the adapter. Requires coordinated migration. |
| #10 | System.Management 10.0.8 supports net8, but changes the actual WMI runtime and System.CodeDom dependency. Requires application and installer-helper WMI validation; not merely a version edit. |
| #11 | System.Memory 4.6.3 is a plausible low-risk dependency update; still requires restore/build/test validation before integration. |
| #70 | Preserve native UdeCx experimental work; do not merge into production. It targets `viiper-ui-rebuild`, carries local-test-only metadata, and requires protected production driver signing/package evidence. |

The source review of #70 covers production packaging/runtime boundaries, not a
complete security audit of the experimental driver.

## Primary references

- [DSX protocol example](https://github.com/Paliverse/DSX/tree/1614f003d7f00fa501e789c16eacb219993c1c16/Mod%20System%20%28DSX%20v3%29)
- [MSTest v3 → v4 migration](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-migration-v3-v4)
- [System.Management 10.0.8 package](https://www.nuget.org/packages/System.Management/10.0.8)
- [System.Memory 4.6.3 package](https://www.nuget.org/packages/System.Memory/4.6.3)

## PR #86 integration corrections

- Correct wire trigger IDs, player LED enumeration, mic pulse, RGB brightness,
  and frequency-first multi-position vibration layout.
- Strict bounded complete-packet validation; reject invalid indices/parameters
  rather than targeting slot 0, coercing values, or inventing effects.
- One bounded response per accepted datagram, including mutation-only packets.
- Session-owned local sockets, generation-safe callback admission, and
  reset/stop/disconnect cleanup without profile mutation.
- Per-field physical output overlays and per-trigger Trigger Lab priority.
  Preserve exact native effect bytes for later restoration.
- Default off, loopback-only, editable port, retry after bind failure, and actual
  listener status distinct from the saved preference.
- Remove the unrelated obsolete VIIPER 0.1.2 checksum. Replace the original
  automatic strong-effect script with status-only default and explicit bounded
  effect testing that requests reset in `finally`.

Hardware mod acceptance remains separate from automated parser, loopback,
ownership, FIFO, and canonical output-composition tests. No physical effect
test, live controller restart, installer action, or reboot is part of this audit.

Combined focused validation: **517 passed, zero failed**, x64 Release net8.0.
This includes ephemeral loopback traffic, official legacy RGB/custom
PulseAB/null-padding packets, exact wire vectors, malformed-packet rejection,
stop/restart/reentrant callbacks, occupied-port retry, unexpected listener exit,
actual service routing with fake controllers, replacement ownership, native
admission, USB/BT composition, reset ordering, and Trigger Lab priority.

Review also corrected repeated trigger strobes, stale local trigger proof after
native/reset intervention, and early publication of unadmitted native Trigger
Lab bytes. Exact native commands remain ordered; no new input mapping stack,
unbounded output queue, or physical writer was added.

The unchanged warmed native-shadow allocation assertion measured **zero bytes
over 6,000 observations**, after fixing value-type equality that initially boxed
256 bytes per report. The existing deliberate-allocation positive control also
passed. This is not a hardware effect-quality or end-to-end latency result.

Full x64 Release suite: **6,350 passed, zero failed, 11 gated skips** (6,361
total). The first full run identified a source-contract test still searching for
the old `void` return signature; its markers were updated to `byte`, preserving
all fixed-scratch/no-per-report-allocation assertions. The entire suite was then
rerun successfully. Evidence: `_results/pr86-full-final/pr86-full-final-rerun.trx`.
The installed-cache fixture used a read-only protected bundle snapshot; no setup
or controller operation ran on this PC.
