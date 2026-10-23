using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Search;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;
using OhMyStudy.Infrastructure.Search;
using OhMyStudy.Application.Diagnostics;

internal static partial class Program
{
    private static async Task SemanticSearchChecks()
    {
        var nearest = VectorSearch.Find([8, 0], [new("opposite", "", [-2, 0]), new("other", "", [0, 9]), new("best", "", [0.2f, 0])], 2);
        Check(nearest.Select(match => match.QuestionId).SequenceEqual(["best", "other"]) && Math.Abs(nearest[0].Score - 1) < 0.00001, "local vector search uses cosine similarity and bounded top K");
        await Fails(() => Task.Run(() => VectorSearch.Normalize([float.NaN])), "nonfinite vectors rejected");
        await Fails(() => Task.Run(() => VectorSearch.Normalize([0, 0])), "zero vectors rejected");
        await Fails(() => Task.Run(() => VectorSearch.Find([1, 0], [new("x", "", [1])], 1)), "dimension mismatch requests index rebuild");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Fails(() => Task.Run(() => VectorSearch.Find([1], [new("x", "", [1])], 1, canceled.Token)), "local vector scan honors cancellation");
        }
        string path = CreateLegacy(2);
        string[] ids;
        string modelKey;
        await using (var library = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)))
        {
            var job = Fixture<Job>("job") with { State = JobState.Running, Sequence = 1, Progress = 0 };
            await library.SaveJobEventAsync(job, Event(job));
            await library.BeginGradingAsync(job);
            await library.SaveGradingReferencesAsync(job.Id, Fixture<List<ReferenceAnswer>>("references"));
            var original = Fixture<Question>("question");
            var math = original with
            {
                DraftKey = "semantic-math",
                Number = "1",
                Subject = "Math",
                Stem = [new TextContent { Text = "calculus derivatives" }],
                Notes = "private-note-marker",
                Items = [original.Items[0] with { Attempt = original.Items[0].Attempt! with { StudentAnswer = "private-answer-marker" } }]
            };
            var biology = original with
            {
                DraftKey = "semantic-biology",
                Number = "2",
                Subject = "Biology",
                Stem = [new TextContent { Text = "botany cells" }],
                Items = original.Items.Select(item => item with
                {
                    Key = "biology-" + item.Key,
                    Attempt = item.Attempt! with { ReferenceKey = null, Teacher = null, Grade = item.Attempt.Grade with { Source = GradeSource.AiSolution } }
                }).ToList()
            };
            var first = await library.SaveGradingQuestionAsync(job.Id, math);
            var second = await library.SaveGradingQuestionAsync(job.Id, biology);
            var finished = job with { State = JobState.Cancelled, Sequence = 2 };
            await library.SaveJobEventAsync(finished, Event(finished));
            ids = [first.Id, second.Id];
            var vault = new MemoryVault();
            var credentials = new SearchCredentialService(library, vault);
            var embedding = new SearchModel("https://embedding.synthetic.invalid/v1", "synthetic-embed");
            var reranker = new SearchModel("https://reranker.synthetic.invalid/v1", "synthetic-rerank");
            var settings = await credentials.SaveAsync(embedding, reranker, Encoding.UTF8.GetBytes("embedding-synthetic"), Encoding.UTF8.GetBytes("reranker-synthetic"));
            Check(settings.Settings.EmbeddingModel!.KeyRef != settings.Settings.RerankerModel!.KeyRef && vault.Values.Count == 2, "embedding and reranker use independent credential targets");
            Check(!DomainJson.Serialize(settings.Settings).Contains("embedding-synthetic", StringComparison.Ordinal) && DomainJson.Deserialize<AppSettings>(DomainJson.Serialize(settings.Settings)) == settings.Settings, "search settings round trip with references only under source generated JSON");
            string oldReference = settings.Settings.EmbeddingModel.KeyRef!;
            vault.FailSave = true;
            await Fails(() => credentials.SaveAsync(embedding, reranker, Encoding.UTF8.GetBytes("replacement-synthetic")), "failed search credential save preserves previous settings");
            Check((await library.GetSettingsAsync()).EmbeddingModel!.KeyRef == oldReference && vault.Values.ContainsKey(oldReference), "previous search key remains available after failure");
            vault.FailSave = false;
            settings = await credentials.SaveAsync(embedding, reranker, Encoding.UTF8.GetBytes("replacement-synthetic"));
            Check(!vault.Values.ContainsKey(oldReference) && vault.Values.Count == 2, "replacing one search key removes only its old credential");
            embedding = settings.Settings.EmbeddingModel!;
            reranker = settings.Settings.RerankerModel!;
            using var handler = new SearchHandler();
            using var http = new HttpClient(handler);
            using var client = new SearchModelClient(http, vault);
            var search = new SemanticSearchService(library, client);
            var plan = await search.PrepareAsync(embedding, new());
            Check(plan.Pending.Count == 2 && plan.Documents.All(document => !document.Text.Contains("private-note-marker", StringComparison.Ordinal)
                && !document.Text.Contains("private-answer-marker", StringComparison.Ordinal)), "semantic index excludes notes and student answers");
            var result = await search.SearchAsync(plan, embedding, null, "calculus");
            Check(result[0].QuestionId == first.Id && handler.EmbeddingRequests == 2, "semantic query retrieves non-keyword candidates by local vector similarity");
            var cached = await search.PrepareAsync(embedding, new());
            Check(cached.Pending.Count == 0, "completed embeddings are reused");
            result = await search.SearchAsync(cached, embedding, reranker, "calculus");
            Check(result[0].QuestionId == second.Id && handler.EmbeddingRequests == 3 && handler.RerankRequests == 1, "reranker order is applied with independent endpoint and key");
            var scoped = await search.PrepareAsync(embedding, new(Subject: "Math"));
            Check(scoped.Documents.Count == 1 && (await search.SearchAsync(scoped, embedding, null, "calculus")).Single().QuestionId == first.Id, "subject scope is applied before vector retrieval");
            await library.SaveQuestionNotesAsync(first.Id, "different private note");
            Check((await search.PrepareAsync(embedding, new())).Pending.Count == 0, "notes do not invalidate semantic vectors");
            await library.EditQuestionAsync(first.Id, math with { Stem = [new TextContent { Text = "calculus integrals" }] });
            Check((await search.PrepareAsync(embedding, new())).Pending.Select(document => document.Id).SequenceEqual([first.Id]), "only edited question text invalidates its cached vector");
            Check((await search.PrepareAsync(embedding with { Model = "different" }, new())).Pending.Count == 2
                && (await search.PrepareAsync(embedding with { BaseUrl = "https://other.synthetic.invalid/v1" }, new())).Pending.Count == 2, "model and endpoint changes isolate vector spaces");
            handler.InvalidEmbedding = true;
            var changedPlan = await search.PrepareAsync(embedding, new());
            await Fails(() => search.SearchAsync(changedPlan, embedding, null, "calculus"), "invalid embedding responses rejected before cache writes");
            Check((await search.PrepareAsync(embedding, new())).Pending.Count == 1, "invalid embedding does not replace previous cached vectors");
            handler.InvalidEmbedding = false;
            handler.WrongDimensions = true;
            await Fails(() => search.SearchAsync(changedPlan, embedding, null, "calculus"), "changed embedding dimensions rejected before cache writes");
            Check((await search.PrepareAsync(embedding, new())).Pending.Count == 1, "dimension mismatch preserves the previous vector space");
            handler.WrongDimensions = false;
            handler.InvalidRerank = true;
            await Fails(() => search.SearchAsync(cached, embedding, reranker, "calculus"), "duplicate reranker indices rejected");
            handler.InvalidRerank = false;
            handler.Status = HttpStatusCode.Unauthorized;
            try { await client.EmbedAsync(embedding, ["calculus"], default); throw new Exception("Expected authentication failure"); }
            catch (SearchModelException error) { Check(!error.Message.Contains("synthetic-secret-response", StringComparison.Ordinal), "provider error bodies are not exposed"); }
            handler.Status = HttpStatusCode.OK;
            handler.DashScope = true;
            var dashEmbedding = embedding with { BaseUrl = "https://embedding.synthetic.invalid/api/v1", ApiType = "dashscope" };
            var dashReranker = reranker with { BaseUrl = "https://reranker.synthetic.invalid/api/v1", ApiType = "dashscope" };
            var dashPlan = await search.PrepareAsync(dashEmbedding, new());
            var dashResult = await search.SearchAsync(dashPlan, dashEmbedding, dashReranker, "calculus");
            Check(dashResult[0].QuestionId == second.Id && handler.TextTypes.SequenceEqual(["document", "query"]), "DashScope native embeddings distinguish document and query inputs and native reranking preserves indices");
            Check(SemanticSearchService.ModelKey(embedding) == SemanticSearchService.Hash("question-meaning-v1\n" + embedding.BaseUrl + "\n" + embedding.Model)
                && SemanticSearchService.ModelKey(embedding) != SemanticSearchService.ModelKey(embedding with { ApiType = "dashscope" }), "legacy OpenAI vector keys remain stable and API types isolate vector spaces");
            var dashSettings = await credentials.SaveAsync(dashEmbedding, dashReranker);
            Check((await library.GetSettingsAsync()).EmbeddingModel!.ApiType == "dashscope"
                && DomainJson.Deserialize<AppSettings>(DomainJson.Serialize(dashSettings.Settings)) == dashSettings.Settings, "search API types persist through source generated settings serialization");
            var legacySettings = JsonNode.Parse(DomainJson.Serialize(dashSettings.Settings))!.AsObject();
            legacySettings["embedding_model"]!.AsObject().Remove("api_type");
            Check(DomainJson.Deserialize<AppSettings>(legacySettings.ToJsonString()).EmbeddingModel!.ApiType == "openai", "search settings without an API type use the existing OpenAI-compatible contract");
            await Fails(() => Task.Run(() => ConfigurationValidation.ValidateSearchModel(embedding with { ApiType = "unknown" })), "unknown search API types are rejected");
            await credentials.SaveAsync(embedding, reranker);
            await BackgroundIndexChecks(library, await library.GetQuestionAsync(first.Id), second, embedding);
            var disabled = await credentials.SaveAsync(null, null);
            Check(disabled.Settings.EmbeddingModel is null && disabled.Settings.RerankerModel is null && vault.Values.Count == 0, "disabling search models removes their stored credentials");
            modelKey = plan.ModelKey;
        }
        await using (var reopened = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)))
        {
            Check((await reopened.ReadSearchVectorsAsync(modelKey)).Count == 2, "vector blobs survive library reopen");
            var summaries = await reopened.ReadRankedQuestionsAsync(ids.Reverse().ToList(), new());
            Check(summaries.Select(item => item.Id).SequenceEqual(ids.Reverse()), "question bank preserves semantic ranking");
            await reopened.DeletePaperAsync(Fixture<Job>("job").Input!.Document.Id);
            Check((await reopened.ReadSearchVectorsAsync(modelKey)).Count == 0, "deleting an exam cascades to its vector cache");
            await reopened.ClearSearchVectorsAsync();
        }
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        public int EmbeddingRequests { get; private set; }
        public int RerankRequests { get; private set; }
        public bool InvalidEmbedding { get; set; }
        public bool WrongDimensions { get; set; }
        public bool InvalidRerank { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool DashScope { get; set; }
        public List<string> TextTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Status != HttpStatusCode.OK) return new(Status) { Content = new StringContent("synthetic-secret-response") };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonObject result;
            var input = DashScope ? body.RootElement.GetProperty("input") : body.RootElement;
            if (request.RequestUri!.AbsolutePath == (DashScope ? "/api/v1/services/embeddings/text-embedding/text-embedding" : "/v1/embeddings"))
            {
                if (request.RequestUri.Host != "embedding.synthetic.invalid" || request.Headers.Authorization?.Parameter != "replacement-synthetic"
                    || body.RootElement.GetProperty("model").GetString() != "synthetic-embed") throw new Exception("Incorrect embedding connection");
                EmbeddingRequests++;
                if (DashScope)
                {
                    Check(!body.RootElement.TryGetProperty("encoding_format", out _), "DashScope requests omit OpenAI-only encoding_format");
                    TextTypes.Add(body.RootElement.GetProperty("parameters").GetProperty("text_type").GetString()!);
                }
                var inputs = (DashScope ? input.GetProperty("texts") : input.GetProperty("input")).EnumerateArray().Select(value => value.GetString()!).ToList();
                result = new JsonObject
                {
                    ["data"] = new JsonArray(inputs.Select((text, index) => (JsonNode)new JsonObject
                    {
                        [DashScope ? "text_index" : "index"] = InvalidEmbedding ? 0 : index,
                        ["embedding"] = WrongDimensions ? new JsonArray(1, 0, 0) : InvalidEmbedding ? new JsonArray(0, 0) : text.Contains("calculus", StringComparison.Ordinal) ? new JsonArray(2, 0) : new JsonArray(0, 3)
                    }).Reverse().ToArray())
                };
                if (DashScope) result = new() { ["output"] = new JsonObject { ["embeddings"] = result["data"]!.DeepClone() } };
            }
            else
            {
                if (request.RequestUri.Host != "reranker.synthetic.invalid" || request.RequestUri.AbsolutePath != (DashScope ? "/api/v1/services/rerank/text-rerank/text-rerank" : "/v1/rerank")
                    || request.Headers.Authorization?.Parameter != "reranker-synthetic" || body.RootElement.GetProperty("model").GetString() != "synthetic-rerank") throw new Exception("Incorrect reranker connection");
                RerankRequests++;
                int count = input.GetProperty("documents").GetArrayLength();
                if (DashScope) Check(body.RootElement.GetProperty("parameters").GetProperty("top_n").GetInt32() == count && input.GetProperty("query").GetString() == "calculus", "DashScope reranker nests inputs and parameters");
                result = new JsonObject { ["results"] = new JsonArray(Enumerable.Range(0, count).Select(index => (JsonNode)new JsonObject { ["index"] = InvalidRerank ? 0 : index, ["relevance_score"] = index }).ToArray()) };
                if (DashScope) result = new() { ["output"] = result };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
