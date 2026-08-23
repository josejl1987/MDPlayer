# MIDI adversarial corpus

`adversarial-manifest.json` is the sidecar contract for semantic MIDI export
checks. The source files are repository fixtures copied into the test output by
the test project; the manifest does not duplicate binary media.

The `featuresToReview` labels are review prompts, not asserted facts. A corpus
entry may only move from `pending` to `reviewed` after a human has inspected the
source timeline and filled `expected.tempo`, `expected.meter`, and the downbeat
annotation. Null expectations intentionally keep the harness from turning an
unverified guess into a golden test.

Corpus checks must compare semantics: channel ownership, pitch reconstruction,
source-time round trips, meter/tempo resolution state, and explicit abstention.
They must not compare complete MIDI byte streams.
