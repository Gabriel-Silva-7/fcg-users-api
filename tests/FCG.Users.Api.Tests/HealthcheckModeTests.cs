using System.Diagnostics;
using Xunit;

namespace FCG.Users.Api.Tests;

public sealed class HealthcheckModeTests
{
    [Fact]
    public async Task Healthcheck_argument_exits_instead_of_starting_a_web_host()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--healthcheck");
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        var exited = false;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7));
            exited = true;
        }
        catch (TimeoutException) { }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        Assert.True(exited, "--healthcheck must terminate without starting the host. " + await output + await errors);
        Assert.InRange(process.ExitCode, 0, 1);
    }
}
