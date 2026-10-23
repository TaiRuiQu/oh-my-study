# Text and meaning search

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Search-zh-CN.md)

## Local text search

In Question bank or Mistakes, use text search for question text, answers, notes, or exam names. Combine subject/result filters and sorting; further rows load while scrolling. Text search does not call a model.

## Configure meaning search

Open **Settings → Meaning search models**. Configure embedding and optional reranking independently, including API type, Base URL, model name, and optional key. **OpenAI-compatible** uses the embedding/reranking contracts of compatible services; **DashScope** uses native APIs with a regional/workspace Base URL ending in `/api/v1`. Operation paths are appended. Save search models. Leave both address and model blank to disable a model.

These connections are separate from recognition providers. Keys live in Windows Credential Manager; an empty key retains a saved key unless Remove saved key is selected.

## Run a search and review uploads

Choose **Meaning**, describe a concept, and select Search or press Enter. Subject/result filters apply first. Confirm the embedding destination, model, and number of new/changed texts; the query is sent on every search. A configured reranker receives up to 50 candidate question texts. Search charges are not included in recognition task estimates.

Indexed text includes stems, formulas, item prompts, options, and knowledge points, up to 6,000 characters per question. Originals, notes, and student answers are excluded. Vectors are cached in the library. Local exact cosine similarity returns up to 50 results, optionally reranked remotely. Change the query or filters and search again to refresh rankings.

When embedding is configured, recognition saves automatically queue background indexing. The recognition confirmation discloses this upload; only changed/new question texts are sent. Disabling embedding stops background uploads. Failed background indexing does not fail recognition and can be retried by a later search. Cancellation retains completed embeddings.

Sources: [search UI](../../src/OhMyStudy.App/MainWindow.SemanticSearch.cs), [settings](../../src/OhMyStudy.App/MainWindow.SearchSettings.cs), [service](../../src/OhMyStudy.Application/Search/SemanticSearchService.cs), [background indexer](../../src/OhMyStudy.Infrastructure/Search/BackgroundSearchIndexer.cs).
