using static RedmineCli.Tests.CliRunner;

namespace RedmineCli.Tests;

/// <summary>Which proxy a URL goes through, by the Node version's rules (no network).</summary>
public sealed class EnvProxyTests
{
    private static EnvProxy? Proxy(params (string Name, string Value)[] env)
    {
        var values = env.ToDictionary(e => e.Name, e => e.Value, StringComparer.OrdinalIgnoreCase);
        return EnvProxy.FromEnvironment(name => values.GetValueOrDefault(name));
    }

    private static string? Via(EnvProxy proxy, string url) => proxy.GetProxy(new Uri(url))?.AbsoluteUri;

    [Fact]
    public void Without_proxy_variables_the_default_proxy_is_left_alone()
    {
        Assert.Null(Proxy());
        Assert.Null(Proxy(("NO_PROXY", "example.co.jp")));
    }

    [Fact]
    public void Https_uses_HTTPS_PROXY_and_http_uses_HTTP_PROXY()
    {
        var proxy = Proxy(("HTTPS_PROXY", "http://s.example:8080"), ("HTTP_PROXY", "http://p.example:3128"))!;
        Assert.Equal("http://s.example:8080/", Via(proxy, "https://redmine.example.co.jp/"));
        Assert.Equal("http://p.example:3128/", Via(proxy, "http://redmine.example.co.jp/"));
    }

    [Fact]
    public void Https_falls_back_to_HTTP_PROXY_but_http_does_not_use_HTTPS_PROXY()
    {
        Assert.Equal("http://p.example:3128/", Via(Proxy(("HTTP_PROXY", "http://p.example:3128"))!, "https://redmine.example.co.jp/"));
        Assert.Null(Via(Proxy(("HTTPS_PROXY", "http://s.example:8080"))!, "http://redmine.example.co.jp/"));
    }

    [Fact]
    public void A_proxy_without_a_scheme_is_http()
    {
        Assert.Equal("http://s.example:8080/", Via(Proxy(("HTTPS_PROXY", "s.example:8080"))!, "https://redmine.example.co.jp/"));
    }

    [Theory]
    [InlineData("redmine.example.co.jp", "https://redmine.example.co.jp/", true)]
    [InlineData("example.co.jp", "https://redmine.example.co.jp/", false)]
    [InlineData(".example.co.jp", "https://redmine.example.co.jp/", true)]
    [InlineData("*.example.co.jp", "https://redmine.example.co.jp/", true)]
    [InlineData("REDMINE.Example.co.jp", "https://redmine.example.co.jp/", true)]
    [InlineData("redmine.example.co.jp:443", "https://redmine.example.co.jp/", true)]
    [InlineData("redmine.example.co.jp:8443", "https://redmine.example.co.jp/", false)]
    [InlineData("localhost other.example redmine.example.co.jp", "https://redmine.example.co.jp/", true)]
    [InlineData("*", "https://redmine.example.co.jp/", true)]
    [InlineData("", "https://redmine.example.co.jp/", false)]
    public void NO_PROXY_entries(string noProxy, string url, bool direct)
    {
        var proxy = Proxy(("HTTPS_PROXY", "http://s.example:8080"), ("NO_PROXY", noProxy))!;
        Assert.Equal(direct, proxy.IsBypassed(new Uri(url)));
    }

    [Fact]
    public void The_proxy_url_given_to_the_handler_has_no_password_and_the_credentials_are_decoded()
    {
        var proxy = Proxy(("HTTPS_PROXY", "http://taro:p%40ss@s.example:8080"))!;
        Assert.Equal("http://s.example:8080/", Via(proxy, "https://redmine.example.co.jp/"));
        var credential = proxy.Credentials!.GetCredential(new Uri("http://s.example:8080/"), "Basic")!;
        Assert.Equal(("taro", "p@ss"), (credential.UserName, credential.Password));
    }

    [Fact]
    public void A_proxy_value_that_is_not_a_url_fails_with_3_without_printing_it()
    {
        var e = Assert.ThrowsAny<CliException>(() => Proxy(("HTTPS_PROXY", "http://taro:secret@:::")));
        Assert.Equal(Exit.Config, e.ExitCode);
        Assert.DoesNotContain("secret", e.Message + e.Hint);
    }

    [Fact]
    public void The_route_names_the_variable()
    {
        Assert.Contains("環境変数 HTTPS_PROXY)", EnvProxy.DescribeRoute(new Uri("https://r.example/"), n => n == "HTTPS_PROXY" ? "http://s.example:8080" : null));
        Assert.Contains("HTTPS_PROXY が無いので", EnvProxy.DescribeRoute(new Uri("https://r.example/"), n => n == "HTTP_PROXY" ? "http://p.example:3128" : null));
        Assert.Contains("NO_PROXY", EnvProxy.DescribeRoute(new Uri("https://r.example/"),
            n => n switch { "HTTPS_PROXY" => "http://s.example:8080", "NO_PROXY" => ".example", _ => null }));
    }
}

/// <summary>The mTLS mock Redmine of <see cref="TlsFixture"/> with a CONNECT proxy in front of it.</summary>
public sealed class ProxyFixture : IAsyncLifetime
{
    public TlsFixture Tls { get; } = new();

