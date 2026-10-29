using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Contracts;
using FCG.Users.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class IdentityStateDeliveryTests(AuthFixture fixture)
{
    [Fact]
    public async Task Actual_transport_delivers_changed_and_recovery_contracts_independently_to_two_subscribers()
    {
        await using var capture = await Capture.CreateAsync(fixture.BrokerSettings);
        await using var app = await fixture.CreateAsync(deliveryEnabled: true); BootstrapTests.Configure(app); using var client = app.CreateClient();
        await RegistrationDeliveryTests.WaitForStatusAsync(client, HttpStatusCode.OK);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = BootstrapTests.PlayerEmail, password = BootstrapTests.PlayerPassword });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        using var response = await client.PatchAsJsonAsync("/api/v1/me", new { name = "Transport Contact", email = "transport-change@example.com" }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var first = await capture.ReadAsync(0); var second = await capture.ReadAsync(1); Assert.Equal(first.GetRawText(), second.GetRawText());
        var changed = IdentityContractJson.Read<UserStateChangedEvent>(first.GetProperty("message").GetRawText()); Assert.Equal(id, changed.UserId); Assert.Equal(2, changed.EventVersion);
        Assert.Contains(first.GetProperty("messageType").EnumerateArray(), item => item.GetString() == "urn:message:FCG.Contracts:UserStateChangedEvent");
        using var scope = app.Services.CreateScope(); Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<IdentityReconciler>().ExecuteAsync(id, false, CancellationToken.None)).Staged);
        first = await capture.ReadAsync(0); second = await capture.ReadAsync(1); Assert.Equal(first.GetRawText(), second.GetRawText());
        var snapshot = IdentityContractJson.Read<UserStateSnapshotEvent>(first.GetProperty("message").GetRawText()); Assert.Equal(id, snapshot.UserId); Assert.Equal(changed.EventVersion, snapshot.EventVersion);
        Assert.Equal(changed.Email, snapshot.Email); Assert.NotEqual(changed.EventId, snapshot.EventId);
        Assert.Contains(first.GetProperty("messageType").EnumerateArray(), item => item.GetString() == "urn:message:FCG.Contracts:UserStateSnapshotEvent");
        var logs = string.Join('\n', app.Logs.Entries); Assert.DoesNotContain("transport-change@example.com", logs); Assert.DoesNotContain(BootstrapTests.PlayerPassword, logs);
    }

    private sealed class Capture(IConnection connection, IChannel channel, string[] queues) : IAsyncDisposable
    {
        public static async Task<Capture> CreateAsync(IReadOnlyDictionary<string, string?> settings)
        {
            var factory = new ConnectionFactory { HostName = settings["RabbitMq:Host"]!, Port = int.Parse(settings["RabbitMq:Port"]!), VirtualHost = settings["RabbitMq:VirtualHost"]!, UserName = settings["RabbitMq:Username"]!, Password = settings["RabbitMq:Password"]! };
            var connection = await factory.CreateConnectionAsync(); var channel = await connection.CreateChannelAsync(); var queues = new[] { "usr06-catalog-" + Guid.NewGuid().ToString("N"), "usr06-notifications-" + Guid.NewGuid().ToString("N") };
            foreach (var type in new[] { "UserStateChangedEvent", "UserStateSnapshotEvent" }) await channel.ExchangeDeclareAsync("FCG.Contracts:" + type, "fanout", durable: true, autoDelete: false);
            foreach (var queue in queues)
            { await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false); foreach (var type in new[] { "UserStateChangedEvent", "UserStateSnapshotEvent" }) await channel.QueueBindAsync(queue, "FCG.Contracts:" + type, ""); }
            return new(connection, channel, queues);
        }
        public async Task<JsonElement> ReadAsync(int index)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (true)
            {
                var delivery = await channel.BasicGetAsync(queues[index], autoAck: true, timeout.Token);
                if (delivery is not null)
                {
                    Assert.True(delivery.BasicProperties.Persistent); using var parsed = JsonDocument.Parse(delivery.Body);
                    var root = parsed.RootElement; Assert.Equal(root.GetProperty("message").GetProperty("eventId").GetGuid(), root.GetProperty("messageId").GetGuid());
                    Assert.Equal(root.GetProperty("message").GetProperty("userId").GetGuid(), root.GetProperty("correlationId").GetGuid()); return root.Clone();
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        { foreach (var queue in queues) await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false); await channel.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
