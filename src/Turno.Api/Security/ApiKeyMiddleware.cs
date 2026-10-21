using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Turno.Contracts;

namespace Turno.Api.Security;

public sealed class ApiOptions
{
    public const string SectionName = "Turno:Api";

    public string ApiKey { get; set; } = string.Empty;
    public List<string> AllowedOrigins { get; set; } = [];
    public bool EnableApiReference { get; set; } = true;
}

public static class ApiKeyComparer
{
    public static bool Matches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var left = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiOptions> options)
{
    private static readonly PathString Api = new("/api");
    private static readonly PathString Hubs = new("/hubs");
    private static readonly PathString Info = new("/api/info");

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var expected = options.Value.ApiKey;
        var path = context.Request.Path;
        var protectedPath = (path.StartsWithSegments(Api) && !path.StartsWithSegments(Info)) || path.StartsWithSegments(Hubs);

        if (string.IsNullOrEmpty(expected) || !protectedPath || HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var provided = context.Request.Headers[TurnoHeaders.ApiKey].FirstOrDefault();
        if (string.IsNullOrEmpty(provided) && path.StartsWithSegments(Hubs))
        {
            provided = context.Request.Query[TurnoHeaders.AccessTokenQuery].FirstOrDefault();
        }

        if (ApiKeyComparer.Matches(provided, expected))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"ApiKey header=\"{TurnoHeaders.ApiKey}\"";
        await Results.Problem(
            title: "Chave de acesso inválida ou ausente.",
            detail: $"Envie a chave configurada em Turno:Api:ApiKey no cabeçalho {TurnoHeaders.ApiKey}.",
            statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (!context.Request.Path.StartsWithSegments("/docs") && !context.Request.Path.StartsWithSegments("/scalar"))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
        }

        return next(context);
    }
}
