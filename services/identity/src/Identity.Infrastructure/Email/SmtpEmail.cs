using System.Threading.Channels;
using Identity.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Identity.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public bool UseStartTls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "no-reply@stackshop.local";
    public string FromName { get; set; } = "StackShop";
}

/// <summary>
/// Queues messages in memory; <see cref="EmailDispatcher"/> delivers them over SMTP. Decoupling the request from SMTP keeps
/// register/forgot-password latency identical for existing and unknown accounts (BR-07) and survives a slow mail server.
/// Messages still queued during a crash are lost; the outbox events will take over once the notification service exists.
/// </summary>
public sealed class QueuedEmailSender : IEmailSender
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ILogger<QueuedEmailSender> _logger;

    public QueuedEmailSender(ILogger<QueuedEmailSender> logger) => _logger = logger;

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!_channel.Writer.TryWrite(message))
            _logger.LogError("Email queue is full; a message was dropped");
        return ValueTask.CompletedTask;
    }
}

public sealed class EmailDispatcher(QueuedEmailSender queue, IOptions<EmailOptions> options, ILogger<EmailDispatcher> logger) : BackgroundService
{
    private const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await SendAsync(message, stoppingToken);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Deliberately no recipient, subject or body in the log: they contain an email address and a one-time token.
                    logger.LogWarning(ex, "Email delivery failed (attempt {Attempt}/{Max})", attempt, MaxAttempts);
                    if (attempt == MaxAttempts) break;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), stoppingToken);
                }
            }
        }
    }

    private async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, o.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, ct);
        if (!string.IsNullOrEmpty(o.Username))
            await client.AuthenticateAsync(o.Username, o.Password ?? string.Empty, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
