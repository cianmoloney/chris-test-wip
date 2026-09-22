using System.Data;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TestFunction.Data;

namespace TestFunction.Services;

public static class AdminSetup
{
    public static async Task RunAsync(AppDbContext database)
    {
        Console.Write("Initial Admin email: ");
        var email = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (email is null || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
            throw new InvalidOperationException("Enter a valid email address.");
        Console.Write("Password (at least 12 characters): ");
        var password = ReadSecret();
        Console.Write("Confirm password: ");
        if (password.Length < 12 || password != ReadSecret()) throw new InvalidOperationException("Passwords must match and contain at least 12 characters.");
        await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            if (await database.Users.AnyAsync()) throw new InvalidOperationException("Initial setup is allowed only when no user accounts exist.");
            var role = await database.Roles.SingleAsync(role => role.Name == "Admin");
            var user = new User { Email = email, RoleId = role.Id };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
            database.Users.Add(user);
            await database.SaveChangesAsync();
            await transaction.CommitAsync();
        });
        Console.WriteLine("Initial Admin created. Optional MFA can be enabled after sign-in.");
    }

    private static string ReadSecret()
    {
        var result = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return result.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (result.Length > 0) result.Length--; }
            else if (!char.IsControl(key.KeyChar)) result.Append(key.KeyChar);
        }
    }
}