/**
 * @file
 * @brief Bearbeitbare Aktivierung und Haltefilter; aktive Haltepunkte bleiben sitzungsbezogen.
 */
using System.Globalization;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.Mvvm;
namespace Testumgebung.Presentation.ViewModels;

public sealed class NodeOptionsViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;
    private readonly NodeViewModel _node;
    public NodeOptionsViewModel(EditorViewModel editor, NodeViewModel node)
    {
        _editor = editor; _node = node;
        Card = new(editor, node, false);
        Code = new(editor, node, true);
    }
    public BreakpointPropertiesViewModel Card { get; }
    public BreakpointPropertiesViewModel Code { get; }
    public bool CanDeactivate => !_node.IsSection;
    public bool HasCode => _node.Step?.BlockType is BlockCatalog.Code or BlockCatalog.LibraryBlock;
    public bool Enabled
    {
        get => !_node.Definition.Disabled;
        set
        {
            if (!CanDeactivate || Enabled == value) return;
            _editor.SetNodeEnabled(_node, value);
            OnPropertyChanged();
        }
    }
    public void Refresh() { OnPropertyChanged(nameof(Enabled)); Card.Refresh(); Code.Refresh(); }
}

public sealed class BreakpointPropertiesViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;
    private readonly NodeViewModel _node;
    private BreakpointMode _mode;
    private string _condition = "", _first = "1", _every = "1", _line = "1";
    private string? _error;
    public BreakpointPropertiesViewModel(EditorViewModel editor, NodeViewModel node, bool code)
    { _editor = editor; _node = node; IsCode = code; Load(); }
    public bool IsCode { get; }
    public string Title => IsCode ? "Codezeilen-Haltepunkt" : "Ablauf-Haltepunkt";
    public bool CanConfigure => _editor.Breakpoints is not null && !_node.IsSection;
    public IReadOnlyList<BreakpointMode> Modes => IsCode ? [BreakpointMode.Always, BreakpointMode.Expression] : Enum.GetValues<BreakpointMode>();
    public bool UsesExpression => Mode == BreakpointMode.Expression;
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    private int LineNumber => int.TryParse(_line, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;
    public string Line
    {
        get => _line;
        set { if (SetProperty(ref _line, value)) { Load(); Refresh(); } }
    }
    public bool Enabled
    {
        get => IsCode ? _editor.Breakpoints?.HasCodeBreakpoint(_node.Id, LineNumber) == true : _editor.Breakpoints?.HasBreakpoint(_node.Id) == true;
        set
        {
            if (!CanConfigure || Enabled == value) return;
            if (value)
            {
                if (!TryRule(out var rule)) { Refresh(); return; }
                if (IsCode) _editor.Breakpoints!.ConfigureCodeBreakpoint(_node.Id, LineNumber, rule!);
                else { _editor.Breakpoints!.SetBreakpoint(_node.Id); _editor.Breakpoints.ConfigureBreakpoint(_node.Id, rule!); }
            }
            else if (IsCode) _editor.Breakpoints!.ClearCodeBreakpoint(_node.Id, LineNumber);
            else _editor.Breakpoints!.ClearBreakpoint(_node.Id);
            _node.HasBreakpoint = _editor.Breakpoints!.HasBreakpoint(_node.Id);
            Refresh();
        }
    }
    public BreakpointMode Mode { get => _mode; set { if (SetProperty(ref _mode, value)) { Apply(); OnPropertyChanged(nameof(UsesExpression)); } } }
    public string Condition { get => _condition; set { if (SetProperty(ref _condition, value)) Apply(); } }
    public string FirstHit { get => _first; set { if (SetProperty(ref _first, value)) Apply(); } }
    public string Every { get => _every; set { if (SetProperty(ref _every, value)) Apply(); } }
    private void Load()
    {
        var rule = IsCode ? _node.Definition.CodeBreakpointRules.GetValueOrDefault(LineNumber, BreakpointRule.Default) : _node.Definition.BreakpointRule;
        _mode = rule.Mode; _condition = rule.Condition ?? "";
        _first = rule.FirstHit.ToString(CultureInfo.InvariantCulture); _every = rule.Every.ToString(CultureInfo.InvariantCulture);
        Error = IsCode && LineNumber == 0 ? "Quelltextzeile muss mindestens 1 sein." : null;
    }
    private bool TryRule(out BreakpointRule? rule)
    {
        rule = null;
        try
        {
            if (IsCode && LineNumber == 0) throw new ArgumentException("Quelltextzeile muss mindestens 1 sein.");
            if (!long.TryParse(_first, NumberStyles.None, CultureInfo.InvariantCulture, out var first) || !long.TryParse(_every, NumberStyles.None, CultureInfo.InvariantCulture, out var every))
                throw new ArgumentException("Trefferzahlen als positive ganze Zahlen angeben.");
            rule = new(_mode, string.IsNullOrWhiteSpace(_condition) ? null : _condition, first, every);
            rule.Validate(IsCode); Error = null; return true;
        }
        catch (Exception ex) when (ex is ArgumentException or Testumgebung.Domain.Expressions.ExpressionException)
        { Error = ex.Message + " Die letzte gültige Einstellung bleibt bestehen."; return false; }
    }
    private void Apply()
    {
        if (!TryRule(out var rule)) return;
        var previous = IsCode ? _node.Definition.CodeBreakpointRules.GetValueOrDefault(LineNumber, BreakpointRule.Default) : _node.Definition.BreakpointRule;
        if (previous == rule) return;
        if (IsCode)
        {
            if (rule == BreakpointRule.Default) _node.Definition.CodeBreakpointRules.Remove(LineNumber);
            else _node.Definition.CodeBreakpointRules[LineNumber] = rule!;
            if (Enabled) _editor.Breakpoints!.ConfigureCodeBreakpoint(_node.Id, LineNumber, rule!);
        }
        else
        {
            _node.Definition.BreakpointRule = rule!;
            if (Enabled) _editor.Breakpoints!.ConfigureBreakpoint(_node.Id, rule!);
        }
        _editor.MarkChanged();
    }
    public void Refresh() => OnPropertiesChanged(nameof(Enabled), nameof(CanConfigure), nameof(Mode), nameof(Condition), nameof(FirstHit), nameof(Every), nameof(UsesExpression));
}
