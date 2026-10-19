using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FCG.Users.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace FCG.Users.Api.Tests;

public sealed class ContainerProbeTests
{
    [Theory]
    [InlineData(200, 0)]
    [InlineData(503, 1)]
    public async Task Probe_reports_real_http_status(int statusCode, int expectedExitCode)
    {
        await using var server = CreateServer();
        server.MapGet("/", () => Results.StatusCode(statusCode));
        await server.StartAsync();
        Assert.Equal(expectedExitCode, await ContainerProbe.RunAsync(new Uri(server.Urls.Single())));
    }

    [Fact]
    public async Task Probe_reports_connection_failure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.Equal(1, await ContainerProbe.RunAsync(new Uri($"http://127.0.0.1:{port}")));
    }

    [Fact]
    public async Task Probe_times_out_when_http_server_does_not_respond()
    {
        await using var server = CreateServer();
        server.MapGet("/", async (HttpContext context) =>
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted));
        await server.StartAsync();
        var elapsed = Stopwatch.StartNew();
        Assert.Equal(1, await ContainerProbe.RunAsync(new Uri(server.Urls.Single()), TimeSpan.FromMilliseconds(150)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Probe must have a finite timeout.");
    }

    private static WebApplication CreateServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }
}
