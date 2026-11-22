using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Quizzler.Ai;

/// <summary>A failure the user should see (bad key, no network, rate limit), with a message written for them.</summary>
public sealed class ExplainException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Streams Claude's explanation of a wrong answer. One request per click; nothing is stored.</summary>
public interface IMistakeExplainer
{
    IAsyncEnumerable<string> ExplainAsync(MistakeRequest request, CancellationToken cancellationToken = default);
}

/// <param name="baseUrl">Tests only: send requests to a local listener instead of the real API.</param>
public sealed class ClaudeMistakeExplainer(string apiKey, string? baseUrl = null) : IMistakeExplainer
{
    public const string Model = "claude-opus-5-5";

    public async IAsyncEnumerable<string> ExplainAsync(MistakeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = baseUrl is null ? new AnthropicClient { ApiKey = apiKey } : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 8000,
            // A short explanation doesn't need deep thinking; low effort keeps the wait short.
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            // If a safety classifier declines (unlikely for flashcards), the API retries on a fallback model in the same call.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = MistakePrompt.System,
            Messages = [new BetaUserMessageParam { Content = MistakePrompt.BuildUserMessage(request) }],
        };

        var stream = client.Beta.Messages.CreateStreaming(parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var any = false;
        try
        {
            while (true)
            {
                BetaRawMessageStreamEvent e;
                try
                {
                    if (!await stream.MoveNextAsync()) break;
                    e = stream.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw Friendly(ex);
                }

                if (e.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    any = true;
                    yield return text.Text;
                }
                else if (e.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason && reason == "refusal")
                {
                    throw new ExplainException("Claude declined to answer this one.");
                }
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        if (!any) throw new ExplainException("Claude returned an empty answer. Try again.");
    }

    private static ExplainException Friendly(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => new("The Anthropic API key was rejected. Check it in Settings.", ex),
        AnthropicRateLimitException => new("Rate limited by the Anthropic API. Wait a minute and try again.", ex),
        Anthropic5xxException => new("The Anthropic API is having trouble right now. Try again shortly.", ex),
        AnthropicIOException or HttpRequestException => new("Couldn't reach the Anthropic API. Check your connection.", ex),
        AnthropicApiException api => new($"The Anthropic API returned an error: {api.Message}", ex),
        _ => new($"Something went wrong asking Claude: {ex.Message}", ex),
    };
}
