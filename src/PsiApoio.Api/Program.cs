using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PsiApoio.Api.Data;
using PsiApoio.Api.Domain;
using PsiApoio.Api.Endpoints;
using PsiApoio.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

// Chave do contrato de infra: ConnectionStrings:Postgres (user-secrets em dev, nunca em appsettings).
var conexao = builder.Configuration.GetConnectionString("Postgres");
if (conexao is not null)
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conexao));

builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();
// Convite: token de redefinição de senha válido por 72 h (ADR 0006).
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(72));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            IssuerSigningKey = TokenService.Chave(builder.Configuration),
            ValidIssuer = "psiapoio",
            ValidAudience = "psiapoio",
            NameClaimType = "sub",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // O JavaScript nunca vê o token: ele vem do cookie httpOnly (ADR 0006).
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                ctx.Token = ctx.Request.Cookies[TokenService.Cookie];
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Papeis.Psicologo, p => p.RequireRole(Papeis.Psicologo));
    o.AddPolicy(Papeis.Paciente, p => p.RequireRole(Papeis.Paciente));
});

builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<ConversaService>();
builder.Services.AddHttpClient<IAgentClient, AgentClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Agent:BaseUrl"] ?? "http://localhost:8000");
    c.Timeout = TimeSpan.FromSeconds(90);
    var chave = builder.Configuration["Agent:ApiKey"];
    if (!string.IsNullOrEmpty(chave)) c.DefaultRequestHeaders.Add("X-Api-Key", chave);
});

var app = builder.Build();

// Erros de regra viram problem+json com o campo "codigo" (specs/transversal.md, 4.7).
app.UseExceptionHandler(h => h.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, codigo, msg) = ex is ApiErro e
        ? (e.Status, e.Codigo, e.Message)
        : (500, "erro_interno", "Erro interno.");
    if (ex is not ApiErro) app.Logger.LogError(ex, "Erro não tratado");
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/problem+json";
    await ctx.Response.WriteAsJsonAsync(new Dictionary<string, object> { ["status"] = status, ["title"] = msg, ["codigo"] = codigo });
}));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await Seed.RodarAsync(app);

app.UseAuthentication();
app.UseAuthorization();

// Liveness: o processo está de pé.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness: o Postgres responde com as credenciais dos secrets.
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
{
    try
    {
        await db.Database.CanConnectAsync(ct);
        return Results.Ok(new { status = "ok", postgres = "ok" });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Postgres indisponível");
        return Results.Json(new { status = "degraded", postgres = "indisponivel" }, statusCode: 503);
    }
});

var api = app.MapGroup("/api/v1");
api.MapAuth();
api.MapPacientes();
api.MapMeu();

app.Run();

public partial class Program;
