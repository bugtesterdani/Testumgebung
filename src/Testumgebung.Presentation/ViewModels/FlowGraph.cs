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
    public double Width => Junction ? 230 : FlowGraphLayout.CardWidth;
    public double Height { get; internal set; }
}
public sealed record GraphLink(string From, string To, GraphLinkKind Kind, string Label);
public sealed record FlowGraph(IReadOnlyList<GraphCard> Cards, IReadOnlyList<GraphLink> Links, double Width, double Height);

/// <summary>Projiziert die ausführbare Gruppenstruktur in einen Graphen. Verzweigungen und Zusammenführungen bleiben explizit.</summary>
public sealed class FlowGraphLayout(IReadOnlyDictionary<string, double>? measuredHeights = null)
{
    public const double CardWidth = 360;
    private const double Gap = 36, LaneGap = 84;
    private sealed record Fragment(List<GraphCard> Cards, List<GraphLink> Links, string First, string Last, double Width, double Height);
    public FlowGraph Build(IEnumerable<NodeViewModel> roots)
    {
        var all = roots.ToList();
        var scene = Sequence(all.Where(node => node.Note is null).Select(BuildNode).ToList());
        var width = scene.Width; var height = scene.Height;
        // Bibliothekskörper sind Definitionen, keine weiteren Programmschritte.
        foreach (var definition in all.Where(node => node.Note is not null))
        {
            var body = BuildNode(definition);
            var xValue = scene.Cards.Count == 0 ? 0 : width + LaneGap;
            Shift(body, xValue, 0); scene.Cards.AddRange(body.Cards); scene.Links.AddRange(body.Links);
            width = xValue + body.Width; height = Math.Max(height, body.Height);
        }
        return new(scene.Cards, scene.Links, width, height);
    }
    private Fragment BuildNode(NodeViewModel node)
    {
        var card = new GraphCard("node:" + node.Id, node, false, node.Name)
        { Height = CardHeight(node) };
        var result = new Fragment([card], [], card.Id, card.Id, CardWidth, card.Height);
        if (node.Group is not { } group) return result;
        var children = node.Children.Where(node2 => !node2.IsExit && !node2.IsTemplateDecision).ToList();
        var decision = node.Children.FirstOrDefault()?.IsTemplateDecision == true;
        // Empty phases are a single terminal, not an empty group plus a duplicate end marker.
        if (node.IsSection && children.Count == 0 && group.Repetition.Mode == RepeatMode.Once && group.ExitGroup is null) return result;
        if (SimpleDecisionBranch(node) && children.Count > 0) return ConditionalSequence(children);
        if (children.Count > 0)
        {
            var body = group.Parallel || decision
                ? Parallel(children.Select(BuildNode).ToList(), decision) : ConditionalSequence(children);
            result = AttachBody(result, body, group.Parallel ? GraphLinkKind.Parallel : GraphLinkKind.Flow, decision);
        }
        // Dann/Sonst wrappers are branch entries; the enclosing decision already owns their shared join.
        if (SimpleDecisionBranch(node))
            return result;
        result = FinishGroup(result, node, decision);
        if (node.Children.FirstOrDefault(node2 => node2.IsExit) is { } exit)
            result = Append(result, BuildNode(exit), GraphLinkKind.Exit, "Exit / Aufräumen");
        return result;
    }
    private double CardHeight(NodeViewModel node)
    {
        var defaultHeight = node.IsGroup ? 64 : 120;
        return measuredHeights?.GetValueOrDefault(node.Id, defaultHeight) ?? defaultHeight;
    }
    private static bool SimpleDecisionBranch(NodeViewModel node)
        => node.Parent?.Children.FirstOrDefault()?.IsTemplateDecision == true
            && node.Group?.Repetition.Mode == RepeatMode.Once && node.Group.ExitGroup is null;
    private static Fragment AttachBody(Fragment header, Fragment body, GraphLinkKind kind, bool decision)
    {
        var width = Math.Max(header.Width, body.Width);
        Shift(header, (width - header.Width) / 2, 0);
        Shift(body, (width - body.Width) / 2, header.Height + Gap);
        var links = header.Links.Concat(body.Links).ToList();
        // Branch entry links are rooted at an invisible placeholder until the header exists.
        if (decision || kind == GraphLinkKind.Parallel)
        {
            foreach (var link in links.Where(graphLink => graphLink.From == "@split").ToArray())
            { links.Remove(link); links.Add(link with { From = header.Last }); }
        }
        else links.Add(new(header.Last, body.First, kind, ""));
        return new([.. header.Cards, .. body.Cards], links, header.First, body.Last, width, header.Height + Gap + body.Height);
    }
    private static Fragment Parallel(List<Fragment> branches, bool decision)
    {
        var cards = new List<GraphCard>(); var links = new List<GraphLink>(); double xValue = 0;
        for (int index = 0; index < branches.Count; index++)
        {
            var branch = branches[index]; Shift(branch, xValue, 0);
            cards.AddRange(branch.Cards); links.AddRange(branch.Links);
            var kind = decision ? index == 0 ? GraphLinkKind.Then : GraphLinkKind.Else : GraphLinkKind.Parallel;
            links.Add(new("@split", branch.First, kind, decision ? index == 0 ? "Dann" : "Sonst" : "Parallel"));
            xValue += branch.Width + LaneGap;
        }
        return new(cards, links, branches[0].First, "@join", xValue - LaneGap, branches.Max(fragment => fragment.Height));
    }
    private Fragment FinishGroup(Fragment body, NodeViewModel node, bool decision)
    {
        var join = new GraphCard("end:" + node.Id, node, true, node.Group!.Parallel ? "Auf alle Zweige warten" : decision ? "Zweige zusammenführen" : node.Name + " · Ende")
        { X = (body.Width - 230) / 2, Y = body.Height + Gap, Height = measuredHeights?.GetValueOrDefault("end:" + node.Id, 42) ?? 42 };
        var outgoing = body.Cards.Where(graphCard => !body.Links.Any(graphLink => graphLink.From == graphCard.Id && graphLink.Kind != GraphLinkKind.Repeat)).ToList();
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
    /// <summary>Consecutive conditional groups get neighbouring lanes without changing sequential control flow.</summary>
    private Fragment ConditionalSequence(IReadOnlyList<NodeViewModel> children)
    {
        var parts = new List<Fragment>();
        for (var index = 0; index < children.Count;)
        {
            var end = index + 1;
            if (IsConditional(children[index]))
                while (end < children.Count && IsConditional(children[end])) end++;
            var row = children.Skip(index).Take(end - index).Select(BuildNode).ToList();
            parts.Add(row.Count == 1 ? row[0] : ConditionalRow(row));
            index = end;
        }
        return Sequence(parts);
    }
    private static bool IsConditional(NodeViewModel node)
        => node.Group is { } group && !node.IsSection && !string.IsNullOrWhiteSpace(group.EntryCondition);

    private static Fragment ConditionalRow(List<Fragment> groups)
    {
        var cards = new List<GraphCard>(); var links = new List<GraphLink>(); double xValue = 0;
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index]; Shift(group, xValue, 0);
            cards.AddRange(group.Cards); links.AddRange(group.Links);
            // These are ordinary fall-through links, not a parallel split or an inferred if/else.
            if (index > 0) links.Add(new(groups[index - 1].Last, group.First, GraphLinkKind.Flow, "Nächste Bedingung"));
            xValue += group.Width + LaneGap;
        }
        return new(cards, links, groups[0].First, groups[^1].Last, xValue - LaneGap, groups.Max(group => group.Height));
    }

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
    private static void Shift(Fragment fragment, double horizontalOffset, double verticalOffset)
    { foreach (var card in fragment.Cards) { card.X += horizontalOffset; card.Y += verticalOffset; } }
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
