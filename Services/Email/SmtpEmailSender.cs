using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace fuseraft.Web.Services.Email;

public interface IEmailSender
{
    /// <summary>Sends a plain-text email; throws if the server refuses it.</summary>
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (!o.Enabled) throw new InvalidOperationException("Email isn't set up: Fuseraft:Email needs a Host and From.");

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(o.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject.ReplaceLineEndings(" ");
        message.Body    = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, o.Security switch
        {
            EmailSecurity.Tls  => SecureSocketOptions.SslOnConnect,
            EmailSecurity.None => SecureSocketOptions.None,
            _                  => SecureSocketOptions.StartTls,
        }, cancellationToken);
        if (!string.IsNullOrWhiteSpace(o.UserName))
            await client.AuthenticateAsync(o.UserName, Environment.GetEnvironmentVariable(EmailOptions.PasswordVariable) ?? "", cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
