using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RedmineCli;

/// <summary>The contents of .redmine.json: where this directory tree writes to.</summary>
/// <param name="File">The full path of the file that was found.</param>
/// <param name="Url">Redmine's root URL, without a trailing slash.</param>
/// <param name="Project">The project identifier (not the numeric id).</param>
/// <param name="Env">The alias used as the suffix of the environment variable names instead of the identifier, if any.</param>
internal sealed record RedmineConfig(string File, string Url, string Project, string? Env);

/// <summary>.redmine.json: finding it, validating it, writing it.</summary>
internal static partial class ConfigFile
{
    public const string FileName = ".redmine.json";

    // A Redmine project identifier: ASCII letters, digits, - and _, not digits only. New projects get lower case only, but
    // older Redmines and migrations left upper-case ones, and Redmine's URLs are case-sensitive, so it is kept as written.
    [GeneratedRegex("^(?![0-9]+$)[A-Za-z0-9_-]+$")]
    private static partial Regex Identifier();

    /// <summary>Looks for .redmine.json from startDir up to the root, like git does. Null when there is none.</summary>
    public static string? Find(string? startDir = null)
    {
        var dir = Path.GetFullPath(startDir ?? Directory.GetCurrentDirectory());
        while (true)
        {
            var candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
            {
                return null;
            }
            dir = parent;
        }
    }

    /// <summary>http(s) only; the query, the fragment and trailing slashes are dropped.</summary>
    public static string NormalizeUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            throw new CliException($"url が URL として解釈できません: {input}", Exit.Config);
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new CliException($"url は http または https で始めてください: {input}", Exit.Config);
        }
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static string ValidateIdentifier(string? identifier)
    {
        if (identifier is null || !Identifier().IsMatch(identifier))
        {
            throw new CliException(
                $"project はプロジェクト識別子 (英数字と - _、例: my-project) で指定してください: {Quote(identifier)}",
                Exit.Config,
                "識別子はブラウザでプロジェクトを開いたときの URL (.../projects/<識別子>) に出ます。大文字小文字はそのまま書いてください。数値 id は使いません。");
        }
        return identifier;
    }

    internal static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";

    /// <summary>Reads and validates .redmine.json. Exit code 3 when it is missing or invalid.</summary>
    public static RedmineConfig Load(string? startDir = null)
    {
        var start = Path.GetFullPath(startDir ?? Directory.GetCurrentDirectory());
        var file = Find(start) ?? throw new CliException(
            $"{FileName} が見つかりません ({start} から上位ディレクトリを探索)",
            Exit.Config,
            "リポジトリ直下で `redmine init --url <RedmineのURL> --project <識別子>` を実行して作成してください。");
        JsonNode? raw;
        try
        {
            raw = Json.Parse(File.ReadAllText(file, Encoding.UTF8));
        }
        catch (JsonException e)
        {
            throw new CliException($"{file} を JSON として読めません: {e.Message}", Exit.Config);
        }
        catch (IOException e)
        {
            throw new CliException($"{file} を読めません: {e.Message}", Exit.Config);
        }
        if (raw is not JsonObject obj)
        {
            throw new CliException($"{file} はオブジェクトである必要があります", Exit.Config);
        }
        var url = obj.Get("url").Str();
        if (string.IsNullOrEmpty(url))
        {
            throw new CliException($"{file} に url がありません", Exit.Config);
        }
        var project = obj.Get("project").Str();
        if (string.IsNullOrEmpty(project))
        {
            throw new CliException($"{file} に project がありません", Exit.Config);
        }
        string? env = null;
        var envNode = obj.Get("env");
        if (envNode is not null && envNode.Str() is not "")
        {
            // A number or an object in "env" is not an alias even when its text would pass.
            env = envNode.Str() is { } alias ? EnvNames.ValidateAlias(alias) : throw EnvNames.InvalidAlias(envNode.Text());
        }
        return new RedmineConfig(file, NormalizeUrl(url), ValidateIdentifier(project), env);
    }

    /// <summary>Writes .redmine.json (UTF-8 without a BOM, LF, a final newline). Exit code 1 when it exists and force is off.</summary>
    public static string Write(string dir, string url, string project, string? env, bool force)
    {
        var file = Path.Combine(Path.GetFullPath(dir), FileName);
        if (File.Exists(file) && !force)
        {
            throw new CliException($"{file} は既に存在します", Exit.Error, "上書きするには --force を付けてください。");
        }
        var content = new JsonObject { ["url"] = NormalizeUrl(url), ["project"] = ValidateIdentifier(project) };
        if (!string.IsNullOrEmpty(env))
        {
            content["env"] = EnvNames.ValidateAlias(env);
        }
        File.WriteAllText(file, Json.Format(content) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return file;
    }
}

