# Decision Moment `01KZGN2DH6C26YQ7NTXGCN4MT7`

- **Mission:** `midi-export-01KZGM3W`
- **Origin flow:** `plan`
- **Slot key:** `plan.architecture.clock-normalization`
- **Input key:** `clock_normalization`
- **Status:** `resolved`
- **Created:** `2026-08-08T12:20:23.718569+00:00`
- **Resolved:** `2026-08-08T12:20:56.419908+00:00`
- **Opened by:** `cli`
- **Other answer:** `false`

## Question

When the producer audit finds a timing producer on a clock that differs from the final playback/output sample clock, should the plan require correcting that producer at its source, or permit one explicit normalization adapter at the producer boundary while rejecting unsupported ambiguity?

## Options

- Correct each producer at source
- Use one explicit boundary normalizer
- Other

## Final answer

Use one explicit normalization adapter at the producer boundary, while rejecting ambiguous clocks.

## Rationale

_(none)_

## Change log

- `2026-08-08T12:20:23.718569+00:00` — opened
- `2026-08-08T12:20:56.419908+00:00` — resolved (final_answer="Use one explicit normalization adapter at the producer boundary, while rejecting ambiguous clocks.")
