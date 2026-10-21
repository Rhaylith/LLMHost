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
