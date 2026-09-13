namespace Citrus.Simulation;

/// <summary>Configures execution costs, rejection probability, margin fractions, and equity borrowing.</summary>
public sealed record SimulationOptions
{
    /// <summary>Gets the fixed commission per external net order, allocated across its attributed fills.</summary>
    public decimal CommissionFixed { get; init; }
    /// <summary>Gets the commission per absolute unit externally executed.</summary>
    public decimal CommissionPerUnit { get; init; }
    /// <summary>Gets the full bid/ask spread in basis points; execution applies half on each side.</summary>
    public decimal SpreadBps { get; init; }
    /// <summary>Gets additional adverse execution movement in basis points.</summary>
    public decimal SlippageBps { get; init; }
    /// <summary>Gets the probability from zero to one that an external submission is rejected.</summary>
    public double RejectionProbability { get; init; }
    /// <summary>Gets the equity notional fraction required when increasing exposure.</summary>
    public decimal EquityInitialMargin { get; init; } = 0.5m;
    /// <summary>Gets the equity notional fraction required to avoid liquidation.</summary>
    public decimal EquityMaintenanceMargin { get; init; } = 0.25m;
    /// <summary>Gets the perpetual notional fraction required when increasing exposure.</summary>
    public decimal PerpetualInitialMargin { get; init; } = 0.1m;
    /// <summary>Gets the perpetual notional fraction required to avoid liquidation.</summary>
    public decimal PerpetualMaintenanceMargin { get; init; } = 0.05m;
    /// <summary>Gets the annual fractional borrow rate charged on net short equity exposure using elapsed time and a 365-day year.</summary>
    public decimal AnnualBorrowRate { get; init; } = 0.03m;
    /// <summary>Gets whether external orders may increase net short equity exposure.</summary>
    public bool ShortsAvailable { get; init; } = true;
    /// <summary>Rejects invalid probabilities, negative costs, nonpositive execution prices implied by costs, and inconsistent margin fractions.</summary>
    public void Validate()
    {
        if (!double.IsFinite(RejectionProbability) || RejectionProbability is < 0 or > 1)
            throw new ArgumentException("RejectionProbability must be between zero and one.");
        if (CommissionFixed < 0 || CommissionPerUnit < 0 || SpreadBps < 0 || SlippageBps < 0 || AnnualBorrowRate < 0)
            throw new ArgumentException("Costs must be nonnegative.");
        if (SpreadBps / 2 + SlippageBps >= 10000) throw new ArgumentException("Combined execution cost must be less than 100 percent.");
        if (EquityMaintenanceMargin <= 0 || EquityInitialMargin < EquityMaintenanceMargin || EquityInitialMargin > 1 ||
            PerpetualMaintenanceMargin <= 0 || PerpetualInitialMargin < PerpetualMaintenanceMargin || PerpetualInitialMargin > 1)
            throw new ArgumentException("Margin fractions must satisfy 0 < maintenance <= initial <= 1.");
    }
}
