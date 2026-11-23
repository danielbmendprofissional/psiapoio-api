using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PsiApoio.Api.Data;
using PsiApoio.Api.Domain;
using PsiApoio.Api.Services;

namespace PsiApoio.Api.Tests;

public class AgenteFalso : IAgentClient
{
    public AgentRespondResponse? Resposta { get; set; } = new("Que bom que você fez o exercício.", false, null, 4, "nenhum", "ok");
    public AgentRespondRequest? UltimaRequisicao { get; private set; }
    public string? Resumo { get; set; } = "Resumo do dia.";
    public AgentAgendaResponse? Pauta { get; set; } = new("Para a sessão", ["Sono"]);

    public Task<AgentRespondResponse?> RespondAsync(AgentRespondRequest req, CancellationToken ct = default)
    {
        UltimaRequisicao = req;
        return Task.FromResult(Resposta);
    }
    public Task<string?> UpdateMemoryAsync(string memoria, List<AgentMsg> mensagens, CancellationToken ct = default) =>
        Task.FromResult<string?>("memória nova");
    public Task<string?> SummaryAsync(AgentSummaryRequest req, CancellationToken ct = default) => Task.FromResult(Resumo);
    public Task<AgentAgendaResponse?> AgendaAsync(AgentAgendaRequest req, CancellationToken ct = default) => Task.FromResult(Pauta);
}

public record EmailEnviado(string Para, string Assunto, string Corpo);

public class EmailFalso : IEmailSender
{
    public List<EmailEnviado> Enviados { get; } = [];
    public Task SendAsync(string para, string assunto, string corpo, CancellationToken ct = default)
    {
        lock (Enviados) Enviados.Add(new(para, assunto, corpo));
        return Task.CompletedTask;
    }
}

public class ApiFactory : WebApplicationFactory<Program>
{
    public AgenteFalso Agente { get; } = new();
    public EmailFalso Email { get; } = new();
    private readonly string _banco = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Testing");
        b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "chave-de-teste-com-mais-de-trinta-e-dois-caracteres-0123",
            ["Seed:PsicologoEmail"] = "psi@teste.dev",
            ["Seed:PsicologoSenha"] = "Senha1234",
            ["Seed:PsicologoNome"] = "Dra. Teste",
            ["Db:AutoCreate"] = "false",
        }));
        b.ConfigureServices(s =>
        {
            s.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_banco));
            s.RemoveAll<IAgentClient>();
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IAgentClient>(Agente);
            s.AddSingleton<IEmailSender>(Email);
        });
    }

    public Sessao NovaSessao() => new(CreateClient());

    public async Task<string> CriarPsicologoAsync(string email, string senha = "Senha1234")
    {
        using var escopo = Services.CreateScope();
        var um = escopo.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = new AppUser { UserName = email, Email = email, Nome = "Outro Psicólogo" };
        Assert.True((await um.CreateAsync(u, senha)).Succeeded);
        await um.AddToRoleAsync(u, Papeis.Psicologo);
        return email;
    }

    public async Task<T> NoBancoAsync<T>(Func<AppDbContext, Task<T>> f)
    {
        using var escopo = Services.CreateScope();
        return await f(escopo.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

/// <summary>Cliente HTTP com o cookie de sessão (o JWT só trafega em cookie httpOnly).</summary>
public class Sessao(HttpClient http)
{
    private string? _cookie;
    public HttpClient Http { get; } = http;

    public async Task<HttpResponseMessage> LoginAsync(string email, string senha)
    {
        var r = await Http.PostAsJsonAsync("/api/v1/auth/login", new { email, senha });
        if (r.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var c = cookies.First(x => x.StartsWith("psiapoio_auth="));
            _cookie = c.Split(';')[0];
        }
        return r;
    }

    private HttpRequestMessage Req(HttpMethod m, string url, object? corpo)
    {
        var req = new HttpRequestMessage(m, url);
        if (_cookie is not null) req.Headers.Add("Cookie", _cookie);
        if (corpo is not null) req.Content = JsonContent.Create(corpo);
        return req;
    }

    public Task<HttpResponseMessage> Get(string url) => Http.SendAsync(Req(HttpMethod.Get, url, null));
    public Task<HttpResponseMessage> Post(string url, object? corpo = null) => Http.SendAsync(Req(HttpMethod.Post, url, corpo ?? new { }));
    public Task<HttpResponseMessage> Put(string url, object corpo) => Http.SendAsync(Req(HttpMethod.Put, url, corpo));
    public Task<HttpResponseMessage> Delete(string url) => Http.SendAsync(Req(HttpMethod.Delete, url, null));

    public static async Task<JsonElement> Json(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>());
}

public static class Cenario
{
    public static readonly object ClinicoPadrao = new
    {
        abordagem = "TCC",
        observacoes = "Tom leve.",
        temas = new[] { "sono" },
        tarefas = new[] { new { descricao = "Caminhada de 20 min", frequencia = "diaria" } },
        gatilhos = new[] { new { texto = "prova", nivel = "atencao" } },
    };

    public static async Task<Sessao> PsicologoAsync(ApiFactory f, string email = "psi@teste.dev", string senha = "Senha1234")
    {
        var s = f.NovaSessao();
        Assert.Equal(HttpStatusCode.OK, (await s.LoginAsync(email, senha)).StatusCode);
        return s;
    }

    /// <summary>Cadastra o paciente, usa o link do convite para definir a senha e devolve (id, sessão logada).</summary>
    public static async Task<(Guid Id, Sessao Paciente)> PacienteAtivoAsync(
        ApiFactory f, Sessao psicologo, string nome = "Helena Prado", string? email = null)
    {
        email ??= $"p{Guid.NewGuid():N}@teste.dev";
        var cad = await psicologo.Post("/api/v1/pacientes", new { nome, email });
        Assert.Equal(HttpStatusCode.Created, cad.StatusCode);
        var dto = await Sessao.Json(cad);
        var link = dto.GetProperty("linkConvite").GetString()!;
        var token = Regex.Match(link, "token=([^&]+)").Groups[1].Value;

        var anon = f.NovaSessao();
        var def = await anon.Post("/api/v1/auth/definir-senha", new { email, token, senha = "Paciente123" });
        Assert.Equal(HttpStatusCode.NoContent, def.StatusCode);

        var paciente = f.NovaSessao();
        Assert.Equal(HttpStatusCode.OK, (await paciente.LoginAsync(email, "Paciente123")).StatusCode);
        return (dto.GetProperty("id").GetGuid(), paciente);
    }

    /// <summary>Perfil aprovado + consentimento dado: o paciente já pode conversar.</summary>
    public static async Task<(Guid Id, Sessao Paciente)> PacienteProntoAsync(ApiFactory f, Sessao psicologo)
    {
        var (id, paciente) = await PacienteAtivoAsync(f, psicologo);
        Assert.Equal(HttpStatusCode.NoContent, (await paciente.Post("/api/v1/meu/consentimento")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await psicologo.Put($"/api/v1/pacientes/{id}/clinico", ClinicoPadrao)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await paciente.Put("/api/v1/meu/pessoal",
            new { comoPrefereSerChamado = "Lena", tom = "acolhedor" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await psicologo.Post($"/api/v1/pacientes/{id}/perfil/aprovar")).StatusCode);
        return (id, paciente);
    }
}
