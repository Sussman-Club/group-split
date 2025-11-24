using GroupSplit.Seeder.Seeders.Base;

namespace GroupSplit.Seeder;

public static class SeederOrderingExtensions
{
    public static List<List<IDatabaseSeeder>> TopologicallySort(this IEnumerable<IDatabaseSeeder> seeders)
    {
        var seederList = seeders.ToList();
        var typeLookup = seederList.ToDictionary(s => s.GetType());

        // adjacency list: A -> [B, C] means B depends on A
        var edges = new Dictionary<Type, List<Type>>();
        var indegree = new Dictionary<Type, int>();

        foreach (var type in seederList.Select(seeder => seeder.GetType()))
        {
            edges[type] = [];
            indegree.TryAdd(type, 0);
        }

        // read DependsOn attributes → build graph
        foreach (var seeder in seederList)
        {
            var type = seeder.GetType();
            var deps = type.GetCustomAttributes(typeof(DependsOnAttribute), true)
                .Cast<DependsOnAttribute>()
                .Select(a => a.SeederType);

            foreach (var dep in deps)
            {
                if (!typeLookup.ContainsKey(dep))
                    throw new InvalidOperationException(
                        $"Seeder {type.Name} depends on unregistered seeder: {dep.Name}"
                    );

                edges[dep].Add(type); // dep → type
                indegree[type]++; // type has one more incoming edge
            }
        }

        // Kahn's algorithm: find all nodes with indegree 0
        var layers = new List<List<IDatabaseSeeder>>();

        var queue = new Queue<Type>(
            indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key)
        );

        while (queue.Count != 0)
        {
            var layer = new List<IDatabaseSeeder>();
            var nextQueue = new Queue<Type>();

            while (queue.Count != 0)
            {
                var current = queue.Dequeue();
                layer.Add(typeLookup[current]);

                foreach (var dependent in edges[current])
                {
                    indegree[dependent]--;
                    if (indegree[dependent] == 0)
                        nextQueue.Enqueue(dependent);
                }
            }

            layers.Add(layer);
            queue = nextQueue;
        }

        // Detect cycles
        if (indegree.Any(kv => kv.Value > 0))
        {
            var cycleNodes = indegree.Where(kv => kv.Value > 0)
                .Select(kv => kv.Key.Name);
            throw new InvalidOperationException(
                $"Cyclic dependencies detected among: {string.Join(", ", cycleNodes)}"
            );
        }

        return layers;
    }
}