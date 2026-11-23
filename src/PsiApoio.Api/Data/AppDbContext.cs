using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PsiApoio.Api.Domain;

namespace PsiApoio.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Paciente> Pacientes => Set<Paciente>();
    public DbSet<Acompanhamento> Acompanhamentos => Set<Acompanhamento>();
    public DbSet<Tema> Temas => Set<Tema>();
    public DbSet<Tarefa> Tarefas => Set<Tarefa>();
    public DbSet<TarefaMarcacao> TarefaMarcacoes => Set<TarefaMarcacao>();
    public DbSet<Gatilho> Gatilhos => Set<Gatilho>();
    public DbSet<Mensagem> Mensagens => Set<Mensagem>();
    public DbSet<RegistroHumor> RegistrosHumor => Set<RegistroHumor>();
    public DbSet<Alerta> Alertas => Set<Alerta>();
    public DbSet<Resumo> Resumos => Set<Resumo>();
    public DbSet<Pauta> Pautas => Set<Pauta>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<StatusPaciente>().HaveConversion<string>();
        b.Properties<PerfilStatus>().HaveConversion<string>();
        b.Properties<Nivel>().HaveConversion<string>();
        b.Properties<NivelGatilho>().HaveConversion<string>();
        b.Properties<Frequencia>().HaveConversion<string>();
        b.Properties<PapelMensagem>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);

        m.Entity<Paciente>(e =>
        {
            e.HasIndex(p => p.UsuarioId).IsUnique();
            e.HasIndex(p => p.PsicologoId);
            e.HasOne(p => p.Acompanhamento).WithOne(a => a.Paciente).HasForeignKey<Acompanhamento>(a => a.PacienteId);
        });
        m.Entity<Acompanhamento>(e =>
        {
            e.HasIndex(a => a.PacienteId).IsUnique();
            e.Property(a => a.FormPessoalJson).HasColumnType("jsonb");
            e.HasMany(a => a.Temas).WithOne().HasForeignKey(t => t.AcompanhamentoId);
            e.HasMany(a => a.Tarefas).WithOne().HasForeignKey(t => t.AcompanhamentoId);
            e.HasMany(a => a.Gatilhos).WithOne().HasForeignKey(t => t.AcompanhamentoId);
            e.Ignore(a => a.ClinicoCompleto);
        });
        m.Entity<TarefaMarcacao>(e =>
        {
            e.HasIndex(t => new { t.TarefaId, t.Dia }).IsUnique();
            e.HasOne<Tarefa>().WithMany().HasForeignKey(t => t.TarefaId);
        });
        m.Entity<Mensagem>(e =>
        {
            e.HasIndex(x => new { x.AcompanhamentoId, x.CriadaEm });
            e.HasOne<Acompanhamento>().WithMany().HasForeignKey(x => x.AcompanhamentoId);
        });
        m.Entity<RegistroHumor>().HasIndex(x => new { x.AcompanhamentoId, x.Dia });
        m.Entity<Alerta>(e =>
        {
            e.HasIndex(x => new { x.AcompanhamentoId, x.Lido });
            e.HasOne<Acompanhamento>().WithMany().HasForeignKey(x => x.AcompanhamentoId);
        });
        m.Entity<Resumo>(e =>
        {
            e.HasIndex(x => new { x.AcompanhamentoId, x.Dia }).IsUnique();
            e.HasOne<Acompanhamento>().WithMany().HasForeignKey(x => x.AcompanhamentoId);
        });
        m.Entity<Pauta>().HasOne<Acompanhamento>().WithMany().HasForeignKey(x => x.AcompanhamentoId);
    }
}
