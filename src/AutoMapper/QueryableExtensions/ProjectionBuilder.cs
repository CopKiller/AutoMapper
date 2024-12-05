using System.Runtime.CompilerServices;

namespace AutoMapper.QueryableExtensions.Impl;

using ParameterBag = IDictionary<string, object>;
using TypePairCount = Dictionary<ProjectionRequest, int>;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IProjectionBuilder
{
    QueryExpressions GetProjection(Type sourceType, Type destinationType, object parameters,
        MemberPath[] membersToExpand);

    QueryExpressions CreateProjection(in ProjectionRequest request, LetPropertyMaps letPropertyMaps);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IProjectionMapper
{
    bool IsMatch(TypePair context);

    Expression Project(IGlobalConfiguration configuration, in ProjectionRequest request, Expression resolvedSource,
        LetPropertyMaps letPropertyMaps);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ProjectionBuilder : IProjectionBuilder
{
    internal static List<IProjectionMapper> DefaultProjectionMappers()
    {
        return
        [
            new AssignableProjectionMapper(), new EnumerableProjectionMapper(), new NullableSourceProjectionMapper(),
            new StringProjectionMapper(), new EnumProjectionMapper()
        ];
    }

    private readonly LockingConcurrentDictionary<ProjectionRequest, QueryExpressions> _projectionCache;
    private readonly IGlobalConfiguration _configuration;
    private readonly IProjectionMapper[] _projectionMappers;

    public ProjectionBuilder(IGlobalConfiguration configuration, IProjectionMapper[] projectionMappers)
    {
        _configuration = configuration;
        _projectionMappers = projectionMappers;
        _projectionCache = new LockingConcurrentDictionary<ProjectionRequest, QueryExpressions>(CreateProjection);
    }

    public QueryExpressions GetProjection(Type sourceType, Type destinationType, object parameters,
        MemberPath[] membersToExpand)
    {
        ProjectionRequest projectionRequest = new(sourceType, destinationType, membersToExpand, []);
        var cachedExpressions = _projectionCache.GetOrAdd(projectionRequest);
        if (parameters == null && !_configuration.EnableNullPropagationForQueryMapping)
        {
            return cachedExpressions;
        }

        return cachedExpressions.Prepare(_configuration.EnableNullPropagationForQueryMapping, parameters);
    }

    private QueryExpressions CreateProjection(ProjectionRequest request)
    {
        var (typeMap, polymorphicMaps) = PolymorphicMaps(request);
        var letPropertyMaps = polymorphicMaps.Length > 0
            ? new LetPropertyMaps(_configuration, MemberPath.Empty, [])
            : new FirstPassLetPropertyMaps(_configuration, MemberPath.Empty, []);
        return CreateProjection(request, letPropertyMaps, typeMap, polymorphicMaps);
    }

    private (TypeMap, TypeMap[]) PolymorphicMaps(in ProjectionRequest request)
    {
        var typeMap = _configuration.ResolveTypeMap(request.SourceType, request.DestinationType) ??
                      throw TypeMap.MissingMapException(request.SourceType, request.DestinationType);
        return (typeMap, PolymorphicMaps(typeMap));
    }

    private TypeMap[] PolymorphicMaps(TypeMap typeMap)
    {
        return _configuration.GetIncludedTypeMaps(typeMap.IncludedDerivedTypes
            .Where(tp => tp.SourceType != typeMap.SourceType && !tp.DestinationType.IsAbstract)
            .DistinctBy(tp => tp.SourceType).ToArray());
    }

    public QueryExpressions CreateProjection(in ProjectionRequest request, LetPropertyMaps letPropertyMaps)
    {
        var (typeMap, polymorphicMaps) = PolymorphicMaps(request);
        return CreateProjection(request, letPropertyMaps, typeMap, polymorphicMaps);
    }

    private QueryExpressions CreateProjection(in ProjectionRequest request, LetPropertyMaps letPropertyMaps,
        TypeMap typeMap, TypeMap[] polymorphicMaps)
    {
        var instanceParameter = Parameter(request.SourceType, "dto" + request.SourceType.Name);
        var projection = CreateProjection(request, letPropertyMaps, typeMap, polymorphicMaps, instanceParameter);
        return letPropertyMaps.Count > 0
            ? letPropertyMaps.GetSubQueryExpression(this, projection, typeMap, request, instanceParameter)
            : new QueryExpressions(projection, instanceParameter);
    }

    private Expression CreateProjection(in ProjectionRequest request, LetPropertyMaps letPropertyMaps, TypeMap typeMap,
        TypeMap[] polymorphicMaps, Expression source)
    {
        var destinationType = typeMap.DestinationType;
        var projection = polymorphicMaps.Length > 0 && destinationType.IsAbstract
            ? Default(destinationType)
            : CreateProjectionCore(request, letPropertyMaps, typeMap, source);
        foreach (var derivedMap in polymorphicMaps)
        {
            var sourceType = derivedMap.SourceType;
            var derivedRequest = request.InnerRequest(sourceType, derivedMap.DestinationType);
            var derivedProjection =
                CreateProjectionCore(derivedRequest, letPropertyMaps, derivedMap, TypeAs(source, sourceType));
            projection = Condition(TypeIs(source, sourceType), derivedProjection, projection, projection.Type);
        }

        return projection;

        Expression CreateProjectionCore(ProjectionRequest request, LetPropertyMaps letPropertyMaps, TypeMap typeMap,
            Expression instanceParameter)
        {
            var customProjection = typeMap.CustomMapExpression?.ReplaceParameters(instanceParameter);
            if (customProjection != null)
            {
                return customProjection;
            }

            List<MemberBinding> propertiesProjections = [];
            int depth;
            if (OverMaxDepth())
            {
                if (typeMap.Profile.AllowNullDestinationValues)
                {
                    return null;
                }
            }
            else
            {
                ProjectProperties();
            }

            var constructorExpression = CreateDestination();
            var expression = MemberInit(constructorExpression, propertiesProjections);
            return expression;

            bool OverMaxDepth()
            {
                depth = letPropertyMaps.IncrementDepth(request);
                return typeMap.MaxDepth > 0 && depth >= typeMap.MaxDepth;
            }

            void ProjectProperties()
            {
                foreach (var propertyMap in typeMap.PropertyMaps)
                {
                    if (!propertyMap.CanResolveValue || !propertyMap.CanBeSet ||
                        typeMap.ConstructorParameterMatches(propertyMap.DestinationName))
                    {
                        continue;
                    }

                    var propertyProjection = TryProjectMember(propertyMap);
                    if (propertyProjection != null)
                    {
                        propertiesProjections.Add(Bind(propertyMap.DestinationMember, propertyProjection));
                    }
                }
            }

            Expression TryProjectMember(MemberMap memberMap, Expression defaultSource = null)
            {
                MemberProjection memberProjection = new(memberMap);
                letPropertyMaps.Push(memberProjection);
                var memberExpression = ShouldExpand() ? ProjectMemberCore() : null;
                letPropertyMaps.Pop();
                return memberExpression;

                bool ShouldExpand()
                {
                    return memberMap.ExplicitExpansion != true ||
                           request.ShouldExpand(letPropertyMaps.GetCurrentPath());
                }

                Expression ProjectMemberCore()
                {
                    var memberTypeMap = _configuration.ResolveTypeMap(memberMap.SourceType, memberMap.DestinationType);
                    var resolvedSource = ResolveSource();
                    memberProjection.Expression ??= resolvedSource;
                    var memberRequest = request.InnerRequest(resolvedSource.Type, memberMap.DestinationType);
                    if (memberRequest.AlreadyExists && depth >= _configuration.RecursiveQueriesMaxDepth)
                    {
                        return null;
                    }

                    Expression mappedExpression;
                    if (memberTypeMap != null)
                    {
                        mappedExpression = CreateProjection(memberRequest, letPropertyMaps, memberTypeMap,
                            PolymorphicMaps(memberTypeMap), resolvedSource);
                        if (mappedExpression != null && memberTypeMap.CustomMapExpression == null &&
                            memberMap.AllowsNullDestinationValues &&
                            resolvedSource is not ParameterExpression && !resolvedSource.Type.IsCollection())
                        {
                            // Handles null source property so it will not create an object with possible non-nullable properties which would result in an exception.
                            mappedExpression =
                                resolvedSource.IfNullElse(Default(mappedExpression.Type), mappedExpression);
                        }
                    }
                    else
                    {
                        var projectionMapper = GetProjectionMapper();
                        mappedExpression = projectionMapper.Project(_configuration, memberRequest, resolvedSource,
                            letPropertyMaps);
                    }

                    return mappedExpression == null
                        ? null
                        : memberMap.ApplyTransformers(mappedExpression, _configuration);

                    Expression ResolveSource()
                    {
                        var customSource = memberMap.IncludedMember?.ProjectToCustomSource;
                        var resolvedSource = memberMap switch
                        {
                            { CustomMapExpression: LambdaExpression mapFrom } => MapFromExpression(mapFrom),
                            { SourceMembers.Length: > 0 } => memberMap.ChainSourceMembers(CheckCustomSource()),
                            _ => defaultSource ?? throw CannotMap(memberMap, request.SourceType)
                        };
                        if (NullSubstitute())
                        {
                            return memberMap.NullSubstitute(resolvedSource);
                        }

                        return resolvedSource;

                        Expression MapFromExpression(LambdaExpression mapFrom)
                        {
                            if (memberTypeMap == null || letPropertyMaps.IsDefault || mapFrom.IsMemberPath(out _) ||
                                mapFrom.Body is ParameterExpression)
                            {
                                return mapFrom.ReplaceParameters(CheckCustomSource());
                            }

                            if (customSource == null)
                            {
                                memberProjection.Expression = mapFrom;
                                return letPropertyMaps.GetSubQueryMarker(mapFrom);
                            }

                            var newMapFrom = IncludedMember.Chain(customSource, mapFrom);
                            memberProjection.Expression = newMapFrom;
                            return letPropertyMaps.GetSubQueryMarker(newMapFrom);
                        }

                        bool NullSubstitute()
                        {
                            return memberMap.NullSubstitute != null && resolvedSource is MemberExpression &&
                                   (resolvedSource.Type.IsNullableType() || resolvedSource.Type == typeof(string));
                        }

                        Expression CheckCustomSource()
                        {
                            if (customSource == null)
                            {
                                return instanceParameter;
                            }

                            return customSource.IsMemberPath(out _) || letPropertyMaps.IsDefault
                                ? customSource.ReplaceParameters(instanceParameter)
                                : letPropertyMaps.GetSubQueryMarker(customSource);
                        }
                    }

                    IProjectionMapper GetProjectionMapper()
                    {
                        var context = memberMap.Types();
                        foreach (var mapper in _projectionMappers)
                        {
                            if (mapper.IsMatch(context))
                            {
                                return mapper;
                            }
                        }

                        throw CannotMap(memberMap, resolvedSource.Type);
                    }
                }
            }

            NewExpression CreateDestination()
            {
                return typeMap switch
                {
                    { CustomCtorExpression: LambdaExpression ctorExpression } => (NewExpression)ctorExpression
                        .ReplaceParameters(instanceParameter),
                    { ConstructorMap: { CanResolve: true } constructorMap } =>
                        New(constructorMap.Ctor,
                            constructorMap.CtorParams.Select(map =>
                                TryProjectMember(map, map.DefaultValue(null)) ?? Default(map.DestinationType))),
                    _ => New(typeMap.DestinationType)
                };
            }
        }
    }

    private static AutoMapperMappingException CannotMap(MemberMap memberMap, Type sourceType)
    {
        return new AutoMapperMappingException(
            $"Unable to create a map expression from {memberMap.SourceMember?.DeclaringType?.Name}.{memberMap.SourceMember?.Name} ({sourceType}) to {memberMap.DestinationType.Name}.{memberMap.DestinationName} ({memberMap.DestinationType})",
            null, memberMap);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    private sealed class FirstPassLetPropertyMaps(
        IGlobalConfiguration configuration,
        MemberPath parentPath,
        TypePairCount builtProjections) : LetPropertyMaps(configuration, parentPath, builtProjections)
    {
        private readonly List<SubQueryPath> _savedPaths = [];

        public override Expression GetSubQueryMarker(LambdaExpression letExpression)
        {
            SubQueryPath subQueryPath = new([.._currentPath.Reverse()], letExpression);
            var existingPath = _savedPaths.SingleOrDefault(s => s.IsEquivalentTo(subQueryPath));
            if (existingPath.Marker != null)
            {
                return existingPath.Marker;
            }

            _savedPaths.Add(subQueryPath);
            return subQueryPath.Marker;
        }

        public override int Count => _savedPaths.Count;
        public override bool IsDefault => false;

        public override LetPropertyMaps New()
        {
            return new FirstPassLetPropertyMaps(Configuration, GetCurrentPath(), BuiltProjections);
        }

        public override QueryExpressions GetSubQueryExpression(ProjectionBuilder builder, Expression projection,
            TypeMap typeMap, in ProjectionRequest request, ParameterExpression instanceParameter)
        {
            var letMapInfos = _savedPaths.Select(path =>
                (path.LetExpression,
                    MapFromSource: path.GetSourceExpression(instanceParameter),
                    Property: path.GetPropertyDescription(),
                    path.Marker)).ToArray();
            var properties = letMapInfos.Select(m => m.Property)
                .Concat(GePropertiesVisitor.Retrieve(projection, instanceParameter));
            var letType = ProxyGenerator.GetSimilarType(typeof(object), properties);
            TypeMap letTypeMap;
            lock (Configuration)
            {
                letTypeMap = new TypeMap(request.SourceType, letType, typeMap.Profile, null);
            }

            var secondParameter = Parameter(letType, "dtoLet");
            ReplaceSubQueries();
            var letClause = builder.CreateProjection(request, base.New(), letTypeMap, [], instanceParameter);
            return new QueryExpressions(Lambda(projection, secondParameter), Lambda(letClause, instanceParameter));

            void ReplaceSubQueries()
            {
                foreach (var letMapInfo in letMapInfos)
                {
                    var letProperty = letType.GetProperty(letMapInfo.Property.Name);
                    var letPropertyMap = letTypeMap.FindOrCreatePropertyMapFor(letProperty, letMapInfo.Property.Type);
                    letPropertyMap.MapFrom(Lambda(letMapInfo.LetExpression.ReplaceParameters(letMapInfo.MapFromSource),
                        secondParameter));
                    projection = projection.Replace(letMapInfo.Marker, Property(secondParameter, letProperty));
                }

                projection = new ReplaceMemberAccessesVisitor(instanceParameter, secondParameter).Visit(projection);
            }
        }

        private readonly record struct SubQueryPath(
            MemberProjection[] Members,
            LambdaExpression LetExpression,
            Expression Marker)
        {
            public SubQueryPath(MemberProjection[] members, LambdaExpression letExpression) : this(members,
                letExpression, Default(letExpression.Body.Type))
            {
            }

            public Expression GetSourceExpression(Expression parameter)
            {
                var sourceExpression = parameter;
                for (var index = 0; index < Members.Length - 1; index++)
                {
                    var sourceMember = Members[index].Expression;
                    sourceExpression = sourceMember is LambdaExpression lambda
                        ? lambda.ReplaceParameters(sourceExpression)
                        : sourceMember;
                }

                return sourceExpression;
            }

            public PropertyDescription GetPropertyDescription()
            {
                return new PropertyDescription(
                    "__" + string.Join("#", Members.Select(p => p.MemberMap.DestinationName)),
                    LetExpression.Body.Type);
            }

            internal bool IsEquivalentTo(SubQueryPath other)
            {
                return LetExpression == other.LetExpression && Members.Length == other.Members.Length &&
                       Members.Take(Members.Length - 1)
                           .Zip(other.Members, (left, right) => left.MemberMap == right.MemberMap).All(item => item);
            }
        }

        private sealed class GePropertiesVisitor(Expression target) : ExpressionVisitor
        {
            private readonly Expression _target = target;
            public List<MemberInfo> Members { get; } = [];

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == _target)
                {
                    Members.TryAdd(node.Member);
                }

                return base.VisitMember(node);
            }

            public static IEnumerable<PropertyDescription> Retrieve(Expression expression, Expression target)
            {
                GePropertiesVisitor visitor = new(target);
                visitor.Visit(expression);
                return visitor.Members.Select(member => new PropertyDescription(member.Name, member.GetMemberType()));
            }
        }

        private sealed class ReplaceMemberAccessesVisitor(Expression oldObject, Expression newObject)
            : ExpressionVisitor
        {
            private readonly Expression _oldObject = oldObject, _newObject = newObject;

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression != _oldObject)
                {
                    return base.VisitMember(node);
                }

                return PropertyOrField(_newObject, node.Member.Name);
            }
        }
    }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public class LetPropertyMaps
{
    private protected readonly Stack<MemberProjection> _currentPath = [];
    private readonly MemberPath _parentPath;

    protected internal LetPropertyMaps(IGlobalConfiguration configuration, MemberPath parentPath,
        TypePairCount builtProjections)
    {
        Configuration = configuration;
        BuiltProjections = builtProjections;
        _parentPath = parentPath;
    }

    protected TypePairCount BuiltProjections { get; }

    public int IncrementDepth(in ProjectionRequest request)
    {
        if (BuiltProjections.TryGetValue(request, out var depth))
        {
            depth++;
        }

        BuiltProjections[request] = depth;
        return depth;
    }

    public virtual Expression GetSubQueryMarker(LambdaExpression letExpression)
    {
        return letExpression.Body;
    }

    public void Push(MemberProjection memberProjection)
    {
        _currentPath.Push(memberProjection);
    }

    public MemberPath GetCurrentPath()
    {
        return _parentPath.Concat(
            _currentPath.Reverse().Select(p => (p.MemberMap as PropertyMap)?.DestinationMember).Where(p => p != null));
    }

    public void Pop()
    {
        _currentPath.Pop();
    }

    public virtual int Count => 0;
    public IGlobalConfiguration Configuration { get; }

    public virtual LetPropertyMaps New()
    {
        return new LetPropertyMaps(Configuration, GetCurrentPath(), BuiltProjections);
    }

    public virtual QueryExpressions GetSubQueryExpression(ProjectionBuilder builder, Expression projection,
        TypeMap typeMap, in ProjectionRequest request, ParameterExpression instanceParameter)
    {
        return default;
    }

    public virtual bool IsDefault => true;
}

