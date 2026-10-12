using Testumgebung.Contracts;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;

namespace Testumgebung.Engine.Tests;

public class ScopeAndExpressionTests
{
    private static (Scope Loop, Scope Dut, Scope Inner) Chain()
    {
        var main = new Scope("Hauptablauf", null);
        main.Define("Auftrag.Nennspannung", VarType.Double, 230.0);
        var loop = new Scope("Test Loop", main);
        loop.Define("Spannung", VarType.Double, 230.0);
        loop.Define("Zaehler", VarType.Int, 0);
        var dut = new Scope("Prüfling", loop);
        dut.Define("Spannung", VarType.Double, 249.0);
        dut.Define("Kennung", VarType.String, "SN-10482");
        var inner = new Scope("Messgruppe", dut);
        return (loop, dut, inner);
    }

    [Fact]
    public void E01_Name_sucht_ab_aktueller_Gruppe_aufwaerts_und_lokale_Definition_verdeckt()
    {
        var (loop, dut, inner) = Chain();

        Assert.Equal(249.0, inner.Get("Spannung"));               // verdeckt durch Prüfling
        Assert.Same(dut, inner.Lookup("Spannung").Owner);
        Assert.Equal(230.0, inner.Get("Auftrag.Nennspannung"));   // weiter nach oben
        Assert.Same(loop, inner.Lookup("Zaehler").Owner);
    }

    [Fact]
    public void E01_Punkt_Punkt_beginnt_hoeher_und_sucht_weiter_nach_oben()
    {
        var (loop, dut, inner) = Chain();

        // ../Spannung ab Messgruppe: beginnt bei Prüfling → findet Prüfling.Spannung
        Assert.Same(dut, inner.Lookup("../Spannung").Owner);
        // ../../Spannung beginnt zwei Ebenen höher (Test Loop)
        Assert.Same(loop, inner.Lookup("../../Spannung").Owner);
        // ../Spannung ab Prüfling → Test Loop
        Assert.Same(loop, dut.Lookup("../Spannung").Owner);
        // ../../Auftrag.Nennspannung ab Prüfling: Start beim Hauptablauf
        Assert.Equal(230.0, dut.Get("../../Auftrag.Nennspannung"));
        // Suche läuft nach oben weiter: ../Auftrag.Nennspannung ab Messgruppe
        Assert.Equal(230.0, inner.Get("../Auftrag.Nennspannung"));
    }

    [Fact]
    public void E01_Untergruppe_aendert_aeussere_Variable()
    {
        var (loop, _, inner) = Chain();
        inner.Set("Zaehler", 5);
        Assert.Equal(5L, loop.Get("Zaehler"));
        inner.Set("../../Spannung", 231.5);
        Assert.Equal(231.5, loop.Get("Spannung"));
    }

    [Fact]
    public void Fehlender_Name_erzeugt_Diagnose_statt_Neuanlage()
    {
        var (_, _, inner) = Chain();
        var ex = Assert.Throws<VariableNotFoundException>(() => inner.Set("GibtEsNicht", 1));
        Assert.Contains("GibtEsNicht", ex.Message);
        Assert.False(inner.TryLookup("GibtEsNicht", out _, out _));
        Assert.Throws<VariableNotFoundException>(() => inner.Get("../../../../Spannung"));
    }

    [Fact]
    public void Bausteingrenze_wird_von_der_Namenssuche_nicht_ueberschritten()
    {
        var (_, dut, _) = Chain();
        var block = new Scope("Baustein", dut, isInterfaceBoundary: true);
        block.Define("Eingang", VarType.Double, 1.0);
        var inside = new Scope("Intern", block);

        Assert.Equal(1.0, inside.Get("Eingang"));
        Assert.Throws<VariableNotFoundException>(() => inside.Get("Kennung"));
        Assert.Throws<VariableNotFoundException>(() => block.Get("../Kennung"));
    }

    [Fact]
    public void Typpruefung_beim_Schreiben()
    {
        var (_, dut, _) = Chain();
        Assert.Throws<TypeMismatchException>(() => dut.Set("Spannung", "abc"));
        Assert.Throws<TypeMismatchException>(() => dut.Set("Kennung", 5));
        dut.Set("Spannung", 240); // Int → Double erlaubt
        Assert.Equal(240.0, dut.Get("Spannung"));
        var ex = Assert.Throws<TypeMismatchException>(() => TypedValue.Parse("abc", VarType.Double));
        Assert.Equal("Wert muss Zahl sein", ex.Message);
        Assert.Equal(249.5, TypedValue.Parse("249,5", VarType.Double));
    }

    [Theory]
    [InlineData("1 + 2 * 3 == 7", true)]
    [InlineData("(1 + 2) * 3 == 9", true)]
    [InlineData("Spannung > 220.0 && Spannung < 240", false)]
    [InlineData("Spannung >= 249 || false", true)]
    [InlineData("!(Kennung == \"SN-10482\")", false)]
    [InlineData("../../Spannung == ../../Auftrag.Nennspannung", true)]
    [InlineData("../Spannung == ../../Auftrag.Nennspannung", false)]
    [InlineData("Zaehler % 2 == 0", true)]
    [InlineData("-Zaehler < 1", true)]
    [InlineData("Kennung + \"x\" == \"SN-10482x\"", true)]
    public void Ausdruecke_werden_typisiert_ausgewertet(string source, bool expected)
    {
        var (_, _, inner) = Chain();
        Assert.Equal(expected, Expression.Parse(source).EvaluateBool(inner));
    }

    [Theory]
    [InlineData("Kennung > 3")]
    [InlineData("Spannung && true")]
    [InlineData("Spannung")]
    [InlineData("1 / 0 == 1")]
    public void Typfehler_im_Ausdruck_werden_gemeldet(string source)
    {
        var (_, _, inner) = Chain();
        Assert.Throws<ExpressionException>(() => Expression.Parse(source).EvaluateBool(inner));
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("(a == 1")]
    [InlineData("a == \"offen")]
    [InlineData("a # b")]
    public void Syntaxfehler_werden_beim_Parsen_gemeldet(string source)
        => Assert.Throws<ExpressionException>(() => Expression.Parse(source));

    [Fact]
    public void Kurzschluss_vermeidet_Auswertung_der_rechten_Seite()
    {
        var (_, _, inner) = Chain();
        Assert.False(Expression.Parse("false && GibtEsNicht == 1").EvaluateBool(inner));
        Assert.True(Expression.Parse("true || GibtEsNicht == 1").EvaluateBool(inner));
        Assert.Equal(["a", "../b"], Expression.Parse("a == 1 && ../b == 2 && a > 0").ReferencedNames);
    }
}
