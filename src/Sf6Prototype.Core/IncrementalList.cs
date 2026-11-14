using System.Collections.ObjectModel;

namespace Sf6Prototype;

public static class IncrementalList
{
    // Preserve item identity, selection and WPF containers while new pages arrive.
    public static void Apply<T,TKey>(ObservableCollection<T> target, IReadOnlyList<T> next,
        Func<T,TKey> key, Action<T,T> update) where T : class where TKey : notnull
    {
        var wanted=next.Select(key).ToHashSet();
        var existing=target.ToDictionary(key);
        for(var i=target.Count-1;i>=0;i--) if(!wanted.Contains(key(target[i]))) target.RemoveAt(i);
        for(var i=0;i<next.Count;i++)
        {
            var incoming=next[i];
            if(existing.TryGetValue(key(incoming),out var item))
            {
                update(item,incoming);
                if(i>=target.Count || !ReferenceEquals(target[i],item)) target.Move(target.IndexOf(item),i);
            }
            else target.Insert(i,incoming);
        }
    }
}
