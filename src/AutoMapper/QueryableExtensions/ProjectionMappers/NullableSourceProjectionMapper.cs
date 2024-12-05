namespace AutoMapper.QueryableExtensions.Impl;

public sealed class NullableSourceProjectionMapper : IProjectionMapper
{
    public Expression Project(IGlobalConfiguration configuration, in ProjectionRequest request,
        Expression resolvedSource, LetPropertyMaps letPropertyMaps)
    {
        return Coalesce(resolvedSource, New(request.DestinationType));
    }

    public bool IsMatch(TypePair context)
    {
        return context.DestinationType.IsValueType && !context.DestinationType.IsNullableType() &&
               context.SourceType.IsNullableType();
    }
}