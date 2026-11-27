using ToolsMcp;
using Xunit;

public class OperationTreeTests
{
    private sealed class Node(string name, params Node[] children)
    {
        public string Name { get; } = name;
        public Node[] Children { get; } = children;
        public Node? Parent { get; set; }
    }

    /// <summary>ENCY's operation iterator over a tree: it moves, and answers whether it could.</summary>
    private sealed class Cursor
    {
        private Node _at;
        public Cursor(Node root) { _at = root; Link(root); }
        private static void Link(Node n) { foreach (var c in n.Children) { c.Parent = n; Link(c); } }
        public bool ToChild() { if (_at.Children.Length == 0) return false; _at = _at.Children[0]; return true; }
        public bool ToSibling()
        {
            var siblings = _at.Parent?.Children ?? [];
            int i = Array.IndexOf(siblings, _at);
            if (i < 0 || i + 1 >= siblings.Length) return false;
            _at = siblings[i + 1];
            return true;
        }
        public bool ToParent() { if (_at.Parent == null) return false; _at = _at.Parent; return true; }
        public Node Current => _at;
    }

    private static List<(string Name, bool Parent, string? Setup)> Walk(Node root)
    {
        var cursor = new Cursor(root);
        var seen = new List<(string, bool, string?)>();
        OperationTree.Walk(cursor.ToChild, cursor.ToSibling, cursor.ToParent,
            setup => (cursor.Current.Name, Setup: setup),
            (node, setup) => node.Name.StartsWith("Setup") ? node.Name : setup,
            (node, parent) => seen.Add((node.Name, parent, node.Setup)));
        return seen;
    }

    /// <summary>
    /// A part copy (TSTPartOpCopy) holds copies of the part's operations and has no tool of its own: listed by the
    /// "Group" ending of its name, it came out as an operation with no tool, a row for the plan to fill.
    /// </summary>
    [Fact]
    public void ANodeWithOperationsUnderItIsAContainerWhateverItsName()
    {
        var tree = new Node("root",
            new Node("Drill"),
            new Node("Setup 2", new Node("Rough"), new Node("Finish")),
            new Node("TSTPartOpCopy", new Node("Rough copy")),
            new Node("Chamfer"));

        var seen = Walk(tree);

        Assert.Equal(["Drill", "Rough", "Finish", "Setup 2", "Rough copy", "TSTPartOpCopy", "Chamfer"], seen.Select(s => s.Name));
        Assert.Equal(["Setup 2", "TSTPartOpCopy"], seen.Where(s => s.Parent).Select(s => s.Name));
        Assert.Equal("Setup 2", seen.Single(s => s.Name == "Finish").Setup);
        Assert.Null(seen.Single(s => s.Name == "Rough copy").Setup);
    }

    [Fact]
    public void AnEmptyTreeVisitsNothing() => Assert.Empty(Walk(new Node("root")));

    [Fact]
    public void ATreeThatCannotBeLeftChangedUnderTheWalk()
    {
        var leaf = new Node("Drill");
        var cursor = new Cursor(new Node("root", leaf));
        leaf.Parent = null;   // the iterator cannot climb back: the tree changed under it
        Assert.Throws<InvalidOperationException>(() => OperationTree.Walk(cursor.ToChild, cursor.ToSibling, cursor.ToParent,
            _ => 0, (_, s) => s, (_, _) => { }));
    }
}
