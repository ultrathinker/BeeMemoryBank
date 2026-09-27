using BeeMemoryBank.Api.Services.BlindBackup;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Pins the SigV4 canonical request shape used for the recovery-set S3 PUT. The real acceptance
/// test is a round-trip against an S3 server on a test host (a wrong signature answers 403 there);
/// these unit tests pin the BYTES that get signed, which MinIO cannot tell us.
/// </summary>
public class S3PutSigningTests
{
    // SHA-256 of the empty string — the well-known constant e3b0c442…, so the expected canonical
    // string below is checkable by hand.
    private const string EmptySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void CanonicalRequest_IsTheExactSigV4Shape()
    {
        var cr = S3Put.BuildCanonicalRequest(
            "PUT", "/bmb-blind/blind-repo.recovery-set.json", "minio.lan:9000", EmptySha, "20260927T120000Z");

        cr.Should().Be(
            "PUT\n" +
            "/bmb-blind/blind-repo.recovery-set.json\n" +
            "\n" +
            "host:minio.lan:9000\n" +
            "x-amz-content-sha256:" + EmptySha + "\n" +
            "x-amz-date:20260927T120000Z\n" +
            "\n" +
            "host;x-amz-content-sha256;x-amz-date\n" +
            EmptySha);
    }

    [Fact]
    public void StringToSign_BindsScopeAndCanonicalHash()
    {
        var canonical = S3Put.BuildCanonicalRequest("PUT", "/x", "h", EmptySha, "20260927T120000Z");
        var sts = S3Put.BuildStringToSign("20260927T120000Z", "20260927/us-east-1/s3/aws4_request", canonical);

        sts.Should().StartWith("AWS4-HMAC-SHA256\n20260927T120000Z\n20260927/us-east-1/s3/aws4_request\n");
        // The last line is the canonical request's own SHA-256, so a one-byte change to the
        // canonical request must change the string to sign.
        var other = S3Put.BuildStringToSign("20260927T120000Z", "20260927/us-east-1/s3/aws4_request",
            canonical.Replace("/x", "/y"));
        other.Should().NotBe(sts, "the payload (via its hash in the canonical request) is signed, not sent blind");
    }

    [Fact]
    public void ObjectKey_KeepsSegmentSlashes_EncodesTheRest()
    {
        S3Put.EscapeKey("bmb/blind repo.recovery-set.json").Should().Be("bmb/blind%20repo.recovery-set.json",
            "a nested prefix is a path in S3; %2F would name a different object than restic's repo sibling");
    }
}
