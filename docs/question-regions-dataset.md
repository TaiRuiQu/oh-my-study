# Chinese Exam Question Regions: adoption decision

[简体中文](question-regions-dataset.zh-CN.md)

Reviewed on 2026-10-30. Sources: the [dataset card](https://huggingface.co/datasets/ccace/chinese-exam-question-regions), [annotation policy](https://huggingface.co/datasets/ccace/chinese-exam-question-regions/blob/main/ANNOTATION_POLICY.md), [format](https://huggingface.co/datasets/ccace/chinese-exam-question-regions/blob/main/FORMAT.md), and [original detector card](https://huggingface.co/erayyapagci/yolo26n-question-segmentation-v2).

The proposed dataset contains 2,360 page images and 12,431 question rectangles across nine school subjects. It reports 2,094 training, 130 validation and 136 test pages. Its grouped whole-exam split, continuation IDs, pixel and YOLO labels, and source hashes are useful foundations. Mathematics accounts for 911 pages. These are publisher-reported figures, not independently verified ground-truth statistics.

The labels are generated and cross-checked by AI. The format explicitly sets `human_verified=false`; no human-audited gold subset or independently measured label precision/recall is documented. A visual spot check over six test pages covered mathematics (`b2-05e43f57ff92-p01`, `p02`), Chinese (`b2-cbefdf713429-p01`, `p03`) and physics (`b3-22228d77166b-p01`, `p03`). The displayed boxes generally followed printed question content, including diagram-bearing questions and separated column continuations. This small inspection does not establish dataset-wide completeness, split independence, photograph robustness or handwriting recall.

Most critically for this feature, the policy excludes long blank answer areas, and the inspected pages have no real student work. A model fine-tuned with these labels learns printed question bounds, not student-answer regions. It can crop away precisely the blank space where a student writes. The original detector likewise has only the `question` class. Question detection and handwriting/answer-region detection must therefore be evaluated separately.

Decision: do not use this dataset for a production Chinese fine-tune in this change. Its sample quality is promising for printed-question proposals, but it is not a verified fallback dataset and is unsuitable as the sole training source for answer crops. No Chinese checkpoint, accuracy claim or local benchmark is produced. The app accepts the original model and an optional future Chinese ONNX model, and sends original answer evidence to the grading stage independently of detector bounds.

Before adopting it, manually verify a stratified subset of all nine subjects, dense single/double-column layouts, shared passages, continuation pages, photographed/rotated papers and diagram/table questions. Inspect source groups and near-duplicate papers across splits. Create a separate gold set with annotated real student work, blank answer space, corrections, and teacher marks. Add an answer-region class or dedicated detector rather than relabeling question boxes as answer boxes. Any later training should preserve the supplied grouped split and report results on that independently checked set on an authorized training machine.

The annotation license is CC BY 4.0; original exam images retain their respective owners' rights. This is a label-quality and task-fit assessment, not an image-rights clearance.
