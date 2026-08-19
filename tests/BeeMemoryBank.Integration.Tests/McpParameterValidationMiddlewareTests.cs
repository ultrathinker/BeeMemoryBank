using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Constructs the middleware directly (its dependencies are plain constructor parameters, not
/// resolved from <see cref="HttpContext.RequestServices"/>), so no test host / DI container is
/// needed to exercise it.
/// </summary>
public class McpParameterValidationMiddlewareTests
{
    private readonly McpToolRegistry _registry = new([typeof(BeeWriteTools), typeof(BeeUploadTools)]);

    private (HttpContext ctx, bool nextCalled) Invoke(string jsonBody)
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new McpParameterValidationMiddleware(next, _registry, NullLogger<McpParameterValidationMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));
        ctx.Response.Body = new MemoryStream();

        middleware.InvokeAsync(ctx).GetAwaiter().GetResult();
        return (ctx, nextCalled);
    }

    private static string ReadErrorText(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(ctx.Response.Body);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public void MissingRequiredParameter_ReturnsClearError_NotPassedToNext()
    {
        // bee_replace_in_article requires id/search/replace; only search+replace are given.
        var (ctx, nextCalled) = Invoke("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"bee_replace_in_article","arguments":{"search":"a","replace":"b"}}}
            """);

        nextCalled.Should().BeFalse();
        ReadErrorText(ctx).Should().Contain("Missing required parameter(s): 'id'");
    }

    [Fact]
    public void AliasedCallWithoutId_ReportsMissingId_NotOpaqueBindingFailure()
    {
        // The exact regression this test guards: a coding-agent's file-edit habit supplies
        // old_string/new_string/filePath and never even considers that an 'id' exists.
        var (ctx, nextCalled) = Invoke("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"bee_replace_in_article","arguments":{"filePath":"x.md","oldString":"a","newString":"b"}}}
            """);

        nextCalled.Should().BeFalse();
        ReadErrorText(ctx).Should().Contain("Missing required parameter(s): 'id'");
    }

    [Fact]
    public void ArgumentsOmittedEntirely_StillReportsMissingRequired()
    {
        var (ctx, nextCalled) = Invoke("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"bee_replace_in_article"}}
            """);

        nextCalled.Should().BeFalse();
        ReadErrorText(ctx).Should().Contain("Missing required parameter(s)").And.Contain("'id'");
    }

    [Fact]
    public void ArgumentsExplicitlyNull_StillReportsMissingRequired()
    {
        var (ctx, nextCalled) = Invoke("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"bee_replace_in_article","arguments":null}}
            """);

        nextCalled.Should().BeFalse();
        ReadErrorText(ctx).Should().Contain("Missing required parameter(s)").And.Contain("'id'");
    }

    [Fact]
    public void UnknownParameterOnly_StillReportedAsBefore()
    {
        var articleId = Guid.NewGuid();
        var (ctx, nextCalled) = Invoke(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bee_replace_in_article\",\"arguments\":{\"id\":\"" +
            articleId + "\",\"search\":\"a\",\"replace\":\"b\",\"bogus\":\"x\"}}}");

        nextCalled.Should().BeFalse();
        ReadErrorText(ctx).Should().Contain("Unknown parameter(s): 'bogus'");
    }

    [Fact]
    public void ValidCompleteCall_PassesThroughToNext()
    {
        var articleId = Guid.NewGuid();
        var (_, nextCalled) = Invoke(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bee_replace_in_article\",\"arguments\":{\"id\":\"" +
            articleId + "\",\"search\":\"a\",\"replace\":\"b\"}}}");

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public void ToolWithNoParameters_ArgumentsOmitted_PassesThrough()
    {
        // bee_get_upload_script takes zero parameters -- `missing` must stay empty and not throw.
        var (_, nextCalled) = Invoke("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"bee_get_upload_script"}}
            """);

        nextCalled.Should().BeTrue();
    }
}
