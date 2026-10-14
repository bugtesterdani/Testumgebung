/**
 * @file
 * @brief Bewahrt importierte Busbeschreibungen im Projekt, sodass Release-Pakete denselben Beschreibungsstand verwenden.
 */

using Testumgebung.Contracts;

namespace Testumgebung.Domain.Definitions;

/// <summary>
/// Importierte Busbeschreibung (DBC, LDF) als Teil des Projekts und damit des Release-Pakets. Der Inhalt bleibt im
/// Originaltext erhalten; die Auswertung erfolgt beim ersten Zugriff.
/// </summary>
public sealed class BusDatabaseDefinition
{
    private BusDatabase? _parsed;
    private string? _error;

    public BusDatabaseDefinition(string name, BusDatabaseFormat format, string content, string? fileName = null)
    {
        Name = name;
        Format = format;
        Content = content;
        FileName = fileName;
    }

    /// <summary>Name, über den Bausteine die Datenbank wählen (z. B. „Antrieb“).</summary>
    public string Name { get; }
    public BusDatabaseFormat Format { get; }
    public string Content { get; }
    public string? FileName { get; }

    /// <summary>Ausgewertete Datenbank; <see cref="BusDatabaseException"/> bei Fehlern im Text.</summary>
    public BusDatabase Parsed
    {
        get
        {
            if (_parsed is not null) return _parsed;
            if (_error is not null) throw new BusDatabaseException(_error);
            try
            {
                return _parsed = BusDatabase.Parse(Content, Format);
            }
            catch (BusDatabaseException ex)
            {
                _error = ex.Message;
                throw;
            }
        }
    }

    public BusDatabaseDefinition WithName(string name) => new(name, Format, Content, FileName);
}
