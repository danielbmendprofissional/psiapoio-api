using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PsiApoio.Api.Domain;

namespace PsiApoio.Api.Data;

/// <summary>
/// Inicialização: em Development cria o esquema com a role dona (ConnectionStrings:PostgresMigrations) e
/// semeia os papéis e o psicólogo de dev. Fora de dev nada é criado aqui (migrations são etapa de deploy).
/// </summary>
public static class Seed
{
    public static async Task RodarAsync(WebApplication app)
    {
        var cfg = app.Configuration;
        if (cfg.GetValue("Db:AutoCreate", app.Environment.IsDevelopment()))
        {
            var dono = cfg.GetConnectionString("PostgresMigrations") ?? cfg.GetConnectionString("Postgres");
            if (dono is not null)
            {
                var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(dono).Options;
                await using var ctxDono = new AppDbContext(opts);
                await ctxDono.Database.EnsureCreatedAsync();
            }
        }

        using var escopo = app.Services.CreateScope();
        var roles = escopo.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var papel in new[] { Papeis.Psicologo, Papeis.Paciente })
            if (!await roles.RoleExistsAsync(papel))
                await roles.CreateAsync(new IdentityRole<Guid>(papel));

        var email = cfg["Seed:PsicologoEmail"] ?? (app.Environment.IsDevelopment() ? "psicologo@psiapoio.dev" : null);
        var senha = cfg["Seed:PsicologoSenha"] ?? (app.Environment.IsDevelopment() ? "Psi12345" : null);
        if (email is null || senha is null) return;

        var um = escopo.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await um.FindByEmailAsync(email) is null)
        {
            var u = new AppUser { UserName = email, Email = email, Nome = cfg["Seed:PsicologoNome"] ?? "Dra. Marina Alves" };
            var r = await um.CreateAsync(u, senha);
            if (!r.Succeeded) throw new InvalidOperationException("Seed do psicólogo falhou: " + string.Join("; ", r.Errors.Select(e => e.Description)));
            await um.AddToRoleAsync(u, Papeis.Psicologo);
        }
    }
}
