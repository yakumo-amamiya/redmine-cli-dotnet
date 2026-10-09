using System.Security.Cryptography.X509Certificates;
using static RedmineCli.Tests.CliRunner;

namespace RedmineCli.Tests;

/// <summary>
/// A mock Redmine over HTTPS that requires a client certificate signed by the test CA. The certificates in Fixtures/tls are
/// self-signed test ones (made with openssl for the Node version); they are good for nothing else.
/// </summary>
public sealed class TlsFixture : IAsyncLifetime
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tls", name);

    public MockRedmine Mock { get; private set; } = null!;

    public string Workdir { get; } = TempDir("redmine-cli-tls-");

    public async Task InitializeAsync()
    {
        var ca = X509CertificateLoader.LoadCertificateFromFile(Fixture("ca.pem"));
        var server = X509Certificate2.CreateFromPemFile(Fixture("server.pem"), Fixture("server.key"));
        if (OperatingSystem.IsWindows())
        {
            // Windows' TLS needs the key in a key container, not the ephemeral one PEM gives.
            server = X509CertificateLoader.LoadPkcs12(server.Export(X509ContentType.Pkcs12), null);
        }
        Mock = await MockRedmine.StartAsync(https =>
        {
            https.ServerCertificate = server;
            https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            };
        });
        Mock.Route("GET /projects/demo.json", """{ "project": { "id": 1, "name": "Demo", "identifier": "demo" } }""");
        Mock.Route("GET /users/current.json", """{ "user": { "id": 7, "login": "tester", "firstname": "T", "lastname": "U" } }""");
        WriteConfig(Workdir, Mock.BaseUrl, "demo");
    }

    public Task DisposeAsync() => Mock.DisposeAsync().AsTask();
}

/// <summary>Connecting with a client certificate (mTLS): PEM, PFX, passphrases, and doctor (the Node version's test/tls.test.js).</summary>
public sealed class TlsTests(TlsFixture fixture) : IClassFixture<TlsFixture>
{
    private static string Fixture(string name) => TlsFixture.Fixture(name);

    private Task<CliResult> RunWith(Dictionary<string, string?> env, params string[] args)
    {
        // The test CA, as a company's root CA would be given to a client outside Windows' store.
        env.TryAdd("REDMINE_EXTRA_CA_CERTS", Fixture("ca.pem"));
        return RunAsync(fixture.Workdir, args, env);
    }

    private Task<CliResult> Run(params string[] args) => RunWith(new Dictionary<string, string?>(), args);

    [Fact]
    public async Task Without_a_certificate_TLS_refuses_and_the_variable_to_set_is_named()
    {
        var r = await Run("me");
        Assert.True(r.Code == 1, r.ToString());
        Assert.Contains("REDMINE_CLIENT_CERT_DEMO", r.Stderr);
    }

    [Fact]
    public async Task A_PEM_certificate_and_key_connect()
    {
        fixture.Mock.ClearRequests();
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pem"), ["REDMINE_CLIENT_KEY_DEMO"] = Fixture("client.key") }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Equal("tester", r.Json["user"]!["login"]!.GetValue<string>());
        Assert.Equal("redmine-cli test client", fixture.Mock.Requests[0].ClientCertificateSubject);
    }

