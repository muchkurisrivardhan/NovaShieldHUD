using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Beacon.Assistant;

/// <summary>
/// Reads the user's inbox over IMAP, the standard every mail service speaks, with an app
/// password: free, no developer registration, nothing in between Beacon and the mail server.
/// Read-only: nothing is marked as read, moved, deleted or sent. Replies open as a draft the
/// user sends themselves.
/// </summary>
public sealed class EmailInbox(Func<EmailAccount?> account)
{
    public sealed record Message(UniqueId Id, string From, string FromAddress, string Subject, DateTimeOffset Date, string MessageId);

    List<Message> _last = [];

    /// <summary>IMAP servers for common addresses, so most people type only their address and app password.</summary>
    public static string? HostFor(string address)
    {
        var domain = address.Split('@').LastOrDefault()?.Trim().ToLowerInvariant() ?? "";
        return domain switch
        {
            "gmail.com" or "googlemail.com" => "imap.gmail.com",
            "yahoo.com" or "yahoo.co.in" or "yahoo.co.uk" or "ymail.com" => "imap.mail.yahoo.com",
            "icloud.com" or "me.com" or "mac.com" => "imap.mail.me.com",
            "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => "outlook.office365.com",
            "aol.com" => "imap.aol.com",
            "zoho.com" or "zohomail.com" => "imap.zoho.com",
            "gmx.com" or "gmx.net" => "imap.gmx.com",
            "proton.me" or "protonmail.com" => null,   // only through Proton Bridge, on 127.0.0.1
            _ => domain.Length > 0 ? "imap." + domain : null,
        };
    }

    public static bool IsGmail(string address) => HostFor(address) == "imap.gmail.com";

