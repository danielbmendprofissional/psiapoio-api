using Microsoft.AspNetCore.Identity;

namespace PsiApoio.Api.Domain;

public enum StatusPaciente { Convidado, Ativo, Removido }
public enum PerfilStatus { Rascunho, Pendente, Aprovado, Reprovado }
public enum Nivel { Nenhum, Atencao, Risco }
public enum NivelGatilho { Atencao, Risco }
public enum Frequencia { Diaria, Semanal, SobDemanda }
public enum PapelMensagem { Paciente, Agente }

public static class Papeis
{
    public const string Psicologo = "Psicologo";
    public const string Paciente = "Paciente";
}

public class AppUser : IdentityUser<Guid>
{
    public string Nome { get; set; } = "";
}

public class Paciente
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public Guid PsicologoId { get; set; }
    public string Nome { get; set; } = "";
    public string Email { get; set; } = "";
    public StatusPaciente Status { get; set; } = StatusPaciente.Convidado;
    public DateTimeOffset? ConviteEnviadoEm { get; set; }
    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public Acompanhamento? Acompanhamento { get; set; }
}

public class Acompanhamento
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PacienteId { get; set; }
    public Paciente? Paciente { get; set; }
    public string? Abordagem { get; set; }
    public string? Observacoes { get; set; }
    public string? FormPessoalJson { get; set; }
    public PerfilStatus PerfilStatus { get; set; } = PerfilStatus.Rascunho;
    public string? PerfilMotivoReprovacao { get; set; }
    public DateTimeOffset? PerfilAprovadoEm { get; set; }
    public DateTimeOffset? ConsentimentoEm { get; set; }
    public string Memoria { get; set; } = "";
    public DateTimeOffset? MemoriaAtualizadaEm { get; set; }
    public List<Tema> Temas { get; set; } = [];
    public List<Tarefa> Tarefas { get; set; } = [];
    public List<Gatilho> Gatilhos { get; set; } = [];

    public bool ClinicoCompleto => !string.IsNullOrWhiteSpace(Abordagem);
}

public class Tema
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AcompanhamentoId { get; set; }
    public string Texto { get; set; } = "";
    public int Ordem { get; set; }
}

public class Tarefa
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AcompanhamentoId { get; set; }
    public string Descricao { get; set; } = "";
    public Frequencia Frequencia { get; set; } = Frequencia.Diaria;
    public bool Ativa { get; set; } = true;
}

public class TarefaMarcacao
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TarefaId { get; set; }
    public DateOnly Dia { get; set; }
}

public class Gatilho
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AcompanhamentoId { get; set; }
    public string Texto { get; set; } = "";
    public NivelGatilho Nivel { get; set; }
}

public class Mensagem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AcompanhamentoId { get; set; }
    public PapelMensagem Papel { get; set; }
    public string Texto { get; set; } = "";
    public DateTimeOffset CriadaEm { get; set; } = DateTimeOffset.UtcNow;
    public Nivel? Risco { get; set; }
    public string? MotivoRisco { get; set; }
    public bool ForaDoEscopo { get; set; }
    public string? TemaPauta { get; set; }
    public bool Fixa { get; set; }
}

public class RegistroHumor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AcompanhamentoId { get; set; }
    public Guid MensagemId { get; set; }
    public DateOnly Dia { get; set; }
    public int Valor { get; set; }
}

public class Alerta
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AcompanhamentoId { get; set; }
    public Guid MensagemId { get; set; }
    public NivelGatilho Nivel { get; set; }
    public bool Lido { get; set; }
    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EmailEnviadoEm { get; set; }
}

public class Resumo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AcompanhamentoId { get; set; }
    public DateOnly Dia { get; set; }
    public string Texto { get; set; } = "";
    public DateTimeOffset GeradoEm { get; set; } = DateTimeOffset.UtcNow;
}

public class Pauta
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AcompanhamentoId { get; set; }
    public string Texto { get; set; } = "";
    public string ItensJson { get; set; } = "[]";
    public DateTimeOffset GeradoEm { get; set; } = DateTimeOffset.UtcNow;
}

public record FormPessoal(string ComoPrefereSerChamado, string? Rotina, string? OQueAjuda, string? Evitar, string Tom);
