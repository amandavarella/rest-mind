using RestMind.Core;
using Xunit;

namespace RestMind.Core.Tests;

public class PasswordHasherTests
{
    // Keep the work factor low here so the suite stays fast; production uses the default.
    private const int TestIterations = 1_000;

    [Fact]
    public void CorrectPassword_Verifies()
    {
        var stored = PasswordHasher.Hash("correct horse battery", TestIterations);

        Assert.True(PasswordHasher.Verify("correct horse battery", stored));
    }

    [Fact]
    public void WrongPassword_IsRejected()
    {
        var stored = PasswordHasher.Hash("correct horse battery", TestIterations);

        Assert.False(PasswordHasher.Verify("wrong password", stored));
        Assert.False(PasswordHasher.Verify("Correct horse battery", stored));
        Assert.False(PasswordHasher.Verify("", stored));
    }

    [Fact]
    public void SamePasswordHashedTwice_ProducesDifferentHashes()
    {
        var first = PasswordHasher.Hash("same", TestIterations);
        var second = PasswordHasher.Hash("same", TestIterations);

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("same", first));
        Assert.True(PasswordHasher.Verify("same", second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1000.notbase64!.alsonot!")]
    [InlineData("0.c2FsdA==.aGFzaA==")]
    [InlineData("notanumber.c2FsdA==.aGFzaA==")]
    public void MalformedStoredHash_IsRejectedWithoutThrowing(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }

    [Fact]
    public void ConfigVerify_IsFalseUntilAPasswordIsSet()
    {
        var config = new AppConfig();
        Assert.False(config.VerifyPassword("anything"));

        config.PasswordHash = PasswordHasher.Hash("parent", TestIterations);
        Assert.True(config.VerifyPassword("parent"));
        Assert.False(config.VerifyPassword("child"));
        Assert.False(config.VerifyPassword(null));
    }
}
