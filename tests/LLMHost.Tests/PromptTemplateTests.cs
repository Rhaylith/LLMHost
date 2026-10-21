using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class PromptTemplateUnitTests
{
    private static ManualTemplate ChatMl() => new()
    {
        System = { Prefix = "<|im_start|>system\n", Suffix = "<|im_end|>\n" },
        User = { Prefix = "<|im_start|>user\n", Suffix = "<|im_end|>\n" },
        Assistant = { Prefix = "<|im_start|>assistant\n", Suffix = "<|im_end|>\n" },
        Tool = { Prefix = "<|im_start|>tool\n", Suffix = "<|im_end|>\n" },
        StopStrings = { "<|im_end|>" },
    };

    [Fact]
    public void ManualFormatWrapsEachTurnAndOpensTheAnswer()
    {
        var msgs = new[] { ("system", "S"), ("user", "hi"), ("assistant", "yo"), ("tool", "42"), ("user", "again") };
        string r = PromptTemplates.RenderManual(ChatMl(), msgs, addAssistant: true);
        Assert.Equal("<|im_start|>system\nS<|im_end|>\n<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\nyo<|im_end|>\n<|im_start|>tool\n42<|im_end|>\n<|im_start|>user\nagain<|im_end|>\n<|im_start|>assistant\n", r);
    }

    [Fact]
    public void ManualFormatWithoutGenerationPromptAndWithBosAndCustomAnswerPrefix()
    {
        var t = ChatMl(); t.Bos = "<s>"; t.GenerationPrefix = "<|im_start|>assistant\n<think>\n";
        Assert.Equal("<s><|im_start|>user\nq<|im_end|>\n", PromptTemplates.RenderManual(t, new[] { ("user", "q") }, false));
        Assert.EndsWith("<think>\n", PromptTemplates.RenderManual(t, new[] { ("user", "q") }, true));
    }

    [Fact]
    public void UnknownRolesCountAsUserAndDeveloperAsSystem()
    {
        string r = PromptTemplates.RenderManual(ChatMl(), new[] { ("developer", "d"), ("narrator", "n") }, false);
        Assert.Equal("<|im_start|>system\nd<|im_end|>\n<|im_start|>user\nn<|im_end|>\n", r);
    }

    [Fact]
    public void CheckFindsBadChoices()
    {
        Assert.Empty(PromptTemplates.Check(new PromptTemplateSettings()));
        Assert.Contains("Mode", PromptTemplates.Check(new PromptTemplateSettings { Mode = "weird" }).Single());
        Assert.Contains("empty", PromptTemplates.Check(new PromptTemplateSettings { Mode = "jinja", JinjaText = " " }).Single());
        Assert.Contains("cannot be parsed", PromptTemplates.Check(new PromptTemplateSettings { Mode = "jinja", JinjaText = "{% if x %}" }).Single());
        Assert.Empty(PromptTemplates.Check(new PromptTemplateSettings { Mode = "jinja", JinjaText = "{{ messages[0].content }}" }));
        Assert.Contains("no user or assistant", PromptTemplates.Check(new PromptTemplateSettings { Mode = "manual" }).Single());
        Assert.Empty(PromptTemplates.Check(new PromptTemplateSettings { Mode = "manual", ManualFormat = ChatMl() }));
    }

    [Fact]
    public void SettingsValidationReportsTemplateProblemsAtTheOptionPath()
    {
        var s = new AppSettings();
        s.DefaultLoad.PromptTemplate = new PromptTemplateSettings { Mode = "jinja", JinjaText = "{{ oops " };
        Assert.Contains(SettingsValidator.Validate(s), e => e.Field == "defaultLoad.promptTemplate");
    }

    [Fact]
    public void CloningCopiesTheTemplateDeeply()
    {
        var o = new LoadOptions { PromptTemplate = new PromptTemplateSettings { Mode = "manual", ManualFormat = ChatMl() } };
        var c = o.Clone();
        c.PromptTemplate.ManualFormat.User.Prefix = "changed";
        c.PromptTemplate.ManualFormat.StopStrings.Add("x");
        Assert.Equal("<|im_start|>user\n", o.PromptTemplate.ManualFormat.User.Prefix);
        Assert.Single(o.PromptTemplate.ManualFormat.StopStrings);
    }

    [Fact]
    public void BosAndEosTextAreReadFromTheTokenList()
    {
        string path = GgufBuilder.WriteFile("templates/special.gguf", GgufBuilder.Build(vocab: 10, bosId: 1, eosId: 7, chatTemplate: "{{ bos_token }}{{ messages[0].content }}{{ eos_token }}"));
        var info = GgufInfo.Read(path);
        Assert.Equal(1, info.BosId);
        Assert.Equal(7, info.EosId);
        Assert.True(info.TokensOffset > 0);
        var (bos, eos) = PromptTemplates.SpecialTokens(path, info);
        Assert.Equal("t1", bos);            // the builder names token i "t<i>"
        Assert.Equal("t7", eos);
    }

    [Fact]
    public void PreviewUsesTheModelTemplateAndItsSpecialTokens()
    {
        string path = GgufBuilder.WriteFile("templates/preview.gguf", GgufBuilder.Build(vocab: 10, bosId: 1, eosId: 7,
            chatTemplate: "{{ bos_token }}{% for m in messages %}[{{ m.role }}:{{ m.content }}]{% endfor %}{% if add_generation_prompt %}<A>{% endif %}{{ eos_token }}"));
        var model = new LocalModel { Id = "t/preview.gguf", Path = path, SizeBytes = new FileInfo(path).Length };

        var (auto, engine) = PromptTemplates.Preview(model, new PromptTemplateSettings(), new[] { ("user", "hi") }, true);
        Assert.Equal("t1[user:hi]<A>t7", auto);
        Assert.Contains("built-in renderer", engine);

        var (custom, _) = PromptTemplates.Preview(model, new PromptTemplateSettings { Mode = "jinja", JinjaText = "{{ messages[0].content | upper }}" }, new[] { ("user", "hi") }, false);
        Assert.Equal("HI", custom);

        var (manual, e2) = PromptTemplates.Preview(model, new PromptTemplateSettings { Mode = "manual", ManualFormat = ChatMl() }, new[] { ("user", "hi") }, true);
        Assert.Equal("manual", e2);
        Assert.EndsWith("<|im_start|>assistant\n", manual);
    }

    [Fact]
    public void ModelWithoutTemplateCannotBePreviewedInAutoMode()
    {
        string path = GgufBuilder.WriteFile("templates/none.gguf", GgufBuilder.Build());
        var model = new LocalModel { Id = "t/none.gguf", Path = path, SizeBytes = new FileInfo(path).Length };
        Assert.Throws<InvalidOperationException>(() => PromptTemplates.Preview(model, new PromptTemplateSettings(), new[] { ("user", "x") }, true));
    }
}

