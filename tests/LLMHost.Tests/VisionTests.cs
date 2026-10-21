using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class ImageSizeTests
{
    private static byte[] Make(int w, int h, ImageFormat fmt)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.CornflowerBlue);
        using var ms = new MemoryStream();
        bmp.Save(ms, fmt);
        return ms.ToArray();
    }

    private static (int W, int H, bool Jpeg) Info(byte[] data)
    {
        using var img = Image.FromStream(new MemoryStream(data));
        return (img.Width, img.Height, img.RawFormat.Equals(ImageFormat.Jpeg));
    }

    [Fact]
    public void LargeImagesAreScaledKeepingTheAspectRatio()
    {
        var r = Info(ImageInput.Resize(Make(400, 200, ImageFormat.Png), 100));
        Assert.Equal((100, 50), (r.W, r.H));
    }

    [Fact]
    public void SmallImagesAndZeroLimitAreUntouched()
    {
        var png = Make(64, 64, ImageFormat.Png);
        Assert.Same(png, ImageInput.Resize(png, 128));
        Assert.Same(png, ImageInput.Resize(png, 0));
    }

    [Fact]
    public void JpegStaysJpegAndOtherFormatsBecomePng()
    {
        Assert.True(Info(ImageInput.Resize(Make(300, 300, ImageFormat.Jpeg), 100)).Jpeg);
        Assert.False(Info(ImageInput.Resize(Make(300, 300, ImageFormat.Bmp), 100)).Jpeg);
    }

    [Fact]
    public void UndecodableDataIsReturnedAsIs()
    {
        var junk = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Same(junk, ImageInput.Resize(junk, 10));
    }

    [Fact]
    public void DetailPicksTheEdge()
    {
        var v = new VisionSettings { MaxImageEdge = 1024, LowDetailEdge = 256 };
        Assert.Equal(256, ImageInput.EdgeFor("low", v));
        Assert.Equal(1024, ImageInput.EdgeFor("high", v));
        Assert.Equal(1024, ImageInput.EdgeFor("auto", v));
        Assert.Equal(1024, ImageInput.EdgeFor(null, v));
        Assert.Equal(0, ImageInput.EdgeFor("high", new VisionSettings()));
    }

    [Fact]
    public void ChatRequestHonoursDetailLow()
    {
        var png = Make(1200, 600, ImageFormat.Png);
        string url = "data:image/png;base64," + Convert.ToBase64String(png);
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "what is this" },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url, ["detail"] = "low" } },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } }),
            }),
        };
        var msgs = OpenAiMapping.ReadMessages(body);
        var images = msgs.Single().Images!;
        Assert.Equal(2, images.Count);
        Assert.Equal(512, Math.Max(Info(images[0]).W, Info(images[0]).H));       // low: 512 px
        Assert.Equal(1200, Info(images[1]).W);                                    // default: no limit
    }

    [Fact]
    public void VisionSettingsAreValidated()
    {
        var s = new AppSettings();
        s.Vision.MaxImageEdge = -1; s.Vision.LowDetailEdge = 1;
        var f = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("vision.maxImageEdge", f);
        Assert.Contains("vision.lowDetailEdge", f);
    }
}

