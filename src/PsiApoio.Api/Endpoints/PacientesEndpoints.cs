using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PsiApoio.Api.Data;
using PsiApoio.Api.Domain;
using PsiApoio.Api.Services;

namespace PsiApoio.Api.Endpoints;

public record NovoPacienteReq(string Nome, string Email);
public record ConviteDto(Guid Id, string LinkConvite, bool EmailEnviado);
public record TarefaReq(Guid? Id, string Descricao, Frequencia Frequencia);
public record GatilhoReq(string Texto, NivelGatilho Nivel);
public record ClinicoReq(string? Abordagem, string? Observacoes, List<string>? Temas, List<TarefaReq>? Tarefas, List<GatilhoReq>? Gatilhos);
public record ReprovarReq(string? Motivo);
public record ResumoReq(DateOnly? Dia);

public record PacienteListaDto(
    Guid Id, string Nome, string Email, StatusPaciente Status, PerfilStatus PerfilStatus,
    Nivel NivelAlerta, int AlertasNaoLidos, DateTimeOffset? UltimaMensagemEm);

public record TarefaDto(Guid Id, string Descricao, Frequencia Frequencia);
public record GatilhoDto(Guid Id, string Texto, NivelGatilho Nivel);
public record PacienteDetalheDto(
    Guid Id, string Nome, string Email, StatusPaciente Status, PerfilStatus PerfilStatus, string? PerfilMotivoReprovacao,
    DateTimeOffset? ConsentimentoEm, DateTimeOffset? ConviteEnviadoEm, string? Abordagem, string? Observacoes,
    List<string> Temas, List<TarefaDto> Tarefas, List<GatilhoDto> Gatilhos, FormPessoal? Pessoal);
public record AlertaDto(Guid Id, NivelGatilho Nivel, bool Lido, DateTimeOffset CriadoEm, string Texto);
public record ResumoDto(Guid Id, DateOnly Dia, string Texto, DateTimeOffset GeradoEm);
public record PautaDto(Guid Id, string Texto, List<string> Itens, DateTimeOffset GeradoEm);

