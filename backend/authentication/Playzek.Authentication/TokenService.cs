using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Playzek.Authentication;

public sealed record AccessToken(string Token, DateTime ExpiresAt);

/// <summary>Emite token-uri de acces JWT semnate (HS256), cu durată scurtă.</summary>
public sealed class TokenService
{
    public const string PlayerTokenType = "player";
    public const string TokenTypeClaim = "pzk_typ";

    private readonly JwtOptions _options;
    private readonly TimeProvider _time;
    private readonly SymmetricSecurityKey _key;

    public TokenService(IOptions<JwtOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        _key = CreateKey(_options.SigningKey);
    }

    public static SymmetricSecurityKey CreateKey(string signingKey)
    {
        var bytes = Encoding.UTF8.GetBytes(signingKey ?? "");
        if (bytes.Length < 32)
            throw new InvalidOperationException(
                "Playzek:Jwt:SigningKey lipsește sau are sub 32 de octeți. Setează variabila de mediu Playzek__Jwt__SigningKey.");
        return new SymmetricSecurityKey(bytes);
    }

    public AccessToken CreatePlayerToken(Guid userId, string username)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = now.Add(_options.AccessTokenLifetime);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(TokenTypeClaim, PlayerTokenType),
            ]),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        };
        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    public TokenValidationParameters ValidationParameters() => CreateValidationParameters(_options, _key, _time);

    /// <summary>Durata de viață e verificată cu același TimeProvider ca restul aplicației.</summary>
    public static TokenValidationParameters CreateValidationParameters(JwtOptions options, SecurityKey key,
        TimeProvider time) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        LifetimeValidator = (notBefore, expires, _, p) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            return expires is { } exp && now <= exp.Add(p.ClockSkew) &&
                   (notBefore is not { } nbf || now >= nbf.Subtract(p.ClockSkew));
        },
        NameClaimType = JwtRegisteredClaimNames.UniqueName,
    };
}
