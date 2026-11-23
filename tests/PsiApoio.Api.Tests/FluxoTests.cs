using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PsiApoio.Api.Domain;
using PsiApoio.Api.Services;

namespace PsiApoio.Api.Tests;

public class AuthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AuthTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Login_entrega_o_jwt_em_cookie_httpOnly_e_nao_no_corpo()
    {
        var r = await _f.NovaSessao().LoginAsync("psi@teste.dev", "Senha1234");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cookie = r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("psiapoio_auth="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var corpo = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("eyJ", corpo);
    }

    [Fact]
    public async Task Senha_errada_da_401_e_sem_sessao_usuarios_me_da_401()
    {
        var s = _f.NovaSessao();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.LoginAsync("psi@teste.dev", "errada123")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/usuarios/me")).StatusCode);
    }

    [Fact]
    public async Task Usuarios_me_devolve_o_papel()
    {
        var s = await Cenario.PsicologoAsync(_f);
        var me = await Sessao.Json(await s.Get("/api/v1/usuarios/me"));
        Assert.Equal("Psicologo", me.GetProperty("papel").GetString());
    }

    [Fact]
    public async Task Paciente_nao_acessa_rotas_do_psicologo()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (_, paciente) = await Cenario.PacienteAtivoAsync(_f, psi, email: "auth1@teste.dev");
        Assert.Equal(HttpStatusCode.Forbidden, (await paciente.Get("/api/v1/pacientes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await psi.Get("/api/v1/meu/acompanhamento")).StatusCode);
    }
}

public class PacientesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PacientesTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Cadastro_envia_convite_por_email_e_o_link_define_a_senha()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        await Cenario.PacienteAtivoAsync(_f, psi, email: "convite@teste.dev");
        var mail = Assert.Single(_f.Email.Enviados, e => e.Para == "convite@teste.dev");
        Assert.Contains("/definir-senha?email=", mail.Corpo);
    }

    [Fact]
    public async Task Paciente_convidado_sem_senha_nao_entra()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        await psi.Post("/api/v1/pacientes", new { nome = "Sem Senha", email = "semsenha@teste.dev" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NovaSessao().LoginAsync("semsenha@teste.dev", "Qualquer123")).StatusCode);
    }

    [Fact]
    public async Task Email_repetido_da_409_e_senha_fraca_e_recusada()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var r1 = await psi.Post("/api/v1/pacientes", new { nome = "A", email = "dup@teste.dev" });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await psi.Post("/api/v1/pacientes", new { nome = "B", email = "dup@teste.dev" })).StatusCode);

        var link = (await Sessao.Json(r1)).GetProperty("linkConvite").GetString()!;
        var token = System.Text.RegularExpressions.Regex.Match(link, "token=([^&]+)").Groups[1].Value;
        var fraca = await _f.NovaSessao().Post("/api/v1/auth/definir-senha", new { email = "dup@teste.dev", token, senha = "123" });
        Assert.Equal(HttpStatusCode.BadRequest, fraca.StatusCode);
        Assert.Equal("senha_fraca", (await Sessao.Json(fraca)).GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task Psicologo_so_ve_os_proprios_pacientes()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, _) = await Cenario.PacienteAtivoAsync(_f, psi, email: "iso@teste.dev");
        await _f.CriarPsicologoAsync("outro@teste.dev");
        var outro = await Cenario.PsicologoAsync(_f, "outro@teste.dev");

        Assert.Equal(HttpStatusCode.NotFound, (await outro.Get($"/api/v1/pacientes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outro.Get($"/api/v1/pacientes/{id}/conversa")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outro.Put($"/api/v1/pacientes/{id}/clinico", Cenario.ClinicoPadrao)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outro.Post($"/api/v1/pacientes/{id}/perfil/aprovar")).StatusCode);
        var lista = await Sessao.Json(await outro.Get("/api/v1/pacientes"));
        Assert.Equal(0, lista.GetArrayLength());
    }

    [Fact]
    public async Task Remover_paciente_bloqueia_o_login_e_tira_da_lista()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, paciente) = await Cenario.PacienteAtivoAsync(_f, psi, email: "remover@teste.dev");
        Assert.Equal(HttpStatusCode.NoContent, (await psi.Delete($"/api/v1/pacientes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await paciente.Get("/api/v1/meu/acompanhamento")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.NovaSessao().LoginAsync("remover@teste.dev", "Paciente123")).StatusCode);
        var lista = await Sessao.Json(await psi.Get("/api/v1/pacientes"));
        Assert.DoesNotContain(lista.EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);
    }
}

public class PerfilTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PerfilTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Ciclo_do_perfil_rascunho_pendente_aprovado_e_edicao_volta_a_pendente()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, paciente) = await Cenario.PacienteAtivoAsync(_f, psi, email: "ciclo@teste.dev");
        string Status(JsonElement e) => e.GetProperty("perfilStatus").GetString()!;

        Assert.Equal("rascunho", Status(await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}"))));

        // sem consentimento o formulário pessoal é recusado
        var semConsent = await paciente.Put("/api/v1/meu/pessoal", new { comoPrefereSerChamado = "Li", tom = "direto" });
        Assert.Equal(HttpStatusCode.Conflict, semConsent.StatusCode);

        await paciente.Post("/api/v1/meu/consentimento");
        await psi.Put($"/api/v1/pacientes/{id}/clinico", Cenario.ClinicoPadrao);
        Assert.Equal("rascunho", Status(await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}"))));

        await paciente.Put("/api/v1/meu/pessoal", new { comoPrefereSerChamado = "Li", tom = "direto" });
        Assert.Equal("pendente", Status(await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}"))));

        await psi.Post($"/api/v1/pacientes/{id}/perfil/aprovar");
        Assert.Equal("aprovado", Status(await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}"))));

        await psi.Put($"/api/v1/pacientes/{id}/clinico", Cenario.ClinicoPadrao);
        Assert.Equal("pendente", Status(await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}"))));

        var rep = await psi.Post($"/api/v1/pacientes/{id}/perfil/reprovar", new { motivo = "Ajustar tarefas" });
        Assert.Equal(HttpStatusCode.NoContent, rep.StatusCode);
        var det = await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}"));
        Assert.Equal("reprovado", Status(det));
        Assert.Equal("Ajustar tarefas", det.GetProperty("perfilMotivoReprovacao").GetString());
    }

    [Fact]
    public async Task Clinico_invalido_e_recusado()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, _) = await Cenario.PacienteAtivoAsync(_f, psi, email: "valida@teste.dev");
        var r = await psi.Put($"/api/v1/pacientes/{id}/clinico", new { abordagem = "Astrologia" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Marcacao_da_tarefa_alterna_e_so_vale_uma_por_dia()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (_, paciente) = await Cenario.PacienteProntoAsync(_f, psi);
        var ac = await Sessao.Json(await paciente.Get("/api/v1/meu/acompanhamento"));
        var tarefa = ac.GetProperty("tarefas")[0];
        Assert.False(tarefa.GetProperty("feitaHoje").GetBoolean());
        var tid = tarefa.GetProperty("id").GetGuid();

        Assert.True((await Sessao.Json(await paciente.Post($"/api/v1/meu/tarefas/{tid}/marcacao"))).GetProperty("feita").GetBoolean());
        ac = await Sessao.Json(await paciente.Get("/api/v1/meu/acompanhamento"));
        Assert.True(ac.GetProperty("tarefas")[0].GetProperty("feitaHoje").GetBoolean());
        Assert.False((await Sessao.Json(await paciente.Post($"/api/v1/meu/tarefas/{tid}/marcacao"))).GetProperty("feita").GetBoolean());
    }
}

public class ConversaTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ConversaTests(ApiFactory f) => _f = f;

    private static object Msg(string t) => new { texto = t };

    [Fact]
    public async Task Sem_consentimento_ou_sem_perfil_aprovado_nao_conversa()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (_, paciente) = await Cenario.PacienteAtivoAsync(_f, psi, email: "bloq@teste.dev");
        var r = await paciente.Post("/api/v1/meu/conversa/mensagens", Msg("oi"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("sem_consentimento", (await Sessao.Json(r)).GetProperty("codigo").GetString());

        await paciente.Post("/api/v1/meu/consentimento");
        r = await paciente.Post("/api/v1/meu/conversa/mensagens", Msg("oi"));
        Assert.Equal("perfil_nao_aprovado", (await Sessao.Json(r)).GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task Conversa_normal_envia_perfil_tarefas_e_gatilhos_ao_agente_e_registra_humor()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, paciente) = await Cenario.PacienteProntoAsync(_f, psi);
        _f.Agente.Resposta = new("Que bom que você fez o exercício.", false, "sono", 4, "nenhum", "ok");

        var r = await paciente.Post("/api/v1/meu/conversa/mensagens", Msg("Fiz a caminhada"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var corpo = await Sessao.Json(r);
        Assert.Equal("Que bom que você fez o exercício.", corpo.GetProperty("mensagemAgente").GetProperty("texto").GetString());
        Assert.Equal(19, corpo.GetProperty("restantes").GetInt32());
        // o paciente não recebe a classificação de risco
        Assert.False(corpo.GetProperty("mensagemPaciente").TryGetProperty("risco", out _));

        var req = _f.Agente.UltimaRequisicao!;
        Assert.Contains("Abordagem do psicólogo: TCC", req.PerfilAgente);
        Assert.Contains("Lena", req.PerfilAgente);
        Assert.Contains(req.Tarefas, t => t.StartsWith("Caminhada de 20 min"));
        Assert.Contains(req.Gatilhos, g => g.Texto == "prova" && g.Nivel == "atencao");

        var humores = await _f.NoBancoAsync(db => Task.FromResult(db.RegistrosHumor.Select(h => h.Valor).ToList()));
        Assert.Contains(4, humores);

        // sem alerta em "nenhum"
        var det = await Sessao.Json(await psi.Get("/api/v1/pacientes"));
        Assert.Equal("nenhum", det.EnumerateArray().First(p => p.GetProperty("id").GetGuid() == id).GetProperty("nivelAlerta").GetString());
    }

    [Fact]
    public async Task Risco_descarta_a_resposta_do_llm_usa_a_fixa_cria_alerta_e_avisa_sem_conteudo_clinico()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, paciente) = await Cenario.PacienteProntoAsync(_f, psi);
        _f.Agente.Resposta = new("TEXTO DO LLM QUE NÃO DEVE APARECER", false, null, null, "risco", "ideação");
        var antes = _f.Email.Enviados.Count;

        var r = await paciente.Post("/api/v1/meu/conversa/mensagens", Msg("Às vezes penso que seria melhor eu não existir"));
        var corpo = await Sessao.Json(r);
        var textoAgente = corpo.GetProperty("mensagemAgente").GetProperty("texto").GetString();
        Assert.Equal(ConversaService.TextoRisco, textoAgente);
        Assert.DoesNotContain("LLM", textoAgente);

        var lista = await Sessao.Json(await psi.Get("/api/v1/pacientes"));
        var item = lista.EnumerateArray().First(p => p.GetProperty("id").GetGuid() == id);
        Assert.Equal("risco", item.GetProperty("nivelAlerta").GetString());
        Assert.Equal(1, item.GetProperty("alertasNaoLidos").GetInt32());

        var novos = _f.Email.Enviados.Skip(antes).ToList();
        var aviso = Assert.Single(novos);
        Assert.Equal("psi@teste.dev", aviso.Para);
        Assert.DoesNotContain("existir", aviso.Corpo + aviso.Assunto);
        Assert.DoesNotContain("Helena", aviso.Corpo + aviso.Assunto);

        var transcricao = await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}/conversa"));
        var msgPaciente = transcricao.EnumerateArray().Last(m => m.GetProperty("papel").GetString() == "paciente");
        Assert.Equal("risco", msgPaciente.GetProperty("risco").GetString());

        await psi.Post($"/api/v1/pacientes/{id}/alertas/lidos");
        lista = await Sessao.Json(await psi.Get("/api/v1/pacientes"));
        Assert.Equal("nenhum", lista.EnumerateArray().First(p => p.GetProperty("id").GetGuid() == id).GetProperty("nivelAlerta").GetString());
    }

    [Fact]
    public async Task Mensagem_nao_classificada_nunca_vira_nenhum_e_aparece_marcada_no_painel()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, paciente) = await Cenario.PacienteProntoAsync(_f, psi);
        _f.Agente.Resposta = new("Entendi.", false, null, null, null, null);

        await paciente.Post("/api/v1/meu/conversa/mensagens", Msg("oi"));
        var transcricao = await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}/conversa"));
        var m = transcricao.EnumerateArray().First(x => x.GetProperty("papel").GetString() == "paciente");
        Assert.True(m.GetProperty("naoClassificada").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("risco").ValueKind);
    }

    [Fact]
    public async Task Falha_do_agente_grava_a_mensagem_e_responde_o_texto_padrao()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, paciente) = await Cenario.PacienteProntoAsync(_f, psi);
        _f.Agente.Resposta = null;

        var r = await paciente.Post("/api/v1/meu/conversa/mensagens", Msg("Preciso falar"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(ConversaService.TextoFalha, (await Sessao.Json(r)).GetProperty("mensagemAgente").GetProperty("texto").GetString());
        var transcricao = await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}/conversa"));
        Assert.Contains(transcricao.EnumerateArray(), m => m.GetProperty("texto").GetString() == "Preciso falar");
    }

    [Fact]
    public async Task Limite_diario_de_20_mensagens_devolve_429()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (_, paciente) = await Cenario.PacienteProntoAsync(_f, psi);
        _f.Agente.Resposta = new("ok", false, null, null, "nenhum", "");
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.OK, (await paciente.Post("/api/v1/meu/conversa/mensagens", Msg($"m{i}"))).StatusCode);

        var r = await paciente.Post("/api/v1/meu/conversa/mensagens", Msg("21"));
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal("limite_diario", (await Sessao.Json(r)).GetProperty("codigo").GetString());
        var c = await Sessao.Json(await paciente.Get("/api/v1/meu/conversa"));
        Assert.Equal(0, c.GetProperty("restantes").GetInt32());
    }

    [Fact]
    public async Task Resumo_e_pauta_sao_gerados_sob_demanda_e_ficam_em_cache()
    {
        var psi = await Cenario.PsicologoAsync(_f);
        var (id, paciente) = await Cenario.PacienteProntoAsync(_f, psi);
        _f.Agente.Resposta = new("ok", false, "ansiedade", 3, "nenhum", "");
        await paciente.Post("/api/v1/meu/conversa/mensagens", Msg("Dia puxado"));

        var hoje = Clock.Hoje().ToString("yyyy-MM-dd");
        var r = await psi.Post($"/api/v1/pacientes/{id}/resumos", new { dia = hoje });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Resumo do dia.", (await Sessao.Json(r)).GetProperty("texto").GetString());
        _f.Agente.Resumo = "Resumo atualizado.";
        await psi.Post($"/api/v1/pacientes/{id}/resumos", new { dia = hoje });
        var lista = await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}/resumos"));
        Assert.Equal(1, lista.GetArrayLength());
        Assert.Equal("Resumo atualizado.", lista[0].GetProperty("texto").GetString());

        var semConversa = await psi.Post($"/api/v1/pacientes/{id}/resumos", new { dia = "2020-01-01" });
        Assert.Equal(HttpStatusCode.Conflict, semConversa.StatusCode);

        var pauta = await Sessao.Json(await psi.Post($"/api/v1/pacientes/{id}/pauta"));
        Assert.Equal("Sono", pauta.GetProperty("itens")[0].GetString());
        Assert.Equal("Para a sessão", (await Sessao.Json(await psi.Get($"/api/v1/pacientes/{id}/pauta"))).GetProperty("texto").GetString());
    }
}
