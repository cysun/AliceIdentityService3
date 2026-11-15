using System.Net;

namespace AliceIdentityService.Services;

public sealed record RenderedEmail(string Subject, string TextBody, string HtmlBody);

public sealed class EmailTemplates(string directory)
{
    // Template names are chosen by the sender, never by an HTTP request.
    public async Task<RenderedEmail> RenderAsync(string name, string link, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(name) || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Invalid email template name.", nameof(name));
        var files = await Task.WhenAll(new[] { ".Subject.txt", ".Body.txt", ".Body.html" }
            .Select(suffix => File.ReadAllTextAsync(Path.Combine(directory, name + suffix), cancellationToken)));
        var subject = files[0].Trim();
        if (string.IsNullOrWhiteSpace(subject) || subject.Contains('\r') || subject.Contains('\n'))
            throw new InvalidOperationException("Email subjects must contain a single nonempty line.");
        if (!files[1].Contains("{{link}}", StringComparison.Ordinal) || !files[2].Contains("{{link}}", StringComparison.Ordinal))
            throw new InvalidOperationException("Email action templates must contain {{link}} in both bodies.");
        return new(subject, files[1].Replace("{{link}}", link, StringComparison.Ordinal),
            files[2].Replace("{{link}}", WebUtility.HtmlEncode(link), StringComparison.Ordinal));
    }
}
