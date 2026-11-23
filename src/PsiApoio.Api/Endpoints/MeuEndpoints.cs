using Microsoft.EntityFrameworkCore;
using PsiApoio.Api.Data;
using PsiApoio.Api.Domain;
using PsiApoio.Api.Services;

namespace PsiApoio.Api.Endpoints;

public record PessoalReq(string? ComoPrefereSerChamado, string? Rotina, string? OQueAjuda, string? Evitar, string? Tom);
public record EnviarReq(string Texto);
public record TarefaDiaDto(Guid Id, string Descricao, Frequencia Frequencia, bool FeitaHoje);
public record MeuAcompanhamentoDto(
    string Nome, PerfilStatus PerfilStatus, DateTimeOffset? ConsentimentoEm, bool PessoalPreenchido, FormPessoal? Pessoal,
    List<TarefaDiaDto> Tarefas, int Limite, int Restantes);
public record MinhaConversaDto(string Saudacao, List<MsgPaciente> Mensagens, int Restantes, int Limite);

/// <summary>Rotas do paciente logado: só enxerga o próprio acompanhamento (ADR 0003).</summary>
public static class MeuEndpoints
{
    private static async Task<(Paciente P, Acompanhamento A)> Carregar(AppDbContext db, HttpContext ctx)
    {
        var uid = ctx.User.Id();
        var p = await db.Pacientes.Include(x => x.Acompanhamento!).ThenInclude(a => a.Tarefas)
            .Include(x => x.Acompanhamento!).ThenInclude(a => a.Temas)
            .Include(x => x.Acompanhamento!).ThenInclude(a => a.Gatilhos)
            .FirstOrDefaultAsync(x => x.UsuarioId == uid)
            ?? throw new ApiErro(404, "nao_encontrado", "Acompanhamento não encontrado.");
        if (p.Status == StatusPaciente.Removido) throw new ApiErro(403, "paciente_removido", "Acesso encerrado.");
        return (p, p.Acompanhamento!);
    }

    public static void MapMeu(this RouteGroupBuilder root)
    {
        var g = root.MapGroup("/meu").RequireAuthorization(Papeis.Paciente);

        g.MapGet("/acompanhamento", async (HttpContext ctx, AppDbContext db, ConversaService conversa, CancellationToken ct) =>
        {
            var (p, a) = await Carregar(db, ctx);
            var hoje = Clock.Hoje();
            var ids = a.Tarefas.Select(t => t.Id).ToList();
            var feitas = await db.TarefaMarcacoes.Where(x => x.Dia == hoje && ids.Contains(x.TarefaId)).Select(x => x.TarefaId).ToListAsync(ct);
            var pessoal = PerfilBuilder.Pessoal(a);
            return Results.Ok(new MeuAcompanhamentoDto(
                p.Nome, a.PerfilStatus, a.ConsentimentoEm, pessoal is not null, pessoal,
                a.Tarefas.Where(t => t.Ativa).Select(t => new TarefaDiaDto(t.Id, t.Descricao, t.Frequencia, feitas.Contains(t.Id))).ToList(),
                conversa.Limite, await conversa.RestantesAsync(a.Id, ct)));
        });

        g.MapPost("/consentimento", async (HttpContext ctx, AppDbContext db) =>
        {
            var (_, a) = await Carregar(db, ctx);
            a.ConsentimentoEm ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPut("/pessoal", async (PessoalReq r, HttpContext ctx, AppDbContext db) =>
        {
            var (_, a) = await Carregar(db, ctx);
            if (a.ConsentimentoEm is null) throw new ApiErro(409, "sem_consentimento", "Aceite o consentimento primeiro.");
            var nome = (r.ComoPrefereSerChamado ?? "").Trim();
            if (nome.Length is 0 or > 60) throw new ApiErro(400, "validacao", "Diga como prefere ser chamado (até 60 caracteres).");
            if (r.Tom is not ("acolhedor" or "direto")) throw new ApiErro(400, "validacao", "Escolha o tom: acolhedor ou direto.");
            string? Limpa(string? s)
            {
                s = s?.Trim();
                if (s?.Length > 200) throw new ApiErro(400, "validacao", "Cada resposta pode ter até 200 caracteres.");
                return string.IsNullOrEmpty(s) ? null : s;
            }
            a.FormPessoalJson = PerfilBuilder.SerializarPessoal(new FormPessoal(nome, Limpa(r.Rotina), Limpa(r.OQueAjuda), Limpa(r.Evitar), r.Tom));
            a.PerfilStatus = a.ClinicoCompleto ? PerfilStatus.Pendente : PerfilStatus.Rascunho;
            a.PerfilMotivoReprovacao = null;
            a.PerfilAprovadoEm = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/conversa", async (HttpContext ctx, AppDbContext db, ConversaService conversa, CancellationToken ct) =>
        {
            var (p, a) = await Carregar(db, ctx);
            var msgs = await db.Mensagens.Where(m => m.AcompanhamentoId == a.Id).OrderByDescending(m => m.CriadaEm).Take(100).ToListAsync(ct);
            msgs.Reverse();
            return Results.Ok(new MinhaConversaDto(
                ConversaService.Saudacao(p, a), msgs.Select(ConversaService.ParaPaciente).ToList(),
                await conversa.RestantesAsync(a.Id, ct), conversa.Limite));
        });

        g.MapPost("/conversa/mensagens", async (EnviarReq r, HttpContext ctx, AppDbContext db, ConversaService conversa, CancellationToken ct) =>
        {
            var (p, a) = await Carregar(db, ctx);
            return Results.Ok(await conversa.EnviarAsync(p, a, r.Texto, ct));
        });

        // Uma marcação por dia; marcar de novo desfaz (a Tarefa é marcada pelo próprio paciente, ADR 0006).
        g.MapPost("/tarefas/{id:guid}/marcacao", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var (_, a) = await Carregar(db, ctx);
            if (!a.Tarefas.Any(t => t.Id == id && t.Ativa)) throw new ApiErro(404, "nao_encontrado", "Tarefa não encontrada.");
            var hoje = Clock.Hoje();
            var m = await db.TarefaMarcacoes.FirstOrDefaultAsync(x => x.TarefaId == id && x.Dia == hoje);
            if (m is null) db.TarefaMarcacoes.Add(new TarefaMarcacao { TarefaId = id, Dia = hoje });
            else db.TarefaMarcacoes.Remove(m);
            await db.SaveChangesAsync();
            return Results.Ok(new { feita = m is null });
        });
    }
}
