namespace CSharp_SMTP_Server.Tests.Compatibility;

/// <summary>
/// Trait names and values shared by every platform compatibility suite, plus the provenance of the
/// behavioural claims those suites assert.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a compatibility suite can and cannot prove.</b> These tests talk to our own listener over
/// loopback. They verify <i>our side of a contract we wrote down</i> — they do not talk to Exchange
/// Online or to an on-premises Exchange organisation, and no green run here is evidence that a real
/// platform interoperates with this server. If the recorded understanding of what the platform sends
/// is wrong, the suite confirms the wrong thing confidently.
/// </para>
/// <para>
/// That is the reason for <see cref="Provenance"/>. Every behavioural claim a test encodes must name
/// where it came from: a Microsoft document, a published RFC, or an observation from a real
/// deployment with a date. A claim with no source is folklore, and folklore in a test suite is worse
/// than no test because it is asserted with the same confidence as fact.
/// </para>
/// <para>
/// <b>The goal is not "100% compatible".</b> That is not a property a loopback suite can certify, and
/// it would be false in the feature-parity sense: this server advertises only <c>AUTH LOGIN PLAIN</c>,
/// <c>STARTTLS</c>, <c>8BITMIME</c> and <c>SIZE</c> (see <c>ClientProcessor</c> EHLO handling). It
/// does not offer <c>PIPELINING</c>, <c>CHUNKING</c>/<c>BDAT</c>, <c>ENHANCEDSTATUSCODES</c>,
/// <c>SMTPUTF8</c> or <c>DSN</c>. What these suites establish is the defensible claim: <i>correct
/// interoperation within the advertised subset, and safe, non-desyncing refusal of everything outside
/// it.</i>
/// </para>
/// </remarks>
public static class PlatformContract
{
    /// <summary>
    /// Trait name marking a test as a platform compatibility contract.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> <c>Category=Load</c>. These are correctness tests about interoperation;
    /// only a couple of them are heavy. Filing them under the load category meant any CI job that
    /// excluded load tests silently dropped the entire Office 365 contract — the coverage looked
    /// present in the repository and was absent from the run. Compatibility and weight are orthogonal
    /// and are now separate traits: <see cref="Name"/> says what a test is about, <c>Load=heavy</c>
    /// says what it costs.
    /// </remarks>
    public const string Name = "Compatibility";

    /// <summary>Exchange Online / Microsoft 365 (the hosted service).</summary>
    public const string Office365 = "Office365";

    /// <summary>On-premises Exchange Server (2016/2019 send and receive connectors).</summary>
    public const string ExchangeOnPrem = "ExchangeOnPrem";

    /// <summary>
    /// Sources for the behavioural claims asserted by the compatibility suites.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cite one of these constants (or add a new one) in the XML docs of any test that encodes a
    /// claim about what a platform does. When a claim is revised, revise the entry here in the same
    /// change so the suite and its evidence never drift apart.
    /// </para>
    /// <para>
    /// <b>Unverified entries are marked as such.</b> An assertion resting on an unverified claim is
    /// still worth having — it pins current behaviour and fails loudly if someone changes it — but it
    /// must not be read as confirmation that the platform behaves that way.
    /// </para>
    /// </remarks>
    public static class Provenance
    {
        /// <summary>
        /// Exchange Online limits: 150 MB maximum message size. Microsoft, "Exchange Online limits —
        /// Message limits".
        /// </summary>
        /// <remarks>
        /// The 150 MB figure is the on-the-wire limit after transport encoding, which is why the
        /// configured ceiling here exceeds 150 MB rather than equalling it: base64 inflates an
        /// attachment by roughly 4/3.
        /// </remarks>
        public const string O365MessageLimits = "MS Exchange Online limits (message limits)";

        /// <summary>
        /// Exchange Online sends <c>MAIL FROM:&lt;&gt; BODY=8BITMIME AUTH=&lt;&gt;</c> on
        /// system-generated reports and DSNs. RFC 3461 / RFC 2554 parameter syntax.
        /// </summary>
        /// <remarks>
        /// Observed in production journaling traffic. The exact form matters: <c>AUTH=&lt;&gt;</c>
        /// carries angle brackets as a parameter <i>value</i>, which is why the null-reverse-path
        /// check is anchored to the address token rather than searching the line for "&lt;&gt;".
        /// </remarks>
        public const string O365EnvelopeParameters = "Observed: Exchange Online journaling envelope";

        /// <summary>
        /// A 4xx reply causes Exchange to queue and retry; a 5xx is permanent and, for a journal
        /// report, destroys a compliance record that exists nowhere else.
        /// </summary>
        /// <remarks>
        /// RFC 5321 §4.2.1 for the reply-code semantics; the operational asymmetry for journaling is
        /// the reason the journaling profile disables or raises every limit that could produce a 5xx
        /// on a well-formed message.
        /// </remarks>
        public const string RetrySemantics = "RFC 5321 §4.2.1 + journaling loss asymmetry";

        /// <summary>
        /// <c>XEXCH50</c> is a proprietary Exchange command used to carry MAPI properties between
        /// Exchange servers. It is offered only when the peer advertises it in EHLO.
        /// </summary>
        /// <remarks>
        /// <b>Unverified against a live on-premises organisation.</b> This server never advertises
        /// <c>XEXCH50</c>, so a conforming Exchange connector must not send it. The corresponding test
        /// therefore pins <i>our</i> behaviour — an unrecognised command must be refused without
        /// desynchronising the session — rather than asserting that Exchange sends it.
        /// </remarks>
        public const string XExch50 = "Unverified: Exchange proprietary command (EHLO-gated)";

        /// <summary>
        /// <c>BDAT</c> (RFC 3030 <c>CHUNKING</c>) is used by Exchange when the peer advertises
        /// <c>CHUNKING</c>, and Exchange falls back to <c>DATA</c> when it is not advertised.
        /// </summary>
        /// <remarks>
        /// RFC 3030 §2 requires a client not to issue <c>BDAT</c> unless <c>CHUNKING</c> was
        /// advertised. This server does not advertise it, so the test pins safe refusal, not platform
        /// behaviour.
        /// </remarks>
        public const string Chunking = "RFC 3030 §2 (CHUNKING is EHLO-gated)";

        /// <summary>
        /// A client may not pipeline commands unless the server advertised <c>PIPELINING</c>
        /// (RFC 2920 §3.1). This server does not advertise it.
        /// </summary>
        /// <remarks>
        /// Pinned anyway: a misconfigured connector or an intermediary proxy can send a pipelined
        /// batch regardless, and command desynchronisation is exactly the failure mode that turns one
        /// bad connection into misattributed mail.
        /// </remarks>
        public const string Pipelining = "RFC 2920 §3.1 (client must not pipeline unbidden)";

        /// <summary>
        /// STARTTLS must reset session state: after a successful TLS handshake the client re-issues
        /// EHLO and the server discards any prior EHLO, MAIL FROM and RCPT TO state (RFC 3207 §4.2).
        /// </summary>
        public const string StartTlsReset = "RFC 3207 §4.2 (discard state after STARTTLS)";

        /// <summary>
        /// Exchange stamps <c>X-MS-Exchange-*</c> headers that downstream compliance tooling reads.
        /// The body must be preserved byte-for-byte through the streaming DATA path.
        /// </summary>
        /// <remarks>
        /// RFC 5321 §4.5.2 for dot-unstuffing; header preservation is our own storage contract rather
        /// than a platform claim, so this needs no external source.
        /// </remarks>
        public const string HeaderPreservation = "RFC 5321 §4.5.2 + local storage contract";
    }
}
