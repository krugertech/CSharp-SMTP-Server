using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace CSharp_SMTP_Server.Tests.Integrity;

/// <summary>
/// A throwaway RSA keypair and an in-memory DKIM public-key locator, for signature-survival tests.
/// </summary>
/// <remarks>
/// <para>
/// The keypair is generated once per test run and exists only in memory. It is never used to sign
/// anything that leaves the process, and there is deliberately no facility to load a key from disk
/// or configuration — this is a test fixture, not a signing capability.
/// </para>
/// <para>
/// <b>This does not give the server DKIM support.</b> The locator implements MimeKit's
/// <see cref="IDkimPublicKeyLocator"/> so that <see cref="DkimVerifier"/> can resolve a key without
/// DNS, and it lives in the test assembly. Nothing in <c>CSharp-SMTP-Server</c> references it, and
/// the server neither signs nor verifies DKIM — see the "No DKIM verification" entry in
/// <c>KNOWN_ISSUES.md</c>, which remains accurate.
/// </para>
/// </remarks>
internal static class DkimTestKey
{
    internal const string Domain = "example.com";
    internal const string Selector = "test";

    private static readonly AsymmetricCipherKeyPair KeyPair = Generate();

    private static AsymmetricCipherKeyPair Generate()
    {
        var generator = new RsaKeyPairGenerator();

        // 2048 bits: the smallest size a modern verifier accepts without complaint, and fast enough
        // to generate once per run without noticeably slowing the suite.
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));

        return generator.GenerateKeyPair();
    }

    /// <summary>A signer bound to the test key, for the given canonicalization pair.</summary>
    internal static DkimSigner Signer(
        DkimCanonicalizationAlgorithm headerAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
        DkimCanonicalizationAlgorithm bodyAlgorithm = DkimCanonicalizationAlgorithm.Relaxed) =>
        new(KeyPair.Private, Domain, Selector)
        {
            HeaderCanonicalizationAlgorithm = headerAlgorithm,
            BodyCanonicalizationAlgorithm = bodyAlgorithm,
            SignatureAlgorithm = DkimSignatureAlgorithm.RsaSha256
        };

    /// <summary>A verifier that resolves the test key from memory instead of DNS.</summary>
    internal static DkimVerifier Verifier() => new(new InMemoryLocator(KeyPair.Public));

    /// <summary>
    /// Returns the test public key for any selector/domain asked of it.
    /// </summary>
    /// <remarks>
    /// Answering unconditionally is intentional: these tests are about whether the signed octets
    /// survived the server, not about DNS lookup behaviour, so key resolution is made trivially
    /// correct to keep it from being a source of failure.
    /// </remarks>
    private sealed class InMemoryLocator : IDkimPublicKeyLocator
    {
        private readonly AsymmetricKeyParameter _publicKey;

        internal InMemoryLocator(AsymmetricKeyParameter publicKey) => _publicKey = publicKey;

        public AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector,
            CancellationToken cancellationToken = default) => _publicKey;

        public Task<AsymmetricKeyParameter> LocatePublicKeyAsync(string methods, string domain,
            string selector, CancellationToken cancellationToken = default) =>
            Task.FromResult(_publicKey);
    }
}
