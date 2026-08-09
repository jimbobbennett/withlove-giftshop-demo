using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using WithLove.Workflows.Chat;

namespace WithLove.Workflows.Telemetry;

internal static class ChatTelemetry
{
    // Must match the Aspire resource name — runs inside "workflowServer"
    private const string SourceName = "workflowServer";
    private const string Version = "1.0.0";

    // Static readonly singletons — creation is expensive, must be reused
    internal static readonly ActivitySource ActivitySource = new(SourceName, Version);
    internal static readonly Meter Meter = new(SourceName, Version);

    internal static readonly Histogram<double> InferenceDuration =
        Meter.CreateHistogram<double>("chat.inference.duration_ms", "ms",
            "End-to-end AI inference latency including all tool calls");

    internal static readonly Counter<long> CartMutations =
        Meter.CreateCounter<long>("chat.cart_mutations", "mutations",
            "Cart mutations triggered by the chat agent");

    internal static void ConfigureAgentSpan(
        Activity activity,
        ChatInferenceInput input,
        bool captureContent)
    {
        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        activity.SetTag("gen_ai.agent.name", "LA");
        activity.SetTag("gen_ai.request.model", "gpt-5-nano");

        if (captureContent)
            activity.SetTag("input.value", JsonSerializer.Serialize(input));
    }

    internal static void CaptureAgentOutput(
        Activity activity,
        ChatInferenceResult result,
        bool captureContent)
    {
        if (captureContent)
            activity.SetTag("output.value", JsonSerializer.Serialize(result));
    }

    internal static void CaptureToolArguments(
        Activity? activity,
        object arguments,
        bool captureContent)
    {
        if (captureContent && activity?.GetTagItem("gen_ai.operation.name") is "execute_tool")
            activity.SetTag("gen_ai.tool.call.arguments", JsonSerializer.Serialize(arguments));
    }

    internal static string CaptureToolResult(
        Activity? activity,
        string result,
        bool captureContent)
    {
        if (captureContent && activity?.GetTagItem("gen_ai.operation.name") is "execute_tool")
            activity.SetTag("gen_ai.tool.call.result", JsonSerializer.Serialize(result));

        return result;
    }
}
