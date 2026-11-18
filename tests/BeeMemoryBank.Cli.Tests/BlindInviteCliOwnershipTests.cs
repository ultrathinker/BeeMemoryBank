using System.Net;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;

namespace BeeMemoryBank.Cli.Tests;

public class BlindInviteCliOwnershipTests
{
    private sealed class ScriptedFullNode : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Calls { get; } = [];
        public string UsersJson { get; set; } = "[]";
        public (int Status, string Body) InviteResponse { get; set; } = (200, """
            {"inviteId":"inv-1","text":"bmb-invite:?v=1&x=abc","expiresAt":"2026-11-16T18:00:00Z","ownership":"shared","ownerLabel":null}
            """);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/users")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UsersJson) };

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.TrimEnd('/') == "/api/blind-nodes/invites")
                return new HttpResponseMessage((HttpStatusCode)InviteResponse.Status) { Content = new StringContent(InviteResponse.Body) };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Invite_DefaultsToShared_WhenNeitherSharedNorOwnerPassed()
    {
        var handler = new ScriptedFullNode();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5300") };
        var output = new StringWriter();

        var code = await BlindCommand.HandleInviteAsync("unused", label: "VPS Backup", httpOverride: http, output: output);

        code.Should().Be(0);
        output.ToString().Should().Contain("Ownership: shared");
        handler.Calls.Should().ContainSingle(c => c.Method == "POST");
        using var sent = JsonDocument.Parse(handler.Calls.Single().Body!);
        sent.RootElement.GetProperty("ownership").GetString().Should().Be("shared");
        sent.RootElement.GetProperty("ownerPersonId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Invite_WithOwner_ResolvesPersonIdFromUsersAndSendsPersonal()
    {
        var alicePersonId = Guid.NewGuid();
        var handler = new ScriptedFullNode
        {
            UsersJson = $$"""
                [
                  {"id":1,"username":"alice","displayName":"Alice Smith","role":"superadmin","isActive":true,"personId":"{{alicePersonId}}"},
                  {"id":2,"username":"bob","displayName":"Bob Jones","role":"admin","isActive":true,"personId":"{{Guid.NewGuid()}}"}
                ]
                """
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5300") };
        var output = new StringWriter();

        var code = await BlindCommand.HandleInviteAsync("unused", owner: "alice", label: "Alice Phone", httpOverride: http, output: output);

        code.Should().Be(0);
        output.ToString().Should().Contain("Ownership: personal (Alice Smith)");
        var inviteCall = handler.Calls.Single(c => c.Method == "POST");
        using var sent = JsonDocument.Parse(inviteCall.Body!);
        sent.RootElement.GetProperty("ownership").GetString().Should().Be("personal");
        sent.RootElement.GetProperty("ownerPersonId").GetGuid().Should().Be(alicePersonId);
    }

    [Fact]
    public async Task Invite_RejectsBothSharedAndOwner()
    {
        var handler = new ScriptedFullNode();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5300") };
        var output = new StringWriter();

        var code = await BlindCommand.HandleInviteAsync("unused", shared: true, owner: "alice", httpOverride: http, output: output);

        code.Should().Be(2);
        handler.Calls.Should().BeEmpty();
    }
}
