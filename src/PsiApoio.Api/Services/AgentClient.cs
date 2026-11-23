using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PsiApoio.Api.Services;

public record AgentMsg([property: JsonPropertyName("papel")] string Papel, [property: JsonPropertyName("texto")] string Texto);
public record AgentGatilho([property: JsonPropertyName("texto")] string Texto, [property: JsonPropertyName("nivel")] string Nivel);

public record AgentRespondRequest(
    [property: JsonPropertyName("perfil_agente")] string PerfilAgente,
    [property: JsonPropertyName("tarefas")] List<string> Tarefas,
    [property: JsonPropertyName("gatilhos")] List<AgentGatilho> Gatilhos,
    [property: JsonPropertyName("memoria")] string Memoria,
    [property: JsonPropertyName("mensagens_recentes")] List<AgentMsg> MensagensRecentes,
    [property: JsonPropertyName("mensagem")] string Mensagem);

public record AgentRespondResponse(
    [property: JsonPropertyName("resposta")] string? Resposta,
    [property: JsonPropertyName("fora_do_escopo")] bool ForaDoEscopo,
    [property: JsonPropertyName("tema_para_pauta")] string? TemaParaPauta,
    [property: JsonPropertyName("humor")] int? Humor,
    [property: JsonPropertyName("risco")] string? Risco,
    [property: JsonPropertyName("motivo_risco")] string? MotivoRisco);

public record AgentTarefaDia([property: JsonPropertyName("descricao")] string Descricao, [property: JsonPropertyName("feita")] bool Feita);

public record AgentSummaryRequest(
    [property: JsonPropertyName("perfil_agente")] string PerfilAgente,
    [property: JsonPropertyName("memoria")] string Memoria,
    [property: JsonPropertyName("dia")] string Dia,
    [property: JsonPropertyName("mensagens")] List<AgentMsg> Mensagens,
    [property: JsonPropertyName("humores")] List<int> Humores,
    [property: JsonPropertyName("tarefas")] List<AgentTarefaDia> Tarefas);

public record AgentAgendaRequest(
    [property: JsonPropertyName("perfil_agente")] string PerfilAgente,
    [property: JsonPropertyName("resumos")] List<string> Resumos,
    [property: JsonPropertyName("temas_para_pauta")] List<string> TemasParaPauta);

public record AgentAgendaResponse(
    [property: JsonPropertyName("texto")] string Texto,
    [property: JsonPropertyName("itens")] List<string> Itens);

/// <summary>Contrato com o serviço Python (specs/transversal.md, seção 3). Falha de rede ou do modelo = null, nunca exceção.</summary>
public interface IAgentClient
{
    Task<AgentRespondResponse?> RespondAsync(AgentRespondRequest req, CancellationToken ct = default);
    Task<string?> UpdateMemoryAsync(string memoria, List<AgentMsg> mensagens, CancellationToken ct = default);
    Task<string?> SummaryAsync(AgentSummaryRequest req, CancellationToken ct = default);
    Task<AgentAgendaResponse?> AgendaAsync(AgentAgendaRequest req, CancellationToken ct = default);
}

public class AgentClient(HttpClient http, ILogger<AgentClient> log) : IAgentClient
{
    private record MemOut([property: JsonPropertyName("memoria")] string Memoria);
    private record TextoOut([property: JsonPropertyName("texto")] string Texto);
    private record MemIn(
        [property: JsonPropertyName("memoria")] string Memoria,
        [property: JsonPropertyName("mensagens")] List<AgentMsg> Mensagens);

    private async Task<T?> Post<T>(string rota, object corpo, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await http.PostAsJsonAsync(rota, corpo, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Agente respondeu {Status} em {Rota}", (int)resp.StatusCode, rota);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning(ex, "Falha ao chamar o agente em {Rota}", rota);
            return null;
        }
    }

    public Task<AgentRespondResponse?> RespondAsync(AgentRespondRequest req, CancellationToken ct = default) =>
        Post<AgentRespondResponse>("/v1/respond", req, ct);

    public async Task<string?> UpdateMemoryAsync(string memoria, List<AgentMsg> mensagens, CancellationToken ct = default) =>
        (await Post<MemOut>("/v1/memory/update", new MemIn(memoria, mensagens), ct))?.Memoria;

    public async Task<string?> SummaryAsync(AgentSummaryRequest req, CancellationToken ct = default) =>
        (await Post<TextoOut>("/v1/summary", req, ct))?.Texto;

    public Task<AgentAgendaResponse?> AgendaAsync(AgentAgendaRequest req, CancellationToken ct = default) =>
        Post<AgentAgendaResponse>("/v1/agenda", req, ct);
}
