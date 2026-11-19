namespace BuildingBlocks.Messaging;

/// <summary>Integration event. EventType convention: context.event.vN (past tense).</summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTime OccurredAtUtc { get; }
    string EventType { get; }
}

/// <summary>Publishes by writing an outbox row in the caller's transaction (implemented in a later phase).</summary>
public interface IEventPublisher
{
    Task PublishAsync(IIntegrationEvent @event, string aggregateId, CancellationToken ct = default);
}

public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken ct = default);
}
