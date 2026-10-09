using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace RedmineCli;

/// <summary>The client certificate files named by the project's variables, read but not decoded yet.</summary>
/// <param name="KeyFile">The PEM key file, for display (set even for a PFX, where it is not read).</param>
internal sealed record ClientCertFiles(
    ClientCertEnvNames Names, string CertFile, string? KeyFile, bool IsPfx, byte[] CertData, byte[]? KeyData, string? Password)
{
    public string Format => IsPfx ? "pfx" : "pem";
}

internal enum CertProblem
{
    MissingPassword,
    WrongPassword,
    BadFormat,
    KeyMismatch,
    NoPrivateKey,
}

/// <summary>The certificate files were read but could not be decoded. Exit code 1, naming the variable to fix.</summary>
internal sealed class CertLoadException(string message, CertProblem problem, string detail, string hint) : CliException(message, Exit.Error, hint)
{
    public CertProblem Problem { get; } = problem;

    /// <summary>The short reason doctor shows on its line.</summary>
    public string Detail { get; } = detail;
}

/// <summary>The certificate offered to the server, with the rest of the chain found in the same file.</summary>
internal sealed class ClientCertificate(X509Certificate2 certificate, X509Certificate2Collection chain) : IDisposable
{
    public X509Certificate2 Certificate { get; } = certificate;

    public X509Certificate2Collection Chain { get; } = chain;

    public void Dispose()
    {
        // On Windows the private key lives in a key file of the user's profile until the certificate is disposed.
        Certificate.Dispose();
        foreach (var certificate in Chain)
        {
            certificate.Dispose();
        }
    }
}

/// <summary>
/// The client certificate (mTLS), from the project's variables:
/// - REDMINE_CLIENT_CERT_&lt;suffix&gt;: the certificate file. PKCS#12 when it ends with .pfx / .p12, else PEM;
/// - REDMINE_CLIENT_KEY_&lt;suffix&gt;: the PEM private key (not needed when the certificate file holds the key too);
/// - REDMINE_CLIENT_CERT_PASSWORD_&lt;suffix&gt;: the PFX password or the PEM key's passphrase (optional).
/// </summary>
internal static partial class ClientCertificates
{
    [GeneratedRegex("-----BEGIN (RSA |EC )?PRIVATE KEY-----")]
    private static partial Regex PlainKey();

    /// <summary>Loads the certificate, or null when the project has none. Exit code 3 when a file cannot be read, 1 when it cannot be decoded.</summary>
    public static ClientCertificate? Load(ClientCertEnvNames names, Func<string, string?>? getEnv = null)
    {
        var files = Read(names, getEnv);
        return files is null ? null : Decode(files);
    }

    /// <summary>Reads the files the variables point to; null when REDMINE_CLIENT_CERT_&lt;suffix&gt; is not set.</summary>
    public static ClientCertFiles? Read(ClientCertEnvNames names, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var certFile = getEnv(names.Cert)?.Trim();
        if (string.IsNullOrEmpty(certFile))
        {
            return null;
        }
        var keyFile = getEnv(names.Key)?.Trim();
        var keyAbs = string.IsNullOrEmpty(keyFile) ? null : Path.GetFullPath(keyFile);
        var password = getEnv(names.Password);
        var (certAbs, certData) = ReadSecretFile(certFile, names.Cert);
        var isPfx = certAbs.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || certAbs.EndsWith(".p12", StringComparison.OrdinalIgnoreCase);
        var keyData = !isPfx && keyAbs is not null ? ReadSecretFile(keyAbs, names.Key).Data : null;
        return new ClientCertFiles(names, certAbs, keyAbs, isPfx, certData, keyData, string.IsNullOrEmpty(password) ? null : password);
    }

