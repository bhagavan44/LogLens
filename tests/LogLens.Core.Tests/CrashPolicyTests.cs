using System.Runtime.InteropServices;
using LogLens.Core.Diagnostics;

namespace LogLens.Core.Tests;

public sealed class CrashPolicyTests
{
    private DateTimeOffset _now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private CrashPolicy NewPolicy() => new(() => _now);

    [Fact]
    public void An_ordinary_exception_is_survived()
    {
        var policy = NewPolicy();

        Assert.Equal(CrashDecision.Continue, policy.Decide(new InvalidOperationException("bug in a handler")));
        Assert.Equal(1, policy.Survived);
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(AccessViolationException))]
    [InlineData(typeof(SEHException))]
    [InlineData(typeof(InvalidProgramException))]
    [InlineData(typeof(InsufficientExecutionStackException))]
    public void A_corrupting_exception_ends_the_process(Type type)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(CrashDecision.Terminate, NewPolicy().Decide(exception));
    }

    [Fact]
    public void A_corrupting_exception_is_found_when_wrapped()
    {
        var wrapped = new InvalidOperationException("outer", new AggregateException(new IOException(), new OutOfMemoryException()));

        Assert.True(CrashPolicy.IsCorrupting(wrapped));
        Assert.False(CrashPolicy.IsCorrupting(new InvalidOperationException("outer", new IOException())));
    }

    [Fact]
    public void A_missing_exception_is_treated_as_fatal() => Assert.Equal(CrashDecision.Terminate, NewPolicy().Decide(null));

    [Fact]
    public void Repeating_failures_end_the_process_instead_of_looping()
    {
        var policy = NewPolicy();
        for (var i = 0; i < CrashPolicy.MaxInWindow; i++)
        {
            Assert.Equal(CrashDecision.Continue, policy.Decide(new InvalidOperationException()));
            _now = _now.AddSeconds(5);
        }

        Assert.Equal(CrashDecision.Terminate, policy.Decide(new InvalidOperationException()));
    }

    [Fact]
    public void Failures_spread_over_time_are_each_survived()
    {
        var policy = NewPolicy();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(CrashDecision.Continue, policy.Decide(new InvalidOperationException()));
            _now += CrashPolicy.Window + TimeSpan.FromSeconds(1);
        }

        Assert.Equal(10, policy.Survived);
    }
}
