using Testumgebung.Domain.Definitions;

namespace Testumgebung.Presentation.ViewModels;

public enum GraphLinkKind { Flow, Then, Else, Parallel, Repeat, Exit }
public sealed class GraphCard(string id, NodeViewModel owner, bool junction, string label)
{
    public string Id { get; } = id;
    public NodeViewModel Owner { get; } = owner;
    public bool Junction { get; } = junction;
    public string Label { get; } = label;
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Width => Junction ? 280 : FlowGraphLayout.CardWidth;
    public double Height { get; internal set; }
}
public sealed record GraphLink(string From, string To, GraphLinkKind Kind, string Label);
public sealed record FlowGraph(IReadOnlyList<GraphCard> Cards, IReadOnlyList<GraphLink> Links, double Width, double Height);

/// <summary>Projiziert die ausführbare Gruppenstruktur in einen Graphen. Verzweigungen und Zusammenführungen bleiben explizit.</summary>
public sealed class FlowGraphLayout(IReadOnlyDictionary<string, double>? measuredHeights = null)
{
    public const double CardWidth = 560;
    private const double Gap = 90, LaneGap = 110;
    private sealed record Fragment(List<GraphCard> Cards, List<GraphLink> Links, string First, string Last, double Width, double Height);
    public FlowGraph Build(IEnumerable<NodeViewModel> roots)
    {
        var all = roots.ToList();
        var scene = Sequence(all.Where(n => n.Note is null).Select(BuildNode).ToList());
        var width = scene.Width; var height = scene.Height;
        // Bibliothekskörper sind Definitionen, keine weiteren Programmschritte.
        foreach (var definition in all.Where(n => n.Note is not null))
        {
            var body = BuildNode(definition);
            var x = scene.Cards.Count == 0 ? 0 : width + LaneGap;
            Shift(body, x, 0); scene.Cards.AddRange(body.Cards); scene.Links.AddRange(body.Links);
            width = x + body.Width; height = Math.Max(height, body.Height);
        }
        return new(scene.Cards, scene.Links, width, height);
    }
    private Fragment BuildNode(NodeViewModel node)
    {
        var card = new GraphCard("node:" + node.Id, node, false, node.Name)
        { Height = measuredHeights?.GetValueOrDefault(node.Id, 220) ?? 220 };
        var result = new Fragment([card], [], card.Id, card.Id, CardWidth, card.Height);
        if (node.Group is not { } group) return result;
        var children = node.Children.Where(c => !c.IsExit && !c.IsTemplateDecision).ToList();
        var decision = node.Children.FirstOrDefault()?.IsTemplateDecision == true;
        var branches = children.Select(BuildNode).ToList();
        if (branches.Count > 0)
        {
            var body = group.Parallel || decision ? Parallel(branches, decision) : Sequence(branches);
            result = AttachBody(result, body, group.Parallel ? GraphLinkKind.Parallel : GraphLinkKind.Flow, decision);
        }
        result = FinishGroup(result, node, decision);
        if (node.Children.FirstOrDefault(c => c.IsExit) is { } exit)
            result = Append(result, BuildNode(exit), GraphLinkKind.Exit, "Exit / Aufräumen");
        return result;
    }
    private static Fragment AttachBody(Fragment header, Fragment body, GraphLinkKind kind, bool decision)
    {
        var width = Math.Max(header.Width, body.Width);
        Shift(header, (width - header.Width) / 2, 0);
        Shift(body, (width - body.Width) / 2, header.Height + Gap);
        var links = header.Links.Concat(body.Links).ToList();
        // Branch entry links are rooted at an invisible placeholder until the header exists.
        if (decision || kind == GraphLinkKind.Parallel)
        {
            foreach (var link in links.Where(l => l.From == "@split").ToArray())
            { links.Remove(link); links.Add(link with { From = header.Last }); }
        }
        else links.Add(new(header.Last, body.First, kind, ""));
        return new([.. header.Cards, .. body.Cards], links, header.First, body.Last, width, header.Height + Gap + body.Height);
    }
    private static Fragment Parallel(List<Fragment> branches, bool decision)
    {
        var cards = new List<GraphCard>(); var links = new List<GraphLink>(); double x = 0;
        for (int i = 0; i < branches.Count; i++)
        {
            var branch = branches[i]; Shift(branch, x, 0);
            cards.AddRange(branch.Cards); links.AddRange(branch.Links);
            var kind = decision ? i == 0 ? GraphLinkKind.Then : GraphLinkKind.Else : GraphLinkKind.Parallel;
            links.Add(new("@split", branch.First, kind, decision ? i == 0 ? "Dann" : "Sonst" : "Parallel"));
            x += branch.Width + LaneGap;
        }
        return new(cards, links, branches[0].First, "@join", x - LaneGap, branches.Max(b => b.Height));
    }
    private Fragment FinishGroup(Fragment body, NodeViewModel node, bool decision)
    {
        var join = new GraphCard("end:" + node.Id, node, true, node.Group!.Parallel ? "Auf alle Zweige warten" : decision ? "Zweige zusammenführen" : node.Name + " · Ende")
        { X = (body.Width - 280) / 2, Y = body.Height + Gap, Height = measuredHeights?.GetValueOrDefault("end:" + node.Id, 90) ?? 90 };
        var outgoing = body.Cards.Where(c => !body.Links.Any(l => l.From == c.Id && l.Kind != GraphLinkKind.Repeat)).ToList();
        foreach (var last in outgoing) body.Links.Add(new(last.Id, join.Id, node.Group!.Parallel ? GraphLinkKind.Parallel : GraphLinkKind.Flow, ""));
        // A conditional group can skip its body; the branch line makes that path visible.
        if (HasBypass(node, decision))
            body.Links.Add(new(body.First, join.Id, GraphLinkKind.Else, node.Group.Repetition.Mode == RepeatMode.While ? "Solange-Bedingung nicht erfüllt" : "Bedingung nicht erfüllt"));
        if (node.Group.Repetition.Mode != RepeatMode.Once)
            body.Links.Add(new(join.Id, body.First, GraphLinkKind.Repeat, node.Annotation));
        body.Cards.Add(join);
        return body with { Last = join.Id, Height = join.Y + join.Height };
    }
    private static bool HasBypass(NodeViewModel node, bool decision)
        => !decision && node.Parent?.Children.FirstOrDefault()?.IsTemplateDecision != true
            && (!string.IsNullOrWhiteSpace(node.Group!.EntryCondition) || node.Group.Repetition.Mode == RepeatMode.While);
    private static Fragment Sequence(List<Fragment> items)
    {
        if (items.Count == 0) return new([], [], "", "", CardWidth, 0);
        var result = items[0];
        foreach (var item in items.Skip(1)) result = Append(result, item, GraphLinkKind.Flow, "");
        return result;
    }
    private static Fragment Append(Fragment first, Fragment next, GraphLinkKind kind, string label)
    {
        var width = Math.Max(first.Width, next.Width);
        Shift(first, (width - first.Width) / 2, 0); Shift(next, (width - next.Width) / 2, first.Height + Gap);
        return new([.. first.Cards, .. next.Cards], [.. first.Links, .. next.Links, new(first.Last, next.First, kind, label)], first.First, next.Last, width, first.Height + Gap + next.Height);
    }
    private static void Shift(Fragment fragment, double x, double y)
    { foreach (var card in fragment.Cards) { card.X += x; card.Y += y; } }
}

/// <summary>Zoom um den Mauszeiger und freies Schieben ohne Änderung des Prüfprogramms.</summary>
public sealed class GraphViewport
{
    public double Zoom { get; private set; } = 1;
    public double X { get; private set; } = 30;
    public double Y { get; private set; } = 30;
    public void Pan(double dx, double dy) { X += dx; Y += dy; }
    public void ZoomAt(double zoom, double screenX, double screenY)
    {
        var next = Math.Clamp(zoom, .15, 2.5);
        X = screenX - (screenX - X) / Zoom * next;
        Y = screenY - (screenY - Y) / Zoom * next; Zoom = next;
    }
    public void Reset() { Zoom = 1; X = Y = 30; }
    public void Fit(double width, double height, double viewportWidth, double viewportHeight)
    {
        Zoom = Math.Clamp(Math.Min((viewportWidth - 60) / Math.Max(1, width), (viewportHeight - 60) / Math.Max(1, height)), .15, 1);
        X = (viewportWidth - width * Zoom) / 2; Y = (viewportHeight - height * Zoom) / 2;
    }
}
