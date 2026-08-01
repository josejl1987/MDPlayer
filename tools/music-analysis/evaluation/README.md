# Symbolic-analysis evaluation corpus

This directory contains small synthetic expectations. No commercial audio or
binary music fixture belongs in the repository.

Each annotation records accepted equivalents and whether a result must be
withheld. Evaluation reports should calculate:

- key top-1/top-3 accuracy, withholding correctness, and strong-label
  precision;
- harmony root/quality accuracy, acceptable-equivalent accuracy,
  segment-boundary tolerance, withholding correctness, and strong-label
  precision;
- motif recurrence precision, loop-duplicate suppression, and trivial-pattern
  rejection;
- relationship type, span overlap, and false-positive rate.

The release gates are precision-first: 90% for strong key/harmony/Roman and
relationship labels, and 85% for exact/transposed motif labels. Local corpus
material belongs in `evaluation/local/` and is excluded from Git.
