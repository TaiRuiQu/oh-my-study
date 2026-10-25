# Providers, models, thinking, and cost configuration

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Models-zh-CN.md)

![Data boundaries: the local library retains originals and records; selected providers receive images and context; optional code follows a separate execution policy.](images/data-boundaries.png)

## Separate the provider from the model

A provider is the connection: name, API type, address, and credential reference. A model is a configuration within that provider: exact ID, display name, context metadata, image/tool support, thinking choices, prices, and currency. Two providers can offer the same ID through different endpoints and prices. Verify the selected provider/model pair before starting.

Save the provider before adding models. Import models retrieves offered IDs; manual entry is useful when a provider's list does not expose the model you have been told to use. A successful listing does not validate image input or reliable tool behavior on an exam.

## Confirm the important fields

| Field | How to use it |
| --- | --- |
| ID | Exact service identifier; do not substitute a friendly display name. |
| Display name | Optional readable label used in choices. |
| Context window | Positive integer metadata when known; not a promise that any exam will fit. |
| Image/tool capabilities | Confirm against your actual endpoint; leave unknown when uncertain. |
| Thinking | Save only supported effort/budget options, then choose among them at task start. |
| Prices | Nonnegative rates per million tokens in the selected currency. |

Messages budget thinking needs an integer of at least 1,024 tokens. Responses and Messages expose different thinking representations. A familiar option name is not evidence that a particular model accepts it.

## Use catalog data as a draft

Fill from models.dev looks up the exact ID. Review capabilities, price units, currency, and context information before Save model. Reference metadata may describe the upstream model rather than your reseller's commercial terms. Saved edits and catalog lookup are distinct actions.

Test connection makes a short request. It can reveal a bad key, wrong endpoint, unknown model, or unsupported options, but it does not demonstrate handwriting recognition or grading quality. Run a small known sample for those.

## Read cost details correctly

The estimator prices each reported bucket at its configured per-million rate. A missing output count omits the output charge and keeps the known input and cache charges. A missing input, cache-read, or cache-write count, an input split that does not match the reported total, or a known nonzero bucket without a rate leaves that request unpriced. The task total adds the priced requests. If none can be priced, the task shows that the estimate is unavailable. A missing cache statistic is not treated as a zero cache count. Zero counts are explicit and need no rate.

For example, 1,000 uncached input tokens at 2 per million plus 500 output tokens at 8 per million costs 0.006 currency units, provided cache counts are explicitly known to be zero. CNY conversion is a dated reference, sometimes from cache; the service's bill remains authoritative. A partial total can leave some requests out.

## Maintain configurations safely

Save form changes before changing selection. Deleting a model/provider keeps previous study data. Re-enter credentials when moving to another Windows account; the library carries configuration references, not a portable copy of the key. Do not share screenshots containing the key when seeking help.

Implementation and reference: [MainWindow.Models.cs](../../src/OhMyStudy.App/MainWindow.Models.cs) · [MainWindow.ModelImport.cs](../../src/OhMyStudy.App/MainWindow.ModelImport.cs) · [ProviderProtocol.cs](../../src/OhMyStudy.Infrastructure/Providers/ProviderProtocol.cs)


## Output limits and prompt profiles

Set **Max output tokens (max_tokens)** per model to override the automatic limit for new tasks. It maps to `max_output_tokens` for Responses and `max_tokens` for Messages, including later rounds. Use a supported positive integer, keeping fixed thinking budgets below it. Catalog enrichment preserves this override.

Recognition selects a local model prompt profile once per task. Unknown versions use family guidance; unknown names use the generic study prompt. See [profiles and sources](../model-prompts.md).

Recognition prompts prefer structured tables and Mermaid flowcharts over unclear crops, and Matplotlib-generated PNGs for simple geometry and diagrams. Generated content must retain original region evidence and visible data. Images and Mermaid blocks support a display width from 1–100 percent (default 100). Font directives and exact table merge markers preserve source typography and structure when needed.

## Codex mode

For a Responses provider, enable **Codex mode** to send `prompt_cache_key` on every turn of a new task. The key is that exam's document id and stays constant across turns, matching the cache-routing key Codex derives from its session. This can raise prompt-cache hit rates on providers that honor the field. It defaults to off and is saved with the provider. Messages providers do not send it. Providers that ignore unknown JSON fields leave the rest of the request unchanged.

## Optional Files API

Enable **Support Files API** only if the provider supports image file IDs and the required expiration contract. It defaults off and affects new tasks. Only requested page images, crops, and generated images upload; identical images upload once per task.

With or without the Files API, model images target 200 KiB each before base64 encoding. Small images remain PNG; larger ones use color JPEG with quality no lower than 0.6 and proportional resizing down to a 1280-pixel long side, without upscaling smaller sources. Images may exceed the target at those limits. Library originals, reading previews, and export assets retain their existing quality; smaller transfers do not guarantee lower image token charges.

Responses uploads use `purpose=user_data`, including on DeepSeek, and request expiry one hour after creation. Messages uses its own upload contract. Completion, failure, and cancellation trigger deletion attempts. Rejected uploads fail explicitly rather than creating permanent files or silently reverting to inline requests.

File IDs reduce transfer size, not image token limits or possible storage charges. See [DeepSeek Files API](https://api-docs.deepseek.com/guides/files_api/) and [OpenAI file upload](https://developers.openai.com/api/reference/resources/files/methods/create). Embedding/reranking is configured independently under [meaning search](Features-Search.md).

---

[Section contents](Feature-Guide.md) · [Next chapter](Features-Import.md)
