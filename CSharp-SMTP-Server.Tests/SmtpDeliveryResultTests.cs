using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.1): pure unit tests for SmtpDeliveryResult — the value type whose fields are written
/// verbatim onto the SMTP wire, including the CR/LF guards that prevent response splitting.
/// </summary>
public sealed class SmtpDeliveryResultTests
{
    [Fact]
    public void Ok_Defaults()
    {
        var r = SmtpDeliveryResult.Ok();

        Assert.Equal(250, r.StatusCode);
        Assert.Equal("2.0.0", r.EnhancedStatus);
        Assert.Equal("OK", r.Message);
    }

    [Fact]
    public void Ok_CustomMessage_KeepsCodeAndEnhancedStatus()
    {
        var r = SmtpDeliveryResult.Ok("stored");

        Assert.Equal(250, r.StatusCode);
        Assert.Equal("2.0.0", r.EnhancedStatus);
        Assert.Equal("stored", r.Message);
    }

    [Fact]
    public void TemporaryFailure_Defaults()
    {
        var r = SmtpDeliveryResult.TemporaryFailure();

        Assert.Equal(451, r.StatusCode);
        Assert.Equal("4.3.0", r.EnhancedStatus);
        Assert.Equal("Requested action aborted: local error in processing", r.Message);
    }

    [Fact]
    public void TemporaryFailure_CustomMessage()
    {
        var r = SmtpDeliveryResult.TemporaryFailure("try again later");

        Assert.Equal(451, r.StatusCode);
        Assert.Equal("4.3.0", r.EnhancedStatus);
        Assert.Equal("try again later", r.Message);
    }

    [Fact]
    public void PermanentFailure_Defaults()
    {
        var r = SmtpDeliveryResult.PermanentFailure();

        Assert.Equal(554, r.StatusCode);
        Assert.Equal("5.7.1", r.EnhancedStatus);
        Assert.Equal("Message rejected", r.Message);
    }

    [Fact]
    public void PermanentFailure_CustomMessage()
    {
        var r = SmtpDeliveryResult.PermanentFailure("no such mailbox");

        Assert.Equal(554, r.StatusCode);
        Assert.Equal("5.7.1", r.EnhancedStatus);
        Assert.Equal("no such mailbox", r.Message);
    }

    [Theory]
    [InlineData(552, "5.4.3", "too big")]
    [InlineData(550, "5.1.1", "unroutable recipient")]
    [InlineData(554, "5.6.0", "content rejected")]
    public void Status_PassesAllThreeFieldsThroughVerbatim(int code, string enhanced, string message)
    {
        var r = SmtpDeliveryResult.Status(code, enhanced, message);

        Assert.Equal(code, r.StatusCode);
        Assert.Equal(enhanced, r.EnhancedStatus);
        Assert.Equal(message, r.Message);
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    [InlineData("line1\r\nline2")]
    public void Message_ContainingCrLf_ThrowsArgumentException(string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => SmtpDeliveryResult.Ok(message));

        Assert.Contains("CR or LF", ex.Message);
        Assert.Equal("message", ex.ParamName); // nameof of the ctor parameter, not the property
    }

    [Theory]
    [InlineData("2.0\n0")]
    [InlineData("2.0\r0")]
    public void EnhancedStatus_ContainingCrLf_ThrowsArgumentException(string enhanced)
    {
        var ex = Assert.Throws<ArgumentException>(() => SmtpDeliveryResult.Status(250, enhanced, "ok"));

        Assert.Contains("CR or LF", ex.Message);
        Assert.Equal("enhancedStatus", ex.ParamName); // nameof of the ctor parameter, not the property
    }

    [Fact]
    public void EmptyMessage_IsAllowed()
    {
        var r = SmtpDeliveryResult.Ok(string.Empty);

        Assert.Equal(string.Empty, r.Message);
    }

    [Fact]
    public void NullMessage_ThrowsNullReferenceException_DocumentCurrentBehavior()
    {
        // The parameter is non-nullable; passing null today produces an NRE from IndexOf.
        // Pin the behavior so a future change to ArgumentNullException is a conscious decision.
        Assert.Throws<NullReferenceException>(() => SmtpDeliveryResult.Ok(null!));
    }
}