public static class PacientesEndpoints
{
    private static readonly string[] Abordagens = ["TCC", "ACT", "Psicanalise", "Humanista", "Sistemica", "Outra"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<Paciente> Obter(AppDbContext db, Guid psicologoId, Guid id, bool completo = true)
    {
        IQueryable<Paciente> q = db.Pacientes;
        if (completo)
            q = q.Include(p => p.Acompanhamento!).ThenInclude(a => a.Temas)
                 .Include(p => p.Acompanhamento!).ThenInclude(a => a.Tarefas)
                 .Include(p => p.Acompanhamento!).ThenInclude(a => a.Gatilhos);
        else
            q = q.Include(p => p.Acompanhamento);
        // Psicólogo só vê os próprios pacientes (ADR 0003): outro psicólogo recebe 404, não 403.
        return await q.FirstOrDefaultAsync(p => p.Id == id && p.PsicologoId == psicologoId && p.Status != StatusPaciente.Removido)
            ?? throw new ApiErro(404, "nao_encontrado", "Paciente não encontrado.");
    }

    public static PacienteDetalheDto Detalhe(Paciente p)
    {
        var a = p.Acompanhamento!;
        return new PacienteDetalheDto(
            p.Id, p.Nome, p.Email, p.Status, a.PerfilStatus, a.PerfilMotivoReprovacao, a.ConsentimentoEm, p.ConviteEnviadoEm,
            a.Abordagem, a.Observacoes,
            a.Temas.OrderBy(t => t.Ordem).Select(t => t.Texto).ToList(),
            a.Tarefas.Where(t => t.Ativa).Select(t => new TarefaDto(t.Id, t.Descricao, t.Frequencia)).ToList(),
            a.Gatilhos.Select(g => new GatilhoDto(g.Id, g.Texto, g.Nivel)).ToList(),
            PerfilBuilder.Pessoal(a));
    }

    private static string Link(IConfiguration cfg, string email, string token) =>
        $"{(cfg["Front:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/')}/definir-senha?email={Uri.EscapeDataString(email)}&token={WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))}";

    private static async Task<bool> EnviarConvite(IEmailSender mail, Paciente p, string link, ILogger log)
    {
        try
        {
            await mail.SendAsync(p.Email, "Convite para o PsiApoio",
                $"Olá, {p.Nome}!\n\nSeu psicólogo convidou você para o PsiApoio. Crie sua senha pelo link abaixo (vale por 72 horas):\n\n{link}\n");
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao enviar convite para o paciente {Id}", p.Id);
            return false;
        }
    }

    public static void MapPacientes(this RouteGroupBuilder root)
    {
        var g = root.MapGroup("/pacientes").RequireAuthorization(Papeis.Psicologo);

        g.MapGet("/", async (HttpContext ctx, AppDbContext db) =>
        {
            var me = ctx.User.Id();
            var pacientes = await db.Pacientes.Include(p => p.Acompanhamento)
                .Where(p => p.PsicologoId == me && p.Status != StatusPaciente.Removido).ToListAsync();
            var acIds = pacientes.Select(p => p.Acompanhamento!.Id).ToList();
            var alertas = await db.Alertas.Where(a => acIds.Contains(a.AcompanhamentoId) && !a.Lido)
                .Select(a => new { a.AcompanhamentoId, a.Nivel }).ToListAsync();
            var ultimas = (await db.Mensagens.Where(m => acIds.Contains(m.AcompanhamentoId))
                .GroupBy(m => m.AcompanhamentoId).Select(x => new { Id = x.Key, Em = x.Max(m => m.CriadaEm) }).ToListAsync())
                .ToDictionary(x => x.Id, x => x.Em);

            var lista = pacientes.Select(p =>
            {
                var meus = alertas.Where(a => a.AcompanhamentoId == p.Acompanhamento!.Id).ToList();
                var nivel = meus.Any(a => a.Nivel == NivelGatilho.Risco) ? Nivel.Risco : meus.Count > 0 ? Nivel.Atencao : Nivel.Nenhum;
                return new PacienteListaDto(p.Id, p.Nome, p.Email, p.Status, p.Acompanhamento!.PerfilStatus, nivel, meus.Count,
                    ultimas.TryGetValue(p.Acompanhamento.Id, out var em) ? em : null);
            })
            .OrderByDescending(x => x.NivelAlerta).ThenByDescending(x => x.UltimaMensagemEm ?? DateTimeOffset.MinValue).ThenBy(x => x.Nome)
            .ToList();
            return Results.Ok(lista);
        });

        g.MapPost("/", async (NovoPacienteReq r, HttpContext ctx, UserManager<AppUser> um, AppDbContext db,
            IEmailSender mail, IConfiguration cfg, ILoggerFactory lf) =>
        {
            var nome = (r.Nome ?? "").Trim();
            var emailAddr = (r.Email ?? "").Trim().ToLowerInvariant();
            if (nome.Length is 0 or > 120) throw new ApiErro(400, "validacao", "Informe o nome do paciente (até 120 caracteres).");
            if (!System.Net.Mail.MailAddress.TryCreate(emailAddr, out _)) throw new ApiErro(400, "validacao", "E-mail inválido.");
            if (await um.FindByEmailAsync(emailAddr) is not null) throw new ApiErro(409, "email_em_uso", "Já existe um usuário com esse e-mail.");

            var user = new AppUser { UserName = emailAddr, Email = emailAddr, Nome = nome };
            var criado = await um.CreateAsync(user);
            if (!criado.Succeeded) throw new ApiErro(400, "validacao", string.Join(" ", criado.Errors.Select(e => e.Description)));
            await um.AddToRoleAsync(user, Papeis.Paciente);

            var p = new Paciente { UsuarioId = user.Id, PsicologoId = ctx.User.Id(), Nome = nome, Email = emailAddr, ConviteEnviadoEm = DateTimeOffset.UtcNow };
            p.Acompanhamento = new Acompanhamento { PacienteId = p.Id };
            db.Pacientes.Add(p);
            await db.SaveChangesAsync();

            var link = Link(cfg, emailAddr, await um.GeneratePasswordResetTokenAsync(user));
            var enviado = await EnviarConvite(mail, p, link, lf.CreateLogger("Convite"));
            return Results.Created($"/api/v1/pacientes/{p.Id}", new ConviteDto(p.Id, link, enviado));
        });

        g.MapPost("/{id:guid}/convite", async (Guid id, HttpContext ctx, UserManager<AppUser> um, AppDbContext db,
            IEmailSender mail, IConfiguration cfg, ILoggerFactory lf) =>
        {
            var p = await Obter(db, ctx.User.Id(), id, completo: false);
            if (p.Status != StatusPaciente.Convidado) throw new ApiErro(409, "convite_desnecessario", "O paciente já definiu a senha.");
            var user = await um.FindByIdAsync(p.UsuarioId.ToString()) ?? throw new ApiErro(404, "nao_encontrado", "Usuário não encontrado.");
            var link = Link(cfg, p.Email, await um.GeneratePasswordResetTokenAsync(user));
            var enviado = await EnviarConvite(mail, p, link, lf.CreateLogger("Convite"));
            p.ConviteEnviadoEm = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new ConviteDto(p.Id, link, enviado));
        });

        g.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var p = await Obter(db, ctx.User.Id(), id, completo: false);
            p.Status = StatusPaciente.Removido; // soft delete (transversal 4.3)
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
            Results.Ok(Detalhe(await Obter(db, ctx.User.Id(), id))));

