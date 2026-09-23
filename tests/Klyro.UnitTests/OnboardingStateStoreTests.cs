using System;
using System.IO;
using Klyro.Core.Persistence;
using Xunit;

namespace Klyro.UnitTests;

public class OnboardingStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly OnboardingStateStore _store;

    public OnboardingStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Klyro_Test_Onboarding_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new OnboardingStateStore(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void IsFirstRunCompleted_WhenNoStateFile_ReturnsFalse()
    {
        Assert.False(_store.IsFirstRunCompleted());
    }

    [Fact]
    public void MarkCompleted_PersistsStateAndReturnsTrue()
    {
        _store.MarkCompleted("student@university.edu", "Jane Doe");

        Assert.True(_store.IsFirstRunCompleted());
        var state = _store.LoadState();
        Assert.True(state.IsFirstRunCompleted);
        Assert.Equal("student@university.edu", state.GoogleEmail);
        Assert.Equal("Jane Doe", state.GoogleDisplayName);
        Assert.NotNull(state.CompletedAt);
    }

    [Fact]
    public void MarkCompleted_WithPhotoUrl_PersistsAllProperties()
    {
        _store.MarkCompleted("student@university.edu", "Jane Doe", "https://example.com/photo.jpg");

        Assert.True(_store.IsFirstRunCompleted());
        var state = _store.LoadState();
        Assert.Equal("student@university.edu", state.GoogleEmail);
        Assert.Equal("Jane Doe", state.GoogleDisplayName);
        Assert.Equal("https://example.com/photo.jpg", state.GooglePhotoUrl);
    }

    [Fact]
    public void AuthenticatedUser_GetInitials_ComputesDeterministicInitials()
    {
        var u1 = new Klyro.Core.Authentication.AuthenticatedUser { DisplayName = "P. Venkata Sai" };
        Assert.Equal("PV", u1.GetInitials());

        var u2 = new Klyro.Core.Authentication.AuthenticatedUser { DisplayName = "Jane Doe" };
        Assert.Equal("JD", u2.GetInitials());

        var u3 = new Klyro.Core.Authentication.AuthenticatedUser { DisplayName = "Alex" };
        Assert.Equal("AL", u3.GetInitials());

        var u4 = new Klyro.Core.Authentication.AuthenticatedUser { DisplayName = "", Email = "student@uni.edu" };
        Assert.Equal("ST", u4.GetInitials());
    }

    [Fact]
    public void Reset_ClearsStateAndRevertsToFirstRun()
    {
        _store.MarkCompleted("test@university.edu");
        Assert.True(_store.IsFirstRunCompleted());

        _store.Reset();

        Assert.False(_store.IsFirstRunCompleted());
    }

    [Fact]
    public void SaveAuthenticatedUser_And_GetAuthenticatedUser_PreservesAllFields()
    {
        var user = new Klyro.Core.Authentication.AuthenticatedUser
        {
            Id = "10987654321",
            GoogleSub = "10987654321",
            Email = "venkat@university.edu",
            DisplayName = "P. Venkata Sai",
            GivenName = "Venkata",
            FamilyName = "Sai",
            PhotoUrl = "https://lh3.googleusercontent.com/a/photo123",
            HostedDomain = "university.edu",
            VerifiedEmail = true
        };

        _store.SaveAuthenticatedUser(user);

        var restored = _store.GetAuthenticatedUser();
        Assert.NotNull(restored);
        Assert.Equal("10987654321", restored.GoogleSub);
        Assert.Equal("venkat@university.edu", restored.Email);
        Assert.Equal("P. Venkata Sai", restored.DisplayName);
        Assert.Equal("Venkata", restored.GivenName);
        Assert.Equal("Sai", restored.FamilyName);
        Assert.Equal("https://lh3.googleusercontent.com/a/photo123", restored.PhotoUrl);
        Assert.Equal("university.edu", restored.HostedDomain);
    }

    [Fact]
    public void AccountSwitching_OverwritesPreviousUserInStore()
    {
        var userA = new Klyro.Core.Authentication.AuthenticatedUser
        {
            GoogleSub = "11111",
            Email = "account.a@gmail.com",
            DisplayName = "Account A"
        };
        _store.SaveAuthenticatedUser(userA);
        Assert.Equal("account.a@gmail.com", _store.GetAuthenticatedUser()?.Email);

        var userB = new Klyro.Core.Authentication.AuthenticatedUser
        {
            GoogleSub = "22222",
            Email = "account.b@gmail.com",
            DisplayName = "Account B"
        };
        _store.SaveAuthenticatedUser(userB);

        var current = _store.GetAuthenticatedUser();
        Assert.NotNull(current);
        Assert.Equal("22222", current.GoogleSub);
        Assert.Equal("account.b@gmail.com", current.Email);
        Assert.Equal("Account B", current.DisplayName);
    }
}
