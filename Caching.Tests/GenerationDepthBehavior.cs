using System.Collections;
using System.Diagnostics;
using AutoFixture.Kernel;

namespace UiPath.Platform.Caching.Tests;
#nullable disable
// from https://stackoverflow.com/questions/19951272/controlling-the-depth-of-generation-of-an-object-tree-with-autofixture/50118981#50118981
[DebuggerStepThrough]
public class GenerationDepthBehavior : ISpecimenBuilderTransformation
{
    private const int DefaultGenerationDepth = 1;
    private readonly int generationDepth;

    public GenerationDepthBehavior() : this(DefaultGenerationDepth)
    {
    }

    public GenerationDepthBehavior(int generationDepth)
    {
        if (generationDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(generationDepth), "Generation depth must be greater than 0.");

        this.generationDepth = generationDepth;
    }

    public ISpecimenBuilderNode Transform(ISpecimenBuilder builder)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));

        return new GenerationDepthGuard(builder, new GenerationDepthHandler(), generationDepth);
    }
}

public interface IGenerationDepthHandler
{
    object HandleGenerationDepthLimitRequest(object request, IEnumerable<object> recordedRequests, int depth);
}

[DebuggerStepThrough]
public class DepthSeededRequest : SeededRequest
{
    public int Depth { get; }

    public int MaxDepth { get; set; }

    public bool ContinueSeed { get; }

    public int GenerationLevel { get; private set; }

    public DepthSeededRequest(object request, object seed, int depth) : base(request, seed)
    {
        Depth = depth;

        var innerRequest = request as Type;

        if (innerRequest != null)
        {
            var nullable = Nullable.GetUnderlyingType(innerRequest) != null;

            ContinueSeed = nullable || innerRequest.IsGenericType;

            if (ContinueSeed)
            {
                GenerationLevel = GetGenerationLevel(innerRequest);
            }
        }
    }

    private int GetGenerationLevel(Type innerRequest)
    {
        var level = 0;

        if (Nullable.GetUnderlyingType(innerRequest) != null)
        {
            level = 1;
        }

        if (innerRequest.IsGenericType)
        {
            foreach (var generic in innerRequest.GetGenericArguments())
            {
                level++;

                level += GetGenerationLevel(generic);
            }
        }

        return level;
    }
}

[DebuggerStepThrough]
public class GenerationDepthGuard : ISpecimenBuilderNode
{
    private readonly ThreadLocal<Stack<DepthSeededRequest>> requestsByThread
        = new ThreadLocal<Stack<DepthSeededRequest>>(() => new Stack<DepthSeededRequest>());

    private Stack<DepthSeededRequest> GetMonitoredRequestsForCurrentThread() => requestsByThread.Value;

    public GenerationDepthGuard(ISpecimenBuilder builder)
        : this(builder, EqualityComparer<object>.Default)
    {
    }

    public GenerationDepthGuard(
        ISpecimenBuilder builder,
        IGenerationDepthHandler depthHandler)
        : this(
            builder,
            depthHandler,
            EqualityComparer<object>.Default,
            1)
    {
    }

    public GenerationDepthGuard(
        ISpecimenBuilder builder,
        IGenerationDepthHandler depthHandler,
        int generationDepth)
        : this(
            builder,
            depthHandler,
            EqualityComparer<object>.Default,
            generationDepth)
    {
    }

    public GenerationDepthGuard(ISpecimenBuilder builder, IEqualityComparer comparer)
    {
        Builder = builder ?? throw new ArgumentNullException(nameof(builder));
        Comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
        GenerationDepth = 1;
    }

    public GenerationDepthGuard(
        ISpecimenBuilder builder,
        IGenerationDepthHandler depthHandler,
        IEqualityComparer comparer)
        : this(
        builder,
        depthHandler,
        comparer,
        1)
    {
    }

