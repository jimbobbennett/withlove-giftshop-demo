using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Sends only GenAI semantic-convention spans to Arize AX. Other telemetry remains
/// available to Aspire and any separately configured OTLP collector.
/// </summary>
internal sealed class GenAiActivityExportProcessor(
    BaseExporter<Activity> exporter,
    string arizeProjectName)
    : BaseProcessor<Activity>
{
    private readonly BatchActivityExportProcessor _processor = new(
        exporter,
        maxQueueSize: 2048,
        scheduledDelayMilliseconds: 5000,
        exporterTimeoutMilliseconds: 30000,
        maxExportBatchSize: 512);

    public override void OnEnd(Activity data)
    {
        if (data.GetTagItem("gen_ai.operation.name") is null)
            return;

        if (data.Status == ActivityStatusCode.Unset)
        {
            var hasRecordedError = data.GetTagItem("error.type") is not null
                || data.Events.Any(@event => @event.Name == "exception");
            data.SetStatus(hasRecordedError ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        }

        data.SetTag("arize.project.name", arizeProjectName);
        _processor.OnEnd(data);
    }

    protected override bool OnForceFlush(int timeoutMilliseconds) =>
        _processor.ForceFlush(timeoutMilliseconds);

    protected override bool OnShutdown(int timeoutMilliseconds) =>
        _processor.Shutdown(timeoutMilliseconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _processor.Dispose();

        base.Dispose(disposing);
    }
}
