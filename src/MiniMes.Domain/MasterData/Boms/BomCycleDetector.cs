namespace MiniMes.Domain.MasterData.Boms;

// Depth-first search with three colours (spec M1 2.4). A plain "visited" set is not enough:
// in a diamond (PCB-A uses PCBA-SMT and CAP, PCBA-SMT also uses CAP) CAP is reached twice
// without any loop. Only meeting a node that is still on the current path (Gray) is a cycle.
public static class BomCycleDetector
{
    private enum Color { White, Gray, Black } // White = not visited (absent from the dictionary)

    /// <param name="graph">Parent itemId -> component itemIds. Items without their own BOM may be absent.</param>
    /// <returns>The loop as a path that starts and ends on the same item (e.g. 1, 2, 3, 1), or null.</returns>
    public static IReadOnlyList<int>? FindCycle(IReadOnlyDictionary<int, List<int>> graph)
    {
        var color = new Dictionary<int, Color>();
        var path = new List<int>(); // current path, so the error can name the items in the loop

        foreach (var root in graph.Keys)
            if (Visit(root) is { } cycle) return cycle;
        return null;

        // Recursive: real BOMs are a handful of levels deep, far from any stack limit.
        List<int>? Visit(int node)
        {
            switch (color.GetValueOrDefault(node))
            {
                case Color.Black:
                    return null; // whole branch already checked: safe to reach again
                case Color.Gray:
                    var cycle = path[path.IndexOf(node)..];
                    cycle.Add(node);
                    return cycle;
            }

            color[node] = Color.Gray;
            path.Add(node);
            if (graph.TryGetValue(node, out var children))
                foreach (var child in children)
                    if (Visit(child) is { } found) return found;

            path.RemoveAt(path.Count - 1);
            color[node] = Color.Black;
            return null;
        }
    }
}
