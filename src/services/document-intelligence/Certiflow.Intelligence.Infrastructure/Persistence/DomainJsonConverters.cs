using System.Text.Json;
using System.Text.Json.Serialization;
using Certiflow.Intelligence.Domain.Scoring;

namespace Certiflow.Intelligence.Infrastructure.Persistence;

/// <summary>
/// JSON converters for the value objects that guard their own construction.
/// <para>
/// <b>Why these exist at all.</b> <see cref="Confidence"/> and <see cref="SignalOutcome"/> both have
/// private constructors and static factories, because a confidence must only ever come out of the
/// scorer and a signal outcome must be in [0,1]. That is exactly the property that makes them
/// trustworthy — and exactly what stops a serialiser from rebuilding them. The right response is to
/// teach the serialiser how to rehydrate them through the sanctioned door, not to open a public
/// constructor and lose the guarantee for the convenience of a database.
/// </para>
/// <para>
/// Every read goes through <c>FromPersistedValue</c>, which still enforces the range. A corrupted
/// row fails loudly rather than producing a confidence of 4.7.
/// </para>
/// </summary>
public static class DomainJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        options.Converters.Add(new ConfidenceJsonConverter());
        options.Converters.Add(new SignalOutcomeJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }
}

internal sealed class ConfidenceJsonConverter : JsonConverter<Confidence>
{
    public override Confidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Confidence.FromPersistedValue(reader.GetDecimal());

    public override void Write(Utf8JsonWriter writer, Confidence value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteNumberValue(value.Value);
    }
}

internal sealed class SignalOutcomeJsonConverter : JsonConverter<SignalOutcome>
{
    public override SignalOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var signal = Enum.Parse<ConfidenceSignal>(root.GetProperty("signal").GetString()!, ignoreCase: true);
        var score = root.GetProperty("score").GetDecimal();
        var detail = root.TryGetProperty("detail", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetString() : null;

        // Through the factory, so the [0,1] guard still applies on the way back in.
        return SignalOutcome.Partial(signal, score, detail);
    }

    public override void Write(Utf8JsonWriter writer, SignalOutcome value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("signal", value.Signal.ToString());
        writer.WriteNumber("score", value.Score);

        if (value.Detail is null)
        {
            writer.WriteNull("detail");
        }
        else
        {
            writer.WriteString("detail", value.Detail);
        }

        writer.WriteEndObject();
    }
}
