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
