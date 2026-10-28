using System.Text.RegularExpressions;

namespace AFP.Application.Common;

/// <summary>Small fluent accumulator for request validation; throws a single <see cref="ValidationException"/>.</summary>
public sealed partial class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.OrdinalIgnoreCase);

    public Validator Required(string field, string? value, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(field, $"{label ?? field} is required.");
        return this;
    }

    public Validator MaxLength(string field, string? value, int max, string? label = null)
    {
        if (value is not null && value.Length > max) Add(field, $"{label ?? field} must be at most {max} characters.");
        return this;
    }

    public Validator Email(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(field, "Email is required.");
        else if (value.Length > 256 || !EmailRegex().IsMatch(value)) Add(field, "Enter a valid email address.");
        return this;
    }

    public Validator Must(bool condition, string field, string message)
    {
        if (!condition) Add(field, message);
        return this;
    }

    public Validator Password(string field, string? password, string? confirmation, string confirmField)
    {
        foreach (var problem in PasswordPolicy.Check(password)) Add(field, problem);
        if (password != confirmation) Add(confirmField, "Passwords do not match.");
        return this;
    }

    public void Add(string field, string message)
    {
        var key = char.ToLowerInvariant(field[0]) + field[1..];
        if (!_errors.TryGetValue(key, out var list)) _errors[key] = list = [];
        list.Add(message);
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
            throw new ValidationException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}

public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    public static IReadOnlyList<string> Check(string? password)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(password))
        {
            problems.Add("Password is required.");
            return problems;
        }
        if (password.Length < MinLength) problems.Add($"Password must be at least {MinLength} characters.");
        if (password.Length > MaxLength) problems.Add($"Password must be at most {MaxLength} characters.");
        if (!password.Any(char.IsUpper)) problems.Add("Password must contain an upper-case letter.");
        if (!password.Any(char.IsLower)) problems.Add("Password must contain a lower-case letter.");
        if (!password.Any(char.IsDigit)) problems.Add("Password must contain a digit.");
        if (password.All(char.IsLetterOrDigit)) problems.Add("Password must contain a symbol.");
        return problems;
    }
}
