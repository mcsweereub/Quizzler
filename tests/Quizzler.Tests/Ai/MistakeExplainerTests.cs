using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Quizzler.Ai;
using Quizzler.Core.Questions;

namespace Quizzler.Tests.Ai;

public class MistakeExplainerTests
{
    private static readonly Question Q = new()
    {
        Id = "cs-types-0001", Type = QuestionType.PredictOutput, Topic = "csharp/types", Difficulty = 2,
        Prompt = "What does this print?", Code = "var a = new P(1); var b = a; b.X = 2; Console.WriteLine(a.X);",
        Answer = "1", Distractors = ["2", "0"], Explanation = "P is a struct, so b is a copy.",
    };

    [Fact]
    public void Prompt_includes_the_card_and_their_answer()
    {
        var message = MistakePrompt.BuildUserMessage(new MistakeRequest(Q, "2", WasMultipleChoice: true));

        Assert.Contains("<code>\nvar a = new P(1);", message);
        Assert.Contains("<correct_answer>\n1\n</correct_answer>", message);
        Assert.Contains("<their_answer how=\"They picked this option\">\n2\n</their_answer>", message);
        Assert.Contains("P is a struct", message);
        Assert.DoesNotContain("run_result", message);
    }

    [Fact]
    public void Exercise_prompt_sends_tests_reference_solution_and_run_report()
    {
        var exercise = new Question
        {
            Id = "dsa-exercises-0001", Type = QuestionType.WriteCode, Language = ExerciseLanguage.CSharp, Topic = "dsa/exercises",
            Difficulty = 2, Prompt = "Write Add.", Answer = "int Add(int a, int b) => a + b;", Explanation = "x",
            Tests = [new("Add(1, 2)", "3")],
        };

        var message = MistakePrompt.BuildUserMessage(new MistakeRequest(exercise, "int Add(int a, int b) => a - b;", false, "0 of 1 tests passed."));

        Assert.Contains("<tests>\nAdd(1, 2)  =>  3\n</tests>", message);
        Assert.Contains("<reference_solution>", message);
        Assert.Contains("how=\"Their code\"", message);
        Assert.Contains("<run_result>\n0 of 1 tests passed.\n</run_result>", message);
    }

    /// <summary>
    /// No real API call: a local listener captures what the SDK sends and replies with a canned stream, so this checks
    /// the request body (model, effort, fallbacks, prompt) and that streamed text comes back out.
    /// </summary>
    [Fact]
    public async Task Sends_the_expected_request_and_streams_text()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? body = null, beta = null;
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using (var reader = new StreamReader(ctx.Request.InputStream)) body = await reader.ReadToEndAsync();
            beta = ctx.Request.Headers["anthropic-beta"];
            ctx.Response.ContentType = "text/event-stream";
            var sse = string.Concat(
                Event("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":0}}}"""),
                Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"You're treating "}}"""),
                Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"a struct as a class."}}"""),
                Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":9}}"""),
                Event("message_stop", """{"type":"message_stop"}"""));
            var bytes = Encoding.UTF8.GetBytes(sse);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var explainer = new ClaudeMistakeExplainer("test-key", $"http://127.0.0.1:{port}");
        var text = new StringBuilder();
        await foreach (var chunk in explainer.ExplainAsync(new MistakeRequest(Q, "2", true)))
            text.Append(chunk);
        await server;

        Assert.Equal("You're treating a struct as a class.", text.ToString());
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Contains("misconception", root.GetProperty("system").GetString());
        Assert.Contains("server-side-fallback-2026-07-01", beta);
    }

    [Fact]
    public async Task A_rejected_key_becomes_a_readable_error()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var explainer = new ClaudeMistakeExplainer("bad-key", $"http://127.0.0.1:{port}");
        var ex = await Assert.ThrowsAsync<ExplainException>(async () =>
        {
            await foreach (var _ in explainer.ExplainAsync(new MistakeRequest(Q, "2", true))) { }
        });
        await server;

        Assert.Contains("API key was rejected", ex.Message);
    }

    private static string Event(string name, string data) => $"event: {name}\ndata: {data}\n\n";

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
