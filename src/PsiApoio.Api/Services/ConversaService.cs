using Microsoft.EntityFrameworkCore;
using PsiApoio.Api.Data;
using PsiApoio.Api.Domain;

namespace PsiApoio.Api.Services;

public record MsgPaciente(Guid Id, PapelMensagem Papel, string Texto, DateTimeOffset CriadaEm);

public record MsgPsicologo(
    Guid Id, PapelMensagem Papel, string Texto, DateTimeOffset CriadaEm,
    Nivel? Risco, string? MotivoRisco, bool NaoClassificada, bool ForaDoEscopo, string? TemaPauta, bool Fixa);

public record EnvioResultado(MsgPaciente MensagemPaciente, MsgPaciente MensagemAgente, int Restantes);

/// <summary>Conversa paciente ↔ agente (ADR 0001, 0002, 0003). Regras em specs/transversal.md, seções 3 e 4.</summary>
public class ConversaService(
    AppDbContext db, IAgentClient agent, IEmailSender email, IServiceScopeFactory scopes,
    IConfiguration cfg, ILogger<ConversaService> log)
{
    public const string TextoRisco =
        "Obrigado por me contar. O que você escreveu é importante, e eu avisei o seu psicólogo agora. " +
        "Se puder, fale também com alguém de confiança que esteja perto de você.";
    public const string TextoFalha =
        "Não consegui responder agora. Sua mensagem ficou registrada e o seu psicólogo pode vê-la. Tente de novo daqui a pouco.";
    private const int MemoriaACada = 6;

    public int Limite => cfg.GetValue("Conversa:LimiteDiario", 20);

    public static MsgPaciente ParaPaciente(Mensagem m) => new(m.Id, m.Papel, m.Texto, m.CriadaEm);

    public static MsgPsicologo ParaPsicologo(Mensagem m) => new(
        m.Id, m.Papel, m.Texto, m.CriadaEm, m.Risco, m.MotivoRisco,
        NaoClassificada: m.Papel == PapelMensagem.Paciente && m.Risco is null,
        m.ForaDoEscopo, m.TemaPauta, m.Fixa);

    public async Task<int> RestantesAsync(Guid acompanhamentoId, CancellationToken ct)
    {
        var (ini, fim) = Clock.Limites(Clock.Hoje());
        var usadas = await db.Mensagens.CountAsync(m =>
            m.AcompanhamentoId == acompanhamentoId && m.Papel == PapelMensagem.Paciente && m.CriadaEm >= ini && m.CriadaEm < fim, ct);
        return Math.Max(0, Limite - usadas);
    }

    public static string Saudacao(Paciente p, Acompanhamento a)
    {
        var nome = PerfilBuilder.Pessoal(a)?.ComoPrefereSerChamado;
        if (string.IsNullOrWhiteSpace(nome)) nome = p.Nome.Split(' ')[0];
        var tarefas = a.Tarefas.Where(t => t.Ativa && t.Frequencia == Frequencia.Diaria).Select(t => t.Descricao).ToList();
        var parte = tarefas.Count > 0 ? $" Hoje você combinou: {string.Join("; ", tarefas)}." : "";
        return $"Oi, {nome}!{parte} Como você está?";
    }

    public async Task<EnvioResultado> EnviarAsync(Paciente p, Acompanhamento a, string texto, CancellationToken ct)
    {
        texto = (texto ?? "").Trim();
        if (texto.Length == 0 || texto.Length > 2000)
            throw new ApiErro(400, "mensagem_invalida", "A mensagem deve ter entre 1 e 2000 caracteres.");
        if (a.ConsentimentoEm is null)
            throw new ApiErro(409, "sem_consentimento", "É preciso aceitar o consentimento antes de conversar.");
        if (a.PerfilStatus != PerfilStatus.Aprovado)
            throw new ApiErro(409, "perfil_nao_aprovado", "O seu psicólogo ainda está ajustando o acompanhamento.");
        if (await RestantesAsync(a.Id, ct) <= 0)
            throw new ApiErro(429, "limite_diario", "Você chegou ao limite de hoje. Amanhã a gente continua.");

        var recentes = await db.Mensagens.Where(m => m.AcompanhamentoId == a.Id)
            .OrderByDescending(m => m.CriadaEm).Take(10).ToListAsync(ct);
        recentes.Reverse();

        var hoje = Clock.Hoje();
        var idsTarefas = a.Tarefas.Select(t => t.Id).ToList();
        var marcadasHoje = await db.TarefaMarcacoes
            .Where(x => x.Dia == hoje && idsTarefas.Contains(x.TarefaId)).Select(x => x.TarefaId).ToListAsync(ct);
        var memoria = a.Memoria;
        var feitas = a.Tarefas.Where(t => marcadasHoje.Contains(t.Id)).Select(t => t.Descricao).ToList();
        if (feitas.Count > 0) memoria += $"\nHoje marcou como feita: {string.Join("; ", feitas)}.";

        var msgPaciente = new Mensagem { AcompanhamentoId = a.Id, Papel = PapelMensagem.Paciente, Texto = texto };
        db.Mensagens.Add(msgPaciente);
        await db.SaveChangesAsync(ct);

        var resp = await agent.RespondAsync(new AgentRespondRequest(
            PerfilBuilder.Montar(a),
            a.Tarefas.Where(t => t.Ativa).Select(t => $"{t.Descricao} ({PerfilBuilder.FrequenciaTexto(t.Frequencia)})").ToList(),
            a.Gatilhos.Select(g => new AgentGatilho(g.Texto, g.Nivel.ToString().ToLowerInvariant())).ToList(),
            memoria,
            recentes.Select(m => new AgentMsg(m.Papel == PapelMensagem.Paciente ? "paciente" : "agente", m.Texto)).ToList(),
            texto), ct);

        Nivel? risco = resp?.Risco switch { "nenhum" => Nivel.Nenhum, "atencao" => Nivel.Atencao, "risco" => Nivel.Risco, _ => null };
        msgPaciente.Risco = risco;
        msgPaciente.MotivoRisco = resp?.MotivoRisco;

        // ADR 0002: em risco a resposta é fixa; sem classificação nunca vira "nenhum"; sem resposta, texto padrão.
        var fixa = risco == Nivel.Risco || string.IsNullOrWhiteSpace(resp?.Resposta);
        var textoAgente = risco == Nivel.Risco ? TextoRisco : fixa ? TextoFalha : resp!.Resposta!.Trim();
        var msgAgente = new Mensagem
        {
            AcompanhamentoId = a.Id,
            Papel = PapelMensagem.Agente,
            Texto = textoAgente,
            Fixa = fixa,
            ForaDoEscopo = !fixa && (resp?.ForaDoEscopo ?? false),
            TemaPauta = fixa ? null : resp?.TemaParaPauta,
            CriadaEm = msgPaciente.CriadaEm.AddMilliseconds(1),
        };
        db.Mensagens.Add(msgAgente);

        if (!fixa && resp?.Humor is >= 1 and <= 5)
            db.RegistrosHumor.Add(new RegistroHumor { AcompanhamentoId = a.Id, MensagemId = msgPaciente.Id, Dia = Clock.Dia(msgPaciente.CriadaEm), Valor = resp.Humor.Value });

        Alerta? alerta = null;
        if (risco is Nivel.Atencao or Nivel.Risco)
        {
            alerta = new Alerta { AcompanhamentoId = a.Id, MensagemId = msgPaciente.Id, Nivel = risco == Nivel.Risco ? NivelGatilho.Risco : NivelGatilho.Atencao };
            db.Alertas.Add(alerta);
        }
        await db.SaveChangesAsync(ct);

        if (alerta is not null) await AvisarPsicologoAsync(p, alerta, ct);

        var total = await db.Mensagens.CountAsync(m => m.AcompanhamentoId == a.Id && m.Papel == PapelMensagem.Paciente, ct);
        if (total % MemoriaACada == 0) _ = Task.Run(() => AtualizarMemoriaAsync(a.Id));

        return new EnvioResultado(ParaPaciente(msgPaciente), ParaPaciente(msgAgente), await RestantesAsync(a.Id, ct));
    }

    /// <summary>E-mail sem conteúdo clínico (ADR 0002). Falha de e-mail não derruba a conversa.</summary>
    private async Task AvisarPsicologoAsync(Paciente p, Alerta alerta, CancellationToken ct)
    {
        try
        {
            var para = await db.Users.Where(u => u.Id == p.PsicologoId).Select(u => u.Email).FirstOrDefaultAsync(ct);
            if (para is null) return;
            await email.SendAsync(para, "PsiApoio: há um alerta novo no seu painel",
                "Você tem um alerta novo no painel do PsiApoio. Entre para ver. Este e-mail não traz o conteúdo da conversa.", ct);
            alerta.EmailEnviadoEm = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao enviar e-mail de alerta {AlertaId}", alerta.Id);
        }
    }

    private async Task AtualizarMemoriaAsync(Guid acompanhamentoId)
    {
        try
        {
            using var escopo = scopes.CreateScope();
            var ctx = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            var ag = escopo.ServiceProvider.GetRequiredService<IAgentClient>();
            var a = await ctx.Acompanhamentos.FirstOrDefaultAsync(x => x.Id == acompanhamentoId);
            if (a is null) return;
            var msgs = await ctx.Mensagens.Where(m => m.AcompanhamentoId == acompanhamentoId)
                .OrderByDescending(m => m.CriadaEm).Take(MemoriaACada * 2).ToListAsync();
            msgs.Reverse();
            var nova = await ag.UpdateMemoryAsync(a.Memoria,
                msgs.Select(m => new AgentMsg(m.Papel == PapelMensagem.Paciente ? "paciente" : "agente", m.Texto)).ToList());
            if (nova is null) return;
            a.Memoria = nova;
            a.MemoriaAtualizadaEm = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao atualizar a memória de {Id}", acompanhamentoId);
        }
    }
}
