using System.Collections.Concurrent;

namespace AutoMapper.Internal;

public readonly struct LockingConcurrentDictionary<TKey, TValue>(Func<TKey, TValue> valueFactory, int capacity = 31)
{
    private readonly Func<TKey, Lazy<TValue>> _valueFactory = key => new Lazy<TValue>(() => valueFactory(key));
    private readonly ConcurrentDictionary<TKey, Lazy<TValue>> _dictionary = new(Environment.ProcessorCount, capacity);

    public TValue GetOrAdd(in TKey key)
    {
        return _dictionary.GetOrAdd(key, _valueFactory).Value;
    }

    public bool IsDefault => _dictionary == null;
}