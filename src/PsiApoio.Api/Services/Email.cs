using System.Net.Mail;

namespace PsiApoio.Api.Services;

public interface IEmailSender
{
    Task SendAsync(string para, string assunto, string corpo, CancellationToken ct = default);
}

/// <summary>SMTP configurável por chave (Smtp:*). Em dev vai ao Mailpit (localhost:1025).</summary>
public class SmtpEmailSender(IConfiguration cfg) : IEmailSender
{
    public async Task SendAsync(string para, string assunto, string corpo, CancellationToken ct = default)
    {
        using var client = new SmtpClient(cfg["Smtp:Host"] ?? "localhost", int.Parse(cfg["Smtp:Port"] ?? "1025"))
        {
            EnableSsl = bool.Parse(cfg["Smtp:Ssl"] ?? "false"),
        };
        if (!string.IsNullOrEmpty(cfg["Smtp:User"]))
            client.Credentials = new System.Net.NetworkCredential(cfg["Smtp:User"], cfg["Smtp:Password"]);
        using var msg = new MailMessage(cfg["Smtp:From"] ?? "psiapoio@localhost", para, assunto, corpo);
        await client.SendMailAsync(msg, ct);
    }
}
