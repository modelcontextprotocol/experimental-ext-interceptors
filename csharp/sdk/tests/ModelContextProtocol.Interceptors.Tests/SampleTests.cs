using System.Text.Json.Nodes;
using InterceptorProxy;
using ModelContextProtocol.Interceptors.Protocol;
using WhyInterceptorsDemo;
using Xunit;

namespace ModelContextProtocol.Interceptors.Tests;

/// <summary>Covers the samples' exfiltration guard and the proxy's command resolution.</summary>
public class SampleTests
{
    [Theory]
    [InlineData("https://acme.example/report")]
    [InlineData("HTTPS://Contoso.Example/renewal")]
    [InlineData("Summary: see https://acme.example/report.")]
    [InlineData("Links: <https://acme.example/a>, (https://contoso.example/b)")]
    [InlineData("Re: Q3 numbers")]
    [InlineData("/home/user/report.txt")]
    public void ExfiltrationGuard_AllowsApprovedDestinationsAndPlainText(string argument) =>
        Assert.True(Guard(argument).Valid);

    [Theory]
    [InlineData("https://enrich-profiles.example/v2/sync")]
    [InlineData("ftp://evil.example/sync")]
    [InlineData("mailto:bob@evil.example")]
    [InlineData("https://acme.example @evil.example/collect")]     // Uri accepts whitespace before the '@'
    [InlineData("Send it to https://acme.example'@evil.example/collect today")]
    [InlineData(@"Send it to http:\\evil.example/collect today")]
    [InlineData("Send it to https://acme.example\"@evil.example/collect today")]
    [InlineData("Send it to https:///evil.example/collect today")] // does not parse: blocked, not skipped
    public void ExfiltrationGuard_BlocksOtherDestinations(string argument) =>
        Assert.False(Guard(argument).Valid);

    [Theory]
    [InlineData("https://acme.example'@evil.example/collect")]
    [InlineData(@"http:/\evil.example/collect")]
    public void ExfiltrationGuard_ReadsUrlsAsFetchUrlDoes(string url)
    {
        // fetch_url would reach evil.example, so the guard must see evil.example too.
        Assert.Contains("from evil.example", AcmeWorkspaceTools.FetchUrl(url));

        var result = Guard(url);
        Assert.False(result.Valid);
        Assert.StartsWith("evil.example is not an approved destination", Assert.Single(result.Messages!).Message);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("npx")]
    public void ProxyConfig_LeavesBareCommandsToPath(string command) =>
        Assert.Equal(command, ProxyConfig.ServerConnection.ResolveCommand(command, ConfigDirectory));

    [Fact]
    public void ProxyConfig_ResolvesCommandPathsAgainstTheConfigDirectory()
    {
        Assert.Equal(Path.Combine(ConfigDirectory, "bin", "guard"), ProxyConfig.ServerConnection.ResolveCommand("./bin/guard", ConfigDirectory));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "guard"), ProxyConfig.ServerConnection.ResolveCommand("../guard", ConfigDirectory));

        var absolute = Path.Combine(Path.GetTempPath(), "tools", "guard");
        Assert.Equal(absolute, ProxyConfig.ServerConnection.ResolveCommand(absolute, ConfigDirectory));
    }

    private static readonly string ConfigDirectory = Path.Combine(Path.GetTempPath(), "proxy-config");

    private static ValidationInterceptorResult Guard(string argument) =>
        SecurityGuardInterceptors.BlockUnapprovedDomains(
            new JsonObject { ["name"] = "fetch_url", ["arguments"] = new JsonObject { ["url"] = argument } },
            config: null);
}
