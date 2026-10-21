using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class LogRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer abcdef1234567890xyz", "abcdef1234567890xyz")]
    [InlineData("header x-api-key: ait_AbCdEf123456789", "ait_AbCdEf123456789")]
    [InlineData("using token hf_abcdefghijklmnopqrstuv for download", "hf_abcdefghijklmnopqrstuv")]
    [InlineData("{\"apiKey\":\"supersecretvalue\"}", "supersecretvalue")]
    [InlineData("key sk-abcdefghijklmnopqrstu", "sk-abcdefghijklmnopqrstu")]
    public void SecretsAreMasked(string input, string secret)
    {
        string output = LogRedactor.Redact(input);
        Assert.DoesNotContain(secret, output);
        Assert.Contains(LogRedactor.Mask, output);
    }

    [Fact]
    public void OrdinaryTextIsUntouched()
    {
        const string text = "Loading model qwen2.5-0.5b (gpu_layers=all, ctx=4096) on port 1234";
        Assert.Equal(text, LogRedactor.Redact(text));
    }

    [Fact]
    public void LogBufferRedactsBeforeStoring()
    {
        LogBuffer.Add("test", "calling with Bearer topsecrettoken12345");
        Assert.DoesNotContain(LogBuffer.Tail(5), l => l.Contains("topsecrettoken12345"));
    }
}

[Trait("Category", "Unit")]
public class FileLogTests
{
    private static string NewDir() => Path.Combine(TestEnvironment.Home, "logs-test", Guid.NewGuid().ToString("N")[..6]);

    [Fact]
    public void WritesOneJsonObjectPerLine()
    {
        var dir = NewDir();
        var log = new FileLog(dir, () => new LoggingSettings());
        var t = new DateTime(2026, 3, 4, 5, 6, 7);

        log.Write(t, LogLevel.Warn, "server", "line \"one\"");
        log.Write(t, LogLevel.Info, "load", "line two");

        var lines = File.ReadAllLines(Path.Combine(dir, "llmhost-20260304.log"));
        Assert.Equal(2, lines.Length);
        var first = JsonNode.Parse(lines[0])!;
        Assert.Equal("warn", (string)first["level"]!);
        Assert.Equal("server", (string)first["source"]!);
        Assert.Equal("line \"one\"", (string)first["msg"]!);
    }

    [Fact]
    public void LevelFiltersWhatIsWritten()
    {
        var dir = NewDir();
        var cfg = new LoggingSettings { Level = "warn" };
        var log = new FileLog(dir, () => cfg);
        var t = DateTime.Now;

        log.Write(t, LogLevel.Info, "s", "dropped");
        log.Write(t, LogLevel.Error, "s", "kept");
        cfg.Level = "off";
        log.Write(t, LogLevel.Error, "s", "dropped too");

        var text = string.Concat(log.Files().Select(File.ReadAllText));
        Assert.Contains("kept", text);
        Assert.DoesNotContain("dropped", text);
    }

    [Fact]
    public void RollsOverWhenTheFileIsFull()
    {
        var dir = NewDir();
        var log = new FileLog(dir, () => new LoggingSettings { MaxFileMb = 1 });
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        string big = new('x', 200_000);

        for (int i = 0; i < 12; i++) log.Write(t, LogLevel.Info, "s", big);   // ~2.4 MB

        var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Contains("llmhost-20260101.log", files);
        Assert.Contains("llmhost-20260101.1.log", files);
        Assert.All(Directory.GetFiles(dir), f => Assert.True(new FileInfo(f).Length <= 1024 * 1024 + 4096));
    }

    [Fact]
    public void OldFilesAreDeletedByAge()
    {
        var dir = NewDir();
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "llmhost-20200101.log");
        var fresh = Path.Combine(dir, "llmhost-20990101.log");
        File.WriteAllText(old, "x"); File.WriteAllText(fresh, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

        new FileLog(dir, () => new LoggingSettings()).Cleanup(14);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void NativeThresholdFollowsSettingsAndLegacySwitch()
    {
        var s = new AppSettings();
        Assert.Equal(LogLevel.Info, NativeBackend.NativeThreshold(s));
        s.Logging.Native = "error";
        Assert.Equal(LogLevel.Error, NativeBackend.NativeThreshold(s));
        s.VerboseNativeLog = true;
        Assert.Equal(LogLevel.Debug, NativeBackend.NativeThreshold(s));
    }

    [Fact]
    public void LoggingSettingsAreValidated()
    {
        var s = new AppSettings();
        s.Logging.Level = "loud";
        s.Logging.MaxFileMb = 0;
        var fields = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("logging.level", fields);
        Assert.Contains("logging.maxFileMb", fields);
    }
}
