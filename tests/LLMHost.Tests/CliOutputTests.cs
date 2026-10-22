using System.Net;
using System.Text.Json.Nodes;
using LLMHostProject.Cli;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class ExitCodeTests
{
    [Fact]
    public void TheCodesAreStable()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 },
            new[] { ExitCodes.Ok, ExitCodes.Generic, ExitCodes.Usage, ExitCodes.NotFound, ExitCodes.Unauthorized, ExitCodes.Unreachable, ExitCodes.OutOfMemory, ExitCodes.Cancelled, ExitCodes.Conflict });
    }

    [Fact]
    public void ExceptionsMapToCodes()
    {
        Assert.Equal(ExitCodes.Cancelled, ExitCodes.For(new OperationCanceledException()));
        Assert.Equal(ExitCodes.Cancelled, ExitCodes.For(new TaskCanceledException()));
        Assert.Equal(ExitCodes.NotFound, ExitCodes.For(new FileNotFoundException()));
        Assert.Equal(ExitCodes.Unauthorized, ExitCodes.For(new UnauthorizedAccessException()));
        Assert.Equal(ExitCodes.OutOfMemory, ExitCodes.For(new ModelLoadException("guardrail", "x")));
        Assert.Equal(ExitCodes.OutOfMemory, ExitCodes.For(new ModelLoadException("out-of-memory", "x")));
        Assert.Equal(ExitCodes.NotFound, ExitCodes.For(new ModelLoadException("missing", "x")));
        Assert.Equal(ExitCodes.Generic, ExitCodes.For(new ModelLoadException("corrupt", "x")));
        Assert.Equal(ExitCodes.Unauthorized, ExitCodes.For(new HttpRequestException("x", null, HttpStatusCode.Forbidden)));
        Assert.Equal(ExitCodes.NotFound, ExitCodes.For(new HttpRequestException("x", null, HttpStatusCode.NotFound)));
        Assert.Equal(ExitCodes.Unreachable, ExitCodes.For(new HttpRequestException("refused")));
        Assert.Equal(ExitCodes.Usage, ExitCodes.For(new ArgumentException("x")));
        Assert.Equal(ExitCodes.Conflict, ExitCodes.For(new InvalidOperationException("'x' is already loaded")));
        Assert.Equal(ExitCodes.Generic, ExitCodes.For(new InvalidOperationException("something else")));
        Assert.Equal(ExitCodes.Generic, ExitCodes.For(new Exception("?")));
    }
}

/// <summary>Runs real CLI verbs in this process with the output captured.</summary>
[Trait("Category", "Unit")]
[Collection("Settings")]
public class CliOutputTests : IDisposable
{
    private readonly StringWriter _out = new(), _err = new();
    private static readonly AppHost Host = new();

    public CliOutputTests() { Out.Reset(); Out.Stdout = _out; Out.Stderr = _err; Ui.Color = false; }
    public void Dispose() => Out.Reset();

    private async Task<int> Run(params string[] args)
    {
        var rest = Out.ParseGlobalFlags(args.Skip(1).ToArray());
        return await new Shell(Host).RunCommandAsync(args[0], rest);
    }

