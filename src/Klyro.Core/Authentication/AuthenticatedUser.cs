using System;

namespace Klyro.Core.Authentication;

/// <summary>
/// Safe representation of an authenticated user profile for presentation and session tracking.
/// </summary>
public sealed class AuthenticatedUser
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public DateTime AuthenticatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Computes deterministic uppercase initials from the display name or email.
    /// Example: "P. Venkata Sai" -> "PV", "Jane Doe" -> "JD", "alex@gmail.com" -> "AL".
    /// </summary>
    public string GetInitials()
    {
        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            var parts = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                var clean = CleanWord(parts[0]);
                return clean.Length >= 2 ? clean[..2].ToUpperInvariant() : clean.ToUpperInvariant();
            }
            if (parts.Length >= 2)
            {
                var first = CleanWord(parts[0]);
                var second = CleanWord(parts[1]);
                var last = CleanWord(parts[^1]);

                // If first part is a single letter initial (e.g. "P." in "P. Venkata Sai"), combine with second part
                if (first.Length == 1 && second.Length >= 1)
                {
                    return $"{char.ToUpperInvariant(first[0])}{char.ToUpperInvariant(second[0])}";
                }

                char c1 = first.Length > 0 ? char.ToUpperInvariant(first[0]) : 'U';
                char c2 = last.Length > 0 ? char.ToUpperInvariant(last[0]) : 'S';
                return $"{c1}{c2}";
            }
        }

        if (!string.IsNullOrWhiteSpace(Email))
        {
            var userPart = Email.Split('@')[0];
            var clean = CleanWord(userPart);
            return clean.Length >= 2 ? clean[..2].ToUpperInvariant() : "GU";
        }

        return "GU";
    }

    private static string CleanWord(string input)
    {
        var chars = input.ToCharArray();
        var letters = Array.FindAll(chars, char.IsLetterOrDigit);
        return new string(letters);
    }
}
