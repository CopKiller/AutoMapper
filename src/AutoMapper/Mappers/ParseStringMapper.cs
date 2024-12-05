namespace AutoMapper.Internal.Mappers;

public sealed class ParseStringMapper : IObjectMapper
{
    public bool IsMatch(TypePair context)
    {
        return context.SourceType == typeof(string) && HasParse(context.DestinationType);
    }

    private static bool HasParse(Type type)
    {
        return type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(DateTimeOffset);
    }

    public Expression MapExpression(IGlobalConfiguration configuration, ProfileMap profileMap, MemberMap memberMap,
        Expression sourceExpression, Expression destExpression)
    {
        return Call(destExpression.Type.GetMethod("Parse", [typeof(string)]), sourceExpression);
    }
}