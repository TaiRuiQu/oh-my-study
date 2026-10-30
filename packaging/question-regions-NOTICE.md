# Question region detection

Source: [erayyapagci/yolo26n-question-segmentation-v2](https://huggingface.co/erayyapagci/yolo26n-question-segmentation-v2).

Author: erayyapagci. Model revision: `30466102640b08833cd5356440989820b87871b7`.

The source checkpoint is published under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The application uses a float32 ONNX conversion with a fixed 1280 × 1280 RGB input and the end-to-end detection head. It has not been fine-tuned on Chinese documents.

Ultralytics and PyTorch are conversion tools only and are not bundled with the application. The application runs the converted graph with ONNX Runtime. The model predicts the `question` class; it does not identify handwriting or blank answer space.