        g.MapPut("/{id:guid}/clinico", async (Guid id, ClinicoReq r, HttpContext ctx, AppDbContext db) =>
        {
            var p = await Obter(db, ctx.User.Id(), id);
            var a = p.Acompanhamento!;
            var temas = (r.Temas ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            var tarefas = (r.Tarefas ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Descricao)).ToList();
            var gatilhos = (r.Gatilhos ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Texto)).ToList();

            if (string.IsNullOrWhiteSpace(r.Abordagem) || !Abordagens.Contains(r.Abordagem))
                throw new ApiErro(400, "validacao", "Escolha a abordagem.");
            if ((r.Observacoes?.Length ?? 0) > 600) throw new ApiErro(400, "validacao", "Orientações: até 600 caracteres.");
            if (temas.Count > 8 || temas.Any(t => t.Length > 80)) throw new ApiErro(400, "validacao", "Temas: até 8, com até 80 caracteres cada.");
            if (tarefas.Count > 10 || tarefas.Any(t => t.Descricao.Length > 200)) throw new ApiErro(400, "validacao", "Tarefas: até 10, com até 200 caracteres cada.");
            if (gatilhos.Count > 15 || gatilhos.Any(t => t.Texto.Length > 80)) throw new ApiErro(400, "validacao", "Gatilhos: até 15, com até 80 caracteres cada.");

            a.Abordagem = r.Abordagem;
            a.Observacoes = string.IsNullOrWhiteSpace(r.Observacoes) ? null : r.Observacoes.Trim();

            db.Temas.RemoveRange(a.Temas);
            db.Temas.AddRange(temas.Select((t, i) => new Tema { AcompanhamentoId = a.Id, Texto = t, Ordem = i }));
            db.Gatilhos.RemoveRange(a.Gatilhos);
            db.Gatilhos.AddRange(gatilhos.Select(x => new Gatilho { AcompanhamentoId = a.Id, Texto = x.Texto.Trim(), Nivel = x.Nivel }));

            // Tarefas: atualiza por Id; as que sumiram ficam inativas (as marcações antigas permanecem).
            var mantidas = new HashSet<Guid>();
            foreach (var t in tarefas)
            {
                var existente = t.Id is null ? null : a.Tarefas.FirstOrDefault(x => x.Id == t.Id);
                if (existente is null)
                {
                    var nova = new Tarefa { AcompanhamentoId = a.Id, Descricao = t.Descricao.Trim(), Frequencia = t.Frequencia };
                    db.Tarefas.Add(nova);
                    mantidas.Add(nova.Id);
                }
                else
                {
                    existente.Descricao = t.Descricao.Trim();
                    existente.Frequencia = t.Frequencia;
                    existente.Ativa = true;
                    mantidas.Add(existente.Id);
                }
            }
            foreach (var t in a.Tarefas.Where(t => !mantidas.Contains(t.Id))) t.Ativa = false;

            // Editar o perfil o devolve a "pendente" (ou "rascunho" se faltar o formulário pessoal).
            a.PerfilStatus = a.FormPessoalJson is null ? PerfilStatus.Rascunho : PerfilStatus.Pendente;
            a.PerfilMotivoReprovacao = null;
            a.PerfilAprovadoEm = null;
            await db.SaveChangesAsync();

            var atualizado = await Obter(db, ctx.User.Id(), id);
            return Results.Ok(Detalhe(atualizado));
        });

        g.MapPost("/{id:guid}/perfil/aprovar", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = (await Obter(db, ctx.User.Id(), id, completo: false)).Acompanhamento!;
            if (a.PerfilStatus != PerfilStatus.Pendente)
                throw new ApiErro(409, "perfil_nao_pendente", "Só é possível aprovar um perfil pendente (clínico e pessoal preenchidos).");
            a.PerfilStatus = PerfilStatus.Aprovado;
            a.PerfilAprovadoEm = DateTimeOffset.UtcNow;
            a.PerfilMotivoReprovacao = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/perfil/reprovar", async (Guid id, ReprovarReq r, HttpContext ctx, AppDbContext db) =>
        {
            var a = (await Obter(db, ctx.User.Id(), id, completo: false)).Acompanhamento!;
            if (a.PerfilStatus is not (PerfilStatus.Pendente or PerfilStatus.Aprovado))
                throw new ApiErro(409, "perfil_nao_pendente", "Só é possível reprovar um perfil pendente ou aprovado.");
            if ((r.Motivo?.Length ?? 0) > 300) throw new ApiErro(400, "validacao", "Motivo: até 300 caracteres.");
            a.PerfilStatus = PerfilStatus.Reprovado;
            a.PerfilMotivoReprovacao = string.IsNullOrWhiteSpace(r.Motivo) ? null : r.Motivo.Trim();
            a.PerfilAprovadoEm = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/{id:guid}/conversa", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var p = await Obter(db, ctx.User.Id(), id, completo: false);
            var msgs = await db.Mensagens.Where(m => m.AcompanhamentoId == p.Acompanhamento!.Id)
                .OrderByDescending(m => m.CriadaEm).Take(500).ToListAsync();
            msgs.Reverse();
            return Results.Ok(msgs.Select(ConversaService.ParaPsicologo));
        });

        g.MapGet("/{id:guid}/alertas", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var p = await Obter(db, ctx.User.Id(), id, completo: false);
            var lista = await (from a in db.Alertas
                               join m in db.Mensagens on a.MensagemId equals m.Id
                               where a.AcompanhamentoId == p.Acompanhamento!.Id
                               orderby a.CriadoEm descending
                               select new AlertaDto(a.Id, a.Nivel, a.Lido, a.CriadoEm, m.Texto)).Take(50).ToListAsync();
            return Results.Ok(lista);
        });

        g.MapPost("/{id:guid}/alertas/lidos", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var p = await Obter(db, ctx.User.Id(), id, completo: false);
            var abertos = await db.Alertas.Where(a => a.AcompanhamentoId == p.Acompanhamento!.Id && !a.Lido).ToListAsync();
            foreach (var a in abertos) a.Lido = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- D3: Resumo diário e Pauta sob demanda, com cache ----
        g.MapGet("/{id:guid}/resumos", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var p = await Obter(db, ctx.User.Id(), id, completo: false);
            var lista = await db.Resumos.Where(r => r.AcompanhamentoId == p.Acompanhamento!.Id)
                .OrderByDescending(r => r.Dia).Take(14).Select(r => new ResumoDto(r.Id, r.Dia, r.Texto, r.GeradoEm)).ToListAsync();
            return Results.Ok(lista);
        });

        g.MapPost("/{id:guid}/resumos", async (Guid id, ResumoReq r, HttpContext ctx, AppDbContext db, IAgentClient agent, CancellationToken ct) =>
        {
            var p = await Obter(db, ctx.User.Id(), id);
            var a = p.Acompanhamento!;
            var dia = r.Dia ?? Clock.Hoje().AddDays(-1);
            var (ini, fim) = Clock.Limites(dia);
            var msgs = await db.Mensagens.Where(m => m.AcompanhamentoId == a.Id && m.CriadaEm >= ini && m.CriadaEm < fim)
                .OrderBy(m => m.CriadaEm).ToListAsync(ct);
            if (msgs.Count == 0) throw new ApiErro(409, "sem_conversa", "Não há conversa nesse dia.");
            var humores = await db.RegistrosHumor.Where(h => h.AcompanhamentoId == a.Id && h.Dia == dia).Select(h => h.Valor).ToListAsync(ct);
            var ids = a.Tarefas.Select(t => t.Id).ToList();
            var feitas = await db.TarefaMarcacoes.Where(x => x.Dia == dia && ids.Contains(x.TarefaId)).Select(x => x.TarefaId).ToListAsync(ct);

            var texto = await agent.SummaryAsync(new AgentSummaryRequest(
                PerfilBuilder.Montar(a), a.Memoria, dia.ToString("yyyy-MM-dd"),
                msgs.Select(m => new AgentMsg(m.Papel == PapelMensagem.Paciente ? "paciente" : "agente", m.Texto)).ToList(),
                humores,
                a.Tarefas.Where(t => t.Ativa).Select(t => new AgentTarefaDia(t.Descricao, feitas.Contains(t.Id))).ToList()), ct)
                ?? throw new ApiErro(502, "agente_indisponivel", "Não foi possível gerar o resumo agora. Tente de novo.");

            var existente = await db.Resumos.FirstOrDefaultAsync(x => x.AcompanhamentoId == a.Id && x.Dia == dia, ct);
            if (existente is null) db.Resumos.Add(existente = new Resumo { AcompanhamentoId = a.Id, Dia = dia });
            existente.Texto = texto;
            existente.GeradoEm = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ResumoDto(existente.Id, existente.Dia, existente.Texto, existente.GeradoEm));
        });

        g.MapGet("/{id:guid}/pauta", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var p = await Obter(db, ctx.User.Id(), id, completo: false);
            var x = await db.Pautas.Where(x => x.AcompanhamentoId == p.Acompanhamento!.Id).OrderByDescending(x => x.GeradoEm).FirstOrDefaultAsync();
            return x is null
                ? Results.Ok((PautaDto?)null)
                : Results.Ok(new PautaDto(x.Id, x.Texto, JsonSerializer.Deserialize<List<string>>(x.ItensJson, Json) ?? [], x.GeradoEm));
        });

        g.MapPost("/{id:guid}/pauta", async (Guid id, HttpContext ctx, AppDbContext db, IAgentClient agent, CancellationToken ct) =>
        {
            var p = await Obter(db, ctx.User.Id(), id);
            var a = p.Acompanhamento!;
            var desde = DateTimeOffset.UtcNow.AddDays(-30);
            var resumos = await db.Resumos.Where(r => r.AcompanhamentoId == a.Id).OrderByDescending(r => r.Dia).Take(14)
                .Select(r => r.Texto).ToListAsync(ct);
            var temas = await db.Mensagens.Where(m => m.AcompanhamentoId == a.Id && m.TemaPauta != null && m.CriadaEm >= desde)
                .Select(m => m.TemaPauta!).Distinct().ToListAsync(ct);
            if (resumos.Count == 0 && temas.Count == 0)
                throw new ApiErro(409, "sem_material", "Ainda não há resumos nem temas para montar a pauta.");

            var res = await agent.AgendaAsync(new AgentAgendaRequest(PerfilBuilder.Montar(a), resumos, temas), ct)
                ?? throw new ApiErro(502, "agente_indisponivel", "Não foi possível montar a pauta agora. Tente de novo.");
            var pauta = new Pauta { AcompanhamentoId = a.Id, Texto = res.Texto, ItensJson = JsonSerializer.Serialize(res.Itens, Json) };
            db.Pautas.Add(pauta);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new PautaDto(pauta.Id, pauta.Texto, res.Itens, pauta.GeradoEm));
        });
    }
}
