using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class ShortcutTests
{
    [Theory]
    [InlineData("Ctrl+N", true)]
    [InlineData("Ctrl+Shift+N", true)]
    [InlineData("Alt+ArrowLeft", true)]
    [InlineData("Ctrl+,", true)]
    [InlineData("?", true)]
    [InlineData("Escape", true)]
    [InlineData("F2", true)]
    [InlineData("Ctrl++", true)]
    [InlineData("Ctrl+Enter", true)]
    [InlineData("", false)]
    [InlineData("Ctrl+", false)]
    [InlineData("Ctrl+Ctrl+N", false)]
    [InlineData("Ctrl+NN", false)]
    [InlineData("Banana+N", false)]
    [InlineData("F13", false)]
    [InlineData("Ctrl N", false)]
    public void CombinationsAreValidatedByTheirSyntax(string combo, bool ok) => Assert.Equal(ok, Shortcuts.IsValid(combo));

    [Theory]
    [InlineData("Shift+Ctrl+n", "Ctrl+Shift+N")]
    [InlineData("Alt+Ctrl+ArrowLeft", "Ctrl+Alt+ArrowLeft")]
    [InlineData("ctrl+K", "ctrl+K")]          // modifiers are case sensitive: only the canonical spelling is valid, so nothing is rewritten
    [InlineData("Ctrl++", "Ctrl++")]
    [InlineData("+", "+")]
    [InlineData("?", "?")]
    public void CombinationsAreWrittenInOneOrder(string combo, string expected)
    {
        if (!Shortcuts.IsValid(combo)) { Assert.Equal(expected, combo); return; }
        Assert.Equal(expected, Shortcuts.Normalize(combo));
    }

    [Fact]
    public void TheDefaultsHaveNoConflictAndCoverTheDocumentedKeys()
    {
        Assert.Empty(Shortcuts.Check(null));
        Assert.Empty(Shortcuts.Check(new Dictionary<string, string>()));
        var eff = Shortcuts.Effective(null);
        foreach (var (id, keys) in new Dictionary<string, string>
        {
            ["newChat"] = "Ctrl+N", ["newFolder"] = "Ctrl+Shift+N", ["runtimes"] = "Ctrl+Shift+R", ["quickOpen"] = "Ctrl+K", ["quickOpen2"] = "Ctrl+P",
            ["send"] = "Ctrl+Enter", ["stop"] = "Escape", ["settings"] = "Ctrl+,", ["focusInput"] = "Ctrl+L", ["copyLast"] = "Ctrl+Shift+C",
            ["prevVariant"] = "Alt+ArrowLeft", ["nextVariant"] = "Alt+ArrowRight", ["help"] = "?",
        }) Assert.Equal(keys, eff[id]);
        Assert.Equal(Shortcuts.Defaults.Count, Shortcuts.Defaults.Select(a => a.Id).Distinct().Count());
        Assert.All(Shortcuts.Defaults, a => Assert.True(Shortcuts.IsValid(a.Keys), a.Id));
    }

    [Fact]
    public void OverridesReplaceDefaultsAndAnEmptyOneSwitchesTheShortcutOff()
    {
        var eff = Shortcuts.Effective(new Dictionary<string, string> { ["newChat"] = "Ctrl+Alt+N", ["help"] = "" });
        Assert.Equal("Ctrl+Alt+N", eff["newChat"]);
        Assert.False(eff.ContainsKey("help"));
        Assert.Equal("Ctrl+Shift+N", eff["newFolder"]);
    }

    [Fact]
    public void TwoActionsOnTheSameKeysAreReported()
    {
        var problems = Shortcuts.Check(new Dictionary<string, string> { ["newChat"] = "Ctrl+K" });          // quick open already has it
        Assert.Single(problems);
        Assert.Contains("already used", problems[0].Message);
        // swapping is fine once the other one is moved away
        Assert.Empty(Shortcuts.Check(new Dictionary<string, string> { ["newChat"] = "Ctrl+K", ["quickOpen"] = "Ctrl+Alt+K" }));
        // a default can be taken over when its owner is switched off
        Assert.Empty(Shortcuts.Check(new Dictionary<string, string> { ["newChat"] = "Ctrl+L", ["focusInput"] = "" }));
    }

    [Fact]
    public void UnknownActionsAndBadCombinationsAreReported()
    {
        var p = Shortcuts.Check(new Dictionary<string, string> { ["nothing"] = "Ctrl+Q", ["newChat"] = "Ctrl+Banana" });
        Assert.Equal(2, p.Count);
        Assert.Contains(p, x => x.Field == "shortcuts.nothing");
        Assert.Contains(p, x => x.Field == "shortcuts.newChat");
    }

    [Fact]
    public void TheSettingsValidatorUsesTheSameRules()
    {
        var s = new AppSettings();
        s.Shortcuts["newChat"] = "Ctrl+K";
        Assert.Contains(SettingsValidator.Validate(s), e => e.Field.StartsWith("shortcuts.") && e.Message.Contains("already used"));
        s.Shortcuts["newChat"] = "Ctrl+Alt+N";
        Assert.DoesNotContain(SettingsValidator.Validate(s), e => e.Field.StartsWith("shortcuts"));
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class ShortcutApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ShortcutApiTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task TheTableListsEveryActionWithItsDefaultAndEffectiveKeys()
    {
        var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/shortcuts"))!.AsArray();
        Assert.Equal(Shortcuts.Defaults.Count, j.Count);
        var send = j.First(x => (string)x!["id"]! == "send")!;
        Assert.Equal("Ctrl+Enter", (string)send["default"]!);
        Assert.Equal("Ctrl+Enter", (string)send["keys"]!);
        Assert.Equal("Chat", (string)send["group"]!);
    }

    [Fact]
    public async Task APageWithAConflictingChoiceIsRejectedWhenSaved()
    {
        var settings = JsonNode.Parse(await _f.Http.GetStringAsync("/api/settings"))!;
        settings["shortcuts"] = new JsonObject { ["newChat"] = "Ctrl+K" };
        var r = await _f.Http.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("already used", await r.Content.ReadAsStringAsync());
    }
}