    private static JsonNode Golden(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name + ".json")))!;
    private static string[] Keys(JsonNode? n) => n!.AsObject().Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
    private static string[] Expected(JsonNode g, string key = "keys") => g[key]!.AsArray().Select(x => (string)x!).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    [Fact]
    public void GlobalFlagsAreReadAnywhereAndTheRestKept()
    {
        var rest = Out.ParseGlobalFlags(new[] { "--json", "hardware", "-q", "--no-color", "--verbose", "x" });
        Assert.Equal(new[] { "hardware", "x", "--json" }, rest);
        Assert.Equal(OutputMode.Json, Out.Mode);                                    // json wins over quiet
        Assert.True(Out.Verbose);
        Assert.False(Ui.Color);
        Out.Reset();
        Out.ParseGlobalFlags(new[] { "-q" });
        Assert.Equal(OutputMode.Quiet, Out.Mode);
    }

    [Fact]
    public async Task HardwareJsonIsOneDocumentOnStdoutAndNothingElse()
    {
        Assert.Equal(0, await Run("hardware", "--json"));
        var j = JsonNode.Parse(_out.ToString())!;                                   // the whole stdout parses: no human text mixed in
        var g = Golden("hardware");
        Assert.Equal(Expected(g), Keys(j));
        foreach (var gpu in j["gpus"]!.AsArray()) Assert.Equal(Expected(g, "gpuKeys"), Keys(gpu));
        Assert.True((int)j["logicalCores"]! >= 1);
        Assert.Equal("", _err.ToString());
    }

    [Fact]
    public async Task PresetsJsonIsAnArrayOfObjectsWithStableKeys()
    {
        Host.Presets.Save("cli-golden", new GenerationOptions { Temperature = 0.4f, TopK = 12 }, "for the golden test");
        try
        {
            Assert.Equal(0, await Run("preset", "--json"));
            var arr = JsonNode.Parse(_out.ToString())!.AsArray();
            var mine = arr.First(x => (string)x!["name"]! == "cli-golden")!;
            Assert.Equal(Expected(Golden("presets")), Keys(mine));
            Assert.Equal("12", (string)mine["top_k"]!);
            Assert.Equal("for the golden test", (string)mine["description"]!);
        }
        finally { Host.Presets.Delete("cli-golden"); }
    }

    [Fact]
    public async Task DownloadsJsonIsAnEmptyArrayWhenThereAreNone()
    {
        Assert.Equal(0, await Run("downloads", "--json"));
        Assert.Equal(0, JsonNode.Parse(_out.ToString())!.AsArray().Count);
        Assert.Equal(Expected(Golden("downloads")).Length, 0);
    }

    [Fact]
    public async Task ModelsJsonListsTheLibrary()
    {
        GgufBuilder.WriteFile("library/clipub/climodel/cli-Q4_0.gguf", GgufBuilder.Build());
        Assert.Equal(0, await Run("ls", "--json"));
        var arr = JsonNode.Parse(_out.ToString())!.AsArray();
        var m = arr.First(x => ((string)x!["id"]!).EndsWith("cli-Q4_0.gguf"))!;
        Assert.NotNull(m["sizeBytes"]);
        Assert.False((bool)m["loaded"]!);
    }

    [Fact]
    public async Task QuietHidesInformationalLinesButKeepsResults()
    {
        Assert.Equal(0, await Run("downloads", "-q"));                              // "No downloads" is information
        Assert.Equal("", _out.ToString());
        Host.Presets.Save("cli-quiet", new GenerationOptions());
        try
        {
            Assert.Equal(0, await Run("preset", "-q"));
            Assert.Contains("cli-quiet", _out.ToString());                           // a table is a result
        }
        finally { Host.Presets.Delete("cli-quiet"); }
    }

    [Fact]
    public async Task InJsonModeHumanLinesGoToStderr()
    {
        Assert.Equal(0, await Run("preset", "save", "cli-json-info", "--json"));
        try
        {
            Assert.Equal("", _out.ToString());
            Assert.Contains("Saved preset", _err.ToString());
        }
        finally { Host.Presets.Delete("cli-json-info"); }
    }

    [Theory]
    [InlineData("nonsense-verb", 2)]
    [InlineData("login", 2)]
    [InlineData("push", 2)]
    [InlineData("dev", 2)]
    [InlineData("load", 2)]
    public async Task UsageAndUnsupportedVerbsExitWith2(string verb, int code)
    {
        Assert.Equal(code, await Run(verb));
        Assert.Contains("error:", _err.ToString());
    }

    [Fact]
    public async Task NotFoundExitsWith3()
    {
        Assert.Equal(3, await Run("preset", "load", "no-such-preset-anywhere"));
        Assert.Equal(3, await Run("preset", "export", "no-such-preset-anywhere"));
    }

    [Fact]
    public async Task LinkStatusAndPeersWorkWithoutAServerAndBadUsageIsRefused()
    {
        Assert.Equal(0, await Run("link", "status", "--json"));
        var j = JsonNode.Parse(_out.ToString())!;
        Assert.False(string.IsNullOrEmpty((string)j["deviceId"]!));
        Assert.NotNull(j["enabled"]);
        Assert.Equal(2, await Run("link", "bogus"));
        Assert.Equal(2, await Run("link", "set-device-name"));                      // the value is missing
        Assert.Equal(2, await Run("link", "unpair"));
        int rc = await Run("link", "pair", "http://127.0.0.1:9", "123456");
        Assert.True(rc == 5, rc + " " + _err);
    }

    [Fact]
    public async Task CloneIsAGetAndNeedsATarget()
    {
        Assert.Equal(1, await Run("clone", "--no-such-flag") is var rc && rc != 0 ? 1 : 0);
        Assert.NotEqual("", _err.ToString() + _out.ToString());
    }
}
