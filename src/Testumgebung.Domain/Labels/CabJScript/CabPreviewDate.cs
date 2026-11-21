using System.Globalization;

namespace Testumgebung.Domain.Labels.CabJScript;

internal sealed partial class CabPreviewContext
{
    private string Date(CabContentField field, int id, IReadOnlyList<string> args)
    {
        var value = DateCore(field, id, args);
        return value.All(char.IsAsciiDigit) ? FillLeadingZeros(value) : value;
    }

    private string DateCore(CabContentField field, int id, IReadOnlyList<string> args)
    {
        var date = settings.ExampleTime;
        if (id <= 9)
        {
            if (args.Count > 0) date = date.AddHours((double)Number(args[0]));
            if (args.Count > 1) date = date.AddMinutes((double)Number(args[1]));
            if (args.Count > 2) date = date.AddSeconds((double)Number(args[2]));
            return Time(date, id);
        }
        if (id == 23) date = date.AddDays((double)Number(args[0]) * 7);
        else date = OffsetDate(field, date, args);
        if (id is >= 30 and <= 37) return Jalali(field, date, id);
        return Gregorian(field, date, id);
    }

    private DateTime OffsetDate(CabContentField field, DateTime date, IReadOnlyList<string> args)
    {
        if (args.Count > 0) date = date.AddDays((double)Number(args[0]));
        if (args.Count > 1)
        {
            Note(field, "Datums-Offsets: Vorschau verwendet die gewählte Monatsregel, anschließend Jahre; Handbuchtext und Abbildung widersprechen sich.");
            date = settings.MonthPolicy == CabPreviewMonthPolicy.ThirtyDays
                ? date.AddDays((double)Number(args[1]) * 30) : date.AddMonths(Integer(args[1]));
        }
        if (args.Count > 2) date = date.AddYears(Integer(args[2]));
        return date;
    }

    private string Time(DateTime date, int id) => id switch
    {
        1 => Invariant((date.Hour + 11) % 12 + 1), 2 => Invariant(date.Hour),
        3 => ((date.Hour + 11) % 12 + 1).ToString("D2", CultureInfo.InvariantCulture),
        4 => date.ToString("HH", CultureInfo.InvariantCulture), 5 => date.ToString("HHmmss", CultureInfo.InvariantCulture),
        6 => date.ToString("mm", CultureInfo.InvariantCulture), 7 => date.ToString("ss", CultureInfo.InvariantCulture),
        8 => date.ToString("T", Culture), 9 => date.Hour < 12 ? "am" : "pm", _ => ""
    };

    private string Gregorian(CabContentField field, DateTime date, int id)
    {
        CalendarAssumptions(field, date, id);
        var culture = (CultureInfo)Culture.Clone();
        // Region controls names and separators; Gregorian fields must not inherit a Persian/Thai calendar.
        if (culture.OptionalCalendars.Any(c => c is GregorianCalendar)) culture.DateTimeFormat.Calendar = new GregorianCalendar();
        if (id <= 16) return BasicDate(date, id, culture);
        if (id <= 20) return Weekday(date, id, culture);
        if (id <= 23) return Week(date).ToString(id == 22 ? "D2" : "D", CultureInfo.InvariantCulture);
        if (id <= 25) return id == 24 ? Prefix(culture.DateTimeFormat.GetMonthName(date.Month), 3) : culture.DateTimeFormat.GetMonthName(date.Month);
        return NumericMonthYear(date, id);
    }

    private void CalendarAssumptions(CabContentField field, DateTime date, int id)
    {
        if (id is 28 or 29 && date.Year is < 1970 or > 2038)
            Note(field, "Beispieljahr liegt außerhalb des für YY/YYYY dokumentierten Bereichs 1970…2038.");
        if (id is 21 or 22 or 23) Note(field, "Kalenderwoche verwendet die ausdrücklich eingestellte Wochenregel.");
    }

    private static string BasicDate(DateTime date, int id, CultureInfo culture) => id switch
    {
        10 => date.ToString("d", culture), 11 => Invariant(date.Day), 12 => date.Day.ToString("D2", CultureInfo.InvariantCulture),
        13 => date.DayOfYear.ToString("D3", CultureInfo.InvariantCulture), 14 => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        15 => Invariant(date.Year) + date.DayOfYear.ToString("D3", CultureInfo.InvariantCulture),
        16 => Invariant((int)date.DayOfWeek), _ => throw new InvalidOperationException()
    };

    private static string Weekday(DateTime date, int id, CultureInfo culture) => id switch
    {
        17 => culture.DateTimeFormat.GetDayName(date.DayOfWeek),
        18 => Prefix(culture.DateTimeFormat.GetDayName(date.DayOfWeek), 2),
        19 => Prefix(culture.DateTimeFormat.GetDayName(date.DayOfWeek), 3),
        20 => Invariant(((int)date.DayOfWeek + 6) % 7 + 1), _ => throw new InvalidOperationException()
    };

    private static string NumericMonthYear(DateTime date, int id) => id switch
    {
        26 => Invariant(date.Month), 27 => date.Month.ToString("D2", CultureInfo.InvariantCulture),
        28 => date.ToString("yy", CultureInfo.InvariantCulture), 29 => Invariant(date.Year),
        38 => Invariant(new ThaiBuddhistCalendar().GetYear(date)), _ => throw new InvalidOperationException()
    };

    private string Jalali(CabContentField field, DateTime date, int id)
    {
        var calendar = new PersianCalendar();
        var month = calendar.GetMonth(date);
        if (id == 37) Note(field, "JWDAY: Vorschau zählt Samstag=0 bis Freitag=6; genaue Zuordnung ist im Handbuch nicht festgelegt.");
        string[] months = ["فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند"];
        return id switch
        {
            30 => Invariant(calendar.GetYear(date)), 31 => Invariant(calendar.GetDayOfMonth(date)),
            32 => calendar.GetDayOfMonth(date).ToString("D2", CultureInfo.InvariantCulture),
            33 => Invariant(month), 34 => month.ToString("D2", CultureInfo.InvariantCulture),
            35 => calendar.GetDayOfYear(date).ToString("D3", CultureInfo.InvariantCulture),
            36 => months[month - 1], 37 => Invariant(((int)date.DayOfWeek + 1) % 7), _ => ""
        };
    }

    private int Week(DateTime date) => new GregorianCalendar().GetWeekOfYear(date, settings.WeekRule, settings.FirstDayOfWeek);
    private static string Prefix(string text, int length) => text[..System.Math.Min(length, text.Length)];
    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
