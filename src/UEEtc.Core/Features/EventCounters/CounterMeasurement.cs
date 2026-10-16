using System.Text.Json.Serialization;

namespace UEEtc.Core.Features.EventCounters;

[JsonConverter(typeof(JsonStringEnumConverter<CounterMeasurement>))]
public enum CounterMeasurement
{
    Cumulative,
    ActiveScope
}
