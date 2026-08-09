using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

namespace WithLove.Workflows.Tests.Unit.Chat;

public class GenAiActivityExportProcessorTests
{
    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void OnEnd_GenAiSpan_AddsProjectAndMarksSuccessfulSpanOk()
    {
        using var provider = CreateTracerProvider();
        using var source = new ActivitySource("processor-test");
        var exporter = new RecordingExporter();
        using var processor = new GenAiActivityExportProcessor(exporter, "telemetry-project");
        var activity = Stop(source, "chat", activity => activity.SetTag("gen_ai.operation.name", "chat"));

        processor.OnEnd(activity);
        processor.ForceFlush(5_000).Should().BeTrue();

        exporter.Spans.Should().ContainSingle();
        activity.Status.Should().Be(ActivityStatusCode.Ok);
        activity.GetTagItem("arize.project.name").Should().Be("telemetry-project");
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void OnEnd_NonGenAiSpan_DoesNotExportOrModifyStatus()
    {
        using var provider = CreateTracerProvider();
        using var source = new ActivitySource("processor-test");
        var exporter = new RecordingExporter();
        using var processor = new GenAiActivityExportProcessor(exporter, "telemetry-project");
        var activity = Stop(source, "database");

        processor.OnEnd(activity);
        processor.ForceFlush(5_000).Should().BeTrue();

        exporter.Spans.Should().BeEmpty();
        activity.Status.Should().Be(ActivityStatusCode.Unset);
        activity.GetTagItem("arize.project.name").Should().BeNull();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void OnEnd_GenAiSpanWithExceptionEvent_MarksSpanError()
    {
        using var provider = CreateTracerProvider();
        using var source = new ActivitySource("processor-test");
        var exporter = new RecordingExporter();
        using var processor = new GenAiActivityExportProcessor(exporter, "telemetry-project");
        var activity = Stop(source, "chat", activity =>
        {
            activity.SetTag("gen_ai.operation.name", "chat");
            activity.AddEvent(new ActivityEvent("exception"));
        });

        processor.OnEnd(activity);
        processor.ForceFlush(5_000).Should().BeTrue();

        exporter.Spans.Should().ContainSingle();
        activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    private static Activity Stop(ActivitySource source, string name, Action<Activity>? configure = null)
    {
        var activity = source.StartActivity(name)!;
        configure?.Invoke(activity);
        activity.Stop();
        return activity;
    }

    private static TracerProvider CreateTracerProvider() => Sdk.CreateTracerProviderBuilder()
        .AddSource("processor-test")
        .Build();

    private sealed class RecordingExporter : BaseExporter<Activity>
    {
        public List<Activity> Spans { get; } = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                Spans.Add(activity);

            return ExportResult.Success;
        }
    }
}
