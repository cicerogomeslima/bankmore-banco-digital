using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = false;

        var issuer = builder.Configuration["JWT:Issuer"] ?? "bankmore";
        var audience = builder.Configuration["JWT:Audience"] ?? "bankmore";
        var key = builder.Configuration["JWT:SigningKey"] ?? throw new InvalidOperationException("JWT:SigningKey não configurado");

        o.TokenValidationParameters = new TokenValidationParameters
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
    });

builder.Services.AddAuthorization();

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/internal/cc"))
    {
        var apiKey = builder.Configuration["Internal:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ctx.Request.Headers["X-Internal-Api-Key"] = apiKey;
        }
    }

    await next();
});
app.MapReverseProxy();

app.Run();

app.MapGet("/swagger", () => Results.Redirect("/conta-corrente/swagger/index.html"));
app.MapGet("/swagger/index.html", () => Results.Redirect("/conta-corrente/swagger/index.html"));

