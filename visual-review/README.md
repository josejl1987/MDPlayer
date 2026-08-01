# MDPlayer real-file visual review

Put redistributable review files under `visual-review/corpus`, or keep them in
an external directory and set `MDPLAYER_REVIEW_CORPUS`.

Add an entry and useful review moments to `files.json`:

```json
{
  "files": [
    {
      "path": "spc/example.spc",
      "label": "Example SPC",
      "tags": ["snes"],
      "moments": [
        { "name": "representative", "time": 8.0 },
        { "name": "dense", "time": 31.2 }
      ]
    }
  ]
}
```

Numeric moments are also accepted; they become `moment-1`, `moment-2`, and so
on. The `path` is resolved relative to the corpus root. The directory names
are organizational only; format, backend, chips, and activity come from the
production inspection and captured timeline.

Generate the gallery with:

```bash
mdplayer-render review \
  --manifest visual-review/files.json \
  --corpus visual-review/corpus \
  --output artifacts/visual-review
```

Open `artifacts/visual-review/index.html`. Use `--file`, `--chip`,
`--composition`, `--moment`, and `--resolution` for a smaller local pass.
`--allow-missing-chips` keeps missing registered chip families from producing
exit code 3. A chip is covered only when inspection detects it and captured
timeline data reports activity for it. Synthetic fixtures are deliberately
excluded: this gallery is evidence from real files through the production
capture, planning, scope, and frame-rendering paths.
