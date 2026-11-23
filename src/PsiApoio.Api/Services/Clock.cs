namespace PsiApoio.Api.Services;

/// <summary>"Dia" do produto = dia em America/Sao_Paulo (specs/transversal.md, 4.1).</summary>
public static class Clock
{
    private static readonly TimeZoneInfo Tz = Resolver();

    private static TimeZoneInfo Resolver()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT");
    }

    public static DateOnly Dia(DateTimeOffset instante) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, Tz).DateTime);

    public static DateOnly Hoje() => Dia(DateTimeOffset.UtcNow);

    /// <summary>[inicio, fim) do dia, em UTC.</summary>
    public static (DateTimeOffset Inicio, DateTimeOffset Fim) Limites(DateOnly dia)
    {
        var ini = dia.ToDateTime(TimeOnly.MinValue);
        var offset = Tz.GetUtcOffset(ini);
        var inicio = new DateTimeOffset(ini, offset).ToUniversalTime();
        return (inicio, inicio.AddDays(1));
    }
}
