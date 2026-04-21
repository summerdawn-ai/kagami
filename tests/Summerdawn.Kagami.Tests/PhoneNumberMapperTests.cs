
using Summerdawn.Kagami.Connectors.Microsoft;

namespace Summerdawn.Kagami.Tests;

public sealed class PhoneNumberMapperTests
{
    // ── ToMicrosoftField ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("work", PhoneNumberMapper.BusinessPhones)]
    [InlineData("Work", PhoneNumberMapper.BusinessPhones)]
    [InlineData("WORK", PhoneNumberMapper.BusinessPhones)]
    public void ToMicrosoftField_WorkLabel_ReturnsBusinessPhones(string label, string expected) =>
        Assert.Equal(expected, PhoneNumberMapper.ToMicrosoftField(label));

    [Theory]
    [InlineData("home", PhoneNumberMapper.HomePhones)]
    [InlineData("Home", PhoneNumberMapper.HomePhones)]
    [InlineData("HOME", PhoneNumberMapper.HomePhones)]
    public void ToMicrosoftField_HomeLabel_ReturnsHomePhones(string label, string expected) =>
        Assert.Equal(expected, PhoneNumberMapper.ToMicrosoftField(label));

    [Theory]
    [InlineData("mobile", PhoneNumberMapper.MobilePhone)]
    [InlineData("Mobile", PhoneNumberMapper.MobilePhone)]
    [InlineData("MOBILE", PhoneNumberMapper.MobilePhone)]
    public void ToMicrosoftField_MobileLabel_ReturnsMobilePhone(string label, string expected) =>
        Assert.Equal(expected, PhoneNumberMapper.ToMicrosoftField(label));

    [Theory]
    [InlineData("other", PhoneNumberMapper.HomePhones)]
    [InlineData("Other", PhoneNumberMapper.HomePhones)]
    [InlineData("OTHER", PhoneNumberMapper.HomePhones)]
    public void ToMicrosoftField_OtherLabel_ReturnsHomePhones(string label, string expected) =>
        Assert.Equal(expected, PhoneNumberMapper.ToMicrosoftField(label));

    // Labels with no direct Microsoft equivalent fall back to businessPhones.
    [Theory]
    [InlineData("main")]
    [InlineData("workFax")]
    [InlineData("homeFax")]
    [InlineData("pager")]
    [InlineData("workMobile")]
    [InlineData("googleVoice")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void ToMicrosoftField_UnrecognisedOrNullLabel_FallsBackToBusinessPhones(string? label) =>
        Assert.Equal(PhoneNumberMapper.BusinessPhones, PhoneNumberMapper.ToMicrosoftField(label));

    // ── FromMicrosoftField ────────────────────────────────────────────────────

    [Fact]
    public void FromMicrosoftField_BusinessPhones_ReturnsWork() =>
        Assert.Equal("work", PhoneNumberMapper.FromMicrosoftField(PhoneNumberMapper.BusinessPhones));

    [Fact]
    public void FromMicrosoftField_HomePhones_ReturnsHome() =>
        Assert.Equal("home", PhoneNumberMapper.FromMicrosoftField(PhoneNumberMapper.HomePhones));

    [Fact]
    public void FromMicrosoftField_MobilePhone_ReturnsMobile() =>
        Assert.Equal("mobile", PhoneNumberMapper.FromMicrosoftField(PhoneNumberMapper.MobilePhone));

    // ── Round-trip semantics ──────────────────────────────────────────────────

    // The labels that have a direct counterpart round-trip exactly.
    [Theory]
    [InlineData("work")]
    [InlineData("home")]
    [InlineData("mobile")]
    public void RoundTrip_DirectlySupportedLabel_PreservesLabel(string label)
    {
        string msField = PhoneNumberMapper.ToMicrosoftField(label);
        string canonical = PhoneNumberMapper.FromMicrosoftField(msField);
        Assert.Equal(label, canonical);
    }

    // Labels that fall back to a Microsoft field may change on the return trip,
    // but the mapping should at least be consistent (idempotent from MS back to canonical).
    [Theory]
    [InlineData("other", "home")]   // other → homePhones → home
    [InlineData("main", "work")]   // main  → businessPhones → work
    [InlineData("workFax", "work")]   // workFax → businessPhones → work
    [InlineData("pager", "work")]   // pager  → businessPhones → work
    public void RoundTrip_FallbackLabel_PreservesValueButLabelMayChange(string label, string expectedAfterRoundTrip)
    {
        string msField = PhoneNumberMapper.ToMicrosoftField(label);
        string canonical = PhoneNumberMapper.FromMicrosoftField(msField);
        Assert.Equal(expectedAfterRoundTrip, canonical);
    }
}
