using System.Net;
using FCG.Users.Api.Tests.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FCG.Users.Api.Tests;

[Collection("auth-postgres")]
public sealed class HealthTests(AuthFixture postgres)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Configured_database_and_bootstrap_are_healthy(string path)
    {
        await using var factory = await postgres.CreateAsync();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
    [Theory]
    [InlineData(HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded)]
    public async Task Readiness_failure_returns_503_without_affecting_liveness(HealthStatus status)
    {
        await using var factory = await WithCheck(status, ["ready"]);
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready");
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_ignores_checks_without_ready_tag()
    {
        await using var factory = await WithCheck(HealthStatus.Unhealthy, []);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<AuthFactory> WithCheck(HealthStatus status, string[] tags) =>
        postgres.CreateAsync(customize: services => services.AddHealthChecks()
                .Add(new HealthCheckRegistration("test-check", _ => new FixedHealthCheck(status), null, tags)));

    private sealed class FixedHealthCheck(HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(new HealthCheckResult(status));
    }
}
