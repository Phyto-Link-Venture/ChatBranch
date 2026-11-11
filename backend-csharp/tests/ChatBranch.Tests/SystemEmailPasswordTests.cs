using ChatBranch.Api.Security;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A pasted SMTP password. Google shows a Gmail App Password as four groups of
/// four and copying it from Google's dialog brings the spaces along; stored as
/// pasted, Gmail answers "5.7.8 Username and Password not accepted", which
/// reads exactly like a wrong password. Only that shape is de-spaced, so a
/// password that genuinely contains a space still works.
/// </summary>
public class SystemEmailPasswordTests
{
    [Theory]
    [InlineData("abcd efgh ijkl mnop", "abcdefghijklmnop")]   // Gmail App Password as Google prints it
    [InlineData("  abcd efgh ijkl mnop  ", "abcdefghijklmnop")]
    [InlineData("ABCD EFGH IJKL MNOP", "ABCDEFGHIJKLMNOP")]
    public void A_gmail_app_password_loses_its_spaces(string pasted, string stored) =>
        Assert.Equal(stored, SystemEmail.NormalizePassword(pasted));

    [Theory]
    [InlineData("  sk_live_not_really  ", "sk_live_not_really")]              // trimmed
    [InlineData("correct horse battery staple", "correct horse battery staple")] // 4 words, NOT 4x4 — untouched
    [InlineData("abcd efgh ijkl", "abcd efgh ijkl")]                          // only 3 groups — untouched
    [InlineData("abcd efgh ijkl mnop qrst", "abcd efgh ijkl mnop qrst")]      // 5 groups — untouched
    [InlineData("ab1d efgh ijkl mnop", "ab1d efgh ijkl mnop")]                // digits — untouched
    [InlineData("my pass word here", "my pass word here")]
    [InlineData("", "")]
    public void Everything_else_is_only_trimmed(string pasted, string stored) =>
        Assert.Equal(stored, SystemEmail.NormalizePassword(pasted));

    [Fact]
    public void Null_is_empty() => Assert.Equal("", SystemEmail.NormalizePassword(null));
}
