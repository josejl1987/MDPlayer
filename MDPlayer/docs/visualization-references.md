# Visualization References

This document records design references for the FMP Visualization 2.0 renderer
(§24 of the implementation specification). No third-party source code has been
vendored into the repository.

## MIDITrail

- **Version inspected:** 1.3.2 (public release)
- **Source:** https://github.com/AzraelK/MIDITrail (BSD-3-Clause)
- **Files/classes inspected (design only):**
  - Active-note flash, whitening, and size enlargement logic
  - Note-on ripple rendering
  - Pitch-bend-following note geometry
  - Channel/pitch-class colour assignment
- **Concepts adopted:**
  - Pitch-bend-following note geometry (continuous ribbon)
  - Active-note whitening (transient white-mix flash)
  - Note-on ripple (expanding rings at the contact point)
  - Pitch-class colouring (12 stable hue assignments)
  - Contact-point activation (playhead as point of musical contact)
- **Code directly ported:** None. All algorithms were reimplemented from
  design observation. The BSD-3-Clause license permits source inspection;
  no MIDITrail source files are committed to this repository.
- **Attribution:** The concept of pitch-bend-following note geometry,
  active-note whitening, and note-on ripples in a piano-roll context is
  inspired by MIDITrail's visual design.

## Kiva

- **Repository:** https://github.com/SayuriForce/Kiva (Don't Be a Dick licence)
- **Concepts adopted (design only):**
  - Dynamic visible pitch range
  - Range animation with hysteresis (delayed contraction)
  - Strong distinction between future, active, and completed notes
- **Code copied:** None. The DBAD licence is incompatible with this project;
  only design observations were used.

## Pianola

- **Licence:** AGPL-3.0 (incompatible — no code or assets used)
- **Concepts adopted (design only):**
  - Smooth critically-damped camera movement
  - Deterministic animation curves based on absolute time
  - Motion independent of previous rendered frames
- **Code copied:** None.

## SeeMusic

- **Licence:** Proprietary (visual design reference only)
- **Concepts adopted (design only):**
  - Sparse note-on light effects
  - Controlled brightness changes
  - Strong note contact
  - Polished title and credits treatment
- **Code/assets copied:** None.

## No vendoring

No complete third-party source trees are committed to this repository.
Local checkouts or downloaded source archives are used outside the repo for
reference only.
