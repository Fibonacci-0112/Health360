namespace Health360.Sync.Tests.Support;

/// <summary>
/// Every sync test class joins this collection, so they run one at a time, as they did when there was one class. They
/// share one local Supabase and PowerSync, take airplane mode on and off, and time a reconnect against the 5 s target
/// (PLAN.md §3); a second class running at the same time would load the same services and skew those timings.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SerialSync
{
    public const string Name = "Sync";
}