    private static (string Abs, byte[] Data) ReadSecretFile(string file, string envName)
    {
        var abs = Path.GetFullPath(file);
        try
        {
            return (abs, File.ReadAllBytes(abs));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var reason = e is FileNotFoundException or DirectoryNotFoundException ? "ファイルがありません" : e.Message;
            throw new CliException(
                $"環境変数 {envName} が指すファイルを読めません: {abs} ({reason})",
                Exit.Config,
                "パスが正しいか、ファイルが存在するか確認してください。値はファイルの中身ではなくパスです。");
        }
    }

    /// <summary>Decodes (and decrypts) the certificate and its key.</summary>
    public static ClientCertificate Decode(ClientCertFiles files) => files.IsPfx ? DecodePfx(files) : DecodePem(files);

    private static ClientCertificate DecodePfx(ClientCertFiles files)
    {
        var names = files.Names;
        // A PKCS#12 file is DER: it starts with a SEQUENCE. Anything else (a PEM renamed to .pfx) is a format problem, not a password one.
        if (files.CertData.Length == 0 || files.CertData[0] != 0x30)
        {
            throw BadFormat(names, "PKCS#12 (PFX) ではありません");
        }
        X509Certificate2Collection all;
        try
        {
            // UserKeySet: Windows' TLS (SChannel) needs the key in a key container; ephemeral keys cannot sign the handshake.
            all = X509CertificateLoader.LoadPkcs12Collection(files.CertData, files.Password, X509KeyStorageFlags.UserKeySet);
        }
        catch (CryptographicException e) when (e.HResult == unchecked((int)0x80070056) || e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw files.Password is null ? MissingPassword(names, pfx: true) : WrongPassword(names, pfx: true);
        }
        catch (CryptographicException e)
        {
            throw BadFormat(names, e.Message);
        }
        var leaf = all.FirstOrDefault(c => c.HasPrivateKey);
        if (leaf is null)
        {
            foreach (var c in all)
            {
                c.Dispose();
            }
            throw new CertLoadException("PFX に秘密鍵が入っていません", CertProblem.NoPrivateKey, "PFX に秘密鍵が入っていません",
                "秘密鍵ごとエクスポートした PFX を使ってください (Windows の証明書のエクスポートで「秘密キーをエクスポートする」を選ぶ)。");
        }
        var chain = new X509Certificate2Collection();
        foreach (var c in all)
        {
            if (!ReferenceEquals(c, leaf))
            {
                chain.Add(c);
            }
        }
        return new ClientCertificate(leaf, chain);
    }

    private static ClientCertificate DecodePem(ClientCertFiles files)
    {
        var names = files.Names;
        var certPem = Encoding.UTF8.GetString(files.CertData);
        var keyPem = files.KeyData is null ? certPem : Encoding.UTF8.GetString(files.KeyData);
        if (!certPem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            throw BadFormat(names, "証明書 (BEGIN CERTIFICATE) がありません");
        }
        if (keyPem.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
        {
            const string legacy = "従来形式の暗号化 PEM 鍵 (Proc-Type: 4,ENCRYPTED) は読めません";
            throw new CertLoadException(legacy, CertProblem.BadFormat, legacy,
                "openssl pkcs8 -topk8 -in <鍵> -out <新しい鍵> で PKCS#8 形式に変換するか、PFX にしてください。");
        }
        var encrypted = keyPem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);
        if (!encrypted && !PlainKey().IsMatch(keyPem))
        {
            throw new CertLoadException("クライアント証明書の秘密鍵が見つかりません", CertProblem.NoPrivateKey, "秘密鍵が見つかりません",
                $"鍵が別のファイルなら、そのパスを {names.Key} に設定してください。");
        }
        if (encrypted && files.Password is null)
        {
            throw MissingPassword(names, pfx: false);
        }
        X509Certificate2 certificate;
        try
        {
            certificate = encrypted
                ? X509Certificate2.CreateFromEncryptedPem(certPem, keyPem, files.Password)
                : X509Certificate2.CreateFromPem(certPem, keyPem);
        }
        catch (CryptographicException e) when (encrypted && e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw WrongPassword(names, pfx: false);
        }
        catch (CryptographicException)
        {
            // The key's label was found above, so what is left is a key that does not belong to the certificate (or a broken key).
            throw new CertLoadException("クライアント証明書と秘密鍵が対応していません", CertProblem.KeyMismatch,
                "証明書と秘密鍵が対応していないか、鍵の形式を読めません", $"別の証明書の鍵を指していないか ({names.Key})、鍵のファイルが壊れていないか確認してください。");
        }
        if (OperatingSystem.IsWindows())
        {
            // A key read from PEM is ephemeral, which Windows' TLS cannot use; round-trip it through PKCS#12 to get a key container.
            using var ephemeral = certificate;
            certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        }
        var all = new X509Certificate2Collection();
        all.ImportFromPem(certPem);
        var chain = new X509Certificate2Collection();
        for (var i = 0; i < all.Count; i++)
        {
            if (i == 0)
            {
                all[i].Dispose();
            }
            else
            {
                chain.Add(all[i]);
            }
        }
        return new ClientCertificate(certificate, chain);
    }

