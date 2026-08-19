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
    private readonly McpToolRegistry _registry = new([
        typeof(BeeWriteTools), typeof(BeeUploadTools), typeof(BeeReadTools),
        typeof(BeeSearchTools), typeof(BeeSessionTools), typeof(BeeAuditTools), typeof(BeeConceptTools)
    ]);

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
    public void NonGuidStringId_ReportsInvalidValue_NotOpaqueBindingFailure()
    {
        // The regression MiniMax hit: passing a tree path where the SDK expects a GUID throws
        // deep inside its own JSON binder before our tool method body ever runs.
        var (ctx, nextCalled) = Invoke("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"bee_get_article_versions","arguments":{"id":"/Projects/_Sync/_README"}}}
            """);

        nextCalled.Should().BeFalse();
        var text = ReadErrorText(ctx);
        text.Should().Contain("Invalid parameter value(s): 'id' must be a GUID, got \"/Projects/_Sync/_README\"");
        text.Should().Contain("never tree paths");
    }

    [Fact]
    public void ExplicitNullOnRequiredGuid_ReportsInvalidValue_NotOpaqueBindingFailure()
    {
        // A required Guid can never legitimately be JSON null (non-nullable value type) --
        // this must not slip past both the `missing` check (the key IS present) and a naive
        // "null means absent" `invalid` check.
        var (ctx, nextCalled) = Invoke("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"bee_get_article_versions","arguments":{"id":null}}}
            """);

        nextCalled.Should().BeFalse();
        ReadErrorText(ctx).Should().Contain("Invalid parameter value(s): 'id' must be a GUID, got null");
    }

    [Fact]
    public void NullOptionalGuid_TreatedAsAbsent_PassesThrough()
    {
        // bee_save_media's articleId is an optional Guid? whose default is null -- an explicit
        // JSON null must be accepted the same as omitting the field entirely.
        var (_, nextCalled) = Invoke("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"bee_save_media","arguments":{"fileName":"a.png","contentBase64":"AA==","articleId":null}}}
            """);

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
