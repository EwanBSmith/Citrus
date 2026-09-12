namespace Citrus.Trading;

/// <summary>Records an attributed cash adjustment or diagnostic execution cost; positive amounts denote credits.</summary>
public sealed record CashMovement(string Substrategy, Instrument Instrument, DateTimeOffset Time,
    string Kind, decimal Amount);
