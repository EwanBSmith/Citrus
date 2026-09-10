namespace Citrus.Trading;

/// <summary>Controls whether an unfilled order persists or expires at the session close (UTC midnight for perpetuals).</summary>
public enum TimeInForce { GoodTillCancelled, Day }
