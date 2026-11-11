using System.Text.Json;
using System.Text.Json.Serialization;
using FCG.Contracts;

namespace FCG.Users.Infrastructure.Messaging;

/// <summary>Uses the existing strict wire contract inside MassTransit's standard envelope.</summary>
public sealed class UserCreatedEventJsonConverter : JsonConverter<UserCreatedEvent>
{
    public override UserCreatedEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return IdentityContractJson.Read<UserCreatedEvent>(document.RootElement.GetRawText());
    }

    public override void Write(Utf8JsonWriter writer, UserCreatedEvent value, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(IdentityContractJson.Write(value));
        document.RootElement.WriteTo(writer);
    }
}