[Trait("Category", "Unit")]
public class ToolResultImageTests
{
    private static string DataUrl() =>
        "data:image/png;base64," + Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });

    private static void AssertToolThenImage(IReadOnlyList<LLMHostProject.Inference.ChatMessage> msgs, string toolName)
    {
        var shown = msgs.Last();
        Assert.Equal("user", shown.Role);
        Assert.Single(shown.Images!);
        Assert.Contains(toolName, shown.Content);
        Assert.Null(msgs[^2].Images);                      // the tool turn itself stays text
    }

    [Fact]
    public void OpenAiToolMessageWithImagePartIsFollowedByAUserTurnWithTheImage()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":"look"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"screenshot","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"c1","content":[{"type":"text","text":"done"},{"type":"image_url","image_url":{"url":"@@IMG@@"}}]}]}
            """.Replace("@@IMG@@", DataUrl()))!;
        var msgs = OpenAiMapping.ReadMessages(body);
        AssertToolThenImage(msgs, "screenshot");
        Assert.Contains("done", msgs[^2].Content);
    }

    [Fact]
    public void ToolMessageWithoutImagesAddsNothing()
    {
        var body = JsonNode.Parse("""{"messages":[{"role":"tool","tool_call_id":"c1","name":"t","content":"plain"}]}""")!;
        Assert.Single(OpenAiMapping.ReadMessages(body));
    }

    [Fact]
    public void ResponsesFunctionCallOutputCanCarryImages()
    {
        var items = JsonNode.Parse("""
            [{"type":"function_call","call_id":"c1","name":"snap","arguments":"{}"},
             {"type":"function_call_output","call_id":"c1","output":[{"type":"input_text","text":"ok"},{"type":"input_image","image_url":"@@IMG@@"}]}]
            """.Replace("@@IMG@@", DataUrl()))!.AsArray().Select(x => x!);
        var msgs = ApiServer.ResponsesMessages(items, null, LLMHostProject.Inference.ToolFormat.Generic);
        AssertToolThenImage(msgs, "snap");
    }

    [Fact]
    public void AnthropicToolResultImageBlocksAreForwarded()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"assistant","content":[{"type":"tool_use","id":"u1","name":"shoot","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"u1","content":[{"type":"text","text":"ok"},
                 {"type":"image","source":{"type":"base64","media_type":"image/png","data":"@@B64@@"}}]}]}]}
            """.Replace("@@B64@@", Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })))!;
        var msgs = ApiServer.ReadAnthropicMessages(body);
        AssertToolThenImage(msgs, "tool");
    }
}

[Trait("Category", "Unit")]
public class VideoFrameTests
{
    [Fact]
    public void ArgumentsRequestFpsFrameLimitAndScale()
    {
        var a = VideoFrames.BuildArguments("in.mp4", "out/f%03d.jpg", 0.5, 6, 768);
        Assert.Equal("in.mp4", a[a.ToList().IndexOf("-i") + 1]);
        string vf = a[a.ToList().IndexOf("-vf") + 1];
        Assert.StartsWith("fps=0.5", vf);
        Assert.Contains("768", vf);
        Assert.Equal("6", a[a.ToList().IndexOf("-frames:v") + 1]);
        Assert.Equal("out/f%03d.jpg", a[^1]);
    }

    [Fact]
    public void NoScaleFilterWhenNoEdgeIsGiven() =>
        Assert.DoesNotContain("scale", string.Join(" ", VideoFrames.BuildArguments("in.mp4", "o.jpg", 1, 4, 0)));

    [Fact]
    public async Task MissingFileAndBadFpsAreRejectedBeforeRunningAnything()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => VideoFrames.ExtractAsync(Path.Combine(TestEnvironment.Home, "nope.mp4")));
        string f = GgufBuilder.WriteFile("video/fake.mp4", new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<ArgumentException>(() => VideoFrames.ExtractAsync(f, fps: 0));
    }

    [Fact]
    public async Task WithoutFfmpegTheErrorSaysHowToGetIt()
    {
        if (VideoFrames.FindFfmpeg() != null) return;                // ffmpeg is installed here: nothing to assert
        string f = GgufBuilder.WriteFile("video/fake2.mp4", new byte[] { 1, 2, 3 });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => VideoFrames.ExtractAsync(f));
        Assert.Contains("ffmpeg", ex.Message);
        Assert.Contains("vision.ffmpegPath", ex.Message);
    }

    [Fact]
    public void ConfiguredFfmpegPathWins()
    {
        string fake = GgufBuilder.WriteFile("video/ffmpeg-fake.exe", new byte[] { 0 });
        var v = AppSettings.Current.Vision;
        string? before = v.FfmpegPath;
        try { v.FfmpegPath = fake; Assert.Equal(fake, VideoFrames.FindFfmpeg()); }
        finally { v.FfmpegPath = before; }
    }
}

/// <summary>Runs only when LLMHOST_TEST_VLM points at a small vision model (e.g. SmolVLM-256M with its mmproj next to it).</summary>
public sealed class VlmFactAttribute : FactAttribute
{
    public VlmFactAttribute()
    {
        string? p = Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM");
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
            Skip = "Set LLMHOST_TEST_VLM to a small vision .gguf (with its mmproj file next to it) to run the image integration tests.";
    }
}

