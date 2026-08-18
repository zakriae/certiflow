# ADR-0002 — Confidence is computed from deterministic checks, not reported by the model

**Status:** Accepted · **Date:** 2026-08-18 · **Answers:** SRS §19 Q3, Q4

## Context

The pipeline reads an expiry date off a certificate and something has to decide whether a human needs
to look at it. The cheapest option is to ask the model: *"return the value and your confidence."*

Model self-reported confidence is not calibrated. A model will say `0.95` about a date it invented,
because the token sequence "0.95" is as generatable as the date was. Gating a compliance decision on
it means an auditor eventually finds an approved certificate whose expiry date does not appear
anywhere in the document, and the only available explanation is "the model was sure".

## Decision

**The model is never asked how confident it is.** `FieldCandidate` has no confidence field, so there
is nowhere to put the answer.

Instead the model is asked for a value **and the verbatim text it read that value from**. Confidence
is then computed from five deterministic checks (`ConfidenceCalculator`):

| Signal | Weight | Check |
|---|---|---|
| Grounding | 0.40 | The cited snippet is located in the parsed source text |
| Type validity | 0.20 | The value parses to its declared type |
| Cross-field consistency | 0.20 | Expiry after issue, plausible span, standard matches the requirement |
| Entity match | 0.15 | Holder matches the supplier; issuer is accepted |
| Model agreement | 0.05 | An optional second pass at temperature 0 returned the same value |

Two properties matter more than the weights:

**Grounding is a veto, not a weight.** A field whose citation cannot be found scores **0**, not 0.60.
Losing only 40% would still let a fabricated value clear a 0.85 bar if the other checks passed — and
"the date is well-formed, internally consistent and belongs to the right company" is worthless when
the date is not in the document.

**Unevaluated signals are renormalised away, not counted as failures.** Model agreement is optional;
if it never runs, a perfect field still scores 1.00. A check that quietly costs 5% for not existing
shows up only as unexplained review volume.

Scores round **toward zero** — 0.8499 becomes 0.84, never 0.85. In a compliance product the rounding
error has to fall on the side of asking a human.

Matching for grounding is exact after normalisation, never fuzzy. The guarantee is "this text is in
the document"; "something 85% like this text is in the document" is a different and much weaker
guarantee.

## Consequences

**Good.** A hallucinated field is caught by construction rather than by luck, and the mechanism is
explainable in two sentences to a non-technical buyer. Every score carries the individual check
outcomes, so an amber 0.62 explains itself on the review screen instead of being an unexplained
number. The whole thing is pure, so the hardest part of the product is covered by unit tests that
touch no PDF, no model and no network.

**Bad.** Normalisation carries real weight: a PDF ligature, a non-breaking space or a curly apostrophe
would each cause a false grounding failure, so `TextNormalizer` has to handle them and is tested
against exactly those cases. Grounding also depends on the parsed text layer being faithful — for
scans, OCR quality becomes a confidence input, which is why `TextSource` is recorded alongside.

**Cost.** Asking for citations increases output tokens per document. Accepted: guardrail G4 caps
document size and pages, and `gpt-4o-mini` is the model precisely because this is a constrained task.

## Alternatives rejected

- **Trust the model's self-reported confidence.** Uncalibrated; see Context.
- **Second model as judge.** Adds cost and latency, and moves the trust problem rather than solving
  it — the judge is uncalibrated too.
- **Fuzzy grounding match.** Would reduce false negatives but weakens the guarantee to the point where
  it is no longer worth stating.
