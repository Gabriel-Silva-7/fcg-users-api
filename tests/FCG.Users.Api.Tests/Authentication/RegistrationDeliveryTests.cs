using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Contracts;
using Npgsql;
using RabbitMQ.Client;
using Xunit;
using static FCG.Users.Api.Tests.Authentication.RegistrationTests;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class RegistrationDeliveryTests(AuthFixture fixture)
{
    [Fact]
    public async Task Real_broker_delivers_canonical_registration_independently_to_two_subscribers()
    {
        await using var capture = await Capture.CreateAsync(fixture.BrokerSettings);
        await using var app = await fixture.CreateAsync(deliveryEnabled: true);
        using var client = app.CreateClient();
        await WaitForStatusAsync(client, HttpStatusCode.OK);
        using var response = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var first = await capture.ReadAsync(0);
        var second = await capture.ReadAsync(1);
        Assert.Equal(first, second);
        Assert.Equal(user, first.UserId);
        Assert.Equal("person@example.com", first.Email);
        Assert.Equal(1, first.EventVersion);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        AssertPrivateLogs(app);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broker_outage_and_API_restart_deliver_existing_intent_without_repeating_registration(bool unavailableBeforeStartup)
    {
        await using var capture = await Capture.CreateAsync(fixture.BrokerSettings);
        var app = await fixture.CreateAsync(deliveryEnabled: true);
        HttpClient? client = null;
        if (!unavailableBeforeStartup)
        {
            client = app.CreateClient();
            await WaitForStatusAsync(client, HttpStatusCode.OK);
        }
        Assert.Equal(0, (await fixture.Broker.ExecAsync(["rabbitmqctl", "stop_app"])).ExitCode);
        try
        {
            client ??= app.CreateClient();
            await WaitForStatusAsync(client, HttpStatusCode.ServiceUnavailable);
            using var live = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            using var response = await RegisterAsync(client);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var user = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var messageId = Assert.IsType<Guid>(await app.ScalarAsync("SELECT \"MessageId\" FROM \"OutboxMessage\""));
            Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
            Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
            client.Dispose();
            await app.DisposeAsync();
            AssertPrivateLogs(app);

            await using var restarted = new AuthFactory(app.ConnectionString, fixture.KeyDirectory,
                brokerSettings: fixture.BrokerSettings, deliveryEnabled: true);
            using var restartedClient = restarted.CreateClient();
            await WaitForStatusAsync(restartedClient, HttpStatusCode.ServiceUnavailable);
            Assert.Equal(messageId, await restarted.ScalarAsync("SELECT \"MessageId\" FROM \"OutboxMessage\""));
            Assert.Equal(0, (await fixture.Broker.ExecAsync(["rabbitmqctl", "start_app"])).ExitCode);
            var first = await capture.ReadAsync(0);
            var second = await capture.ReadAsync(1);
            Assert.Equal(first, second);
            Assert.Equal(user, first.UserId);
            Assert.Equal(messageId, first.EventId);
            await WaitForStatusAsync(restartedClient, HttpStatusCode.OK);
            using var login = await restartedClient.PostAsJsonAsync(LoginTests.Endpoint,
                new { email = "person@example.com", password = Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Equal(1L, await restarted.ScalarAsync("SELECT count(*) FROM \"Users\""));
            AssertPrivateLogs(restarted);
        }
        finally
        {
            client?.Dispose();
            await app.DisposeAsync();
            Assert.Equal(0, (await fixture.Broker.ExecAsync(["rabbitmqctl", "start_app"])).ExitCode);
        }
    }

    internal static async Task WaitForStatusAsync(HttpClient client, HttpStatusCode expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            using var response = await client.GetAsync("/health/ready", timeout.Token);
            if (response.StatusCode == expected) return;
            await Task.Delay(100, timeout.Token);
        }
    }

    private void AssertPrivateLogs(AuthFactory app)
    {
        var logs = string.Join('\n', app.Logs.Entries);
        foreach (var secret in new[] { "person@example.com", Password,
                     new NpgsqlConnectionStringBuilder(app.ConnectionString).Password!, fixture.BrokerSettings["RabbitMq:Password"]! })
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", logs);
    }

    private sealed class Capture(IConnection connection, IChannel channel, string[] queues) : IAsyncDisposable
    {
        public static async Task<Capture> CreateAsync(IReadOnlyDictionary<string, string?> settings)
        {
            var factory = new ConnectionFactory
            {
                HostName = settings["RabbitMq:Host"]!, Port = int.Parse(settings["RabbitMq:Port"]!),
                VirtualHost = settings["RabbitMq:VirtualHost"]!, UserName = settings["RabbitMq:Username"]!,
                Password = settings["RabbitMq:Password"]!, NetworkRecoveryInterval = TimeSpan.FromMilliseconds(250)
            };
            var connection = await factory.CreateConnectionAsync();
            var channel = await connection.CreateChannelAsync();
            var queues = new[] { "usr03-catalog-" + Guid.NewGuid().ToString("N"), "usr03-notifications-" + Guid.NewGuid().ToString("N") };
            await channel.ExchangeDeclareAsync("FCG.Contracts:UserCreatedEvent", "fanout", durable: true, autoDelete: false);
            foreach (var queue in queues)
            {
                await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
                await channel.QueueBindAsync(queue, "FCG.Contracts:UserCreatedEvent", "");
            }
            return new Capture(connection, channel, queues);
        }

        public async Task<UserCreatedEvent> ReadAsync(int index)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            BasicGetResult? result = null;
            while (result is null)
            {
                if (channel.IsOpen) result = await channel.BasicGetAsync(queues[index], autoAck: false, timeout.Token);
                if (result is null) await Task.Delay(100, timeout.Token);
            }
            using var envelope = JsonDocument.Parse(result.Body);
            var root = envelope.RootElement;
            Assert.Contains(root.GetProperty("messageType").EnumerateArray(),
                value => value.GetString() == "urn:message:FCG.Contracts:UserCreatedEvent");
            var message = IdentityContractJson.Read<UserCreatedEvent>(root.GetProperty("message").GetRawText());
            Assert.Equal(message.EventId, root.GetProperty("messageId").GetGuid());
            Assert.Equal(message.UserId, root.GetProperty("correlationId").GetGuid());
            Assert.Equal(message.EventId, Guid.Parse(result.BasicProperties.MessageId!));
            Assert.Equal(message.UserId, Guid.Parse(result.BasicProperties.CorrelationId!));
            Assert.DoesNotContain(Password, root.GetRawText());
            Assert.DoesNotContain("passwordHash", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
            await channel.BasicAckAsync(result.DeliveryTag, multiple: false, timeout.Token);
            return message;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (channel.IsOpen)
                    foreach (var queue in queues) await channel.QueueDeleteAsync(queue);
            }
            finally { await channel.DisposeAsync(); await connection.DisposeAsync(); }
        }
    }
}