[EditorBrowsable(EditorBrowsableState.Never)]
public readonly record struct QueryExpressions(LambdaExpression Projection, LambdaExpression LetClause = null)
{
    public QueryExpressions(Expression projection, ParameterExpression parameter) : this(projection == null
        ? null
        : Lambda(projection, parameter))
    {
    }

    public bool Empty => Projection == null;

    public T Chain<T>(T source, Func<T, LambdaExpression, T> select)
    {
        return LetClause == null ? select(source, Projection) : select(select(source, LetClause), Projection);
    }

    internal QueryExpressions Prepare(bool enableNullPropagationForQueryMapping, object parameters)
    {
        return new QueryExpressions(Prepare(Projection), Prepare(LetClause));

        LambdaExpression Prepare(Expression cachedExpression)
        {
            var result = parameters == null
                ? cachedExpression
                : ParameterVisitor.SetParameters(parameters, cachedExpression);
            return (LambdaExpression)(enableNullPropagationForQueryMapping
                ? NullsafeQueryRewriter.NullCheck(result)
                : result);
        }
    }
}

public sealed record MemberProjection(MemberMap MemberMap)
{
    public Expression Expression { get; set; }
}

internal abstract class ParameterVisitor : ExpressionVisitor
{
    public static Expression SetParameters(object parameters, Expression expression)
    {
        ParameterVisitor visitor = parameters is ParameterBag dictionary
            ? new ConstantVisitor(dictionary)
            : new PropertyVisitor(parameters);
        return visitor.Visit(expression);
    }

