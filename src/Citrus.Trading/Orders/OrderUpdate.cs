namespace Citrus.Trading;

/// <summary>Records an order status change at a simulation time, with an optional rejection or cancellation reason.</summary>
public sealed record OrderUpdate(long OrderId, OrderRequest Request, OrderStatus Status,
    /// <summary>Gets the current simulation time in UTC.</summary>
    DateTimeOffset Time, string? Reason = null);