    [Fact]
    public async Task A_PEM_with_the_certificate_and_the_key_needs_no_key_variable()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client-bundle.pem") }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
    }

    [Fact]
    public async Task A_PFX_and_its_password_connect()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pfx"), ["REDMINE_CLIENT_CERT_PASSWORD_DEMO"] = "testpass" }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
    }

    [Fact]
    public async Task An_encrypted_PEM_key_is_decrypted_with_the_password_variable()
    {
        var r = await RunWith(new()
        {
            ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pem"),
            ["REDMINE_CLIENT_KEY_DEMO"] = Fixture("client-enc.key"),
            ["REDMINE_CLIENT_CERT_PASSWORD_DEMO"] = "keypass",
        }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
    }

    [Fact]
    public async Task An_encrypted_PEM_key_in_one_file_with_the_certificate_connects()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client-bundle-enc.pem"), ["REDMINE_CLIENT_CERT_PASSWORD_DEMO"] = "keypass" }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
    }

    [Fact]
    public async Task An_encrypted_PEM_key_without_a_password_exits_1_naming_the_password_variable()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pem"), ["REDMINE_CLIENT_KEY_DEMO"] = Fixture("client-enc.key") }, "me");
        Assert.True(r.Code == 1, r.ToString());
        Assert.Contains("REDMINE_CLIENT_CERT_PASSWORD_DEMO", r.Stderr);
    }

    [Fact]
    public async Task A_PFX_without_a_password_connects_without_the_password_variable()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client-nopass.pfx") }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
    }

    [Fact]
    public async Task A_wrong_PFX_password_exits_1_naming_the_password_variable()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pfx"), ["REDMINE_CLIENT_CERT_PASSWORD_DEMO"] = "wrong" }, "me");
        Assert.True(r.Code == 1, r.ToString());
        Assert.Contains("REDMINE_CLIENT_CERT_PASSWORD_DEMO", r.Stderr);
    }

    [Fact]
    public async Task A_missing_certificate_file_exits_3()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Path.Combine(fixture.Workdir, "missing.pfx") }, "me");
        Assert.True(r.Code == 3, r.ToString());
        Assert.Contains("REDMINE_CLIENT_CERT_DEMO", r.Stderr);
    }

    [Fact]
    public async Task Doctor_offline_reports_a_wrong_password_at_reading_the_certificate_without_network()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pfx"), ["REDMINE_CLIENT_CERT_PASSWORD_DEMO"] = "wrong" }, "doctor", "--offline", "--json");
        Assert.True(r.Code == 1, r.ToString());
        var json = r.Json;
        Assert.False(json["ok"]!.GetValue<bool>());
        var steps = json["steps"]!.AsArray();
        var load = steps.First(s => s!["name"]!.GetValue<string>() == "証明書の読み込み")!;
        Assert.Equal("NG", load["status"]!.GetValue<string>());
        Assert.Contains("パスフレーズが合いません", load["detail"]!.GetValue<string>());
        Assert.Contains("REDMINE_CLIENT_CERT_PASSWORD_DEMO", load["hint"]!.GetValue<string>());
        Assert.DoesNotContain(steps, s => s!["name"]!.GetValue<string>() == "接続と認証");
    }

    [Fact]
    public async Task Doctor_reports_an_encrypted_key_without_a_password_as_not_set()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pem"), ["REDMINE_CLIENT_KEY_DEMO"] = Fixture("client-enc.key") }, "doctor", "--offline", "--json");
        Assert.True(r.Code == 1, r.ToString());
        var load = r.Json["steps"]!.AsArray().First(s => s!["name"]!.GetValue<string>() == "証明書の読み込み")!;
        Assert.Equal("NG", load["status"]!.GetValue<string>());
        Assert.Contains("未設定", load["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Doctor_with_the_right_certificate_is_OK_through_the_connection_and_shows_the_certificate()
    {
        var r = await RunWith(new()
        {
            ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pem"),
            ["REDMINE_CLIENT_KEY_DEMO"] = Fixture("client-enc.key"),
            ["REDMINE_CLIENT_CERT_PASSWORD_DEMO"] = "keypass",
        }, "doctor", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var json = r.Json;
        Assert.True(json["ok"]!.GetValue<bool>());
        var steps = json["steps"]!.AsArray();
        string Step(string name, string field) => steps.First(s => s!["name"]!.GetValue<string>() == name)![field]!.GetValue<string>();
        Assert.Contains("redmine-cli test client", Step("証明書の内容", "detail"));
        Assert.Equal("OK", Step("接続と認証", "status"));
        Assert.Equal("OK", Step("対象プロジェクト", "status"));
        Assert.StartsWith("直接接続", Step("経路", "detail"));
    }

    [Fact]
    public async Task Doctor_without_a_certificate_shows_the_cut_connection_as_NG_at_connecting()
    {
        var r = await Run("doctor", "--json");
        Assert.True(r.Code == 1, r.ToString());
        var steps = r.Json["steps"]!.AsArray();
        Assert.Equal("--", steps.First(s => s!["name"]!.GetValue<string>() == "証明書")!["status"]!.GetValue<string>());
        var connection = steps.First(s => s!["name"]!.GetValue<string>() == "接続と認証")!;
        Assert.Equal("NG", connection["status"]!.GetValue<string>());
        Assert.Contains("REDMINE_CLIENT_CERT_DEMO", connection["hint"]!.GetValue<string>());
    }

    [Fact]
    public async Task Target_shows_the_certificate_settings()
    {
        var r = await RunWith(new() { ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pfx") }, "target", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var cert = r.Json["client_cert"]!;
        Assert.True(cert["configured"]!.GetValue<bool>());
        Assert.True(cert["cert_file_exists"]!.GetValue<bool>());
        Assert.Equal("REDMINE_CLIENT_CERT_DEMO", cert["env"]!["cert"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_untrusted_server_certificate_exits_1_with_the_CA_hint()
    {
        var r = await RunWith(new()
        {
            ["REDMINE_EXTRA_CA_CERTS"] = null,
            ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pem"),
            ["REDMINE_CLIENT_KEY_DEMO"] = Fixture("client.key"),
        }, "me");
        Assert.True(r.Code == 1, r.ToString());
        Assert.Contains("REDMINE_EXTRA_CA_CERTS", r.Stderr);
        Assert.Contains("サーバー証明書", r.Stderr);
    }

    [Fact]
    public async Task The_Node_versions_NODE_EXTRA_CA_CERTS_is_read_when_REDMINE_EXTRA_CA_CERTS_is_not_set()
    {
        var r = await RunWith(new()
        {
            ["REDMINE_EXTRA_CA_CERTS"] = null,
            ["NODE_EXTRA_CA_CERTS"] = Fixture("ca.pem"),
            ["REDMINE_CLIENT_CERT_DEMO"] = Fixture("client.pfx"),
            ["REDMINE_CLIENT_CERT_PASSWORD_DEMO"] = "testpass",
        }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
    }
}
