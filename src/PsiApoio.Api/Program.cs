using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Chave do contrato de infra: ConnectionStrings:Postgres (user-secrets em dev, nunca em appsettings).
builder.Services.AddSingleton(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Postgres ausente. Configure via user-secrets (ver README).");
    return NpgsqlDataSource.Create(connectionString);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Liveness: o processo está de pé.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness: o Postgres responde com as credenciais dos secrets.
app.MapGet("/health/ready", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    try
    {
        await using var cmd = db.CreateCommand("select 1");
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ok", postgres = "ok" });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Postgres indisponível");
        return Results.Json(new { status = "degraded", postgres = "indisponivel" }, statusCode: 503);
    }
});

app.Run();