[Trait("Category", "Integration")]
public class VisionIntegrationTests
{
    private static async Task<LLMHostProject.Inference.ModelRuntime> Load(int slots = 3)
    {
        var model = new LLMHostProject.Models.ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = 4096, MaxConcurrent = 1, CacheSlots = slots, Threads = 8, SkipFitCheck = true };
        return await LLMHostProject.Inference.ModelRuntime.LoadAsync(model, o, "vlm-test", null, CancellationToken.None);
    }

    private static byte[] Picture(Color back, string text)
    {
        using var bmp = new Bitmap(320, 240);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(back);
            using var font = new Font("Arial", 48, FontStyle.Bold);
            g.DrawString(text, font, Brushes.White, 20, 80);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static GenerationOptions Greedy() => new() { Temperature = 0, MaxTokens = 12, ReasoningEffort = "off" };

    private static async Task<(string Text, LLMHostProject.Inference.GenerationStats Stats)> Ask(LLMHostProject.Inference.ModelRuntime rt, List<LLMHostProject.Inference.ChatMessage> msgs)
    {
        var prompt = rt.BuildFittingPrompt(msgs, 32, out _, out var images, "off");
        var stats = new LLMHostProject.Inference.GenerationStats();
        string text = await rt.CompleteAsync(prompt, Greedy(), stats, CancellationToken.None, images);
        return (text, stats);
    }

    private static LLMHostProject.Inference.ChatMessage User(string t, params byte[][] imgs) => new("user", t, imgs.Length > 0 ? imgs : null);

    [VlmFact]
    public async Task AnImageCanBeDescribed()
    {
        using var rt = await Load();
        Assert.True(rt.SupportsVision);
        var (text, stats) = await Ask(rt, new() { User("What colour is the background?", Picture(Color.DarkRed, "HI")) });
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(stats.PromptTokens > 50, "the image should add many prompt tokens");
        Assert.Equal(0, stats.CachedTokens);
    }

    [VlmFact]
    public async Task AFollowUpTurnReusesTheImageFromTheCache()
    {
        using var rt = await Load();
        var img = Picture(Color.DarkGreen, "OK");
        var turn1 = new List<LLMHostProject.Inference.ChatMessage> { User("Describe this picture in a few words.", img) };
        var (a1, s1) = await Ask(rt, turn1);

        var turn2 = new List<LLMHostProject.Inference.ChatMessage>(turn1) { new("assistant", a1), User("And what is written on it?") };
        var (a2, s2) = await Ask(rt, turn2);

        Assert.True(s2.CachedTokens >= s1.PromptTokens - 8,
            $"the image prompt should come from the cache ({s2.CachedTokens} cached of {s2.PromptTokens}; first prompt was {s1.PromptTokens})");
        Assert.False(string.IsNullOrWhiteSpace(a2));
    }

    [VlmFact]
    public async Task ReusedImageGivesTheSameAnswerAsAFreshEvaluation()
    {
        using var rt = await Load();
        var img = Picture(Color.Navy, "A1");
        var turn1 = new List<LLMHostProject.Inference.ChatMessage> { User("Describe this picture.", img) };
        var (a1, _) = await Ask(rt, turn1);
        var turn2 = new List<LLMHostProject.Inference.ChatMessage>(turn1) { new("assistant", a1), User("Describe it again in one sentence.") };

        var (warm, ws) = await Ask(rt, turn2);          // image + first answer come from the cache
        await rt.ClearCacheAsync();
        var (cold, cs) = await Ask(rt, turn2);          // everything evaluated from scratch

        Assert.True(ws.CachedTokens > 0);
        Assert.Equal(0, cs.CachedTokens);
        Assert.Equal(cold, warm);
    }

    [VlmFact]
    public async Task ADifferentImageOnlyReusesTheTextBeforeIt()
    {
        using var rt = await Load();
        var m1 = new List<LLMHostProject.Inference.ChatMessage> { User("Describe this picture.", Picture(Color.Purple, "X")) };
        var (_, s1) = await Ask(rt, m1);
        var m2 = new List<LLMHostProject.Inference.ChatMessage> { User("Describe this picture.", Picture(Color.Orange, "Y")) };
        var (_, s2) = await Ask(rt, m2);

        Assert.True(s2.CachedTokens < s1.PromptTokens / 2, $"a changed image must not be served from the old cache ({s2.CachedTokens} cached)");
    }

    [VlmFact]
    public async Task TextOnlyRequestAfterAnImageStillWorks()
    {
        using var rt = await Load();
        await Ask(rt, new() { User("Describe this picture.", Picture(Color.Teal, "Z")) });
        var (text, _) = await Ask(rt, new() { User("Say hello.") });
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
