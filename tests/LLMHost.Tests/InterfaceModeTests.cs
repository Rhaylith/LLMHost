using System.Text.RegularExpressions;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class InterfaceModeSettingsTests
{
    [Fact]
    public void TheModeIsValidatedAndDefaultsToPowerSoNothingDisappearsForExistingUsers()
    {
        Assert.Equal("power", new AppSettings().UiMode);
        var s = new AppSettings { UiMode = "expert" };
        Assert.Contains("uiMode", SettingsValidator.Validate(s).Select(e => e.Field));
        foreach (var m in new[] { "user", "power", "developer" }) { s.UiMode = m; Assert.DoesNotContain("uiMode", SettingsValidator.Validate(s).Select(e => e.Field)); }
    }
}

[Trait("Category", "Api")]
public class InterfaceModePageTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public InterfaceModePageTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task EveryControlInTheFeatureTableExistsOnThePage()
    {
        string html = await _f.Http.GetStringAsync("/");
        string js = await _f.Http.GetStringAsync("/ui/features.js");
        int start = js.IndexOf("const FEATURES = [", StringComparison.Ordinal);
        string table = js[start..js.IndexOf("];", start, StringComparison.Ordinal)];
        var entries = Regex.Matches(table, @"\['([^']+)', '(user|power|developer)'\]").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.True(entries.Count > 30);
        foreach (var (sel, _) in entries)
        {
            if (sel.StartsWith("row:") || sel.StartsWith("field:")) Assert.Contains($"id=\"{sel[(sel.IndexOf(':') + 1)..]}\"", html);
            else if (sel.StartsWith("section:")) Assert.Contains("<h2", html[html.IndexOf(sel[8..].Replace("&", "&amp;"), StringComparison.Ordinal)..]);
            else if (Regex.Match(sel, @"^#([a-z0-9_-]+)$") is { Success: true } idm) Assert.Contains($"id=\"{idm.Groups[1].Value}\"", html);
            else if (Regex.Match(sel, @"data-tab=""([a-z]+)""") is { Success: true } tab) Assert.Contains($"data-tab=\"{tab.Groups[1].Value}\"", html);
        }
    }

    [Fact]
    public async Task TheSwitchTheSearchAndTheHidingRulesAreThere()
    {
        string html = await _f.Http.GetStringAsync("/");
        Assert.Contains("id=\"ui-mode\"", html);
        Assert.Contains("id=\"set-search\"", html);
        string css = await _f.Http.GetStringAsync("/ui/app.css");
        Assert.Contains("body[data-mode=\"user\"] [data-min=\"power\"]", css);
        Assert.Contains("body[data-mode=\"power\"] [data-min=\"developer\"]", css);
        Assert.Contains("display: none !important", css);                          // hidden, never disabled
        string js = await _f.Http.GetStringAsync("/ui/features.js");
        Assert.DoesNotContain(".disabled", js);
    }

    [Fact]
    public async Task TheModeIsSavedWithTheSettings()
    {
        var old = _f.Host.Settings.UiMode;
        try
        {
            var s = System.Text.Json.Nodes.JsonNode.Parse(await _f.Http.GetStringAsync("/api/settings"))!;
            s["uiMode"] = "developer";
            var r = await _f.Http.PutAsync("/api/settings", new StringContent(s.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            Assert.True(r.IsSuccessStatusCode);
            Assert.Equal("developer", (string)System.Text.Json.Nodes.JsonNode.Parse(await _f.Http.GetStringAsync("/api/settings"))!["uiMode"]!);
        }
        finally { _f.Host.Settings.UiMode = old; }
    }
}
