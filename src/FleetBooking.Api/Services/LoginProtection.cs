using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using FleetBooking.Shared;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FleetBooking.Api.Services;

public static class LoginProtection
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<LoginAttemptLimiter>();
        builder.Services.AddRateLimiter(limits =>
        {
            limits.AddFixedWindowLimiter("login", policy =>
            {
                policy.PermitLimit = 60;
                policy.Window = TimeSpan.FromMinutes(1);
                policy.QueueLimit = 0;
                policy.AutoReplenishment = true;
            });
            limits.OnRejected = async (rejected, cancellationToken) =>
            {
                rejected.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                var retry = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
                    ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 60;
                rejected.HttpContext.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await rejected.HttpContext.Response.WriteAsJsonAsync(
                    new ApiError("Previše pokušaja prijave. Sačekajte i pokušajte ponovo."), cancellationToken);
            };
        });
    }

}

public sealed class LoginAttemptLimiter(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AttemptWindow> _windows = new(StringComparer.Ordinal);
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);
    private const int MaximumEntries = 4096;

    public bool TryAcquire(string normalizedEmail, out int retrySeconds)
    {
        // Keep only a digest in bounded, expiring memory; never log e-mail addresses or passwords.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (!_windows.TryGetValue(key, out var window) || window.Expires <= now)
            {
                if (_windows.Count >= MaximumEntries)
                    foreach (var expired in _windows.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray())
                        _windows.Remove(expired);
                if (_windows.Count >= MaximumEntries && !_windows.ContainsKey(key))
                {
                    retrySeconds = 60;
                    return false;
                }
                window = new AttemptWindow(now + Duration);
                _windows[key] = window;
            }
            retrySeconds = Math.Max(1, (int)Math.Ceiling((window.Expires - now).TotalSeconds));
            if (window.Attempts >= 10) return false;
            window.Attempts++;
            return true;
        }
    }

    private sealed class AttemptWindow(DateTimeOffset expires)
    {
        public DateTimeOffset Expires { get; } = expires;
        public int Attempts { get; set; }
    }
}
