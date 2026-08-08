---
affected_files: []
cycle_number: 3
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T16:23:57Z'
reviewer_agent: user
wp_id: WP03
---

P1: Require transition evidence before splitting on a lone BPM observation. In BeatGridFitter.cs:332-336 the collapse guard only merges later segments whose BPM is within 2% of the pre-change rate. With authoritative anchors proving constant 120 BPM and only one mid-source DriverTimingEvent reporting 150 BPM, FitValidatedTempoSegments still emits a 120/150 pre/post map even though that lone observation is not evidence of a sustained transition; non-strict exports map later samples at the wrong rate, and strict mode rejects the otherwise usable anchor grid. This bypasses Section 10 anchors-first precedence. Fix: require actual transition evidence (e.g. >=2 distinct sustained validated values, or consistency with anchor-derived rate), or retain the anchor-derived constant grid when anchors already establish the rate and the lone BPM conflicts with them.

P2: Warn when validated tempo has no phase evidence. The validated-tempo path (BeatGridFitter.cs:274-282) creates a grid for a timeline with no beat anchors but does not add the existing 'tempo known but beat phase unknown' warning. MusicalTimeMapBuilder only sets PhaseUnknown (line 98); the CLI/timing-report surface Diagnostics.Warnings, so a non-strict driver-tempo export proceeds with an arbitrary sample-zero phase while reporting no warning. Fix: add the phase-unknown warning to Diagnostics.Warnings on this path so the CLI and timing report surface it.