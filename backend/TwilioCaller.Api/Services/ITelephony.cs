using TwilioCaller.Api.Contracts;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Services.Plivo;

namespace TwilioCaller.Api.Services;

/// <summary>
/// What the app-facing API needs from a telephony provider, in the app's own DTOs.
/// Twilio and Plivo each implement it; nothing provider-shaped crosses this line.
/// </summary>
public interface ITelephonyApi
{
    Task<IReadOnlyList<PhoneNumberDto>> ListNumbersAsync(
        TwilioConnection connection, CancellationToken ct = default);

    Task<MessageDto> SendMessageAsync(
        TwilioConnection connection, SendMessageRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<MessageDto>> ListMessagesAsync(
        TwilioConnection connection, string ownedNumber, string? peerNumber, int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConversationDto>> ListConversationsAsync(
        TwilioConnection connection, string ownedNumber, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<CallDto>> ListCallsAsync(
        TwilioConnection connection, string? ownedNumber, int limit, CancellationToken ct = default);

    Task<CallDto> DialOutAsync(
        TwilioConnection connection, DialOutRequest request, CancellationToken ct = default);
}

/// <summary>Points a provider's numbers and voice application at this backend.</summary>
public interface INumberProvisioner
{
    /// <summary>Creates or refreshes the provider-side application. Idempotent.</summary>
    Task<string> EnsureAppAsync(TwilioConnection connection, CancellationToken ct = default);

    Task<ProvisionResponse> ProvisionNumbersAsync(
        TwilioConnection connection, ProvisionRequest request, CancellationToken ct = default);
}

/// <summary>Picks the implementation matching a connection's provider.</summary>
public class ProviderRouter(
    TwilioApiService twilio,
    ProvisioningService twilioProvisioning,
    PlivoApiService plivo,
    PlivoProvisioningService plivoProvisioning)
{
    public ITelephonyApi Api(TwilioConnection connection) =>
        connection.Provider == Providers.Plivo ? plivo : twilio;

    public INumberProvisioner Provisioner(TwilioConnection connection) =>
        connection.Provider == Providers.Plivo ? plivoProvisioning : twilioProvisioning;
}

internal static class Conversations
{
    /// <summary>
    /// Groups one owned number's recent messages into per-peer threads, which is
    /// what the app's inbox list renders.
    /// </summary>
    public static IReadOnlyList<ConversationDto> Group(
        IEnumerable<MessageDto> messages, string ownedNumber) =>
        messages
            .GroupBy(m => string.Equals(m.From, ownedNumber, StringComparison.OrdinalIgnoreCase)
                ? m.To
                : m.From)
            .Select(g =>
            {
                var latest = g.OrderByDescending(m => m.SentAt ?? DateTimeOffset.MinValue).First();
                return new ConversationDto(
                    OwnedNumber: ownedNumber,
                    PeerNumber: g.Key,
                    LastBody: latest.Body,
                    LastDirection: latest.Direction,
                    LastAt: latest.SentAt,
                    MessageCount: g.Count());
            })
            .OrderByDescending(c => c.LastAt ?? DateTimeOffset.MinValue)
            .ToList();
}
