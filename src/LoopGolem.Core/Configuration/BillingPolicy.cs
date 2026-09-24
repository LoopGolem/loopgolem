namespace LoopGolem.Core.Configuration;

public sealed record BillingPolicy(
    bool AllowApiFallback = false,
    bool AllowAutomaticCreditPurchase = false,
    bool WaitForIncludedQuota = true);
