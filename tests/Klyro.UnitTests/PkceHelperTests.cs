using System;
using System.IO;
using System.Text;
using Klyro.Core.Authentication;
using Xunit;

namespace Klyro.UnitTests;

public class PkceHelperTests
{
    [Fact]
    public void GenerateCodeVerifier_ReturnsUrlSafeHighEntropyString()
    {
        string verifier = PkceHelper.GenerateCodeVerifier();

        Assert.NotNull(verifier);
        Assert.InRange(verifier.Length, 43, 128);

        // Verify URL-safe characters only (RFC 7636 unreserved characters)
        foreach (char c in verifier)
        {
            bool isAllowed = char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '~';
            Assert.True(isAllowed, $"Character '{c}' is not RFC 7636 URL-safe.");
        }
    }

    [Fact]
    public void GenerateCodeChallenge_ProducesDeterministicSha256Base64Url()
    {
        // RFC 7636 Appendix B test vector:
        // verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
        // challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
        string testVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        string challenge = PkceHelper.GenerateCodeChallenge(testVerifier);

        Assert.Equal(expectedChallenge, challenge);
    }

    [Fact]
    public void Base64Url_RoundTrip_PreservesArbitraryBytes()
    {
        byte[] original = new byte[] { 0, 1, 255, 128, 64, 32, 16, 8, 4, 2, 1 };
        string encoded = PkceHelper.Base64UrlEncode(original);

        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
        Assert.DoesNotContain("=", encoded);

        byte[] decoded = PkceHelper.Base64UrlDecode(encoded);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ParseGoogleIdToken_WithValidJwtClaims_ExtractsAuthenticCandidateProfile()
    {
        // Construct standard OpenID Connect JWT payload
        string headerJson = """{"alg":"RS256","kid":"mock_key"}""";
        string payloadJson = """
        {
            "iss": "https://accounts.google.com",
            "sub": "109876543210987654321",
            "email": "exam.candidate@university.edu",
            "email_verified": true,
            "name": "Jane Candidate",
            "given_name": "Jane",
            "family_name": "Candidate",
            "picture": "https://lh3.googleusercontent.com/a/mock-photo",
            "hd": "university.edu"
        }
        """;

        string headerB64 = PkceHelper.Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        string payloadB64 = PkceHelper.Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        string mockSignatureB64 = PkceHelper.Base64UrlEncode(new byte[] { 1, 2, 3, 4 });

        string mockJwt = $"{headerB64}.{payloadB64}.{mockSignatureB64}";

        var user = PkceHelper.ParseGoogleIdToken(mockJwt);

        Assert.NotNull(user);
        Assert.Equal("exam.candidate@university.edu", user.Email);
        Assert.Equal("Jane Candidate", user.DisplayName);
        Assert.Equal("109876543210987654321", user.Id);
        Assert.Equal("109876543210987654321", user.GoogleSub);
        Assert.Equal("https://lh3.googleusercontent.com/a/mock-photo", user.PhotoUrl);
        Assert.Equal("JC", user.GetInitials());
        Assert.True(user.VerifiedEmail);
    }

    [Fact]
    public void GoogleOAuthConfiguration_BuildsValidAuthorizationUrl()
    {
        var config = new GoogleOAuthConfiguration
        {
            ClientId = "test-client-id.apps.googleusercontent.com",
            Scopes = "openid email profile"
        };

        string redirectUri = "http://127.0.0.1:58291/auth-callback/";
        string challenge = "test_challenge_hash";
        string state = "random_csrf_token";

        string url = config.BuildAuthorizationUrl(redirectUri, challenge, state);

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth", url);
        Assert.Contains("client_id=test-client-id.apps.googleusercontent.com", url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(redirectUri), url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge=" + challenge, url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=" + state, url);
        Assert.Contains("prompt=select_account", url);
    }

    [Fact]
    public void ParseFirefoxLoginsFile_WithGoogleAccount_ExtractsIdentityCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        string loginsPath = Path.Combine(tempDir, "logins.json");

        try
        {
            string firefoxLoginsJson = """
            {
                "logins": [
                    {
                        "hostname": "https://accounts.google.com",
                        "username": "firefox.candidate@school.org",
                        "timeLastUsed": 1700000000000
                    }
                ]
            }
            """;
            File.WriteAllText(loginsPath, firefoxLoginsJson);

            var user = BrowserProfileDetector.ParseFirefoxLoginsFile(loginsPath);

            Assert.NotNull(user);
            Assert.Equal("firefox.candidate@school.org", user.Email);
            Assert.Equal("Firefox Candidate", user.DisplayName);
            Assert.Equal("FC", user.GetInitials());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