    public TunnelProxy Proxy { get; } = new();

    public Task InitializeAsync() => Tls.InitializeAsync();

    public async Task DisposeAsync()
    {
        await Proxy.DisposeAsync();
        await Tls.DisposeAsync();
    }
}

/// <summary>
/// Going through a company proxy as the Node version did: HTTPS_PROXY (or HTTP_PROXY when it is not set), NO_PROXY,
/// credentials in the proxy URL, and the client certificate offered through the tunnel.
/// </summary>
public sealed class ProxyTests : IClassFixture<ProxyFixture>
{
    private readonly ProxyFixture _f;

    public ProxyTests(ProxyFixture fixture)
    {
        _f = fixture;
        _f.Proxy.Clear();
        _f.Proxy.RequiredCredentials = null;
    }

    private string TlsTarget => $"127.0.0.1:{_f.Tls.Mock.Port}";

    private Task<CliResult> Run(Dictionary<string, string?> env, params string[] args)
    {
        // The runner sets NO_PROXY=127.0.0.1,localhost for the other tests; here the proxy must be used unless a test says so.
        env.TryAdd("NO_PROXY", null);
        env.TryAdd("REDMINE_EXTRA_CA_CERTS", TlsFixture.Fixture("ca.pem"));
        env.TryAdd("REDMINE_CLIENT_CERT_DEMO", TlsFixture.Fixture("client.pfx"));
        env.TryAdd("REDMINE_CLIENT_CERT_PASSWORD_DEMO", "testpass");
        return RunAsync(_f.Tls.Workdir, args, env);
    }

    [Fact]
    public async Task HTTPS_PROXY_tunnels_to_Redmine_and_the_client_certificate_goes_through_it()
    {
        _f.Tls.Mock.ClearRequests();
        var r = await Run(new() { ["HTTPS_PROXY"] = _f.Proxy.Url }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains(TlsTarget, _f.Proxy.Connects);
        Assert.Equal("redmine-cli test client", _f.Tls.Mock.Requests[0].ClientCertificateSubject);
    }

    [Fact]
    public async Task Lower_case_https_proxy_works_the_same()
    {
        var r = await Run(new() { ["https_proxy"] = _f.Proxy.Url }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains(TlsTarget, _f.Proxy.Connects);
    }

    [Fact]
    public async Task Without_HTTPS_PROXY_an_https_Redmine_goes_through_HTTP_PROXY_as_in_the_Node_version()
    {
        var r = await Run(new() { ["HTTP_PROXY"] = _f.Proxy.Url }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains(TlsTarget, _f.Proxy.Connects);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost, 127.0.0.1")]
    [InlineData("*")]
    public async Task NO_PROXY_goes_direct(string noProxy)
    {
        var r = await Run(new() { ["HTTPS_PROXY"] = _f.Proxy.Url, ["NO_PROXY"] = noProxy }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Empty(_f.Proxy.Connects);
    }

    [Fact]
    public async Task NO_PROXY_with_another_port_still_uses_the_proxy()
    {
        var r = await Run(new() { ["HTTPS_PROXY"] = _f.Proxy.Url, ["NO_PROXY"] = "127.0.0.1:1" }, "me", "--json");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains(TlsTarget, _f.Proxy.Connects);
    }

    [Fact]
    public async Task Credentials_in_the_proxy_url_answer_the_proxys_challenge_and_are_never_printed()
    {
        _f.Proxy.RequiredCredentials = "taro:p@ss word";
        var url = _f.Proxy.Url.Replace("http://", "http://taro:p%40ss%20word@");
        var r = await Run(new() { ["HTTPS_PROXY"] = url }, "me", "--json", "--verbose");
        Assert.True(r.Code == 0, r.ToString());
        Assert.Contains("taro:p@ss word", _f.Proxy.Credentials);
        Assert.DoesNotContain("p@ss", r.Stdout + r.Stderr);
        Assert.DoesNotContain("p%40ss", r.Stdout + r.Stderr);
    }

    [Fact]
    public async Task A_proxy_that_wants_credentials_it_did_not_get_exits_1_with_the_proxy_hint()
    {
        _f.Proxy.RequiredCredentials = "taro:secret";
        var r = await Run(new() { ["HTTPS_PROXY"] = _f.Proxy.Url }, "me");
        Assert.True(r.Code == 1, r.ToString());
        Assert.Contains("HTTPS_PROXY", r.Stderr);
        Assert.Empty(_f.Proxy.Connects);
    }

    [Fact]
    public async Task Doctor_names_the_proxy_and_the_variable_it_came_from()
    {
        var r = await Run(new() { ["HTTPS_PROXY"] = _f.Proxy.Url }, "doctor", "--json");
        Assert.True(r.Code == 0, r.ToString());
        var route = r.Json["steps"]!.AsArray().First(s => s!["name"]!.GetValue<string>() == "経路")!["detail"]!.GetValue<string>();
        Assert.Contains(_f.Proxy.Url, route);
        Assert.Contains("HTTPS_PROXY", route);
    }
}