    async Task<ImapClient> ConnectAsync(EmailAccount a, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 20_000 };
        try
        {
            await client.ConnectAsync(a.Host, a.Port, SecureSocketOptions.SslOnConnect, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(a.Address, a.Password, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Signs in and out, for the Settings page's Test button. Null when it worked.</summary>
    public async Task<string?> TestAsync(CancellationToken ct)
    {
        if (account() is not { } a) return "Enter your email address and app password first.";
        try
        {
            using var client = await ConnectAsync(a, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception e) when (e is AuthenticationException or ImapProtocolException or ImapCommandException
                                       or System.Net.Sockets.SocketException or IOException or SslHandshakeException or TimeoutException)
        {
            return Explain(e, a);
        }
    }

    /// <summary>The newest unread messages, numbered for the conversation ("the second one").</summary>
    public async Task<string> CheckAsync(CancellationToken ct)
    {
        if (account() is not { } a)
            return "Email isn't set up. The user can add their address and an app password in Beacon Settings, on the Assistant page.";
        try
        {
            using var client = await ConnectAsync(a, ct).ConfigureAwait(false);
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            var unread = await inbox.SearchAsync(SearchQuery.NotSeen, ct).ConfigureAwait(false);
            var newest = unread.Reverse().Take(5).ToList();
            var summaries = newest.Count == 0 ? [] : await inbox.FetchAsync(newest,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);

            _last = summaries
                .OrderByDescending(m => m.Envelope.Date ?? m.InternalDate ?? DateTimeOffset.MinValue)
                .Select(m =>
                {
                    var from = m.Envelope.From.Mailboxes.FirstOrDefault();
                    return new Message(m.UniqueId, from?.Name is { Length: > 0 } n ? n : from?.Address ?? "someone",
                        from?.Address ?? "", m.Envelope.Subject ?? "(no subject)",
                        m.Envelope.Date ?? m.InternalDate ?? DateTimeOffset.Now, m.Envelope.MessageId ?? "");
                })
                .ToList();

            if (_last.Count == 0) return "No unread emails.";
            var sb = new StringBuilder();
            sb.AppendLine(unread.Count > _last.Count
                ? $"{unread.Count} unread emails; the newest {_last.Count}:"
                : $"{_last.Count} unread email{(_last.Count == 1 ? "" : "s")}:");
            for (var i = 0; i < _last.Count; i++)
                sb.AppendLine($"{i + 1}. From {_last[i].From} <{_last[i].FromAddress}>, subject \"{_last[i].Subject}\", {When(_last[i].Date)}");
            return sb.ToString().TrimEnd();
        }
        catch (Exception e) when (e is AuthenticationException or ImapProtocolException or ImapCommandException
                                       or System.Net.Sockets.SocketException or IOException or SslHandshakeException or TimeoutException)
        {
            return "Couldn't reach the inbox: " + Explain(e, a);
        }
    }

    /// <summary>One message's text, for summarising. Fetched with PEEK: it stays unread.</summary>
    public async Task<string> ReadAsync(int number, CancellationToken ct)
    {
        if (account() is not { } a) return "Email isn't set up.";
        if (Pick(number) is not { } m) return NoSuch(number);
        try
        {
            using var client = await ConnectAsync(a, ct).ConfigureAwait(false);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            var message = await client.Inbox.GetMessageAsync(m.Id, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);

            var body = message.TextBody ?? StripHtml(message.HtmlBody ?? "");
            body = Regex.Replace(body, @"(\r?\n\s*){3,}", "\n\n").Trim();
            // Quoted earlier messages and signatures say little and fill the model's small window.
            var quote = Regex.Match(body, @"\n(On .{5,80} wrote:|-----Original Message-----|From: .+\nSent: )", RegexOptions.IgnoreCase);
            if (quote.Success) body = body[..quote.Index].Trim();
            if (body.Length > 3000) body = body[..3000] + " [...]";
            return $"Email {number}\nFrom: {m.From} <{m.FromAddress}>\nSubject: {m.Subject}\nDate: {When(m.Date)}\n\n{body}";
        }
        catch (Exception e) when (e is AuthenticationException or ImapProtocolException or ImapCommandException or MessageNotFoundException
                                       or System.Net.Sockets.SocketException or IOException or SslHandshakeException or TimeoutException)
        {
            return "Couldn't open that email: " + Explain(e, a);
        }
    }

    /// <summary>The message in the browser: the exact message for Gmail, the webmail inbox otherwise.</summary>
    public Task<string> OpenAsync(int number)
    {
        if (account() is not { } a) return Task.FromResult("Email isn't set up.");
        if (Pick(number) is not { } m) return Task.FromResult(NoSuch(number));
        var url = IsGmail(a.Address) && m.MessageId.Length > 0
            ? "https://mail.google.com/mail/u/0/#search/rfc822msgid%3A" + Uri.EscapeDataString(m.MessageId)
            : Webmail(a.Host);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return Task.FromResult($"Opened email {number} ({m.Subject}) in the browser.");
    }

    /// <summary>A reply draft with the text filled in. The user reads it and presses Send.</summary>
    public Task<string> ReplyAsync(int number, string text)
    {
        if (account() is not { } a) return Task.FromResult("Email isn't set up.");
        if (Pick(number) is not { } m) return Task.FromResult(NoSuch(number));
        var subject = m.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? m.Subject : "Re: " + m.Subject;
        var url = IsGmail(a.Address)
            ? $"https://mail.google.com/mail/?view=cm&fs=1&to={Uri.EscapeDataString(m.FromAddress)}&su={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(text)}"
            : $"mailto:{m.FromAddress}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(text)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return Task.FromResult($"Opened a reply draft to {m.From}. It is not sent: the user checks it and presses Send.");
    }

    Message? Pick(int number) => number >= 1 && number <= _last.Count ? _last[number - 1] : null;

    string NoSuch(int number) => _last.Count == 0
        ? "There's no list of emails yet: check the inbox first."
        : $"There's no email {number}; the last list had {_last.Count}.";

    static string When(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return local.Date == DateTime.Today ? "today " + local.ToString("h:mm tt")
            : local.Date == DateTime.Today.AddDays(-1) ? "yesterday " + local.ToString("h:mm tt")
            : local.ToString("ddd d MMM");
    }

    static string Webmail(string host) => host switch
    {
        "imap.mail.yahoo.com" => "https://mail.yahoo.com/",
        "imap.mail.me.com" => "https://www.icloud.com/mail/",
        "outlook.office365.com" => "https://outlook.live.com/mail/",
        "imap.aol.com" => "https://mail.aol.com/",
        "imap.zoho.com" => "https://mail.zoho.com/",
        _ => "https://" + host.Replace("imap.", ""),
    };

    static string StripHtml(string html)
    {
        var t = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<br\s*/?>|</p>|</div>|</tr>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = WebUtility.HtmlDecode(t);
        return Regex.Replace(t, @"[ \t]{2,}", " ");
    }

    static string Explain(Exception e, EmailAccount a) => e switch
    {
        AuthenticationException when a.Host == "outlook.office365.com" =>
            "Outlook and Hotmail no longer accept app passwords for IMAP, so this can't sign in yet.",
        AuthenticationException when a.Host == "imap.gmail.com" =>
            "Gmail refused the password. It needs an app password (Google Account > Security > 2-Step Verification > App passwords), not your normal one.",
        AuthenticationException => "the server refused the password. Many services need an app password here, not your normal one.",
        System.Net.Sockets.SocketException or IOException or TimeoutException => $"couldn't connect to {a.Host}. Check the internet connection and the server name.",
        _ => e.Message,
    };
}

/// <summary>The account in plain text, for the length of one call; stored encrypted in Settings.</summary>
public sealed record EmailAccount(string Address, string Password, string Host, int Port);
