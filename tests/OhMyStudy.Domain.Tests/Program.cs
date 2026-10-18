using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

var suite = new CompatibilitySuite();
return suite.Run(args);

internal sealed class CompatibilitySuite
{
    private readonly JsonObject _contracts = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/contracts.synthetic.json")))!.AsObject();
    private readonly JsonObject _cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/cases.synthetic.json")))!.AsObject();
    private readonly JsonArray _results = [];
    private int _failed;

    public int Run(string[] args)
    {
        using var process = Process.GetCurrentProcess();
        bool coreClrLoaded = process.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase));
        bool nativeAot = !RuntimeFeature.IsDynamicCodeSupported && !coreClrLoaded;
        RunFixtureCases();
        RunAdditionalCases();
        var report = new JsonObject
        {
            ["recorded_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["native_aot"] = nativeAot,
            ["dynamic_code_supported"] = RuntimeFeature.IsDynamicCodeSupported,
            ["coreclr_loaded"] = coreClrLoaded,
            ["reflection_json_enabled"] = JsonSerializer.IsReflectionEnabledByDefault,
            ["status"] = _failed > 0 ? "failed" : "passed",
            ["checks"] = _results.Count,
            ["failures"] = _failed,
            ["fixtures"] = new JsonObject
            {
                ["contracts_sha256"] = HashFile("contracts.synthetic.json"),
                ["cases_sha256"] = HashFile("cases.synthetic.json")
            },
            ["results"] = _results,
            ["not_tested"] = new JsonArray("Real provider semantics", "Windows compatibility matrix", "Task scheduling and persistence workflows", "Sandbox lifecycle", "Printing", "Whole application performance")
        };
        string path = args.FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "output/migration/domain-tests.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Domain checks: {_results.Count}; failures: {_failed}; Native AOT: {nativeAot}; CoreCLR: {coreClrLoaded}");
        Console.WriteLine($"Evidence: {Path.GetFullPath(path)}");
        return _failed > 0 ? 1 : 0;
    }

    private static string HashFile(string name) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name))));
    private T Contract<T>(string key) => DomainJson.Deserialize<T>(_contracts[key]!.ToJsonString());

    private void Check(string group, string id, Action action)
    {
        try
        {
            action();
            _results.Add((JsonNode)new JsonObject { ["group"] = group, ["id"] = id, ["status"] = "passed" });
        }
        catch (Exception error)
        {
            _failed++;
            _results.Add((JsonNode)new JsonObject { ["group"] = group, ["id"] = id, ["status"] = "failed", ["error"] = error.Message });
            Console.WriteLine($"FAIL {group}/{id}: {error.Message}");
        }
    }

    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static bool Accepted(Action action)
    {
        try { action(); return true; }
        catch (DomainValidationException) { return false; }
        catch (JsonException) { return false; }
    }

    private static JsonNode Pointer(JsonNode root, string path)
    {
        foreach (string encoded in path.Split('/').Skip(1))
        {
            string segment = encoded.Replace("~1", "/").Replace("~0", "~");
            root = root is JsonArray array ? array[int.Parse(segment)]! : root[segment]!;
        }
        return root;
    }

    private static void SetPointer(JsonNode root, string path, JsonNode? value)
    {
        int slash = path.LastIndexOf('/');
        JsonNode parent = slash == 0 ? root : Pointer(root, path[..slash]);
        string key = path[(slash + 1)..].Replace("~1", "/").Replace("~0", "~");
        if (parent is JsonArray array) array[int.Parse(key)] = value?.DeepClone(); else parent[key] = value?.DeepClone();
    }

    private void RunFixtureCases()
    {
        foreach (var item in _cases["json_contract_cases"]!.AsArray())
        {
            var test = item!.AsObject();
            string id = test["id"]!.GetValue<string>();
            Check("json_contract", id, () =>
            {
                var expected = test["expected"]!.AsObject();
                JsonNode value = test["raw_json"] is { } raw ? JsonNode.Parse(raw.GetValue<string>())! : Pointer(_contracts, test["base"]!.GetValue<string>()).DeepClone();
                if (test["add"] is JsonObject add) foreach (var pair in add) value[pair.Key] = pair.Value?.DeepClone();
                if (test["remove"] is JsonArray remove) foreach (var key in remove) value.AsObject().Remove(key!.GetValue<string>());
                JsonNode? parsed = null;
                bool accepted = Accepted(() => parsed = ParseContract(test["type"]!.GetValue<string>(), value.ToJsonString()));
                Assert(accepted == (expected["accepted"]?.GetValue<bool>() ?? expected["deserialization_accepted"]?.GetValue<bool>() ?? true), "Deserialization result differs from legacy fixture");
                if (!accepted) return;
                foreach (var pair in expected)
                {
                    if (pair.Key is "accepted" or "deserialization_accepted" or "request_validation_accepted") continue;
                    if (pair.Key == "future_flag_preserved") { Assert(parsed!.AsObject().ContainsKey("future_flag") == pair.Value!.GetValue<bool>()); continue; }
                    if (pair.Key.EndsWith("_json_type", StringComparison.Ordinal)) { Assert(parsed![pair.Key[..^10]]!.GetValue<ulong>() >= 0); continue; }
                    Assert(JsonNode.DeepEquals(parsed![pair.Key], pair.Value), $"Field {pair.Key} differs from fixture");
                }
                if (expected["request_validation_accepted"] is { } requestExpected)
                {
                    var options = Options() with { Thinking = DomainJson.Deserialize<Thinking>(value.ToJsonString()) };
                    Assert(Accepted(() => options.Validate(Protocol.Messages, true, true)) == requestExpected.GetValue<bool>());
                }
            });
        }

        var document = Contract<Document>("document");
        var question = Contract<Question>("question");
        var refs = Contract<List<ReferenceAnswer>>("references");
        foreach (var item in _cases["grade_cases"]!.AsArray())
        {
            var test = item!;
            Check("grade", test["id"]!.GetValue<string>(), () =>
            {
                var grade = DomainJson.Deserialize<Grade>(test["grade"]!.ToJsonString());
                var q = question with { Attempt = question.Attempt! with { ReferenceKey = null, Grade = grade } };
                Assert(Accepted(() => GradingValidation.ValidateQuestion(q, document, [], false)) == test["accepted"]!.GetValue<bool>());
            });
        }
        foreach (var item in _cases["region_cases"]!.AsArray())
        {
            var test = item!;
            Check("region", test["id"]!.GetValue<string>(), () =>
            {
                var region = new Region { PageId = test["page_id"]?.GetValue<string>() ?? document.Pages[0].Id, X = test["x"]!.GetValue<double>(), Y = test["y"]!.GetValue<double>(), Width = test["width"]!.GetValue<double>(), Height = test["height"]!.GetValue<double>() };
                Assert(Accepted(() => GradingValidation.ValidateRegion(region, document, test["role"]!.GetValue<string>())) == test["accepted"]!.GetValue<bool>());
            });
        }
        foreach (var item in _cases["question_cases"]!.AsArray())
        {
            var test = item!;
            Check("question", test["id"]!.GetValue<string>(), () =>
            {
                if (test["verdict"] is { } verdict)
                {
                    var q = question with { Attempt = question.Attempt! with { Grade = question.Attempt.Grade with { Verdict = DomainJson.Deserialize<Verdict>(verdict.ToJsonString()) } } };
                    Assert(GradingValidation.IsWrongQuestion(q) == test["wrong_question"]!.GetValue<bool>());
                    return;
                }
                var node = _contracts["question"]!.DeepClone();
                if (test["set"] is JsonObject set) foreach (var pair in set) node[pair.Key] = pair.Value?.DeepClone();
                if (test["set_paths"] is JsonObject paths) foreach (var pair in paths) SetPointer(node, pair.Key, pair.Value);
                var edited = DomainJson.Deserialize<Question>(node.ToJsonString());
                var doc = document with { Mode = DomainJson.Deserialize<ImportMode>(test["document_mode"]!.ToJsonString()) };
                Assert(Accepted(() => GradingValidation.ValidateQuestion(edited, doc, refs, test["manual"]!.GetValue<bool>())) == test["accepted"]!.GetValue<bool>());
                if (test["wrong_question"] is { } wrong) Assert(GradingValidation.IsWrongQuestion(edited) == wrong.GetValue<bool>());
            });
        }
        foreach (var item in _cases["policy_cases"]!.AsArray())
        {
            var test = item!;
            Check("policy", test["id"]!.GetValue<string>(), () =>
            {
                var policy = ExecutionPolicy.Freeze(DomainJson.Deserialize<ExecutionMode>(test["preferred_mode"]!.ToJsonString()), new() { State = DomainJson.Deserialize<CapabilityState>(test["capability_state"]!.ToJsonString()), Reason = "synthetic unavailable" }, test["sandbox_networking"]!.GetValue<bool>());
                var expected = test["expected"]!;
                Assert(DomainJson.Serialize(policy.EffectiveMode) == expected["effective_mode"]!.ToJsonString());
                Assert(DomainJson.Serialize(policy.ExecutorKind) == expected["executor_kind"]!.ToJsonString());
                Assert((policy.Warning is not null) == expected["warning_present"]!.GetValue<bool>());
                Assert((policy.FallbackReason is not null) == expected["fallback_reason_present"]!.GetValue<bool>());
            });
        }
    }

    private static JsonNode ParseContract(string type, string raw) => JsonNode.Parse(type switch
    {
        "Provider" => DomainJson.Serialize(DomainJson.Deserialize<Provider>(raw)),
        "Question" => DomainJson.Serialize(DomainJson.Deserialize<Question>(raw)),
        "Content" => DomainJson.Serialize(DomainJson.Deserialize<Content>(raw)),
        "Region" => DomainJson.Serialize(DomainJson.Deserialize<Region>(raw)),
        "ExecutionCall" => DomainJson.Serialize(DomainJson.Deserialize<ExecutionCall>(raw)),
        "AppSettings" => DomainJson.Serialize(DomainJson.Deserialize<AppSettings>(raw)),
        "Grade" => DomainJson.Serialize(DomainJson.Deserialize<Grade>(raw)),
        "Job" => DomainJson.Serialize(DomainJson.Deserialize<Job>(raw)),
        "Thinking" => DomainJson.Serialize(DomainJson.Deserialize<Thinking>(raw)),
        "Document" => DomainJson.Serialize(DomainJson.Deserialize<Document>(raw)),
        _ => throw new InvalidOperationException($"Unknown contract {type}")
    })!;

    private RequestOptions Options() => new() { Model = new() { Id = "synthetic", Capabilities = Contract<Provider>("provider").Models[0].Capabilities, Raw = JsonDocument.Parse("null").RootElement.Clone() }, MaxOutputTokens = 16384, Stream = false, AllowUnknownCapabilities = true, Extra = [] };

    private void RunAdditionalCases()
    {
        var document = Contract<Document>("document");
        var question = Contract<Question>("question");
        var refs = Contract<List<ReferenceAnswer>>("references");
        var saved = Contract<SavedQuestion>("saved_question");
        var coverage = Contract<List<Coverage>>("coverage");
        var viewed = document.Pages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var call = Contract<ExecutionCall>("execution_call");
        Check("digest", "exact_serde_utf8", () =>
        {
            Assert(call.CanonicalJson() == _contracts["permission_digest"]!["canonical_json"]!.GetValue<string>());
            Assert(call.Digest() == _contracts["permission_digest"]!["sha256"]!.GetValue<string>());
            Assert(DomainJson.Serialize(call) == call.CanonicalJson());
            call.Validate();
        });
        foreach (var changed in new[] { call with { ProtocolVersion = 2 }, call with { JobId = call.JobId + "a" }, call with { CallId = call.CallId + "a" }, call with { Tool = ToolKind.Shell }, call with { Content = call.Content + " " }, call with { Arguments = [.. call.Arguments, ""] }, call with { WorkingDirectory = call.WorkingDirectory + "a" }, call with { TimeoutMs = call.TimeoutMs + 1 } })
            Check("digest", "review_field_" + _results.Count, () => Assert(changed.Digest() != call.Digest()));
        Check("digest", "serde_escaping_and_unicode", () =>
        {
            var unusual = call with { Content = "\b\f\n\r\t\0\u001f\u2028\u2029\uE000😀\"\\" };
            Assert(unusual.CanonicalJson().Contains("\\b\\f\\n\\r\\t\\u0000\\u001f\u2028\u2029\uE000😀\\\"\\\\", StringComparison.Ordinal));
        });
        Check("json", "source_generated_context_only", () => { Assert(!JsonSerializer.IsReflectionEnabledByDefault); Assert(!Accepted(() => DomainJson.Deserialize<Grade>("null"))); });
        Check("json", "content_discriminator_can_follow_data", () => Assert(DomainJson.Deserialize<Content>("{\"text\":\"中文\",\"kind\":\"text\"}") is TextContent { Text: "中文" }));
        Check("json", "enum_case_and_integer_rejected", () => { Assert(!Accepted(() => DomainJson.Deserialize<Verdict>("\"Correct\""))); Assert(!Accepted(() => DomainJson.Deserialize<Verdict>("0"))); });
        Check("json", "serde_float_notation_and_range", () =>
        {
            Assert(DomainJson.Serialize(question.Sources[0] with { X = 1, Y = -0.0, Width = 0.00001, Height = 0.000001 }).Contains("\"x\":1.0,\"y\":-0.0,\"width\":0.00001,\"height\":1e-6", StringComparison.Ordinal));
            Assert(!Accepted(() => DomainJson.Deserialize<Region>("{\"page_id\":\"p\",\"x\":1e400,\"y\":0,\"width\":1,\"height\":1}")));
        });
        Check("json", "required_scalars_and_null_collections_rejected", () =>
        {
            Assert(!Accepted(() => DomainJson.Deserialize<Region>("{\"page_id\":\"p\",\"y\":0,\"width\":1,\"height\":1}")));
            var value = _contracts["question"]!.DeepClone(); value["options"] = null;
            Assert(!Accepted(() => DomainJson.Deserialize<Question>(value.ToJsonString())));
        });
        Check("json", "null_collection_elements_and_duplicate_fields_rejected", () =>
        {
            var value = _contracts["question"]!.DeepClone(); value["options"] = new JsonArray((JsonNode?)null);
            Assert(!Accepted(() => DomainJson.Deserialize<Question>(value.ToJsonString())));
            Assert(!Accepted(() => DomainJson.Deserialize<Grade>("{\"verdict\":\"correct\",\"verdict\":\"incorrect\",\"reason\":\"\",\"source\":\"ai_solution\"}")));
            DomainJson.Deserialize<AppSettings>("{\"execution_mode\":\"sandbox\",\"sandbox_networking\":false,\"unknown\":1,\"unknown\":2}");
        });
        Check("json", "provider_has_key_type_is_ignored", () =>
        {
            var value = _contracts["provider"]!.DeepClone(); value["has_key"] = "untrusted";
            Assert(!DomainJson.Deserialize<Provider>(value.ToJsonString()).HasKey);
        });
        Check("json", "job_and_document_times_progress_preserved", () =>
        {
            var job = Contract<Job>("job"); var roundtrip = DomainJson.Deserialize<Job>(DomainJson.Serialize(job));
            Assert(roundtrip.CreatedAt == job.CreatedAt && roundtrip.UpdatedAt == job.UpdatedAt && roundtrip.Progress == 100 && roundtrip.Sequence == job.Sequence);
            Assert(DomainJson.Deserialize<Document>(DomainJson.Serialize(document)).CreatedAt == document.CreatedAt);
        });
        Check("json", "all_enum_values", () =>
        {
            RoundtripEnums<ImportMode>(); RoundtripEnums<Protocol>(); RoundtripEnums<Verdict>(); RoundtripEnums<GradeSource>(); RoundtripEnums<JobState>(); RoundtripEnums<ExecutionMode>(); RoundtripEnums<ExecutorKind>(); RoundtripEnums<CapabilityState>(); RoundtripEnums<DiagnosticKind>(); RoundtripEnums<ToolKind>(); RoundtripEnums<PermissionChoice>(); RoundtripEnums<ErrorKind>();
        });
        Check("json", "dto_roundtrips", () =>
        {
            Roundtrip(Contract<GradingInput>("grading_input")); Roundtrip(Contract<JobEvent>("job_event")); Roundtrip(saved); Roundtrip(refs); Roundtrip(coverage);
            Roundtrip(GradingResult.Empty); Roundtrip(Model.Manual("manual")); Roundtrip(AppSettings.Default);
        });
        foreach (var thinking in new Thinking[] { new ResponsesThinking { Effort = "low" }, new AdaptiveThinking { Effort = "high" }, new BudgetThinking { Tokens = ulong.MaxValue }, new GeminiLevelThinking { Level = "high" }, new GeminiBudgetThinking { Tokens = -1 } })
            Check("json", "thinking_" + thinking.GetType().Name, () => Assert(DomainJson.Deserialize<Thinking>(DomainJson.Serialize(thinking)) == thinking));

        Check("coverage", "complete", () => GradingValidation.ValidateCoverage(document, coverage, [saved], false, viewed));
        Check("coverage", "reference_only", () => GradingValidation.ValidateCoverage(document, [coverage[1]], [saved], true, viewed));
        CoverageFailure("missing_page", [coverage[0]], [saved], viewed);
        CoverageFailure("duplicate_page", [.. coverage, coverage[0]], [saved], viewed);
        CoverageFailure("unviewed", coverage, [saved], []);
        CoverageFailure("invalid_status", [coverage[0] with { Status = "finished" }, coverage[1]], [saved], viewed);
        CoverageFailure("unreadable_reason_required", [coverage[0] with { Status = "unreadable", Reason = " " }, coverage[1]], [saved], viewed);
        CoverageFailure("unknown_question", [coverage[0] with { QuestionKeys = ["missing"] }, coverage[1]], [saved], viewed);
        CoverageFailure("covered_material_needs_key", [coverage[0] with { QuestionKeys = [] }, coverage[1]], [saved], viewed);
        CoverageFailure("key_source_must_match_page", coverage, [saved with { Question = question with { Sources = [question.Sources[0] with { PageId = document.Pages[1].Id }] } }], viewed);

        Check("grade", "nonfinite_rejected", () => { foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }) Assert(!Accepted(() => GradingValidation.ValidateGrade(question.Attempt!.Grade with { Possible = number }))); });
        Check("region", "nonfinite_rejected", () => Assert(!Accepted(() => GradingValidation.ValidateRegion(question.Sources[0] with { X = double.NaN }, document, "material"))));
        Check("question", "utf8_key_128_boundary", () =>
        {
            var blank = document with { Mode = ImportMode.Blank };
            var key128 = new string('中', 42) + "ab";
            GradingValidation.ValidateQuestion(question with { DraftKey = key128, Attempt = null }, blank, [], false);
            Assert(!Accepted(() => GradingValidation.ValidateQuestion(question with { DraftKey = new string('中', 43), Attempt = null }, blank, [], false)));
        });
        Check("question", "duplicate_keys_rejected", () => Assert(!Accepted(() => GradingValidation.ValidateQuestion(question with { Parts = [question] }, document, refs, false))));
        Check("question", "depth_eight_boundary", () =>
        {
            Question q = question with { Attempt = null, Points = null };
            for (int i = 1; i <= 8; i++) q = question with { DraftKey = "depth" + i, Attempt = null, Points = null, Parts = [q] };
            var blank = document with { Mode = ImportMode.Blank };
            GradingValidation.ValidateQuestion(q, blank, [], false);
            Assert(!Accepted(() => GradingValidation.ValidateQuestion(question with { DraftKey = "depth9", Attempt = null, Parts = [q] }, blank, [], false)));
        });
        Check("question", "teacher_priority_and_evidence", () =>
        {
            var grade = Contract<Grade>("teacher_grade");
            var attempt = question.Attempt! with { Teacher = new() { Raw = "教师批注", Regions = question.Sources, Grade = grade }, Grade = grade, ReferenceKey = null };
            var teacher = document with { Mode = ImportMode.TeacherGraded };
            GradingValidation.ValidateQuestion(question with { Attempt = attempt }, teacher, refs, false);
            Assert(!Accepted(() => GradingValidation.ValidateQuestion(question with { Attempt = attempt with { Grade = grade with { Reason = "changed" } } }, teacher, refs, false)));
            Assert(!Accepted(() => GradingValidation.ValidateQuestion(question with { Attempt = attempt with { Teacher = null } }, teacher, refs, false)));
        });
        Check("question", "reference_cannot_be_bypassed", () => Assert(!Accepted(() => GradingValidation.ValidateQuestion(question with { Attempt = question.Attempt! with { ReferenceKey = null, Grade = question.Attempt.Grade with { Source = GradeSource.AiSolution } } }, document, refs, false))));
        Check("question", "part_scores_must_sum", () =>
        {
            var part = question with { DraftKey = "part", Points = 1, Attempt = question.Attempt! with { ReferenceKey = null, Grade = question.Attempt.Grade with { Earned = 1, Possible = 1, Verdict = Verdict.Correct, Source = GradeSource.AiSolution } } };
            Assert(!Accepted(() => GradingValidation.ValidateQuestion(question with { Parts = [part] }, document, refs, false)));
        });
        Check("question", "one_mib_utf8_limit", () =>
        {
            var blank = document with { Mode = ImportMode.Blank };
            var q = question with { Attempt = null, Content = [new TextContent { Text = "" }] };
            int fixedBytes = GradingValidation.QuestionByteSize(q);
            q = q with { Content = [new TextContent { Text = new string('a', 1_048_576 - fixedBytes) }] };
            GradingValidation.ValidateQuestion(q, blank, [], false);
            Assert(!Accepted(() => GradingValidation.ValidateQuestion(q with { Content = [new TextContent { Text = new string('a', 1_048_577 - fixedBytes) }] }, blank, [], false)));
            Assert(!Accepted(() => GradingValidation.ValidateQuestion(q with { Content = [new TextContent { Text = new string('中', 349_526) }] }, blank, [], false)));
        });
        Check("execution", "utf8_content_argument_limits", () =>
        {
            (call with { Content = new string('a', 1_048_576), Arguments = [] }).Validate();
            Assert(!Accepted(() => (call with { Content = new string('中', 349_526) }).Validate()));
            Assert(!Accepted(() => (call with { Arguments = [new string('中', 21_846)] }).Validate()));
            Assert(!Accepted(() => (call with { TimeoutMs = 3_600_001 }).Validate()));
            Assert(!Accepted(() => (call with { WorkingDirectory = "C:relative" }).Validate()));
        });
        Check("request", "unknown_and_known_false_capabilities", () =>
        {
            var options = Options() with { Model = Model.Manual("model"), AllowUnknownCapabilities = false };
            Assert(!Accepted(() => options.Validate(Protocol.Responses, true, true)));
            (options with { AllowUnknownCapabilities = true }).Validate(Protocol.Responses, true, true);
            Assert(!Accepted(() => (options with { AllowUnknownCapabilities = true, Model = options.Model with { Capabilities = Capabilities.Unknown with { Vision = false } } }).Validate(Protocol.Responses, true, false)));
        });
        Check("request", "native_thinking_budget_rules", () =>
        {
            var options = Options();
            (options with { Thinking = new BudgetThinking { Tokens = 1024 } }).Validate(Protocol.Messages, false, false);
            Assert(!Accepted(() => (options with { Thinking = new BudgetThinking { Tokens = 1023 } }).Validate(Protocol.Messages, false, false)));
            Assert(!Accepted(() => (options with { Thinking = new BudgetThinking { Tokens = 16384 } }).Validate(Protocol.Messages, false, false)));
            Assert(!Accepted(() => (options with { Thinking = new BudgetThinking { Tokens = 2048 } }).Validate(Protocol.Responses, false, false)));
            Assert(!Accepted(() => (options with { Thinking = new GeminiBudgetThinking { Tokens = -2 } }).Validate(Protocol.Gemini, false, false)));
            (options with { Thinking = new GeminiBudgetThinking { Tokens = -1 } }).Validate(Protocol.Gemini, false, false);
        });
        Check("configuration", "provider_url_and_utf8_limits", () =>
        {
            var provider = Contract<Provider>("provider"); ConfigurationValidation.ValidateProvider(provider);
            foreach (string url in new[] { "http://synthetic.invalid", "https://user:secret@synthetic.invalid", "https://synthetic.invalid?x=1", "https://synthetic.invalid#x" }) Assert(!Accepted(() => ConfigurationValidation.ValidateProvider(provider with { BaseUrl = url })));
            foreach (string url in new[] { "http://localhost/v1", "http://127.0.0.1/v1", "http://[::1]/v1" }) ConfigurationValidation.ValidateProvider(provider with { BaseUrl = url });
            Assert(!Accepted(() => ConfigurationValidation.ValidateProvider(provider with { Name = new string('中', 67) })));
            Assert(!Accepted(() => ConfigurationValidation.ValidateProvider(provider with { Models = [provider.Models[0], provider.Models[0]] })));
            Assert(!Accepted(() => ConfigurationValidation.ValidateProvider(provider with { Models = [provider.Models[0] with { PriceInput = double.NaN }] })));
        });
        Check("configuration", "settings_utf8_limit", () =>
        {
            ConfigurationValidation.ValidateSettings(AppSettings.Default);
            Assert(!Accepted(() => ConfigurationValidation.ValidateSettings(AppSettings.Default with { DefaultModel = new string('中', 342) })));
            Assert(!Accepted(() => ConfigurationValidation.ValidateSettings(AppSettings.Default with { ExecutionMode = "invalid" })));
        });
        Check("job", "terminal_states_and_default_policy", () =>
        {
            Assert(Enum.GetValues<JobState>().Count(s => s.IsTerminal()) == 4);
            Assert(ExecutionPolicy.Freeze(null, new() { State = CapabilityState.Ready, Reason = "" }, true).EffectiveMode == ExecutionMode.Sandbox);
        });

        void CoverageFailure(string id, List<Coverage> values, List<SavedQuestion> questions, HashSet<string> read) => Check("coverage", id, () => Assert(!Accepted(() => GradingValidation.ValidateCoverage(document, values, questions, false, read))));
    }

    private static void Roundtrip<T>(T value) { string first = DomainJson.Serialize(value); Assert(DomainJson.Serialize(DomainJson.Deserialize<T>(first)) == first); }
    private static void RoundtripEnums<T>() where T : struct, Enum { foreach (var value in Enum.GetValues<T>()) Assert(EqualityComparer<T>.Default.Equals(value, DomainJson.Deserialize<T>(DomainJson.Serialize(value)))); }
}
