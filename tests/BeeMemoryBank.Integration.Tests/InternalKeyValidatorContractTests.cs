using BeeMemoryBank.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Integration.Tests;

public class InternalKeyValidatorContractTests
{
    [Fact]
    public void Validate_AcceptsOnlyTheConfiguredInternalKey()
    {
        var previous = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        const string configured = BmbWebApplicationFactory.InternalKeyForTests;
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", configured);
        try
        {
            var correct = new DefaultHttpContext();
            correct.Request.Headers["X-Internal-Key"] = configured;

            var wrong = new DefaultHttpContext();
            wrong.Request.Headers["X-Internal-Key"] = "wrong-key";

            var none = new DefaultHttpContext();

            InternalKeyValidator.Validate(correct).Should().BeTrue();
            InternalKeyValidator.Validate(wrong).Should().BeFalse();
            InternalKeyValidator.Validate(none).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", previous);
        }
    }
}
