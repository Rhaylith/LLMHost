using System.Globalization;
using System.Text.RegularExpressions;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Api")]
public class ThemeContrastTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ThemeContrastTests(ServerFixture f) => _f = f;

    private static double Lum(string hex)
    {
        hex = hex.TrimStart('#');
        double Ch(int i) { double v = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(0) + 0.7152 * Ch(2) + 0.0722 * Ch(4);
    }

    private static double Contrast(string a, string b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    private static Dictionary<string, string> Vars(string css, string selector)
    {
        var block = Regex.Match(css, Regex.Escape(selector) + @"\{([^}]*)\}").Groups[1].Value;
        return Regex.Matches(block, @"--([a-z0-9-]+):(#[0-9a-fA-F]{6})").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    public static IEnumerable<object[]> Themes() { yield return new object[] { ":root" }; yield return new object[] { ":root[data-theme=\"light\"]" }; }

    [Theory]
    [MemberData(nameof(Themes))]
    public async Task EveryTextAndBackgroundPairReaches4Point5To1(string selector)
    {
        string css = await _f.Http.GetStringAsync("/ui/app.css");
        var v = Vars(css, selector);
        var dark = selector == ":root" ? v : Vars(css, ":root").Concat(v).GroupBy(k => k.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        var pairs = new (string Fg, string Bg)[]
        {
            ("text", "bg"), ("text", "panel"), ("text", "panel2"), ("text", "code-bg"),
            ("dim", "bg"), ("dim", "panel"), ("dim", "panel2"),
            ("acc", "bg"), ("acc", "panel"), ("acc", "panel2"),
            ("ok", "bg"), ("ok", "panel"), ("warn", "panel"), ("warn", "panel2"), ("link", "code-bg"), ("link", "panel"),
            ("on-acc", "acc2"), ("on-err", "acc"),
        };
        var low = pairs.Where(p => Contrast(dark[p.Fg], dark[p.Bg]) < 4.5).Select(p => $"{p.Fg} {dark[p.Fg]} on {p.Bg} {dark[p.Bg]} = {Contrast(dark[p.Fg], dark[p.Bg]):0.00}").ToList();
        Assert.True(low.Count == 0, string.Join("; ", low));
    }

    [Fact]
    public async Task TheLightThemeDefinesEveryColourTheDarkOneDoes()
    {
        string css = await _f.Http.GetStringAsync("/ui/app.css");
        var dark = Vars(css, ":root"); var light = Vars(css, ":root[data-theme=\"light\"]");
        Assert.Empty(dark.Keys.Except(light.Keys));
    }

    [Fact]
    public async Task NoColourIsFixedOutsideTheVariables()
    {
        string css = await _f.Http.GetStringAsync("/ui/app.css");
        string rest = Regex.Replace(css, @":root(\[data-theme=""light""\])?\{[^}]*\}", "");
        var fixedColours = Regex.Matches(rest, @"#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b").Select(m => m.Value).Distinct().ToList();
        Assert.True(fixedColours.Count == 0, "colours outside the theme variables: " + string.Join(", ", fixedColours));
    }

    [Fact]
    public async Task ThePageSetsTheThemeBeforeItIsPainted()
    {
        string html = await _f.Http.GetStringAsync("/");
        Assert.True(html.IndexOf("localStorage.getItem('uiTheme')", StringComparison.Ordinal) < html.IndexOf("/ui/app.css", StringComparison.Ordinal));
        string js = await _f.Http.GetStringAsync("/ui/theme.js");
        Assert.Contains("prefers-color-scheme: dark", js);
        Assert.Contains("readableOn", js);
    }
}

[Trait("Category", "Unit")]
public class ThemeSettingsTests
{
    [Fact]
    public void ThemeAccentAndLanguageAreValidated()
    {
        Assert.Equal("system", new AppSettings().UiTheme);
        Assert.Equal("auto", new AppSettings().UiLanguage);
        Assert.Empty(SettingsValidator.Validate(new AppSettings { UiTheme = "light", UiAccent = "#1f5fd6", UiLanguage = "de" }).Where(e => e.Field.StartsWith("ui")));
        var f = SettingsValidator.Validate(new AppSettings { UiTheme = "blue", UiAccent = "red", UiLanguage = "english please" }).Select(e => e.Field).ToList();
        Assert.Contains("uiTheme", f); Assert.Contains("uiAccent", f); Assert.Contains("uiLanguage", f);
    }
}
