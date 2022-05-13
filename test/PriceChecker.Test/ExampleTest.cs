using FluentAssertions;
using NUnit.Framework;

namespace PriceChecker.Test;

[TestFixture]
public static class ExampleTest
{
    [Test]
    public static void Example() => (40 + 2).Should().Be(42);
}