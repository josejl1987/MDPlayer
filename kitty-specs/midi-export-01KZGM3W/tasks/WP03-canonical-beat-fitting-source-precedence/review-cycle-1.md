---
affected_files: []
cycle_number: 1
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T15:57:53Z'
reviewer_agent: user
wp_id: WP03
---

P1: FitValidatedTempoSegments only collapses adjacent validated-BPM changes when rounded us/qn integers are identical (previousUs != currentUs). Small jitter such as 120.0/120.1 BPM yields different us/qn values, so every observation becomes a tempo segment. Implement sustained-change filtering per spec Section 17 (tolerance window, not identical-integer collapse) and add a test with near-identical BPMs that must yield ONE segment.

P2: FitValidatedTempoSegments computes residuals but never assigns TimingDiagnostics.MaxResidualQuarters / RmsResidualQuarters for the validated-tempo regions, so RMS reports 0 regardless of jitter/outliers, breaking residual reporting and IsTrustworthy.

P1: The builder forwards tempoChanges whenever source==DriverValidatedTempo (comment requires >=2 transitions). A single validated BPM event at a nonzero sample now creates a pre-event and post-event segment (both same BPM/fallback), treating a lone observation as a transition. Require evidence of an actual transition before segmenting.

P2: The validated-tempo path adds outlier entries to Diagnostics.RejectedAnchors, but IsTrustworthy remains true whenever RmsResidualQuarters<0.25 with no conflict, and Rms is never populated on that path, so StrictTiming accepts severe outliers and the RejectedAnchors trust branch is unreachable. Assign residuals on the validated path so strict mode fails on severe outliers.