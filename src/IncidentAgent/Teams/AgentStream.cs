using System.Net;
using System.Text;
using IncidentAgent.Agent;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Clients;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;

namespace IncidentAgent.Teams;

/// <summary>
/// Shows a reply while it's written, using the Teams streaming protocol: informative updates ("Checking the
/// runbook"), then the text so far, then a final message that replaces the bubble. Updates are sent through the agent
/// user's own API client, so each one comes from the agent user.
/// </summary>
/// <remarks>
/// Streaming is best effort. If Teams refuses a chunk for any reason other than the person pressing Stop or the
/// two-minute limit, the stream stops and the reply is posted as a normal message instead.
/// </remarks>
internal sealed class AgentStream(ConversationApiClient conversations, string conversationId, string requestId, ILogger logger) : IReplyStream
{
    /// <summary>Teams throttles streaming; text that arrives faster is sent with the next chunk.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly StringBuilder _text = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private string? _streamId;
    private int _sequence;
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
    private bool _timedOut;
    private bool _broken;

    public bool Cancelled { get; private set; }

    public bool Finished { get; private set; }

    public async Task InformAsync(string text, CancellationToken cancellationToken)
    {
        if (Cancelled || Finished || _broken || _text.Length > 0)
        {
            return;
        }

        // Informative updates are rare, so wait out the throttle rather than drop one.
        var wait = _lastSent + MinInterval - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }

        await SendAsync(text, StreamTypes.Informative, cancellationToken);
    }

    public async Task AppendAsync(string text, CancellationToken cancellationToken)
    {
        if (Cancelled || Finished)
        {
            return;
        }

        _text.Append(text);
        if (_timedOut || _broken || DateTimeOffset.UtcNow - _lastSent < MinInterval)
        {
            return;
        }

        await SendAsync(_text.ToString(), StreamTypes.Streaming, cancellationToken);
    }

    /// <summary>
    /// Replaces the streamed bubble with the final message and returns its ID. Returns null when nothing was streamed
    /// (or the stream broke), so the caller posts the message normally, or when the person stopped the reply.
    /// </summary>
    public async Task<string?> FinishAsync(MessageActivityInput final, CancellationToken cancellationToken)
    {
        Finished = true;
        if (Cancelled || _broken || _streamId is null)
        {
            return null;
        }

        final.ReplyToId = requestId;
        if (_timedOut)
        {
            return await ReplaceAsync(final, cancellationToken);
        }

        final.AddEntity(new StreamInfoEntity { StreamId = _streamId, StreamType = StreamTypes.Final });
        try
        {
            await conversations.CreateActivityAsync(conversationId, final, cancellationToken: cancellationToken);
            logger.LogInformation("Finished a streamed reply ({Chunks} updates)", _sequence);
            return _streamId;
        }
        catch (HttpRequestException ex) when (Classify(ex) is { } stop)
        {
            if (stop == Stop.TimedOut)
            {
                return await ReplaceAsync(final, cancellationToken);
            }

            logger.LogInformation("The reply was stopped before it finished");
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && final.Attachments is null)
        {
            // Close the bubble with just the text rather than leave the partial text behind.
            logger.LogWarning("Teams refused the end of a streamed reply ({Message}), so ending it with the text only", ex.Message);
            var plain = new MessageActivityInput { Text = final.Text, TextFormat = final.TextFormat, ReplyToId = requestId };
            plain.AddEntity(new StreamInfoEntity { StreamId = _streamId, StreamType = StreamTypes.Final });
            try
            {
                await conversations.CreateActivityAsync(conversationId, plain, cancellationToken: cancellationToken);
                return _streamId;
            }
            catch (HttpRequestException)
            {
                _broken = true;
                return null;
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Teams refused the end of a streamed reply ({Status}), so posting it normally", ex.StatusCode);
            _broken = true;
            return null;
        }
    }

    private async Task SendAsync(string text, StreamType type, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken);
        try
        {
            if (Cancelled || Finished || _broken)
            {
                return;
            }

            _sequence++;
            var chunk = new MessageActivityInput
            {
                Type = "typing",
                Text = text,
                ReplyToId = requestId,
                ChannelData = new TeamsOutboundChannelData { StreamId = _streamId, StreamType = type, StreamSequence = _sequence },
            };
            chunk.AddEntity(new StreamInfoEntity { StreamId = _streamId, StreamType = type, StreamSequence = _sequence });
            var sent = await conversations.CreateActivityAsync(conversationId, chunk, cancellationToken: cancellationToken);
            _streamId ??= sent?.Id;
            _lastSent = DateTimeOffset.UtcNow;
        }
        catch (HttpRequestException ex) when (Classify(ex) is { } stop)
        {
            if (stop == Stop.TimedOut)
            {
                _timedOut = true;
                logger.LogWarning("A streamed reply passed the two-minute limit; the rest goes in the final message");
            }
            else
            {
                Cancelled = true;
                logger.LogInformation("The person stopped the streamed reply");
            }
        }
        catch (HttpRequestException ex)
        {
            _broken = true;
            logger.LogWarning("Teams refused a streaming update ({Status}: {Message}), so the reply will be posted normally", ex.StatusCode, ex.Message);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>After the two-minute limit, the final text replaces the bubble as an ordinary edit.</summary>
    private async Task<string?> ReplaceAsync(MessageActivityInput final, CancellationToken cancellationToken)
    {
        final.Entities?.RemoveAll(e => e is StreamInfoEntity);
        if (final.ChannelData is { } data)
        {
            data.StreamId = null;
            data.StreamType = null;
            data.StreamSequence = null;
        }

        await conversations.UpdateActivityAsync(conversationId, _streamId!, final, cancellationToken: cancellationToken);
        return _streamId;
    }

    private enum Stop
    {
        Cancelled,
        TimedOut,
    }

    /// <summary>The soft stops from https://learn.microsoft.com/microsoftteams/platform/bots/streaming-ux#error-codes.</summary>
    private static Stop? Classify(HttpRequestException ex)
    {
        var message = ex.Message ?? string.Empty;
        return ex.StatusCode switch
        {
            HttpStatusCode.Forbidden when message.Contains("exceeded streaming time", StringComparison.OrdinalIgnoreCase) => Stop.TimedOut,
            HttpStatusCode.Forbidden when message.Contains("cancel", StringComparison.OrdinalIgnoreCase) => Stop.Cancelled,
            HttpStatusCode.NoContent or HttpStatusCode.Gone => Stop.Cancelled,
            _ => null,
        };
    }
}
