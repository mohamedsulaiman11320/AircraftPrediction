using System.Security.Claims;
using System.Text;
using AFP.Application.Abstractions;
using AFP.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AFP.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public const int MinimumKeyBytes = 32;

    public string Issuer { get; set; } = "AircraftFailurePrediction";
    public string Audience { get; set; } = "AircraftFailurePrediction.Web";

    /// <summary>HMAC-SHA256 signing secret. Supply via the Jwt__SigningKey environment variable or user-secrets — never in source.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 60;

    public SymmetricSecurityKey GetKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey ?? string.Empty);
        if (bytes.Length < MinimumKeyBytes)
            throw new InvalidOperationException(
                $"Jwt:SigningKey must be configured with at least {MinimumKeyBytes} bytes (set the Jwt__SigningKey environment variable or dotnet user-secrets).");
        return new SymmetricSecurityKey(bytes);
    }
}

public static class AfpClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string TokenId = JwtRegisteredClaimNames.Jti;
    public const string Role = "role";
    public const string Name = "name";
    public const string Email = "email";
    public const string Username = "preferred_username";
    public const string SecurityStamp = "afp_sstamp";
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    public IssuedToken Issue(AppUser user)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var expires = now.AddMinutes(o.ExpiryMinutes);
        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new(AfpClaims.Subject, user.Id.ToString()),
            new(AfpClaims.TokenId, jti),
            new(AfpClaims.Email, user.Email),
            new(AfpClaims.Name, user.DisplayName),
            new(AfpClaims.SecurityStamp, user.SecurityStamp),
            new(AfpClaims.Username, user.Username),
        };
        // One claim per active user_role assignment.
        claims.AddRange(user.ActiveRoles.Select(role => new Claim(AfpClaims.Role, role.ToString())));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(o.GetKey(), SecurityAlgorithms.HmacSha256),
        });
        return new IssuedToken(token, jti, expires);
    }
}

/// <summary>PBKDF2 hashing via ASP.NET Core Identity's PasswordHasher (format v3, HMAC-SHA512, 100k iterations by default).</summary>
public sealed class IdentityPasswordHasher : Application.Abstractions.IPasswordHasher
{
    private readonly PasswordHasher<AppUser> _inner = new();

    public string Hash(AppUser user, string password) => _inner.HashPassword(user, password);

    public bool Verify(AppUser user, string password, out bool needsRehash)
    {
        needsRehash = false;
        if (string.IsNullOrEmpty(user.PasswordHash)) return false;
        var result = _inner.VerifyHashedPassword(user, user.PasswordHash, password);
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
