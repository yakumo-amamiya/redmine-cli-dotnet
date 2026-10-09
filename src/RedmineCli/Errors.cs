namespace RedmineCli;

/// <summary>
/// Exit codes. The same table is in the root help (Cli.cs), guide.md and README.md; change all of them together.
/// </summary>
internal static class Exit
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>Any other failure (network, file IO, refusing to overwrite).</summary>
    public const int Error = 1;

    /// <summary>Bad arguments, a name that does not resolve, nothing to change.</summary>
    public const int Usage = 2;

    /// <summary>.redmine.json or the API key variable (REDMINE_API_KEY_&lt;suffix&gt;) missing or invalid, unknown identifier.</summary>
    public const int Config = 3;

    /// <summary>Refused by a safety check (outside the target project, no --yes, no --unsafe, declined).</summary>
    public const int Refused = 4;

    /// <summary>The Redmine server returned an error (401/403/404/422 ...).</summary>
    public const int Http = 5;
}

/// <summary>An error shown to the user as "エラー: message" and "ヒント: hint", ending the process with <see cref="ExitCode"/>.</summary>
internal class CliException(string message, int exitCode = Exit.Error, string? hint = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;

    public string? Hint { get; } = hint;
}

/// <summary>The server answered with a status outside 2xx.</summary>
internal sealed class HttpStatusException(string message, int status, string method, string url, IReadOnlyList<string> details, string? hint)
    : CliException(message, Exit.Http, hint)
{
    public int Status { get; } = status;

    public string Method { get; } = method;

    public string Url { get; } = url;

    public IReadOnlyList<string> Details { get; } = details;
}