    protected abstract Expression GetValue(string name);

    protected override Expression VisitMember(MemberExpression node)
    {
        var member = node.Member;
        if (!member.DeclaringType.Has<CompilerGeneratedAttribute>())
        {
            return base.VisitMember(node);
        }

        var parameterName = member.Name;
        var parameterValue = GetValue(parameterName);
        if (parameterValue == null)
        {
            const string VbPrefix = "$VB$Local_";
            if (!parameterName.StartsWith(VbPrefix, StringComparison.Ordinal) ||
                (parameterValue = GetValue(parameterName[VbPrefix.Length..])) == null)
            {
                return base.VisitMember(node);
            }
        }

        return ToType(parameterValue, member.GetMemberType());
    }

    private sealed class PropertyVisitor(object parameters) : ParameterVisitor
    {
        private readonly object _parameters = parameters;

        protected override Expression GetValue(string name)
        {
            var matchingMember = _parameters.GetType().GetProperty(name);
            return matchingMember != null ? Property(Constant(_parameters), matchingMember) : null;
        }
    }

    private sealed class ConstantVisitor(ParameterBag paramValues) : ParameterVisitor
    {
        private readonly ParameterBag _paramValues = paramValues;

        protected override Expression GetValue(string name)
        {
            return _paramValues.TryGetValue(name, out var parameterValue) ? Constant(parameterValue) : null;
        }
    }
}

