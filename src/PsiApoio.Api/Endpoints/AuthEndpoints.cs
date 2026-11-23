using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PsiApoio.Api.Data;
using PsiApoio.Api.Domain;
using PsiApoio.Api.Services;

namespace PsiApoio.Api.Endpoints;

public record LoginReq(string Email, string Senha);
public record DefinirSenhaReq(string Email, string Token, string Senha);
public record UsuarioDto(Guid Id, string Nome, string Email, string Papel);

public static class AuthEndpoints
{
    public static void MapAuth(this RouteGroupBuilder g)
    {
        g.MapPost("/auth/login", async (LoginReq r, UserManager<AppUser> um, AppDbContext db, TokenService tokens, HttpContext ctx) =>
        {
            var user = string.IsNullOrWhiteSpace(r.Email) ? null : await um.FindByEmailAsync(r.Email.Trim());
            if (user is null || string.IsNullOrEmpty(r.Senha) || !await um.CheckPasswordAsync(user, r.Senha))
                throw new ApiErro(401, "credenciais_invalidas", "E-mail ou senha incorretos.");

            var papel = (await um.GetRolesAsync(user)).FirstOrDefault()
                ?? throw new ApiErro(403, "sem_papel", "Usuário sem papel.");
            if (papel == Papeis.Paciente)
            {
                var status = await db.Pacientes.Where(p => p.UsuarioId == user.Id).Select(p => p.Status).FirstAsync();
                if (status == StatusPaciente.Removido) throw new ApiErro(403, "paciente_removido", "Acesso encerrado.");
            }
            tokens.Emitir(ctx, user, papel);
            return Results.Ok(new UsuarioDto(user.Id, user.Nome, user.Email!, papel));
        });

        g.MapPost("/auth/logout", (TokenService tokens, HttpContext ctx) =>
        {
            tokens.Limpar(ctx);
            return Results.NoContent();
        });

        g.MapPost("/auth/definir-senha", async (DefinirSenhaReq r, UserManager<AppUser> um, AppDbContext db) =>
        {
            var user = string.IsNullOrWhiteSpace(r.Email) ? null : await um.FindByEmailAsync(r.Email.Trim());
            string token;
            try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(r.Token ?? "")); }
            catch (FormatException) { throw new ApiErro(400, "token_invalido", "Link inválido ou expirado."); }
            if (user is null) throw new ApiErro(400, "token_invalido", "Link inválido ou expirado.");

            var res = await um.ResetPasswordAsync(user, token, r.Senha ?? "");
            if (!res.Succeeded)
            {
                var senhaFraca = res.Errors.Any(e => e.Code.StartsWith("Password"));
                throw new ApiErro(400, senhaFraca ? "senha_fraca" : "token_invalido",
                    senhaFraca ? "A senha precisa ter ao menos 8 caracteres, com letra e número." : "Link inválido ou expirado.");
            }
            var p = await db.Pacientes.FirstOrDefaultAsync(x => x.UsuarioId == user.Id);
            if (p is { Status: StatusPaciente.Convidado })
            {
                p.Status = StatusPaciente.Ativo;
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        });

        g.MapGet("/usuarios/me", async (HttpContext ctx, UserManager<AppUser> um, AppDbContext db) =>
        {
            var user = await um.FindByIdAsync(ctx.User.Id().ToString());
            if (user is null) throw new ApiErro(401, "nao_autenticado", "Sessão inválida.");
            var papel = (await um.GetRolesAsync(user)).First();
            if (papel == Papeis.Paciente
                && await db.Pacientes.AnyAsync(p => p.UsuarioId == user.Id && p.Status == StatusPaciente.Removido))
                throw new ApiErro(403, "paciente_removido", "Acesso encerrado.");
            return Results.Ok(new UsuarioDto(user.Id, user.Nome, user.Email!, papel));
        }).RequireAuthorization();
    }
}