[Trait("Category", "Api")]
public class PromptTemplateApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public PromptTemplateApiTests(ServerFixture f) => _f = f;

    private LocalModel PutModel(string name, string template)
    {
        string dir = Path.Combine(AppSettings.Current.ResolvedModelsDirectory, "pub", "tpl-repo");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), GgufBuilder.Build(vocab: 10, bosId: 1, eosId: 7, chatTemplate: template));
        return new ModelLibrary().List().Single(m => m.FileName == name);
    }

    [Fact]
    public async Task TemplateEndpointShowsWhatTheFileSays()
    {
        var m = PutModel("api-tpl.gguf", "{% for m in messages %}<{{ m.content }}>{% endfor %}<think>");
        var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/models/template?model=" + Uri.EscapeDataString(m.Id)))!;
        Assert.True((bool)j["hasTemplate"]!);
        Assert.Contains("<think>", (string)j["template"]!);
        Assert.Equal("t1", (string)j["bos"]!);
    }

    [Fact]
    public async Task PreviewRendersManualAndJinjaAndReportsErrorsInTheBody()
    {
        var m = PutModel("api-prev.gguf", "{{ messages[0].content }}");

        var ok = await _f.Http.PostAsJsonAsync("/api/models/template/preview", new
        {
            model = m.Id,
            template = new { mode = "manual", manualFormat = new { user = new { prefix = "U:", suffix = "\n" }, assistant = new { prefix = "A:" } } },
            messages = new[] { new { role = "user", content = "hi" } },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("U:hi\nA:", (string)JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["prompt"]!);

        var bad = await _f.Http.PostAsJsonAsync("/api/models/template/preview", new { model = m.Id, template = new { mode = "jinja", jinjaText = "{% if %}" } });
        var body = JsonNode.Parse(await bad.Content.ReadAsStringAsync())!;
        Assert.Null(body["prompt"]);
        Assert.Contains("Jinja", (string)body["error"]!);

        var raised = await _f.Http.PostAsJsonAsync("/api/models/template/preview", new { model = m.Id, template = new { mode = "jinja", jinjaText = "{{ raise_exception('nope') }}" } });
        Assert.Contains("nope", (string)JsonNode.Parse(await raised.Content.ReadAsStringAsync())!["error"]!);
    }
}

/// <summary>
/// The built-in Jinja engine against llama.cpp's own renderer, on the templates of real models. Both must give the same prompt.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Models")]
public class TemplateParityTests
{
    private static readonly (string Role, string Content)[][] Conversations =
    {
        new[] { ("user", "Hello") },
        new[] { ("system", "You are terse."), ("user", "Hello") },
        new[] { ("user", "Hi"), ("assistant", "Hello! How can I help?"), ("user", "Tell me a joke.") },
        new[] { ("system", "Be brief."), ("user", "Hi"), ("assistant", "Hey."), ("user", "Why is the sky blue?"), ("assistant", "Rayleigh scattering."), ("user", "Thanks") },
    };

    private static async Task Compare(string path, bool withSystem = true)
    {
        var model = new ModelLibrary().Find(path)!;
        var options = new LoadOptions { Device = "cpu", ContextSize = 2048, MaxConcurrent = 1, Threads = 8, SkipFitCheck = true, Vision = false };
        using var native = await ModelRuntime.LoadAsync(model, options, "tpl-native", null, CancellationToken.None);
        string template = FitEstimator.ReadModel(model.Path).Info.ChatTemplate!;

        var overridden = options.Clone();
        overridden.PromptTemplate = new PromptTemplateSettings { Mode = "jinja", JinjaText = template };
        using var ours = await ModelRuntime.LoadAsync(model, overridden, "tpl-jinja", null, CancellationToken.None);
        Assert.DoesNotContain(ours.LoadWarnings, w => w.StartsWith("Prompt template"));

        foreach (var conv in Conversations.Where(c => withSystem || c.All(m => m.Role != "system")))
        {
            var msgs = conv.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
            string expected = native.ApplyChatTemplate(msgs, true);
            string actual = ours.ApplyChatTemplate(msgs, true);
            Assert.Equal(expected, actual);
        }
    }

    [ModelFact]
    public Task Qwen3TemplateMatchesLlamaCpp() => Compare(Environment.GetEnvironmentVariable("LLMHOST_TEST_MODEL")!);

    [VlmFact]
    public Task SmolVlmTemplateMatchesLlamaCpp() =>
        // llama.cpp's built-in shim for this format folds a system message into the first user turn, which the model's Jinja template
        // does not do; the Jinja engine follows the template, so only conversations without a system message are compared
        Compare(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!, withSystem: false);

    [VlmFact]
    public async Task ManualTemplateReplacesTheModelFormatAndAddsItsStopStrings()
    {
        var model = new ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!)!;
        var options = new LoadOptions
        {
            Device = "cpu", ContextSize = 2048, MaxConcurrent = 1, Threads = 8, SkipFitCheck = true, Vision = false,
            PromptTemplate = new PromptTemplateSettings
            {
                Mode = "manual",
                ManualFormat = new ManualTemplate
                {
                    User = { Prefix = "User: ", Suffix = "\n" }, Assistant = { Prefix = "Assistant:", Suffix = "\n" }, StopStrings = { "\nUser:" },
                },
            },
        };
        using var rt = await ModelRuntime.LoadAsync(model, options, "tpl-manual", null, CancellationToken.None);
        string prompt = rt.BuildFittingPrompt(new() { new ChatMessage("user", "Say hi.") }, 32, out _, out _, "off");
        Assert.Equal("User: Say hi.\nAssistant:", prompt);

        var stats = new GenerationStats();
        string text = await rt.CompleteAsync(prompt, new GenerationOptions { Temperature = 0, MaxTokens = 24, ReasoningEffort = "off" }, stats);
        Assert.DoesNotContain("\nUser:", text);          // the manual stop string ended the answer
    }

    [VlmFact]
    public async Task ABrokenOverrideFallsBackToTheModelsOwnTemplate()
    {
        var model = new ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!)!;
        var options = new LoadOptions
        {
            Device = "cpu", ContextSize = 2048, MaxConcurrent = 1, Threads = 8, SkipFitCheck = true, Vision = false,
            PromptTemplate = new PromptTemplateSettings { Mode = "jinja", JinjaText = "{{ raise_exception('always fails') }}" },
        };
        using var rt = await ModelRuntime.LoadAsync(model, options, "tpl-broken", null, CancellationToken.None);
        Assert.Contains(rt.LoadWarnings, w => w.StartsWith("Prompt template"));
        string prompt = rt.ApplyChatTemplate(new[] { new ChatMessage("user", "hi") }, true);
        Assert.Contains("hi", prompt);                      // the engine's own template produced it
    }
}
