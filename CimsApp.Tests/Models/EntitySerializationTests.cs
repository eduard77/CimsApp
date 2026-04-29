using System.Text.Json;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Models;

/// <summary>
/// Pins the [JsonIgnore] contract on secret-bearing entity
/// properties. A real-DB smoke test of POST /api/v1/projects on
/// 2026-04-29 surfaced that User.PasswordHash was leaking via
/// Project.Members[].User in the JSON response — bcrypt hashes
/// were appearing in any project read. The fix was [JsonIgnore]
/// on PasswordHash, RefreshToken.Token, and Invitation.TokenHash;
/// these tests pin the contract so a future "remove the attribute
/// to debug serialization" change is caught immediately.
/// </summary>
public class EntitySerializationTests
{
    [Fact]
    public void User_PasswordHash_does_not_appear_in_serialized_JSON()
    {
        var user = new User
        {
            Email = "u@example.com",
            PasswordHash = "$2a$11$verysecretbcryptdigeststring",
            FirstName = "U", LastName = "T",
        };

        var json = JsonSerializer.Serialize(user);

        Assert.DoesNotContain("PasswordHash", json);
        Assert.DoesNotContain("passwordHash", json);
        Assert.DoesNotContain("$2a$11$", json);
    }

    [Fact]
    public void RefreshToken_Token_does_not_appear_in_serialized_JSON()
    {
        var t = new RefreshToken
        {
            Token = "secret-refresh-token-string-do-not-leak",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        };

        var json = JsonSerializer.Serialize(t);

        // Property name appears in the model but the value must not
        // round-trip through default serialization.
        Assert.DoesNotContain("Token\":", json);
        Assert.DoesNotContain("token\":", json);
        Assert.DoesNotContain("secret-refresh-token-string", json);
    }

    [Fact]
    public void Invitation_TokenHash_does_not_appear_in_serialized_JSON()
    {
        var inv = new Invitation
        {
            OrganisationId = Guid.NewGuid(),
            TokenHash = "F0E1D2C3B4A5968778695A4B3C2D1E0F",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        };

        var json = JsonSerializer.Serialize(inv);

        Assert.DoesNotContain("TokenHash", json);
        Assert.DoesNotContain("tokenHash", json);
        Assert.DoesNotContain("F0E1D2C3B4A5968778695A4B3C2D1E0F", json);
    }
}
