using Xunit;

namespace LLMHostProject.Tests;

/// <summary>Cheap guards for the single-file web UI: it is edited by scripts, and a bad edit once left stray text before the doctype.</summary>
[Trait("Category", "Api")]
public class UiPageTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public UiPageTests(ServerFixture f) => _f = f;

    /// <summary>The page together with every script it loads, as one text (the scripts share one global scope).</summary>
    private async Task<string> Page()
    {
        string html = await _f.Http.GetStringAsync("/");
        var sb = new System.Text.StringBuilder(html);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"<script src=""(/ui/[^""]+)"""))
            sb.Append('\n').Append(await _f.Http.GetStringAsync(m.Groups[1].Value));
        return sb.ToString();
    }

    [Fact]
    public async Task EveryScriptAndTheStylesheetAreServed()
    {
        string html = await _f.Http.GetStringAsync("/");
        var urls = System.Text.RegularExpressions.Regex.Matches(html, @"(?:src|href)=""(/ui/[^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.True(urls.Count >= 9);
        foreach (var u in urls)
        {
            var r = await _f.Http.GetAsync(u);
            Assert.True(r.IsSuccessStatusCode, u);
            Assert.True((await r.Content.ReadAsStringAsync()).Length > 100, u);
        }
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _f.Http.GetAsync("/ui/nothing.js")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _f.Http.GetAsync("/ui/..%2Fapp.css")).StatusCode);
    }

    [Fact]
    public async Task ThePageStartsWithTheDoctypeAndHasItsLoadDialogFields()
    {
        string html = await _f.Http.GetStringAsync("/");
        Assert.StartsWith("<!doctype html>", html.TrimStart('﻿', ' ', '\r', '\n'), StringComparison.OrdinalIgnoreCase);
        foreach (var id in new[] { "l-lora", "l-moe", "l-embed", "l-pooling", "l-template", "l-draft", "p-overflow" })
            Assert.Contains($"id=\"{id}\"", html);
    }


    [Fact]
    public async Task EveryHelpTipIsValidJavaScript()
    {
        // a stray apostrophe in one help text breaks the whole script; the tips are written as  'id': {t:'..', r:'..', e:'..'}
        string html = await Page();
        var tips = html.Split('\n').Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*'[a-z]-[a-z0-9_-]+': \{t:")).ToList();
        Assert.True(tips.Count > 20);
        foreach (var line in tips)
        {
            int quotes = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == '\'') quotes++;
            }
            Assert.True(quotes == 8, $"help tip has {quotes} unescaped quotes (expected 8): {line.Trim()[..Math.Min(80, line.Trim().Length)]}");
        }
    }
    [Fact]
    public async Task TheLoadDialogSendsEveryNewOption()
    {
        string html = await Page();
        int start = html.IndexOf("function collectLoad", StringComparison.Ordinal);
        Assert.True(start > 0);
        string body = html[start..Math.Min(html.Length, start + 3000)];
        foreach (var key in new[] { "draftModel:", "promptTemplate:", "moeCpuLayers:", "loras:", "pooling:", "embedBatchSize:", "normalize:" })
            Assert.Contains(key, body);
        Assert.Contains("window._lorasWanted = d.loras", html);
    }
}
