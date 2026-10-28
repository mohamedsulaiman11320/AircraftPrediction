namespace AFP.Domain;

/// <summary>Raised when a business rule or state transition is violated.</summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
