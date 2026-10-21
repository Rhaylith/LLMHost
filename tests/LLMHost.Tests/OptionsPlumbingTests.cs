using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

/// <summary>
/// Guards the option-plumbing pattern described in docs/adding-options.md: every public option of
/// <see cref="LoadOptions"/> / <see cref="GenerationOptions"/> must survive Clone() without sharing mutable state,
/// round-trip through JSON (defaults, presets and chats store them), and be known to the unknown-key check.
/// A new option that breaks one of these fails here instead of silently being lost.
/// </summary>
[Trait("Category", "Unit")]
public class OptionsPlumbingTests
{
    public static IEnumerable<object[]> OptionTypes() => new[] { new object[] { typeof(LoadOptions) }, new object[] { typeof(GenerationOptions) } };

    private static IEnumerable<PropertyInfo> Settable(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    /// <summary>A value different from the default, for every supported option type.</summary>
    private static object NonDefault(Type type, object? current)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool)) return !(current is true);
        if (type == typeof(int)) return ((int?)current ?? 0) + 7;
        if (type == typeof(uint)) return ((uint?)current ?? 0u) + 7;
        if (type == typeof(long)) return ((long?)current ?? 0L) + 7;
        if (type == typeof(float)) return ((float?)current ?? 0f) + 0.25f;
        if (type == typeof(double)) return ((double?)current ?? 0d) + 0.25;
        if (type == typeof(string)) return (current as string ?? "") + "x";
        if (type == typeof(List<string>)) return new List<string> { "a", "b" };
        if (type == typeof(List<int>)) return new List<int> { 1, 2 };
        if (type == typeof(List<float>)) return new List<float> { 0.5f, 0.5f };
        if (type == typeof(Dictionary<int, float>)) return new Dictionary<int, float> { [3] = 1.5f };
        if (type == typeof(LLMHostProject.Inference.PromptTemplateSettings))
            return new LLMHostProject.Inference.PromptTemplateSettings
            {
                Mode = "manual", JinjaText = "{{ x }}",
                ManualFormat = new LLMHostProject.Inference.ManualTemplate
                {
                    Bos = "b", GenerationPrefix = "g", StopStrings = new List<string> { "s1", "s2" },
                    User = { Prefix = "up", Suffix = "us" }, Assistant = { Prefix = "ap", Suffix = "as" },
                },
            };
        throw new NotSupportedException(
            $"OptionsPlumbingTests does not know how to build a value of type {type.Name}. Add it to NonDefault() so the new option is covered.");
    }

    private static object Filled(Type t)
    {
        var o = Activator.CreateInstance(t)!;
        foreach (var p in Settable(t)) p.SetValue(o, NonDefault(p.PropertyType, p.GetValue(o)));
        return o;
    }

    [Theory, MemberData(nameof(OptionTypes))]
    public void CloneCopiesEveryOption(Type t)
    {
        var original = Filled(t);
        var clone = t.GetMethod("Clone")!.Invoke(original, null)!;
        foreach (var p in Settable(t))
            Assert.True(Equal(p.GetValue(original), p.GetValue(clone)), $"{t.Name}.{p.Name} was not copied by Clone()");
    }

    [Theory, MemberData(nameof(OptionTypes))]
    public void CloneDoesNotShareCollections(Type t)
    {
        var original = Filled(t);
        var clone = t.GetMethod("Clone")!.Invoke(original, null)!;
        foreach (var p in Settable(t).Where(p => p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType)))
            Assert.False(ReferenceEquals(p.GetValue(original), p.GetValue(clone)), $"{t.Name}.{p.Name} is shared between a clone and its original; copy it in Clone()");
        // nested option objects must be copied too, not shared
        foreach (var p in Settable(t).Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(p.PropertyType)))
            Assert.False(ReferenceEquals(p.GetValue(original), p.GetValue(clone)), $"{t.Name}.{p.Name} is shared between a clone and its original; copy it in Clone()");
    }

    [Theory, MemberData(nameof(OptionTypes))]
    public void JsonRoundTripKeepsEveryOption(Type t)
    {
        var original = Filled(t);
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, t, Json.Options), t, Json.Options)!;
        foreach (var p in Settable(t))
            Assert.True(Equal(p.GetValue(original), p.GetValue(back)), $"{t.Name}.{p.Name} does not survive JSON serialisation");
    }

    [Fact]
    public void SerialisedDefaultsHaveNoUnknownKeys()
    {
        var s = new AppSettings();
        s.DefaultLoad = (LoadOptions)Filled(typeof(LoadOptions));
        s.DefaultGeneration = (GenerationOptions)Filled(typeof(GenerationOptions));
        var node = JsonSerializer.SerializeToNode(s, Json.Options);
        Assert.Empty(Migrations.FindUnknownKeys(node, typeof(AppSettings)));
    }

    [Fact]
    public void ADefaultOptionNeverFailsValidation()
    {
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
    }

    private static bool Equal(object? a, object? b)
    {
        if (a == null || b == null) return a == b;
        // nested option objects: compare what they serialise to
        if (a is not string && a is not IEnumerable && a.GetType().IsClass && a.GetType().Namespace?.StartsWith("LLMHostProject") == true)
            return JsonSerializer.Serialize(a, Json.Options) == JsonSerializer.Serialize(b, Json.Options);
        if (a is string || !(a is IEnumerable ea)) return a.Equals(b);
        if (a is IDictionary da)
        {
            var db = (IDictionary)b;
            if (da.Count != db.Count) return false;
            foreach (DictionaryEntry e in da) if (!db.Contains(e.Key) || !Equals(e.Value, db[e.Key])) return false;
            return true;
        }
        return ea.Cast<object?>().SequenceEqual(((IEnumerable)b).Cast<object?>());
    }
}
