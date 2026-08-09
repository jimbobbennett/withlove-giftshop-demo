using WithLove.Workflows.Telemetry;
using OpenTelemetry.Trace;

namespace WithLove.Workflows.Tests.Unit.Chat;

public class ChatTelemetryTests
{
    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void ConfigureAgentSpan_WithContentCapture_RecordsExactActivityInput()
    {
        using var provider = CreateTracerProvider();
        using var source = new ActivitySource("chat-telemetry-test");
        using var activity = source.StartActivity("invoke_agent LA")!;
        var input = new ChatInferenceInput(
            [new ChatHistoryEntry(true, "Find a gift for Mum", DateTime.UnixEpoch)],
            "Find a gift for Mum",
            [new CartSnapshot(42, "Card", 4.50m, 2)],
            new UserContext("Ada", "ada@example.test", "user-42"));

        ChatTelemetry.ConfigureAgentSpan(activity, input, captureContent: true);

        activity.GetTagItem("gen_ai.operation.name").Should().Be("invoke_agent");
        activity.GetTagItem("gen_ai.agent.name").Should().Be("LA");
        activity.GetTagItem("gen_ai.request.model").Should().Be("gpt-5-nano");
        activity.GetTagItem("input.value").Should().Be(JsonSerializer.Serialize(input));
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void ConfigureAgentSpan_WithoutContentCapture_DoesNotRecordActivityInput()
    {
        using var provider = CreateTracerProvider();
        using var source = new ActivitySource("chat-telemetry-test");
        using var activity = source.StartActivity("invoke_agent LA")!;
        var input = new ChatInferenceInput([], "Hello");

        ChatTelemetry.ConfigureAgentSpan(activity, input, captureContent: false);

        activity.GetTagItem("gen_ai.operation.name").Should().Be("invoke_agent");
        activity.GetTagItem("input.value").Should().BeNull();
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void CaptureAgentOutput_WithContentCapture_RecordsExactActivityOutput()
    {
        using var provider = CreateTracerProvider();
        using var source = new ActivitySource("chat-telemetry-test");
        using var activity = source.StartActivity("invoke_agent LA")!;
        var result = new ChatInferenceResult(
            "I found a card.",
            [new CartAction(CartActionType.Add, 42, "Card", "/card.png", 4.50m, "price_42", 1)],
            [new NavigationAction(NavigationTarget.Cart, "/cart")]);

        ChatTelemetry.CaptureAgentOutput(activity, result, captureContent: true);

        activity.GetTagItem("output.value").Should().Be(JsonSerializer.Serialize(result));
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void CaptureToolDetails_OnToolSpan_RecordsArgumentsAndResult()
    {
        using var provider = CreateTracerProvider();
        using var source = new ActivitySource("chat-telemetry-test");
        using var activity = source.StartActivity("execute_tool view_cart")!;
        activity.SetTag("gen_ai.operation.name", "execute_tool");

        ChatTelemetry.CaptureToolArguments(activity, new { productId = 42 }, captureContent: true);
        var result = ChatTelemetry.CaptureToolResult(activity, "The cart is empty.", captureContent: true);

        result.Should().Be("The cart is empty.");
        activity.GetTagItem("gen_ai.tool.call.arguments")
            .Should().Be(JsonSerializer.Serialize(new { productId = 42 }));
        activity.GetTagItem("gen_ai.tool.call.result")
            .Should().Be(JsonSerializer.Serialize("The cart is empty."));
    }

    [Fact]
    [Trait(TestTraits.Category, TestTraits.Unit)]
    [Trait(TestTraits.Feature, TestTraits.Chat)]
    public void CaptureToolDetails_OnNonToolSpan_DoesNotRecordContent()
    {
        using var provider = CreateTracerProvider();
        using var source = new ActivitySource("chat-telemetry-test");
        using var activity = source.StartActivity("chat gpt-5-nano")!;
        activity.SetTag("gen_ai.operation.name", "chat");

        ChatTelemetry.CaptureToolArguments(activity, new { productId = 42 }, captureContent: true);
        ChatTelemetry.CaptureToolResult(activity, "ignored", captureContent: true);

        activity.GetTagItem("gen_ai.tool.call.arguments").Should().BeNull();
        activity.GetTagItem("gen_ai.tool.call.result").Should().BeNull();
    }

    private static TracerProvider CreateTracerProvider() => Sdk.CreateTracerProviderBuilder()
        .AddSource("chat-telemetry-test")
        .Build();
}
