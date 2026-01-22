namespace BankMore.Contracts.Events;

public sealed record TransferCreatedEvent(
    Guid TransferId,
    Guid ContaCorrenteOrigemId,
    Guid ContaCorrenteDestinoId,
    decimal Valor,
    DateTimeOffset DataMovimento
);

