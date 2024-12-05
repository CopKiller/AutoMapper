namespace AutoMapper.Internal.Mappers;

public sealed class NullableDestinationMapper : IObjectMapper
{
    public bool IsMatch(TypePair context)
    {
        return context.DestinationType.IsNullableType();
    }

    public Expression MapExpression(IGlobalConfiguration configuration, ProfileMap profileMap, MemberMap memberMap,
        Expression sourceExpression, Expression destExpression)
    {
        return configuration.MapExpression(profileMap, GetAssociatedTypes(sourceExpression.Type, destExpression.Type),
            sourceExpression, memberMap);
    }

    public TypePair? GetAssociatedTypes(TypePair initialTypes)
    {
        return GetAssociatedTypes(initialTypes.SourceType, initialTypes.DestinationType);
    }

    private TypePair GetAssociatedTypes(Type sourceType, Type destinationType)
    {
        return new TypePair(sourceType, Nullable.GetUnderlyingType(destinationType));
    }
}