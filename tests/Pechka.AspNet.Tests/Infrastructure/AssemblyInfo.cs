using Xunit;

// TickingServiceWorkerBase.IntervalOverride is static and several tests share process-global state
// (retry budgets, the ticking service scanned out of this assembly), so the suite runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
