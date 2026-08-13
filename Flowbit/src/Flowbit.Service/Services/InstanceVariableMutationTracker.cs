using Flowbit.Service.Abstractions;

namespace Flowbit.Service.Services;

public sealed class InstanceVariableMutationTracker : IInstanceVariableMutationTracker
{
    private readonly object gate = new();
    private readonly Dictionary<long, HashSet<string>> dirtyNames = [];

    public void Record(long instanceId, string variableName)
    {
        if (instanceId <= 0 || string.IsNullOrWhiteSpace(variableName))
        {
            return;
        }

        lock (gate)
        {
            if (!dirtyNames.TryGetValue(instanceId, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dirtyNames.Add(instanceId, names);
            }

            names.Add(variableName);
        }
    }

    public IReadOnlyCollection<string> Consume(long instanceId)
    {
        lock (gate)
        {
            if (!dirtyNames.Remove(instanceId, out var names))
            {
                return [];
            }

            return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public bool HasPending(long instanceId)
    {
        lock (gate)
        {
            return dirtyNames.TryGetValue(instanceId, out var names)
                   && names.Count > 0;
        }
    }

    public void Clear(long instanceId)
    {
        lock (gate)
        {
            dirtyNames.Remove(instanceId);
        }
    }
}
