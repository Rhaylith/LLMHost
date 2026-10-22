using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class ChatTransferTests
{
    [Theory]
    [InlineData("\"Planning a trip to Rome.\"", "Planning a trip to Rome")]
    [InlineData("Title: Rust lifetimes\nBecause the user asked", "Rust lifetimes")]
    [InlineData("**Weekend recipes**", "Weekend recipes")]
    [InlineData("  \n  ", "")]
    public void ModelTitlesAreCleaned(string raw, string expected) => Assert.Equal(expected, ChatTransfer.CleanTitle(raw));

    [Fact]
    public void LongTitlesAreShortened() => Assert.True(ChatTransfer.CleanTitle(new string('x', 200)).Length <= 61);

    [Theory]
    [InlineData("", "New chat")]
    [InlineData("  hello   world  ", "hello world")]
    public void TheFallbackTitleIsTheStartOfTheQuestion(string text, string expected) => Assert.Equal(expected, ChatTransfer.FallbackTitle(text));

    [Fact]
    public void TheFallbackTitleIsCutAtFortyCharacters() => Assert.Equal(41, ChatTransfer.FallbackTitle(new string('a', 100)).Length);

    [Fact]
    public void SnippetsAreShortOneLinersWithEllipses()
    {
        string text = string.Join("\n", Enumerable.Repeat("filler words here", 20)) + " NEEDLE " + string.Join("\n", Enumerable.Repeat("more filler", 20));
        var s = ChatSearchIndex.Snippet(text, text.IndexOf("NEEDLE"), 6);
        Assert.Contains("NEEDLE", s);
        Assert.DoesNotContain("\n", s);
        Assert.StartsWith("…", s);
        Assert.EndsWith("…", s);
        Assert.True(s.Length < 160);
    }

    [Fact]
    public void WordsAreLowerCasedAndTinyOnesDropped() =>
        Assert.Equal(new[] { "hello", "world", "42", "ünï" }, ChatSearchIndex.Tokens("Hello, WORLD! a 42 Ünï").ToArray());

    private static Conversation Chat() => new()
    {
        Title = "Trip", SystemPrompt = "Be brief.",
        Messages =
        {
            new StoredMessage { Id = "a", Role = "user", Content = "Where to?" },
            new StoredMessage { Id = "b", ParentId = "a", Role = "assistant", Content = "Rome.", Reasoning = "thinking about Italy" },
            new StoredMessage { Id = "c", ParentId = "a", Role = "assistant", Content = "Paris." },     // another variant, not on the active branch
        },
        ActiveLeaf = "b",
    };

    [Fact]
    public void MarkdownAndTextFollowTheActiveBranchOnly()
    {
        var md = ChatTransfer.ToMarkdown(Chat());
        Assert.Contains("# Trip", md);
        Assert.Contains("## You", md);
        Assert.Contains("Rome.", md);
        Assert.Contains("thinking about Italy", md);
        Assert.DoesNotContain("Paris.", md);
        Assert.Contains("> **System:** Be brief.", md);
        var txt = ChatTransfer.ToText(Chat());
        Assert.Contains("You: Where to?", txt);
        Assert.Contains("Assistant: Rome.", txt);
        Assert.DoesNotContain("Paris.", txt);
    }

    [Fact]
    public void OurOwnJsonRoundTripsWithAFreshId()
    {
        var c = Chat();
        c.FolderId = "f1"; c.Pinned = true;
        var back = ChatTransfer.Import(System.Text.Json.JsonSerializer.Serialize(c, Json.Options)).Single();
        Assert.NotEqual(c.Id, back.Id);
        Assert.Equal(3, back.Messages.Count);
        Assert.Equal("b", back.ActiveLeaf);
        Assert.Null(back.FolderId);                // folders belong to the installation that made them
        Assert.False(back.Pinned);
    }

    [Fact]
    public void AMessageListWithoutIdsBecomesALinearBranch()
    {
        var c = ChatTransfer.Import("""{"title":"Plain","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]}""").Single();
        Assert.Equal(2, c.ActivePath().Count);
        Assert.Equal(c.Messages[0].Id, c.Messages[1].ParentId);
    }

    [Fact]
    public void AChatGptExportIsReadAlongItsCurrentBranch()
    {
        const string json = """
        [{"title":"Cooking","create_time":1700000000,"current_node":"n3","mapping":{
          "root":{"id":"root","parent":null,"message":null},
          "n1":{"id":"n1","parent":"root","message":{"author":{"role":"system"},"content":{"parts":["Be nice"]}}},
          "n2":{"id":"n2","parent":"n1","message":{"author":{"role":"user"},"content":{"parts":["How to boil an egg?"]}}},
          "n2b":{"id":"n2b","parent":"n1","message":{"author":{"role":"user"},"content":{"parts":["an abandoned branch"]}}},
          "n3":{"id":"n3","parent":"n2","message":{"author":{"role":"assistant"},"content":{"parts":["Eight minutes."]}}}}}]
        """;
        var c = ChatTransfer.Import(json).Single();
        Assert.Equal("Cooking", c.Title);
        Assert.Equal("Be nice", c.SystemPrompt);
        Assert.Equal(new[] { "How to boil an egg?", "Eight minutes." }, c.Messages.Select(m => m.Content));
        Assert.Equal("user", c.Messages[0].Role);
        Assert.Equal(2023, c.CreatedAt.Year);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"messages\":[]}")]
    public void FilesWithoutAConversationAreRefused(string json) => Assert.Throws<ArgumentException>(() => ChatTransfer.Import(json));
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class ChatManagementApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ChatManagementApiTests(ServerFixture f) => _f = f;

    private static StringContent Json_(string json) => new(json, Encoding.UTF8, "application/json");
    private async Task<JsonNode> Get(string url) => JsonNode.Parse(await _f.Http.GetStringAsync(url))!;
    private async Task<JsonNode> Send(HttpMethod m, string url, string? body = null)
    {
        var r = await _f.Http.SendAsync(new HttpRequestMessage(m, url) { Content = body == null ? null : Json_(body) });
        string text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{m} {url}: {(int)r.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    private async Task<string> NewChat(string title, params (string Role, string Text)[] msgs)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        var messages = new JsonArray();
        string? parent = null;
        for (int i = 0; i < msgs.Length; i++)
        {
            string mid = $"m{i}";
            messages.Add(new JsonObject { ["id"] = mid, ["parentId"] = parent, ["role"] = msgs[i].Role, ["content"] = msgs[i].Text });
            parent = mid;
        }
        await Send(HttpMethod.Put, "/api/chats/" + id, new JsonObject { ["title"] = title, ["messages"] = messages, ["activeLeaf"] = parent }.ToJsonString());
        return id;
    }

    [Fact]
    public async Task FoldersNestAndRefuseLoopsAndMissingParents()
    {
        string a = "fa" + Guid.NewGuid().ToString("N")[..6], b = "fb" + Guid.NewGuid().ToString("N")[..6];
        await Send(HttpMethod.Put, "/api/chat-folders/" + a, """{"name":"Work"}""");
        await Send(HttpMethod.Put, "/api/chat-folders/" + b, $$"""{"name":"Reports","parentId":"{{a}}"}""");
        var list = await Get("/api/chat-folders");
        Assert.Equal(a, (string)list.AsArray().First(x => (string)x!["id"]! == b)!["parentId"]!);

        var loop = await _f.Http.PutAsync("/api/chat-folders/" + a, Json_($$"""{"name":"Work","parentId":"{{b}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, loop.StatusCode);
        var self = await _f.Http.PutAsync("/api/chat-folders/" + a, Json_($$"""{"name":"Work","parentId":"{{a}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        var orphan = await _f.Http.PutAsync("/api/chat-folders/fx" + Guid.NewGuid().ToString("N")[..6], Json_("""{"name":"X","parentId":"nowhere"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, orphan.StatusCode);
        var empty = await _f.Http.PutAsync("/api/chat-folders/fy" + Guid.NewGuid().ToString("N")[..6], Json_("""{"name":"  "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        await Send(HttpMethod.Delete, "/api/chat-folders/" + a);                            // takes the sub-folder with it
        Assert.DoesNotContain((await Get("/api/chat-folders")).AsArray(), x => (string)x!["id"]! == a || (string)x["id"]! == b);
    }

    [Fact]
    public async Task ChatsMoveIntoFoldersAndComeBackUpWhenTheFolderGoes()
    {
        string f = "fm" + Guid.NewGuid().ToString("N")[..6];
        await Send(HttpMethod.Put, "/api/chat-folders/" + f, """{"name":"Box"}""");
        string id = await NewChat("move me", ("user", "hello"), ("assistant", "hi"));
        await Send(HttpMethod.Put, $"/api/chats/{id}/meta", $$"""{"folderId":"{{f}}","pinned":true}""");

        // saving the messages again must not undo the folder or the pin
        await Send(HttpMethod.Put, "/api/chats/" + id, """{"title":"move me","messages":[{"id":"m0","role":"user","content":"hello"}]}""");
        var item = (await Get("/api/chats")).AsArray().First(x => (string)x!["id"]! == id)!;
        Assert.Equal(f, (string)item["folderId"]!);
        Assert.True((bool)item["pinned"]!);

        var bad = await _f.Http.PutAsync($"/api/chats/{id}/meta", Json_("""{"folderId":"nowhere"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var res = await Send(HttpMethod.Delete, "/api/chat-folders/" + f);
        Assert.Equal(1, (int)res["moved"]!);
        Assert.Null((await Get("/api/chats")).AsArray().First(x => (string)x!["id"]! == id)!["folderId"]);
    }

    [Fact]
    public async Task DeletingAFolderCanDeleteItsChatsToo()
    {
        string f = "fd" + Guid.NewGuid().ToString("N")[..6];
        await Send(HttpMethod.Put, "/api/chat-folders/" + f, """{"name":"Trash"}""");
        string id = await NewChat("doomed", ("user", "x"));
        await Send(HttpMethod.Put, $"/api/chats/{id}/meta", $$"""{"folderId":"{{f}}"}""");
        var res = await Send(HttpMethod.Delete, $"/api/chat-folders/{f}?chats=delete");
        Assert.Equal(1, (int)res["deleted"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/api/chats/" + id)).StatusCode);
    }

    [Fact]
    public async Task RenamingMakesTheTitleTheUsersAndPinningWorks()
    {
        string id = await NewChat("New chat", ("user", "first words of the question"));
        var first = (await Get("/api/chats")).AsArray().First(x => (string)x!["id"]! == id)!;
        Assert.True((bool)first["titleAuto"]!);                                              // made up from the first message
        Assert.StartsWith("first words", (string)first["title"]!);

        var r = await Send(HttpMethod.Put, $"/api/chats/{id}/rename", """{"title":"My own title"}""");
        Assert.Equal("My own title", (string)r["title"]!);
        Assert.False((bool)r["titleAuto"]!);
        var empty = await _f.Http.PutAsync($"/api/chats/{id}/rename", Json_("""{"title":" "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        // a plain save keeps the typed title and its flag
        await Send(HttpMethod.Put, "/api/chats/" + id, """{"title":"My own title","messages":[]}""");
        Assert.False((bool)(await Get("/api/chats")).AsArray().First(x => (string)x!["id"]! == id)!["titleAuto"]!);
    }

    [Fact]
    public async Task DuplicatingCopiesMessagesAndAttachmentsUnderNewIds()
    {
        string docId;
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Some attached text about lighthouses."));
        {
            var up = await _f.Http.PostAsync("/api/documents", Json_($$"""{"name":"notes.txt","data":"{{b64}}"}"""));
            Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
            docId = (string)JsonNode.Parse(await up.Content.ReadAsStringAsync())!["id"]!;
        }
        string id = "dup" + Guid.NewGuid().ToString("N")[..8];
        await Send(HttpMethod.Put, "/api/chats/" + id, $$"""{"title":"Original","messages":[{"id":"m0","role":"user","content":"see file"}],"documents":[{"id":"{{docId}}","name":"notes.txt","chars":37}]}""");

        var copy = await Send(HttpMethod.Post, $"/api/chats/{id}/duplicate");
        string copyId = (string)copy["id"]!;
        Assert.NotEqual(id, copyId);
        Assert.Equal("Original (copy)", (string)copy["title"]!);

        var full = await Get("/api/chats/" + copyId);
        string copiedDoc = (string)full["documents"]![0]!["id"]!;
        Assert.NotEqual(docId, copiedDoc);
        Assert.Equal("see file", (string)full["messages"]![0]!["content"]!);

        // deleting the original removes its own attachment and leaves the copy's
        await Send(HttpMethod.Delete, "/api/chats/" + id);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/api/documents/" + docId)).StatusCode);
        Assert.True((await _f.Http.GetAsync("/api/documents/" + copiedDoc)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task BulkDeleteRemovesSeveralChatsAndRefusesBadIds()
    {
        string a = await NewChat("bulk a", ("user", "x")), b = await NewChat("bulk b", ("user", "y"));
        var res = await Send(HttpMethod.Post, "/api/chats/bulk-delete", $$"""{"ids":["{{a}}","{{b}}","doesnotexist"]}""");
        Assert.Equal(2, (int)res["deleted"]!);
        var bad = await _f.Http.PostAsync("/api/chats/bulk-delete", Json_("""{"ids":["../evil"]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.GetAsync("/api/chats/bad%20id")).StatusCode);
    }

    [Fact]
    public async Task SearchFindsWordsAndPrefixesInAnyMessageAndFollowsEditsAndDeletes()
    {
        string tag = "zq" + Guid.NewGuid().ToString("N")[..8];
        string id = await NewChat("Search target", ("user", $"Tell me about {tag}marmalade please"), ("assistant", "It is a preserve made from citrus fruit."));

        var hit = (await Get($"/api/chats/search?q={tag}marmalade citrus"))["hits"]!.AsArray();
        Assert.Single(hit);
        Assert.Equal(id, (string)hit[0]!["chatId"]!);
        Assert.Equal("m0", (string)hit[0]!["messageId"]!);
        Assert.Contains(tag, (string)hit[0]!["snippet"]!);

        Assert.Single((await Get($"/api/chats/search?q={tag}marm"))["hits"]!.AsArray());        // the last word may be a prefix
        Assert.Empty((await Get($"/api/chats/search?q={tag}marmalade nonexistentword"))["hits"]!.AsArray());
        Assert.Empty((await Get("/api/chats/search?q="))["hits"]!.AsArray());

        await Send(HttpMethod.Put, "/api/chats/" + id, $$"""{"title":"Search target","messages":[{"id":"m0","role":"user","content":"now about {{tag}}jam"}]}""");
        Assert.Empty((await Get($"/api/chats/search?q={tag}marmalade"))["hits"]!.AsArray());
        Assert.Single((await Get($"/api/chats/search?q={tag}jam"))["hits"]!.AsArray());

        await Send(HttpMethod.Delete, "/api/chats/" + id);
        Assert.Empty((await Get($"/api/chats/search?q={tag}jam"))["hits"]!.AsArray());
    }

    [Fact]
    public async Task ExportGivesJsonMarkdownAndTextAndRefusesOtherFormats()
    {
        string id = await NewChat("Export me", ("user", "ping"), ("assistant", "pong"));
        var md = await _f.Http.GetAsync($"/api/chats/{id}/export?format=markdown");
        Assert.Contains("attachment; filename=\"Export-me.md\"", md.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains("pong", await md.Content.ReadAsStringAsync());
        Assert.Contains("Assistant: pong", await _f.Http.GetStringAsync($"/api/chats/{id}/export?format=text"));
        Assert.Equal("Export me", (string)JsonNode.Parse(await _f.Http.GetStringAsync($"/api/chats/{id}/export"))!["title"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.GetAsync($"/api/chats/{id}/export?format=pdf")).StatusCode);
    }

    [Fact]
    public async Task ImportAcceptsAnExportAndMakesNewChats()
    {
        string id = await NewChat("Round trip", ("user", "a"), ("assistant", "b"));
        string exported = await _f.Http.GetStringAsync($"/api/chats/{id}/export?format=json");
        var res = await Send(HttpMethod.Post, "/api/chats/import", new JsonObject { ["data"] = exported }.ToJsonString());
        Assert.Equal(1, (int)res["imported"]!);
        Assert.NotEqual(id, (string)res["chats"]![0]!["id"]!);
        Assert.Equal("Round trip", (string)res["chats"]![0]!["title"]!);
        var bad = await _f.Http.PostAsync("/api/chats/import", Json_("""{"data":"nonsense"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task WithoutALoadedModelTheAutoTitleFallsBackToTheFirstWords()
    {
        string id = await NewChat("New chat", ("user", "How do I bake sourdough bread at home without a dutch oven?"), ("assistant", "Use a baking tray with steam."));
        var r = await Send(HttpMethod.Post, $"/api/chats/{id}/auto-title");
        Assert.Equal("fallback", (string)r["source"]!);
        Assert.Equal("How do I bake sourdough bread at home wi…", (string)r["title"]!);

        await Send(HttpMethod.Put, $"/api/chats/{id}/rename", """{"title":"Mine"}""");
        var again = await Send(HttpMethod.Post, $"/api/chats/{id}/auto-title");
        Assert.Equal("unchanged", (string)again["source"]!);                                   // a typed title is never replaced
        Assert.Equal("Mine", (string)again["title"]!);
    }
}
