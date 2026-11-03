using Hihooi.AspNetCore;

namespace Hihooi.Tests.Security;

public class DashboardAuthTests
{
    [Fact]
    public void ApiKeyMatches_uses_length_check_and_equality()
    {
        Assert.True(DashboardAuthHandler.ApiKeyMatches("secret-key", "secret-key"));
        Assert.False(DashboardAuthHandler.ApiKeyMatches("secret-key", "secret-kez"));
        Assert.False(DashboardAuthHandler.ApiKeyMatches("secret-key", null));
        Assert.False(DashboardAuthHandler.ApiKeyMatches(null, "secret-key"));
        Assert.False(DashboardAuthHandler.ApiKeyMatches("ab", "abc"));
    }

    [Fact]
    public void SanitizeErrors_redacts_password_and_server()
    {
        var input = new[]
        {
            "Failed open Server=prod.db;Password=SuperSecret;Database=x",
            "ApiKey=abcd1234 failed"
        };
        var cleaned = HihooiDashboardExtensions.SanitizeErrors(input);
        Assert.All(cleaned, line => Assert.DoesNotContain("SuperSecret", line));
        Assert.All(cleaned, line => Assert.DoesNotContain("abcd1234", line));
        Assert.Contains(cleaned, l => l.Contains("Password=***", StringComparison.OrdinalIgnoreCase));
    }
}
