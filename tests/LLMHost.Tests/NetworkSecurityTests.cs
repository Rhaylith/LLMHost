using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class IpAllowListTests
{
    private static bool Ok(string[] entries, string ip) => new IpAllowList(entries).Allows(IPAddress.Parse(ip));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.5", false)]
    [InlineData("8.8.8.8", false)]
    public void LoopbackOnly(string ip, bool expected) => Assert.Equal(expected, Ok(new[] { "loopback" }, ip));

    [Theory]
    [InlineData("10.0.0.7", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.40.1.1", false)]
    [InlineData("192.168.50.2", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700::1", false)]
    public void PrivateRanges(string ip, bool expected) => Assert.Equal(expected, Ok(new[] { "private" }, ip));

    [Theory]
    [InlineData("203.0.113.9", true)]
    [InlineData("203.0.113.200", false)]
    [InlineData("198.51.100.1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("2001:db9::1", false)]
    public void CidrAndSingleAddresses(string ip, bool expected) =>
        Assert.Equal(expected, Ok(new[] { "203.0.113.0/25", "198.51.100.1", "2001:db8::/32" }, ip));

    [Fact]
    public void OddPrefixLengthsMatchBitwise()
    {
        Assert.True(Ok(new[] { "10.0.0.0/9" }, "10.127.255.255"));
        Assert.False(Ok(new[] { "10.0.0.0/9" }, "10.128.0.0"));
        Assert.True(Ok(new[] { "0.0.0.0/0" }, "1.2.3.4"));
    }

    [Fact]
    public void AnyAllowsEveryoneAndNothingAllowsNobodyNotEvenNull()
    {
        Assert.True(new IpAllowList(new[] { "any" }).Allows(IPAddress.Parse("8.8.8.8")));
        Assert.False(new IpAllowList(Array.Empty<string>()).Allows(IPAddress.Loopback));
        Assert.False(new IpAllowList(new[] { "loopback" }).Allows(null));
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/x")]
    [InlineData("::1/129")]
    public void BadEntriesAreRefused(string entry) => Assert.Throws<ArgumentException>(() => new IpAllowList(new[] { entry }));

    [Fact]
    public void TheDefaultDependsOnWhereTheServerListens()
    {
        Assert.Equal(new[] { "loopback" }, IpAllowList.DefaultFor("127.0.0.1"));
        Assert.Equal(new[] { "loopback", "private" }, IpAllowList.DefaultFor("0.0.0.0"));
    }
}

[Trait("Category", "Unit")]
public class RateLimiterTests
{
    [Fact]
    public void ABurstUpToTheLimitThenARetryAfter()
    {
        long now = 0;
        var rl = new RequestRateLimiter(() => now);
        for (int i = 0; i < 6; i++) Assert.True(rl.TryAcquire("k", 6, out _));
        Assert.False(rl.TryAcquire("k", 6, out int wait));
        Assert.InRange(wait, 1, 10);                                       // 6 per minute = one every 10 s
        now += 10_000;
        Assert.True(rl.TryAcquire("k", 6, out _));                         // one token came back
        Assert.False(rl.TryAcquire("k", 6, out _));
    }

    [Fact]
    public void KeysAreIndependentAndZeroMeansUnlimited()
    {
        long now = 0;
        var rl = new RequestRateLimiter(() => now);
        Assert.True(rl.TryAcquire("a", 1, out _));
        Assert.False(rl.TryAcquire("a", 1, out _));
        Assert.True(rl.TryAcquire("b", 1, out _));
        for (int i = 0; i < 1000; i++) Assert.True(rl.TryAcquire("c", 0, out _));
    }

    [Fact]
    public void TheBucketNeverHoldsMoreThanTheLimit()
    {
        long now = 0;
        var rl = new RequestRateLimiter(() => now);
        Assert.True(rl.TryAcquire("k", 2, out _));
        now += 3_600_000;                                                   // an hour idle does not save up 120 requests
        Assert.True(rl.TryAcquire("k", 2, out _));
        Assert.True(rl.TryAcquire("k", 2, out _));
        Assert.False(rl.TryAcquire("k", 2, out _));
    }
}

[Trait("Category", "Unit")]
public class RequestCapsTests
{
    private static string? Check(string json, int n, int t) => RequestCaps.Check(JsonNode.Parse(json)!.AsObject(), n, t);

    [Fact]
    public void CapsApplyToChoicesAndEveryTokenField()
    {
        Assert.Null(Check("""{"n":2,"max_tokens":100}""", 4, 200));
        Assert.Contains("at most 4", Check("""{"n":9}""", 4, 0));
        Assert.Contains("max_tokens", Check("""{"max_tokens":500}""", 0, 200));
        Assert.Contains("max_completion_tokens", Check("""{"max_completion_tokens":500}""", 0, 200));
        Assert.Contains("max_output_tokens", Check("""{"max_output_tokens":500}""", 0, 200));
    }

    [Fact]
    public void ZeroMeansNoCapAndMissingFieldsAreFine()
    {
        Assert.Null(Check("""{"n":1000,"max_tokens":99999999}""", 0, 0));
        Assert.Null(Check("""{"messages":[]}""", 1, 1));
        Assert.Null(Check("""{"max_tokens":-1}""", 0, 100));                   // -1 = unlimited asks for the cap's worth at most: not an excess
    }
}

[Trait("Category", "Unit")]
public class TlsCertTests
{
    [Fact]
    public void ASelfSignedCertificateCarriesTheNamesAndAnEcdsaKey()
    {
        using var c = TlsCerts.Generate(new[] { "localhost", "my-box", "127.0.0.1", "192.168.1.20", "::1" });
        Assert.True(c.HasPrivateKey);
        Assert.Equal("1.2.840.10045.2.1", c.PublicKey.Oid.Value);                // id-ecPublicKey
        Assert.InRange((c.NotAfter - DateTime.Now).TotalDays, 700, 731);
        Assert.True(c.NotBefore <= DateTime.Now);
        string names = string.Join("|", TlsCerts.Names(c));
        Assert.Contains("localhost", names);
        Assert.Contains("my-box", names);
        Assert.Contains("192.168.1.20", names);
        Assert.Contains("127.0.0.1", names);
    }

    [Fact]
    public void ThePemExportParsesBackToTheSameCertificate()
    {
        using var c = TlsCerts.Generate(new[] { "localhost" });
        string pem = TlsCerts.ExportPem(c);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
        using var back = X509Certificate2.CreateFromPem(pem);
        Assert.Equal(c.Thumbprint, back.Thumbprint);
        Assert.False(back.HasPrivateKey);                                         // only the public part is exported
    }

    [Fact]
    public void TheStoredCertificateIsReusedAndRenewedBeforeItExpires()
    {
        using var first = TlsCerts.LoadOrCreate();
        using var again = TlsCerts.LoadOrCreate();
        Assert.Equal(first.Thumbprint, again.Thumbprint);
        using var renewed = TlsCerts.LoadOrCreate(DateTime.Now.AddDays(720));      // less than 30 days left on the stored one
        Assert.NotEqual(first.Thumbprint, renewed.Thumbprint);
        using var current = TlsCerts.LoadOrCreate();
        Assert.Equal(renewed.Thumbprint, current.Thumbprint);
    }

    [Fact]
    public void TheValidatorChecksTheNetworkSettings()
    {
        var s = new AppSettings();
        s.Network.AllowedNetworks = new() { "10.0.0.0/40" };
        s.Network.Tls.Mode = "file";
        s.Network.Limits.MaxBodyMb = 0;
        var f = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("network.allowedNetworks", f);
        Assert.Contains("network.tls.certPath", f);
        Assert.Contains("network.limits.maxBodyMb", f);
        Assert.Empty(SettingsValidator.Validate(new AppSettings()).Where(e => e.Field.StartsWith("network")));
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class NetworkSecurityApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public NetworkSecurityApiTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static NetworkSettings Net => AppSettings.Current.Network;
    private void Reset() { AppSettings.Current.Network = new NetworkSettings(); }

    [Fact]
    public async Task APerAddressLimitAnswers429WithRetryAfter()
    {
        try
        {
            Net.Limits.RequestsPerMinutePerIp = 3;
            var codes = new List<HttpStatusCode>();
            HttpResponseMessage? last = null;
            for (int i = 0; i < 6; i++) { last = await _f.Http.GetAsync("/api/status"); codes.Add(last.StatusCode); }
            Assert.Equal(3, codes.Count(c => c == HttpStatusCode.OK));
            Assert.Equal(3, codes.Count(c => c == HttpStatusCode.TooManyRequests));
            Assert.True(last!.Headers.Contains("Retry-After"));
            Assert.Equal("rate_limit_exceeded", (string)JsonNode.Parse(await last.Content.ReadAsStringAsync())!["error"]!["type"]!);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task AnAddressOutsideTheAllowListIsRefusedWith403()
    {
        try
        {
            Net.AllowedNetworks = new() { "10.99.0.0/16" };              // the test client comes from 127.0.0.1
            var r = await _f.Http.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Net.AllowedNetworks = new() { "10.99.0.0/16", "loopback" };
            Assert.Equal(HttpStatusCode.OK, (await _f.Http.GetAsync("/api/status")).StatusCode);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task TooManyTokensOrChoicesAreRefusedBeforeAnyModelIsNeeded()
    {
        try
        {
            Net.Limits.MaxTokens = 50; Net.Limits.MaxChoices = 2;
            var a = await _f.Http.PostAsync("/v1/chat/completions", Body("""{"model":"x","max_tokens":500,"messages":[{"role":"user","content":"hi"}]}"""));
            Assert.Equal(HttpStatusCode.BadRequest, a.StatusCode);
            Assert.Contains("at most 50", await a.Content.ReadAsStringAsync());
            var b = await _f.Http.PostAsync("/v1/chat/completions", Body("""{"model":"x","n":5,"messages":[{"role":"user","content":"hi"}]}"""));
            Assert.Equal(HttpStatusCode.BadRequest, b.StatusCode);
            Assert.Contains("at most 2", await b.Content.ReadAsStringAsync());
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task ARequestBiggerThanTheLimitIsRefusedWith413()
    {
        try
        {
            Net.Limits.MaxBodyMb = 1;
            string big = "{\"model\":\"x\",\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('a', 2 * 1024 * 1024) + "\"}]}";
            var r = await _f.Http.PostAsync("/v1/chat/completions", Body(big));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
            Net.Limits.MaxBodyMb = 32;
            var small = await _f.Http.PostAsync("/v1/chat/completions", Body("""{"model":"x","messages":[{"role":"user","content":"hi"}]}"""));
            Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, small.StatusCode);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task HttpsServesTheSameApiWithTheSelfSignedCertificate()
    {
        int Free() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
        int httpPort = Free(), tlsPort = Free();
        try
        {
            Net.Tls.Mode = "selfSigned"; Net.Tls.Port = tlsPort;
            var host = new AppHost();
            var server = new ApiServer(host);
            await server.StartAsync("127.0.0.1", httpPort);
            try
            {
                Assert.Equal($"https://127.0.0.1:{tlsPort}", server.HttpsUrl);
                // plain http still answers, and publishes the certificate for clients to trust
                using var plain = new HttpClient();
                string pem = await plain.GetStringAsync($"http://127.0.0.1:{httpPort}/api/tls/cert");
                using var trusted = X509Certificate2.CreateFromPem(pem);

                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null && cert.Thumbprint == trusted.Thumbprint,
                };
                using var https = new HttpClient(handler);
                var r = await https.GetAsync($"https://127.0.0.1:{tlsPort}/api/status");
                Assert.True(r.IsSuccessStatusCode);

                // a client that does not trust the certificate is refused
                using var strict = new HttpClient();
                await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync($"https://127.0.0.1:{tlsPort}/api/status"));
            }
            finally { await server.StopAsync(); host.Dispose(); }
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task WithoutHttpsThereIsNoCertificateToDownload()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/api/tls/cert")).StatusCode);
    }
}
