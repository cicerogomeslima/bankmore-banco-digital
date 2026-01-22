namespace BankMore.Shared;

public sealed record ErrorResponse(string tipoFalha, string mensagem);
