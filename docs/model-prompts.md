# Workflow prompts

[Usage](usage.md) · [中文](model-prompts.zh-CN.md)

Recognition uses four short stage prompts in [WorkflowPrompts.cs](../src/OhMyStudy.Infrastructure/Grading/WorkflowPrompts.cs): layout classification, splitting, solving, and assessment. JSON Schema is sent through the API output-format field (`text.format` for Responses, `output_config.format` for Messages) rather than embedded in these prompts. Direct DeepSeek classification and splitting also use Responses with thinking disabled. Whole-paper agent prompt profiles and model-driven page/read/save/finish/shell tools have been removed.

The splitting prompt groups OCR and source-image evidence, binds shared materials, retains stable question/item IDs, identifies student work and matches verified references. It does not solve questions. Every OCR block and detected question region must be accounted for in its typed JSON plan.

The solving prompt answers one bound question independently and can use only the controlled Python mathematical evaluator. It returns typed answer/explanation JSON. The assessment prompt receives the independent solution, verified references and original answer crops in a fresh context and returns per-item attempts and grades without tools.

The model-output parser normalizes common envelopes, field-name variants, keyed/single-item collections, numeric strings, score fractions, and normalized corner coordinates before schema and domain validation. Explicit shared item-key lists and question-level answer keys are expanded into per-item bindings. An otherwise ambiguous parent key can be shared when its regions exactly match the question's complete student regions. Unique OCR question-number labels on a bound answer-sheet page are retained with the answer evidence. It retains the raw response and records any normalized version. It does not invent evidence IDs, student answers, or unknown maximum points. Missing item maxima remain model estimates based on exam-setting conventions, difficulty, solution steps, and printed totals; existing maxima are preserved.

Source text is untrusted data. All stages preserve original-page coordinates, separate student work from solutions, and retain uncertainty when evidence is unreadable. Repository validation protects manual answers, scores, teacher evidence and notes. Prompts remain fixed within each bounded question conversation. Model IDs are chosen in settings/import; no family-name prompt routing changes them.

See [OCR and model workflow](wiki/Tech-Model-Loop.md) for uploads, defaults, costs and failure behavior, and [dataset assessment](question-regions-dataset.md) for the Chinese detector decision.
