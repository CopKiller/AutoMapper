namespace AutoMapper;

/// <summary>
/// Defines a naming convention strategy
/// </summary>
public interface INamingConvention
{
    string[] Split(string input);
    string SeparatorCharacter { get; }
}

public sealed class ExactMatchNamingConvention : INamingConvention
{
    public static readonly ExactMatchNamingConvention Instance = new();

    public string[] Split(string _)
    {
        return [];
    }

    public string SeparatorCharacter => null;
}

public sealed class PascalCaseNamingConvention : INamingConvention
{
    public static readonly PascalCaseNamingConvention Instance = new();
    public string SeparatorCharacter => "";

    public string[] Split(string input)
    {
        List<string> result = null;
        var lower = 0;
        for (var index = 1; index < input.Length; index++)
        {
            if (char.IsUpper(input[index]))
            {
                result ??= [];
                result.Add(input[lower..index]);
                lower = index;
            }
        }

        if (result == null)
        {
            return [];
        }

        result.Add(input[lower..]);
        return [..result];
    }
}

public sealed class LowerUnderscoreNamingConvention : INamingConvention
{
    public static readonly LowerUnderscoreNamingConvention Instance = new();
    public string SeparatorCharacter => "_";

    public string[] Split(string input)
    {
        return input.Split('_', StringSplitOptions.RemoveEmptyEntries);
    }
}