using System.Runtime.CompilerServices;

namespace Certiflow.SharedKernel;

/// <summary>
/// Terse guards for aggregate and value-object constructors, so an invariant reads as one line
/// instead of five. Every failure surfaces as a <see cref="DomainRuleViolationException"/> with
/// a rule code, because "the domain refused this" and "the framework threw" should never look
/// the same to a caller.
/// </summary>
public static class Guard
{
    public static string AgainstNullOrWhiteSpace(
        string? value,
        string rule,
        [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainRuleViolationException(rule, $"{name} is required.");
        }

        return value.Trim();
    }

    public static T AgainstNull<T>(
        T? value,
        string rule,
        [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class
    {
        return value ?? throw new DomainRuleViolationException(rule, $"{name} is required.");
    }

    public static string AgainstTooLong(
        string value,
        int maxLength,
        string rule,
        [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value.Length > maxLength)
        {
            throw new DomainRuleViolationException(
                rule,
                $"{name} must be {maxLength} characters or fewer, but was {value.Length}.");
        }

        return value;
    }

    public static void Against(bool condition, string rule, string message)
    {
        if (condition)
        {
            throw new DomainRuleViolationException(rule, message);
        }
    }

    public static void Require(bool condition, string rule, string message) =>
        Against(!condition, rule, message);

    public static T AgainstOutOfRange<T>(
        T value,
        T inclusiveMin,
        T inclusiveMax,
        string rule,
        [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(inclusiveMin) < 0 || value.CompareTo(inclusiveMax) > 0)
        {
            throw new DomainRuleViolationException(
                rule,
                $"{name} must be between {inclusiveMin} and {inclusiveMax}, but was {value}.");
        }

        return value;
    }
}