[EditorBrowsable(EditorBrowsableState.Never)]
[DebuggerDisplay("{SourceType.Name}, {DestinationType.Name}")]
public readonly record struct ProjectionRequest(
    Type SourceType,
    Type DestinationType,
    MemberPath[] MembersToExpand,
    ICollection<ProjectionRequest> PreviousRequests)
{
    public ProjectionRequest InnerRequest(Type sourceType, Type destinationType)
    {
        var previousRequests = PreviousRequests.ToList();
        previousRequests.TryAdd(this);
        return new ProjectionRequest(sourceType, destinationType, MembersToExpand, previousRequests);
    }

    public bool AlreadyExists => PreviousRequests.Contains(this);

    public bool Equals(ProjectionRequest other)
    {
        return SourceType == other.SourceType && DestinationType == other.DestinationType &&
               MembersToExpand.SequenceEqual(other.MembersToExpand);
    }

    public override int GetHashCode()
    {
        HashCode hashCode = new();
        hashCode.Add(SourceType);
        hashCode.Add(DestinationType);
        foreach (var member in MembersToExpand)
        {
            hashCode.Add(member);
        }

        return hashCode.ToHashCode();
    }

    public bool ShouldExpand(MemberPath currentPath)
    {
        foreach (var memberToExpand in MembersToExpand)
        {
            if (memberToExpand.StartsWith(currentPath))
            {
                return true;
            }
        }

        return false;
    }
}