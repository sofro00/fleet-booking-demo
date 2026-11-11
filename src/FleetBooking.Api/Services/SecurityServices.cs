using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FleetBooking.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace FleetBooking.Api.Services;

public sealed class TokenService(IConfiguration configuration)
{
    public string Create(AppUser user, bool rememberMe = false)
    {
        var secret = configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret nije podešen.");
        if (Encoding.UTF8.GetByteCount(secret) < 32) throw new InvalidOperationException("Jwt:Secret mora imati najmanje 32 bajta.");
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var token = new JwtSecurityToken(configuration["Jwt:Issuer"], configuration["Jwt:Audience"], claims,
            expires: rememberMe ? DateTime.UtcNow.AddDays(30) : DateTime.UtcNow.AddHours(8), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static string DisplayName(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name) ?? "Nepoznat korisnik";
}
