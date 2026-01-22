using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace BankMore.Gateway;

public static class JwtTokenValidation
{
    public static TokenValidationParameters CreateParameters(IConfiguration cfg)
    {
        var issuer = cfg["JWT:Issuer"] ?? "bankmore";
        var audience = cfg["JWT:Audience"] ?? "bankmore";
        var key = cfg["JWT:SigningKey"] ?? "Zy4m9z4uX4WcV8N7QJp0rQZ3m9M1sJvXJ6K8eK4q8XU=";

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,

            ValidateAudience = true,
            ValidAudience = audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }
}
