using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PsiApoio.Api.Domain;

namespace PsiApoio.Api.Services;

/// <summary>Erro de regra de negócio: vira application/problem+json com o campo extra "codigo".</summary>
public class ApiErro(int status, string codigo, string mensagem) : Exception(mensagem)
{
    public int Status { get; } = status;
    public string Codigo { get; } = codigo;
}

public static class UsuarioAtual
{
    public static Guid Id(this ClaimsPrincipal u) =>
        Guid.TryParse(u.FindFirstValue("sub"), out var id) ? id : throw new ApiErro(401, "nao_autenticado", "Sessão inválida.");
}

public class TokenService(IConfiguration cfg, IHostEnvironment env)
{
    public const string Cookie = "psiapoio_auth";
    public const int Minutos = 60;

    public static SymmetricSecurityKey Chave(IConfiguration cfg)
    {
        var k = cfg["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(k) || k.Length < 32)
            throw new InvalidOperationException("Jwt:Key ausente ou curta (mín. 32 caracteres). Configure via user-secrets (ver README).");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k));
    }

    /// <summary>JWT só com sub e papel (sem dado clínico), entregue em cookie httpOnly (ADR 0006).</summary>
    public void Emitir(HttpContext ctx, AppUser u, string papel)
    {
        var expira = DateTime.UtcNow.AddMinutes(Minutos);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity([new("sub", u.Id.ToString()), new("role", papel)]),
            Expires = expira,
            Issuer = "psiapoio",
            Audience = "psiapoio",
            SigningCredentials = new SigningCredentials(Chave(cfg), SecurityAlgorithms.HmacSha256),
        });
        ctx.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = !env.IsDevelopment(),
            Expires = expira,
            Path = "/",
        });
    }

    public void Limpar(HttpContext ctx) => ctx.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
}

public static class PerfilBuilder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static FormPessoal? Pessoal(Acompanhamento a) =>
        string.IsNullOrWhiteSpace(a.FormPessoalJson) ? null : JsonSerializer.Deserialize<FormPessoal>(a.FormPessoalJson, Json);

    public static string SerializarPessoal(FormPessoal f) => JsonSerializer.Serialize(f, Json);

    public static string Wire(Nivel n) => n.ToString().ToLowerInvariant();

    /// <summary>Texto do perfil_agente a partir dos formulários (specs/transversal.md, 3.3).</summary>
    public static string Montar(Acompanhamento a)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(a.Abordagem)) sb.AppendLine($"Abordagem do psicólogo: {a.Abordagem}");
        var temas = a.Temas.OrderBy(t => t.Ordem).Select(t => t.Texto).ToList();
        if (temas.Count > 0) sb.AppendLine($"Temas-foco: {string.Join("; ", temas)}");
        if (!string.IsNullOrWhiteSpace(a.Observacoes)) sb.AppendLine($"Orientações do psicólogo: {a.Observacoes}");
        var p = Pessoal(a);
        if (p is not null)
        {
            sb.AppendLine($"O paciente prefere ser chamado de: {p.ComoPrefereSerChamado}");
            if (!string.IsNullOrWhiteSpace(p.Rotina)) sb.AppendLine($"Rotina: {p.Rotina}");
            if (!string.IsNullOrWhiteSpace(p.OQueAjuda)) sb.AppendLine($"O que ajuda quando está mal: {p.OQueAjuda}");
            if (!string.IsNullOrWhiteSpace(p.Evitar)) sb.AppendLine($"Evitar: {p.Evitar}");
            sb.AppendLine($"Tom preferido: {p.Tom}");
        }
        return sb.ToString().Trim();
    }

    public static string FrequenciaTexto(Frequencia f) => f switch
    {
        Frequencia.Diaria => "diária",
        Frequencia.Semanal => "semanal",
        _ => "sob demanda",
    };
}
