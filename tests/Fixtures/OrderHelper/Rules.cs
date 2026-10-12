namespace OrderHelper;
public static class Rules
{
    public static string Program(string order) => order == "A123" ? "Programm A" : throw new ArgumentException("Auftrag unbekannt: " + order);
    public static double Evaluate(double voltage) => voltage * 2;
}
