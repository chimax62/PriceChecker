using FluentAssertions;
using NUnit.Framework;

namespace PriceChecker.Test;

[SetUpFixture]
public class Tests
{
    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        AssertionOptions.AssertEquivalencyUsing(opt => opt
            .RespectingRuntimeTypes()
        );
    }
}