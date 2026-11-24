using System.Net;
using Identity.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Identity.Application.Auth;

/// <summary>Builds the confirmation and reset emails. Tokens only ever travel inside the link, never into logs.</summary>
public sealed class AuthEmails(IOptions<AuthOptions> options, IEmailSender sender)
{
    private string Base => options.Value.FrontendBaseUrl.TrimEnd('/');

    public ValueTask SendConfirmationAsync(Guid userId, string email, string? fullName, string token, CancellationToken ct)
    {
        var link = $"{Base}/verify-email?userId={userId}&token={Uri.EscapeDataString(token)}";
        var name = WebUtility.HtmlEncode(fullName ?? "there");
        return sender.EnqueueAsync(new EmailMessage(
            email,
            "Confirm your StackShop email",
            $"<p>Hi {name},</p><p>Welcome to StackShop. Please <a href=\"{link}\">confirm your email address</a>.</p><p>The link is valid for {options.Value.ConfirmTokenHours} hours.</p>",
            $"Hi {fullName ?? "there"},\n\nWelcome to StackShop. Confirm your email: {link}\n\nThe link is valid for {options.Value.ConfirmTokenHours} hours."), ct);
    }

    public ValueTask SendPasswordResetAsync(string email, string token, CancellationToken ct)
    {
        var link = $"{Base}/reset-password?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";
        return sender.EnqueueAsync(new EmailMessage(
            email,
            "Reset your StackShop password",
            $"<p>We received a request to reset your password. <a href=\"{link}\">Choose a new password</a>.</p><p>The link is valid for {options.Value.ResetTokenMinutes} minutes. If you did not ask for this, ignore this email.</p>",
            $"Reset your StackShop password: {link}\n\nThe link is valid for {options.Value.ResetTokenMinutes} minutes. If you did not ask for this, ignore this email."), ct);
    }
}
