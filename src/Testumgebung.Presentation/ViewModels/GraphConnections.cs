namespace Testumgebung.Presentation.ViewModels;

public enum GraphSocketKind { Next, Inside, Then, Else, Parallel }
public sealed record GraphSocket(NodeViewModel Node, GraphSocketKind Kind);

public sealed partial class EditorViewModel
{
    private bool _graphView;
    /// <summary>Identität der geöffneten Arbeitsfläche; bleibt bei Speichern und Undo unverändert.</summary>
    public Guid GraphWorkspaceId { get; private set; } = Guid.NewGuid();
    /// <summary>Zusätzliche Knotenansicht; die Ansicht verändert die gespeicherte Ablaufstruktur nicht.</summary>
    public bool GraphView { get => _graphView; set { if (SetProperty(ref _graphView, value)) OnPropertyChanged(nameof(Palette)); } }

    /// <summary>Ein Ausgang wird an den Eingang eines vorhandenen Blocks gesteckt. Rückgängig bleibt atomar.</summary>
    public bool ConnectGraph(GraphSocket output, NodeViewModel target)
    {
        if (!TryGraphPosition(output, target, out var parent, out var index)) return false;
        return Move(target, parent!, index);
    }
    public bool CanConnectGraph(GraphSocket output, NodeViewModel target) => TryGraphPosition(output, target, out _, out _);
    private bool TryGraphPosition(GraphSocket socket, NodeViewModel target, out NodeViewModel? parent, out int index)
    {
        parent = null; index = 0;
        if (!ValidGraphEndpoints(socket.Node, target)) return false;
        (parent, index) = socket.Kind switch
        {
            GraphSocketKind.Then or GraphSocketKind.Else => (DecisionBranch(socket), 0),
            GraphSocketKind.Inside or GraphSocketKind.Parallel => (InsideGroup(socket), 0),
            GraphSocketKind.Next => FollowingPosition(socket.Node),
            _ => (null, 0)
        };
        if (parent?.Group is null || !ReferenceEquals(FindNode(parent.Id), parent)) return false;
        if (SameGraphPosition(target, parent, index)) return false;
        return CanMove(target, parent, index);
    }
    private bool ValidGraphEndpoints(NodeViewModel source, NodeViewModel target)
        => ReferenceEquals(FindNode(source.Id), source) && ReferenceEquals(FindNode(target.Id), target)
            && !ReferenceEquals(source, target) && !target.IsTemplateDecision && !IsDecisionBranch(target);
    private static NodeViewModel? DecisionBranch(GraphSocket socket)
    {
        if (socket.Node.Children.FirstOrDefault()?.IsTemplateDecision != true) return null;
        return socket.Node.Children.ElementAtOrDefault(socket.Kind == GraphSocketKind.Then ? 1 : 2);
    }
    private static NodeViewModel? InsideGroup(GraphSocket socket)
    {
        if (socket.Node.Group is not { } group) return null;
        if (socket.Kind == GraphSocketKind.Parallel && !group.Parallel) return null;
        return socket.Node;
    }
    private static (NodeViewModel? Parent, int Index) FollowingPosition(NodeViewModel source)
    {
        if (source.IsExit || IsDecisionBranch(source) || source.Parent?.Group?.Parallel == true) return (null, 0);
        if (source.IsSection) return (source, source.Group!.Children.Count);
        var parent = source.Parent;
        return parent?.Group is { } group ? (parent, group.Children.IndexOf(source.Definition) + 1) : (null, 0);
    }
    private static bool SameGraphPosition(NodeViewModel target, NodeViewModel parent, int index)
    {
        var oldIndex = parent.Group!.Children.IndexOf(target.Definition);
        return ReferenceEquals(target.Parent, parent) && (oldIndex == index || oldIndex + 1 == index);
    }
    private static bool IsDecisionBranch(NodeViewModel node)
        => node.Parent?.Children.FirstOrDefault()?.IsTemplateDecision == true && node.Group is not null;
}
