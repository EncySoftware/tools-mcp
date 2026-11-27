namespace ToolsMcp;

/// <summary>
/// A walk over ENCY's operation tree through its iterator. Each node is read where the iterator stands on it and
/// handed over once the operations under it have been walked, with whether it holds any: a node that does is a
/// container whatever its name says. A group's name ends in "Group", but a part copy (TSTPartOpCopy) holds copies
/// of the part's operations and has no tool of its own, and ENCY's API tells neither apart from an operation.
/// </summary>
internal static class OperationTree
{
    /// <returns>Whether the node the iterator stood on holds any operations.</returns>
    internal static bool Walk<T>(Func<bool> toChild, Func<bool> toSibling, Func<bool> toParent,
        Func<string?, T> read, Func<T, string?, string?> setupOf, Action<T, bool> visited, string? setup = null)
    {
        if (!toChild()) return false;
        do {
            var node = read(setup);
            bool parent = Walk(toChild, toSibling, toParent, read, setupOf, visited, setupOf(node, setup));
            visited(node, parent);
        } while (toSibling());
        if (!toParent()) throw new InvalidOperationException("Operation tree changed during inspection.");
        return true;
    }
}
