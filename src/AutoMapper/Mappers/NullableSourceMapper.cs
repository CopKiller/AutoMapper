namespace AutoMapper.Internal.Mappers;

public sealed class NullableSourceMapper : IObjectMapper
{
    public bool IsMatch(TypePair context)
    {
        return context.SourceType.IsNullableType();
    }

    public Expression MapExpression(IGlobalConfiguration configuration, ProfileMap profileMap, MemberMap memberMap,
        Expression sourceExpression, Expression destExpression)
    {
        return configuration.MapExpression(profileMap, GetAssociatedTypes(sourceExpression.Type, destExpression.Type),
            ExpressionBuilder.Property(sourceExpression, "Value"), memberMap, destExpression);
    }

    public TypePair? GetAssociatedTypes(TypePair initialTypes)
    {
        return GetAssociatedTypes(initialTypes.SourceType, initialTypes.DestinationType);
    }

    private TypePair GetAssociatedTypes(Type sourceType, Type destinationType)
    {
        return new TypePair(Nullable.GetUnderlyingType(sourceType), destinationType);
    }
}