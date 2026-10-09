using System.Text;

namespace RedmineCli;

/// <summary>Text given inline, from a file, or from stdin ("-").</summary>
internal static class Input
{
    /// <summary>All of stdin as UTF-8, whatever the console's code page is.</summary>
    public static string ReadStdin()
    {
        using var stream = Console.OpenStandardInput();
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>--label TEXT or --label-file FILE (- for stdin); null when neither is given.</summary>
    public static string? ReadTextOption(string? inline, string? file, string label)
    {
        if (inline is not null && file is not null)
        {
            throw new CliException($"--{label} と --{label}-file は同時に指定できません", Exit.Usage);
        }
        if (inline is not null)
        {
            return inline;
        }
        if (file is null)
        {
            return null;
        }
        if (file == "-")
        {
            return ReadStdin();
        }
        try
        {
            return File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"--{label}-file を読めません: {file} ({e.Message})", Exit.Usage);
        }
    }
}
