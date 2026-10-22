namespace Payments.CustomerSimulation.Support;

/// <summary>One collection so scenarios share the stack serially; the daily-summary test compares totals before and after.</summary>
[CollectionDefinition(Name)]
public sealed class StackCollection
{
    public const string Name = "Running stack";
}
