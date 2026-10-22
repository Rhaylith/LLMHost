using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace LLMHostProject.Tests;

/// <summary>The check for missing and unused translation keys (what a CI job would run).</summary>
[Trait("Category", "Api")]
public class LocaleTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public LocaleTests(ServerFixture f) => _f = f;

    private async Task<JsonObject> Locale(string lang) => JsonNode.Parse(await _f.Http.GetStringAsync($"/ui/locales/{lang}.json"))!.AsObject();

    private async Task<HashSet<string>> UsedKeys()
    {
        string html = await _f.Http.GetStringAsync("/");
        var keys = Regex.Matches(html, @"data-i18n(?:-title|-placeholder)?=""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (Match m in Regex.Matches(html, @"<script src=""(/ui/(?!i18n\.js)[a-z0-9_-]+\.js)"""))
            foreach (Match k in Regex.Matches(await _f.Http.GetStringAsync(m.Groups[1].Value), @"\bt\(\s*['""]([a-z0-9_.]+)['""]"))
                keys.Add(k.Groups[1].Value);
        return keys;
    }

    [Fact]
    public async Task EveryKeyTheUiUsesExistsInEnglish()
    {
        var en = await Locale("en");
        var missing = (await UsedKeys()).Where(k => !en.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "keys used but missing in en.json: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task NoEnglishKeyIsUnused()
    {
        var en = await Locale("en");
        var used = await UsedKeys();
        var unused = en.Select(kv => kv.Key).Where(k => !used.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(unused.Count == 0, "keys in en.json that nothing uses: " + string.Join(", ", unused));
    }

    [Fact]
    public async Task GermanHasExactlyTheEnglishKeys()
    {
        var en = await Locale("en"); var de = await Locale("de");
        var missing = en.Select(kv => kv.Key).Except(de.Select(kv => kv.Key)).OrderBy(k => k).ToList();
        var extra = de.Select(kv => kv.Key).Except(en.Select(kv => kv.Key)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "missing in de.json: " + string.Join(", ", missing));
        Assert.True(extra.Count == 0, "only in de.json: " + string.Join(", ", extra));
    }

    private static IEnumerable<string> Strings(JsonNode? v) => v is JsonObject o ? o.Select(kv => (string)kv.Value!) : v is JsonValue ? new[] { (string)v! } : Array.Empty<string>();
    private static HashSet<string> Placeholders(JsonNode? v) => Strings(v).SelectMany(s => Regex.Matches(s, @"\{(\w+)\}").Select(m => m.Groups[1].Value)).ToHashSet();

    [Fact]
    public async Task TranslationsKeepThePlaceholdersAndPluralForms()
    {
        var en = await Locale("en"); var de = await Locale("de");
        foreach (var (key, value) in en)
        {
            var d = de[key];
            Assert.True(Strings(d).All(s => s.Trim().Length > 0), key + " is empty in de.json");
            Assert.True(Placeholders(value).SetEquals(Placeholders(d)), $"{key}: placeholders differ");
            if (value is JsonObject) { Assert.True(d is JsonObject dd && dd.ContainsKey("other"), key + ": plural forms need an 'other'"); Assert.True(((JsonObject)value).ContainsKey("other")); }
        }
    }

    [Fact]
    public async Task UnknownLanguagesAreNotServed()
    {
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _f.Http.GetAsync("/ui/locales/xx.json")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _f.Http.GetAsync("/ui/locales/..%2Fen.json")).StatusCode);
    }

    [Fact]
    public async Task TheLanguageListMatchesTheFilesWeShip()
    {
        string js = await _f.Http.GetStringAsync("/ui/i18n.js");
        foreach (var lang in new[] { "en", "de" }) { Assert.Contains($"{lang}: ", js); Assert.True((await _f.Http.GetAsync($"/ui/locales/{lang}.json")).IsSuccessStatusCode); }
    }
}
