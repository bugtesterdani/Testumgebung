/** @file @brief Begrenzte Live-Karten je Schritt/Kanal; Änderungen erfolgen über den UI-Dispatcher der Station. */
using System.Collections.ObjectModel;
using Testumgebung.Domain.Definitions;
using Testumgebung.Presentation.Mvvm;
namespace Testumgebung.Presentation.ViewModels;

public sealed class LiveOutputViewModel(string nodeId, string title, string channel) : ObservableObject
{
    public string NodeId { get; } = nodeId;
    public string Channel { get; } = channel;
    public string Title { get; } = title + " · " + channel;
    public string Text { get; private set; } = "";
    public string Status { get; private set; } = "";
    public double? Percent { get; private set; }
    public double Value => Percent ?? 0;
    private bool _complete;
    public bool Indeterminate => Percent is null && !_complete;
    public void Complete() { _complete = true; OnPropertyChanged(nameof(Indeterminate)); }
    public string PercentText => Percent is { } value ? value.ToString("0.#") + " %" : "";
    public void Apply(LiveOutputUpdate update)
    {
        if (update.Reset) { Text = ""; Status = ""; Percent = null; _complete = false; }
        var combined = Text + update.Text;
        Text = combined.Length > 20000 ? combined[^20000..] : combined;
        if (update.Status is not null) Status = update.Status;
        if (update.Percent is not null) Percent = update.Percent;
        OnPropertiesChanged(nameof(Text), nameof(Status), nameof(Percent), nameof(Value), nameof(Indeterminate), nameof(PercentText));
    }
}

/// <summary>Eine Station hält höchstens acht Live-Karten; wiederholte Aufrufe desselben Kanals aktualisieren ihre Karte.</summary>
public sealed class LiveOutputCollection : ObservableObject
{
    public ObservableCollection<LiveOutputViewModel> Items { get; } = [];
    public bool HasContent => Items.Count > 0;
    public void Apply(string nodeId, string title, LiveOutputUpdate update)
    {
        var item = Items.FirstOrDefault(i => i.NodeId == nodeId && i.Channel == update.Channel);
        if (item is null)
        {
            if (Items.Count >= 8) Items.RemoveAt(0);
            item = new(nodeId, title, update.Channel); Items.Add(item);
            OnPropertyChanged(nameof(HasContent));
        }
        item.Apply(update);
    }
    public void Complete(string nodeId)
    {
        foreach (var item in Items.Where(i => i.NodeId == nodeId)) item.Complete();
    }
    public void Clear() { Items.Clear(); OnPropertyChanged(nameof(HasContent)); }
}
