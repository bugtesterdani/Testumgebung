/**
 * @file
 * @brief Bindet bearbeitbare Definitionen an Eigenschaftsfelder und übernimmt validierte Änderungen ins Projekt.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public abstract class PropertiesViewModel : ObservableObject
{
    public abstract string Title { get; }
    public virtual NodeOptionsViewModel? Options => null;
}

internal static class Format
{
    public static string Number(double? value, string format = "0.###")
        => value?.ToString(format, CultureInfo.GetCultureInfo("de-DE")) ?? "";
}
