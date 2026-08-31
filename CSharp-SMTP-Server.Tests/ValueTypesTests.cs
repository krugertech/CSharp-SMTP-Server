using CSharp_SMTP_Server.Networking;
using CSharp_SMTP_Server.Protocol;
using CSharp_SMTP_Server.Protocol.Responses;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§3.9): sanity tests for the small value types whose numeric values are part of the wire
/// protocol or filter contract — reordering any member would silently change behavior.
/// </summary>
public sealed class ValueTypesTests
{
    [Fact]
    public void SmtpResult_DefaultFailMessage_IsNull()
    {
        var r = new SmtpResult(SmtpResultType.Success);

        Assert.Equal(SmtpResultType.Success, r.Type);
        Assert.Null(r.FailMessage);
    }

    [Fact]
    public void SmtpResult_StoresTypeAndFailMessage()
    {
        var r = new SmtpResult(SmtpResultType.PermanentFail, "custom reason");

        Assert.Equal(SmtpResultType.PermanentFail, r.Type);
        Assert.Equal("custom reason", r.FailMessage);
    }

    [Fact]
    public void UserExistsCodes_HaveStableRfcOrdering()
    {
        // The values map 1:1 to SMTP responses in TransactionCommands (250/5.1.x) — pin the order.
        Assert.Equal(0, (int)UserExistsCodes.DestinationAddressValid);
        Assert.Equal(1, (int)UserExistsCodes.BadDestinationMailboxAddress);
        Assert.Equal(2, (int)UserExistsCodes.BadDestinationSystemAddress);
        Assert.Equal(3, (int)UserExistsCodes.DestinationMailboxAddressAmbiguous);
        Assert.Equal(4, (int)UserExistsCodes.DestinationAddressHasMovedAndNoForwardingAddress);
        Assert.Equal(5, (int)UserExistsCodes.BadSendersSystemAddress);
    }

    [Fact]
    public void SmtpResultType_HasStableValues()
    {
        Assert.Equal(0, (byte)SmtpResultType.Success);
        Assert.Equal(1, (byte)SmtpResultType.TemporaryFail);
        Assert.Equal(2, (byte)SmtpResultType.PermanentFail);
    }

    [Fact]
    public void ConnectionEncryption_HasStableValues()
    {
        Assert.Equal(0, (byte)ConnectionEncryption.Plaintext);
        Assert.Equal(1, (byte)ConnectionEncryption.StartTls);
        Assert.Equal(2, (byte)ConnectionEncryption.Tls);
    }

    [Fact]
    public void ValidationResult_HasStableValues()
    {
        // SPF/DMARC results are lower-cased into Authentication-Results headers and compared in the
        // protocol layer — pin both names and values.
        Assert.Equal(0, (int)ValidationResult.None);
        Assert.Equal(1, (int)ValidationResult.Neutral);
        Assert.Equal(2, (int)ValidationResult.Pass);
        Assert.Equal(3, (int)ValidationResult.Fail);
        Assert.Equal(4, (int)ValidationResult.Softfail);
        Assert.Equal(5, (int)ValidationResult.Temperror);
        Assert.Equal(6, (int)ValidationResult.Permerror);
        Assert.Equal(7, (int)ValidationResult.CheckDisabled);
        Assert.Equal(8, (int)ValidationResult.UserAuthenticated);
    }
}
