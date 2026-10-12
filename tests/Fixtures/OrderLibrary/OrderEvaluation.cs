namespace OrderLibrary;
public static class OrderEvaluation
{
    public static string Select(string order) => OrderHelper.Rules.Program(order);
    public static double Measure(double voltage) => OrderHelper.Rules.Evaluate(voltage);
}