    private static CertLoadException MissingPassword(ClientCertEnvNames names, bool pfx) => new(
        pfx
            ? $"クライアント証明書 (PFX) はパスワードで保護されていますが、環境変数 {names.Password} が設定されていません"
            : $"クライアント証明書の秘密鍵はパスフレーズで暗号化されていますが、環境変数 {names.Password} が設定されていません",
        CertProblem.MissingPassword,
        "パスフレーズで暗号化されていますが未設定です",
        $"{names.Password} にパスワード (パスフレーズ) を設定してください。");

    private static CertLoadException WrongPassword(ClientCertEnvNames names, bool pfx) => new(
        pfx ? "クライアント証明書 (PFX) を開けません。パスワードが合いません" : "クライアント証明書の秘密鍵を復号できません。パスフレーズが合いません",
        CertProblem.WrongPassword,
        "パスフレーズが合いません",
        pfx ? $"PFX のパスワード ({names.Password}) が合っているか確認してください。" : $"{names.Password} に正しいパスフレーズを設定してください。");

    private static CertLoadException BadFormat(ClientCertEnvNames names, string reason) => new(
        $"クライアント証明書を読めません ({reason})",
        CertProblem.BadFormat,
        $"ファイルの形式を読めません ({reason})",
        $"PFX なら拡張子を .pfx か .p12 に、PEM なら証明書と鍵のファイルを {names.Cert} / {names.Key} に正しく割り当ててください。");
}

/// <summary>
/// Extra root CAs for a proxy that decrypts TLS (swaps the certificates). Windows' certificate store is used anyway, which is
/// where a company usually puts its root CA; these are for a CA that is not there. REDMINE_EXTRA_CA_CERTS names a PEM file;
/// NODE_EXTRA_CA_CERTS (the Node version's variable) is read when it is not set.
/// </summary>
internal static class ExtraRoots
{
    public const string EnvName = "REDMINE_EXTRA_CA_CERTS";
    public const string NodeEnvName = "NODE_EXTRA_CA_CERTS";

    /// <summary>The variable in use and its file, or null when neither is set.</summary>
    public static (string EnvName, string File)? Source(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        foreach (var name in new[] { EnvName, NodeEnvName })
        {
            var value = getEnv(name)?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return (name, Path.GetFullPath(value));
            }
        }
        return null;
    }

    public static X509Certificate2Collection Load(Func<string, string?>? getEnv = null)
    {
        var roots = new X509Certificate2Collection();
        if (Source(getEnv) is not var (name, file))
        {
            return roots;
        }
        try
        {
            roots.ImportFromPemFile(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new CliException($"環境変数 {name} が指すファイルを読めません: {file} ({e.Message})", Exit.Config,
                "社内ルート CA の証明書 (PEM 形式) のパスを指定してください。");
        }
        if (roots.Count == 0)
        {
            throw new CliException($"環境変数 {name} が指すファイルに PEM の証明書がありません: {file}", Exit.Config,
                "-----BEGIN CERTIFICATE----- で始まる PEM 形式のファイルを指定してください。");
        }
        return roots;
    }
}
