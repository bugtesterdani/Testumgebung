"""Check that documentation gaps are neither hidden nor counted twice."""
import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("documentation_summary", Path(__file__).parents[1] / "documentation-summary.py")
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class DocumentationSummaryTests(unittest.TestCase):
    def test_descriptions_parameters_duplicates_and_stale_xml(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            xml = output / "xml"
            xml.mkdir()
            (xml / "index.xml").write_text('<doxygenindex><compound refid="current"/><compound refid="duplicate"/></doxygenindex>')
            declaration = '''<memberdef id="method" kind="function" prot="public"><name>Send</name>
<param><declname>data</declname></param><param><declname>token</declname></param>
<briefdescription><para>Send bytes.</para></briefdescription>
<detaileddescription><para><parameterlist kind="param"><parameteritem><parameternamelist><parametername>data</parametername></parameternamelist><parameterdescription><para>Bytes.</para></parameterdescription></parameteritem><parameteritem><parameternamelist><parametername>token</parametername></parameternamelist><parameterdescription/></parameteritem></parameterlist></para></detaileddescription>
<location file="src/Testumgebung.Contracts/Transport.cs" line="8"/></memberdef>'''
            content = '''<doxygen><compounddef kind="interface"><compoundname>Transport</compoundname>
<briefdescription><para>Transport contract.</para></briefdescription>
<location file="src/Testumgebung.Contracts/Transport.cs" line="3"/>
<sectiondef>''' + declaration + '''<memberdef id="missing" kind="variable"><name>State</name><location file="src/Testumgebung.Contracts/Transport.cs" line="9"/></memberdef>
<memberdef id="external" kind="function"><name>External</name><location file="tests/Fake.cs" line="1"/></memberdef></sectiondef></compounddef></doxygen>'''
            (xml / "current.xml").write_text(content)
            (xml / "duplicate.xml").write_text('<doxygen><compounddef kind="file"><sectiondef>' + declaration + '</sectiondef></compounddef></doxygen>')
            (xml / "stale.xml").write_text('<this-is-not-valid-xml')
            with contextlib.redirect_stdout(io.StringIO()):
                report.summarize(output)
            summary = json.loads((output / "Dokumentationsstatus.json").read_text())
            stats = summary["modules"]["Testumgebung.Contracts"]
            self.assertEqual(3, stats["ParsedMembers"])
            self.assertEqual(2, stats["DescribedMembers"])
            self.assertEqual(1, stats["MembersWithMissingParameterDescriptions"])
            self.assertFalse(summary["semanticQualityVerified"])
            self.assertIn("token", (output / "Dokumentationsstatus.csv").read_text(encoding="utf-8-sig"))

    def test_missing_index_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(RuntimeError, "index missing"):
                report.summarize(Path(directory))

    def test_empty_input_fails_instead_of_claiming_documentation(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / "xml").mkdir()
            (output / "xml/index.xml").write_text('<doxygenindex/>')
            with self.assertRaisesRegex(RuntimeError, "No own declarations"):
                report.summarize(output)


if __name__ == "__main__":
    unittest.main()
