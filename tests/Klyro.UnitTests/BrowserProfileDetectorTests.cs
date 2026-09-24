using System;
using System.IO;
using System.Text.Json;
using Klyro.Core.Authentication;
using Xunit;

namespace Klyro.UnitTests;

public class BrowserProfileDetectorTests
{
    [Fact]
    public void ParsePreferencesFile_WithValidGoogleAccount_ExtractsProfileCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        string prefPath = Path.Combine(tempDir, "Preferences");

        try
        {
            var prefJson = """
            {
                "account_info": [
                    {
                        "account_id": "123456789",
                        "email": "student.test@university.edu",
                        "full_name": "Student Tester",
                        "given_name": "Student",
                        "picture_url": "https://lh3.googleusercontent.com/test-photo",
                        "gaia": "123456789"
                    }
                ]
            }
            """;
            File.WriteAllText(prefPath, prefJson);

            var user = BrowserProfileDetector.ParsePreferencesFile(prefPath);

            Assert.NotNull(user);
            Assert.Equal("student.test@university.edu", user.Email);
            Assert.Equal("Student Tester", user.DisplayName);
            Assert.Equal("https://lh3.googleusercontent.com/test-photo", user.PhotoUrl);
            Assert.Equal("123456789", user.Id);
            Assert.Equal("ST", user.GetInitials());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ParsePreferencesFile_WithEdgeAccountFields_ExtractsProfileCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        string prefPath = Path.Combine(tempDir, "Preferences");

        try
        {
            var prefJson = """
            {
                "account_info": [
                    {
                        "account_id": "987654321",
                        "email": "student2@university.edu",
                        "full_name": "",
                        "edge_account_first_name": "venkat",
                        "edge_account_last_name": "sai",
                        "picture_url": "",
                        "gaia": "987654321"
                    }
                ]
            }
            """;
            File.WriteAllText(prefPath, prefJson);

            var user = BrowserProfileDetector.ParsePreferencesFile(prefPath);

            Assert.NotNull(user);
            Assert.Equal("student2@university.edu", user.Email);
            Assert.Equal("Venkat Sai", user.DisplayName);
            Assert.Equal("VS", user.GetInitials());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ParsePreferencesFile_WhenMalformed_ReturnsNullSafely()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        string prefPath = Path.Combine(tempDir, "Preferences");

        try
        {
            File.WriteAllText(prefPath, "{ not valid json ... ");

            var user = BrowserProfileDetector.ParsePreferencesFile(prefPath);
            Assert.Null(user);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ParsePreferencesFile_WhenFileMissing_ReturnsNullSafely()
    {
        var user = BrowserProfileDetector.ParsePreferencesFile(@"C:\NonExistentDirectory\DoesNotExist\Preferences");
        Assert.Null(user);
    }

    [Theory]
    [InlineData("john doe", "john@example.com", "John Doe")]
    [InlineData("JANE DOE", "jane@example.com", "Jane Doe")]
    [InlineData("Dr. Alice Smith", "alice@example.com", "Dr. Alice Smith")]
    [InlineData("", "alex.morgan@univ.edu", "Alex Morgan")]
    [InlineData(null, "p.venkatsai333@gmail.com", "P Venkatsai")]
    public void FormatCleanDisplayName_HandlesVariousInputs(string? raw, string email, string expected)
    {
        var result = BrowserProfileDetector.FormatCleanDisplayName(raw, email);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("p.venkatasai07@gmail.com", true)]
    [InlineData("venkatasai@gmail.com", true)]
    [InlineData("alex.morgan@univ.edu", true)]
    [InlineData("user.name+tag@sub.domain.co.uk", true)]
    [InlineData("student123@campus.edu", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-an-email", false)]
    [InlineData("@gmail.com", false)]
    [InlineData("user@", false)]
    [InlineData("user@domain", false)]
    [InlineData("user@domain.c", false)]
    [InlineData("user with spaces@gmail.com", false)]
    [InlineData("user%name@gmail.com", false)]
    [InlineData("null@example.com", false)]
    [InlineData("user@gstatic.com", false)]
    [InlineData("user@googleapis.com", false)]
    [InlineData("https://www.gstatic.com/test", false)]
    [InlineData("javascript:alert(1)@test.com", false)]
    [InlineData("%.@.null,null,null,\"https://www.gstatic.com/_/mss/boq-identity/_/js/test\"]", false)]
    public void IsValidGoogleEmail_ValidatesStrictly(string? candidate, bool expected)
    {
        bool isValid = BrowserProfileDetector.IsValidGoogleEmail(candidate);
        Assert.Equal(expected, isValid);
    }

    [Fact]
    public void FormatCleanDisplayName_RejectsGarbledRawNamesAndDerivesCleanly()
    {
        // Garbled gstatic bundle / percent strings as raw name
        string garbled1 = "%";
        string garbled2 = "%.@.null";
        string garbled3 = "https://www.gstatic.com/something";

        Assert.Equal("P Venkatasai", BrowserProfileDetector.FormatCleanDisplayName(garbled1, "p.venkatasai07@gmail.com"));
        Assert.Equal("P Venkatasai", BrowserProfileDetector.FormatCleanDisplayName(garbled2, "p.venkatasai07@gmail.com"));
        Assert.Equal("P Venkatasai", BrowserProfileDetector.FormatCleanDisplayName(garbled3, "p.venkatasai07@gmail.com"));

        // User initials
        var user = new AuthenticatedUser
        {
            Email = "p.venkatasai07@gmail.com",
            DisplayName = BrowserProfileDetector.FormatCleanDisplayName(null, "p.venkatasai07@gmail.com")
        };
        Assert.Equal("P Venkatasai", user.DisplayName);
        Assert.Equal("PV", user.GetInitials());

        var userWithGreetingName = new AuthenticatedUser
        {
            Email = "p.venkatasai07@gmail.com",
            DisplayName = "Venkata Sai"
        };
        Assert.Equal("VS", userWithGreetingName.GetInitials());
    }

    [Fact]
    public void DetectLatestGoogleAccount_OnRealMachine_ReturnsRealAccountIfPresent()
    {
        // On any developer or testing machine with Chrome/Edge signed in, this verifies non-throwing operation
        var user = BrowserProfileDetector.DetectLatestGoogleAccount();
        if (user != null)
        {
            Assert.Contains("@", user.Email);
            Assert.False(string.IsNullOrWhiteSpace(user.DisplayName));
            Assert.NotEqual("Google User", user.DisplayName);
            Assert.NotEqual("Google User", user.Email);
        }
    }
}
