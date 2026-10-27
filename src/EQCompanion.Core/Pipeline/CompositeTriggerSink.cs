namespace EQCompanion.Core.Pipeline;

/// <summary>
/// Fans one trigger-route line out to several sinks (the trigger engine and the buff tracker). A sink that throws is
/// reported and does not stop the others from seeing the line.
/// </summary>
public sealed class CompositeTriggerSink : ITriggerLineSink
{
    private readonly ITriggerLineSink[] _sinks;
    private readonly Action<Exception>? _onError;

    public CompositeTriggerSink(IEnumerable<ITriggerLineSink> sinks, Action<Exception>? onError = null)
    {
        _sinks = sinks?.ToArray() ?? throw new ArgumentNullException(nameof(sinks));
        _onError = onError;
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        foreach (ITriggerLineSink sink in _sinks)
        {
            try
            {
                sink.OnLine(characterName, message, receivedUtc);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
        }
    }
}