/// <summary>The environment variable names for the client certificate (mTLS); they share the API key's suffix.</summary>
internal sealed record ClientCertEnvNames(string Cert, string Key, string Password)
{
    /// <summary>Placeholders for messages where no project is known.</summary>
    public static readonly ClientCertEnvNames Placeholder = new(
        EnvNames.ClientCertPrefix + "<識別子>",
        EnvNames.ClientKeyPrefix + "<識別子>",
        EnvNames.ClientCertPasswordPrefix + "<識別子>");
}

/// <summary>
/// The secrets are read from variables named REDMINE_API_KEY_&lt;suffix&gt; and so on; the generic REDMINE_API_KEY is never read.
/// The suffix comes from .redmine.json's env (an alias) if present, else from the project identifier. In the latter case a
/// changed project leaves no matching variable and the CLI stops (the file and the variable are two locks). An alias turns
/// that protection off.
/// </summary>
internal static partial class EnvNames
{
    public const string ApiKeyPrefix = "REDMINE_API_KEY_";
    public const string ClientCertPrefix = "REDMINE_CLIENT_CERT_";
    public const string ClientKeyPrefix = "REDMINE_CLIENT_KEY_";
    public const string ClientCertPasswordPrefix = "REDMINE_CLIENT_CERT_PASSWORD_";

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex Alias();

    public static string ValidateAlias(string? alias) =>
        alias is not null && Alias().IsMatch(alias) ? alias : throw InvalidAlias(ConfigFile.Quote(alias));

    public static CliException InvalidAlias(string shown) => new(
        $"env は英数字と - _ だけで指定してください (例: HOSYU): {shown}",
        Exit.Config,
        "env は環境変数名の接尾辞になります (REDMINE_API_KEY_<env> など)。");

    /// <summary>The alias if any, else the identifier; upper-cased with - turned into _ (hosyu → HOSYU, my-project → MY_PROJECT).</summary>
    public static string Suffix(string project, string? env = null)
    {
        var source = string.IsNullOrEmpty(env) ? ConfigFile.ValidateIdentifier(project) : ValidateAlias(env);
        return source.ToUpperInvariant().Replace('-', '_');
    }

    public static string ApiKey(string project, string? env = null) => ApiKeyPrefix + Suffix(project, env);

    public static string ApiKey(RedmineConfig config) => ApiKey(config.Project, config.Env);

    public static ClientCertEnvNames ClientCert(string project, string? env = null)
    {
        var suffix = Suffix(project, env);
        return new ClientCertEnvNames(ClientCertPrefix + suffix, ClientKeyPrefix + suffix, ClientCertPasswordPrefix + suffix);
    }

    public static ClientCertEnvNames ClientCert(RedmineConfig config) => ClientCert(config.Project, config.Env);

    /// <summary>
    /// The API key, from the project's own variable only. Its value is never printed. Exit code 3 when it is not set.
    /// With url (a .redmine.json exists), the hint points at redmine setup and at the page that shows the key.
    /// </summary>
    public static string GetApiKey(string project, string? env, Func<string, string?>? getEnv = null, string? url = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var name = ApiKey(project, env);
        var key = getEnv(name);
        if (string.IsNullOrWhiteSpace(key))
        {
            var manual = $"PowerShell: setx {name} \"<キー>\" のあと新しいシェルを開く";
            throw new CliException(
                $"環境変数 {name} が設定されていません",
                Exit.Config,
                url is null
                    ? $"プロジェクト「{project}」用の API アクセスキー (Redmine の「個人設定」→「APIアクセスキー」) を、この名前のユーザー環境変数として設定してください ({manual})。変数名は .redmine.json の env (無ければ project) から決まり、汎用の REDMINE_API_KEY は読みません。"
                    : $"このリポジトリで `redmine setup` を端末から実行すると、プロジェクト「{project}」用の API アクセスキーを対話で設定できます (キーは {url}/my/account の「APIアクセスキー」)。手で設定するなら {manual}。変数名は .redmine.json の env (無ければ project) から決まり、汎用の REDMINE_API_KEY は読みません。");
        }
        return key.Trim();
    }
}
