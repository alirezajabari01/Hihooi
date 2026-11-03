using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Hihooi.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hihooi.AspNetCore;

/// <summary>
/// Accepts a configured API key (header or SignalR access_token) or an already-authenticated host user.
/// </summary>
public sealed class DashboardAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly HihooiOptions _hihooiOptions;

    public DashboardAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        HihooiOptions hihooiOptions)
        : base(options, logger, encoder)
    {
        _hihooiOptions = hihooiOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = _hihooiOptions.DashboardAuth;
        if (!auth.RequireAuthorization)
        {
            var anon = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "anonymous") }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(anon, Scheme.Name)));
        }

        if (ApiKeyMatches(auth.ApiKey, Request.Headers[auth.ApiKeyHeaderName].ToString()))
            return Success("api-key");

        if (ApiKeyMatches(auth.ApiKey, Request.Query["access_token"].ToString()))
            return Success("api-key");

        // Host-authenticated user (cookies/JWT) — do not treat as API key leak path.
        if (Context.User?.Identity?.IsAuthenticated == true
            && !string.Equals(Context.User.Identity.AuthenticationType, Scheme.Name, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(Context.User, Scheme.Name)));
        }

        return Task.FromResult(AuthenticateResult.Fail(
            "Dashboard authorization required (configure DashboardAuth.ApiKey or authenticate the host user)."));
    }

    private Task<AuthenticateResult> Success(string name)
    {
        var id = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, name), new Claim("hihooi", "dashboard") },
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name)));
    }

    internal static bool ApiKeyMatches(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
