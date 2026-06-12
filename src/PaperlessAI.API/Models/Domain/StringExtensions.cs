namespace PaperlessAI.API.Models.Domain;

internal static class StringExtensions
{
    public static bool IsTrue(this string? value) =>
        value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
}