    public GenerationDepthGuard(
        ISpecimenBuilder builder,
        IGenerationDepthHandler depthHandler,
        IEqualityComparer comparer,
        int generationDepth)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (depthHandler == null) throw new ArgumentNullException(nameof(depthHandler));
        if (comparer == null) throw new ArgumentNullException(nameof(comparer));
        if (generationDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(generationDepth), "Generation depth must be greater than 0.");

        Builder = builder;
        GenerationDepthHandler = depthHandler;
        Comparer = comparer;
        GenerationDepth = generationDepth;
    }

    public ISpecimenBuilder Builder { get; }

    public IGenerationDepthHandler GenerationDepthHandler { get; }

    public int GenerationDepth { get; }

    public int CurrentDepth { get; }

    public IEqualityComparer Comparer { get; }

    protected IEnumerable RecordedRequests => GetMonitoredRequestsForCurrentThread();

    public virtual object HandleGenerationDepthLimitRequest(object request, int currentDepth)
    {
        return GenerationDepthHandler.HandleGenerationDepthLimitRequest(
            request,
            GetMonitoredRequestsForCurrentThread(), currentDepth);
    }

    public object Create(object request, ISpecimenContext context)
    {
        if (request is SeededRequest)
        {
            var currentDepth = 0;

            var requestsForCurrentThread = GetMonitoredRequestsForCurrentThread();

            if (requestsForCurrentThread.Count > 0)
            {
                currentDepth = requestsForCurrentThread.Max(x => x.Depth) + 1;
            }

            var depthRequest = new DepthSeededRequest(((SeededRequest)request).Request, ((SeededRequest)request).Seed, currentDepth);

            if (depthRequest.Depth >= GenerationDepth)
            {
                var parentRequest = requestsForCurrentThread.Peek();

                depthRequest.MaxDepth = parentRequest.Depth + parentRequest.GenerationLevel;

                if (!(parentRequest.ContinueSeed && currentDepth < depthRequest.MaxDepth))
                {
                    return HandleGenerationDepthLimitRequest(request, depthRequest.Depth);
                }
            }

            requestsForCurrentThread.Push(depthRequest);
            try
            {
                return Builder.Create(request, context);
            }
            finally
            {
                requestsForCurrentThread.Pop();
            }
        }
        else
        {
            return Builder.Create(request, context);
        }
    }

    public virtual ISpecimenBuilderNode Compose(
        IEnumerable<ISpecimenBuilder> builders)
    {
        var composedBuilder = ComposeIfMultiple(
            builders);
        return new GenerationDepthGuard(
            composedBuilder,
            GenerationDepthHandler,
            Comparer,
            GenerationDepth);
    }

    internal static ISpecimenBuilder ComposeIfMultiple(IEnumerable<ISpecimenBuilder> builders)
    {
        ISpecimenBuilder singleItem = null;
        List<ISpecimenBuilder> multipleItems = null;
        var hasItems = false;

        using (var enumerator = builders.GetEnumerator())
        {
            if (enumerator.MoveNext())
            {
                singleItem = enumerator.Current;
                hasItems = true;

                while (enumerator.MoveNext())
                {
                    if (multipleItems == null)
                    {
                        multipleItems = new List<ISpecimenBuilder> { singleItem };
                    }

                    multipleItems.Add(enumerator.Current);
                }
            }
        }

        if (!hasItems)
        {
            return new CompositeSpecimenBuilder();
        }

        if (multipleItems == null)
        {
            return singleItem;
        }

        return new CompositeSpecimenBuilder(multipleItems);
    }

    public virtual IEnumerator<ISpecimenBuilder> GetEnumerator()
    {
        yield return Builder;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

[DebuggerStepThrough]
public class GenerationDepthHandler : IGenerationDepthHandler
{
    public object HandleGenerationDepthLimitRequest(
        object request,
        IEnumerable<object> recordedRequests, int depth)
    {
        return new OmitSpecimen();
    }
}
