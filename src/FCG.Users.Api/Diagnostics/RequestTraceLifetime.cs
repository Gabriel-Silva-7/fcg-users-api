using System.Diagnostics;

namespace FCG.Users.Api.Diagnostics;

/// <summary>Keep W3C propagation independent of framework request logging, without collecting request data.</summary>
internal sealed class RequestTraceLifetime : IHostedService, IDisposable
{
    private ActivityListener? listener;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(listener);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
    public void Dispose() { listener?.Dispose(); listener = null; }
}
