using FleetBooking.Shared;
using Xunit;

namespace FleetBooking.Tests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Abcde!", false)]
    [InlineData("abcdefgh!", false)]
    [InlineData("ABCDEFGH!", false)]
    [InlineData("Abcdefgh", false)]
    [InlineData("Abcdefgh!", true)]
    [InlineData("Abcdefg1!", true)]
    public void Digits_are_optional_while_other_requirements_remain(string? password, bool expected) =>
        Assert.Equal(expected, PasswordPolicy.IsValid(password));
}
