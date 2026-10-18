"""Installable ZIP contract and rejection paths, using only standalone Mod input.

Run: python -m unittest discover -s Tests/Package -v
Requires PowerShell 7.2+ and .NET SDK 8+. Test evidence is retained and its path
is printed; NEKO_MOD_PACKAGE_TEST_OUTPUT can specify a new evidence directory.
"""

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERSION = ET.parse(ROOT / "About/About.xml").findtext("modVersion")
PACKAGE_NAME = f"NekoRimWorld-{VERSION}"
SENTINEL = b"private-test-fixture-must-not-be-distributed"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def copy_mod(destination):
    for relative in ("Source", "Tests/BridgeSmoke", "Tests/fixtures", "Tools", "About", "Languages", "ThirdParty"):
        shutil.copytree(ROOT / relative, destination / relative,
                        ignore=shutil.ignore_patterns("bin", "obj", "__pycache__", ".git"))
    for relative in ("LoadFolders.xml", "LICENSE", "README.zh-CN.md", "README.en.md"):
        shutil.copy2(ROOT / relative, destination / relative)


class PackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which("pwsh") or not shutil.which("dotnet"):
            raise unittest.SkipTest("PowerShell 7.2+ and a .NET SDK are required")
        target = os.environ.get("NEKO_MOD_PACKAGE_TEST_OUTPUT")
        cls.evidence = Path(target).resolve() if target else Path(tempfile.mkdtemp(prefix="neko-mod-package-"))
        if target:
            cls.evidence.mkdir(parents=True, exist_ok=False)
        print(f"Package test evidence: {cls.evidence}", flush=True)
        cls.invocations = 0
        cls.source = cls.evidence / "standalone mod"
        copy_mod(cls.source)
        # Stale dev binaries, cached generated C# and unrelated files must not
        # enter a package built from the repository's isolated source snapshot.
        for relative in ("1.6/Assemblies/NekoRimWorld.dll", "1.6/Assemblies/Assembly-CSharp.dll",
                         "About/personal-settings.toml", "Languages/English/private.txt", "bench/private.rws"):
            path = cls.source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(SENTINEL)
        invalid = cls.source / "Source/obj/OldGeneratedFile.cs"
        invalid.parent.mkdir(parents=True)
        invalid.write_text("This is deliberately invalid C# and must be excluded.", encoding="utf-8")
        code, output = cls.run_script(cls.source, cls.evidence / "valid package")
        if code:
            raise AssertionError(f"Standalone package failed: {output}")
        cls.bundle = cls.evidence / "valid package" / PACKAGE_NAME
        cls.archive = cls.bundle / (PACKAGE_NAME + ".zip")

    @classmethod
    def run_script(cls, source, output):
        command = ["pwsh", "-NoProfile", "-File", str(source / "Tools/Build-Package.ps1"),
                   "-OutputDirectory", str(output)]
        result = subprocess.run(command, cwd=source, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=120, check=False)
        cls.invocations += 1
        log = cls.evidence / f"{cls.invocations:02d}-{output.parent.name}-{output.name.replace(' ', '-')}.log"
        with log.open("x", encoding="utf-8") as stream:
            stream.write(result.stdout + result.stderr)
        return result.returncode, result.stdout + result.stderr

    def case(self, name):
        source = self.evidence / name / "mod"
        copy_mod(source)
        return source, self.evidence / name / "output"

    def test_zip_installs_under_one_mod_folder_and_excludes_workspace_data(self):
        with zipfile.ZipFile(self.archive) as archive:
            names = archive.namelist()
            self.assertEqual(len(names), len(set(names)))
            for name in names:
                self.assertTrue(name.startswith("NekoRimWorld/"), name)
                self.assertNotIn("..", Path(name).parts)
                self.assertNotIn("\\", name)
                self.assertNotIn(SENTINEL, archive.read(name))
            self.assertTrue(all(not set(Path(name).parts) & {"Source", "Tests", "bench", ".git", "obj", "bin", "artifacts"} for name in names))
            self.assertFalse(any(name.endswith((".pdb", ".rws", ".cs", ".csproj", ".py")) for name in names))
            recipe = json.loads((ROOT / "Tools/RuntimeDependencies.json").read_text(encoding="utf-8"))
            dlls = {Path(name).name for name in names if name.endswith(".dll")}
            self.assertEqual(dlls, {"NekoRimWorld.dll", *(item["assembly"] for item in recipe)})
            self.assertTrue(archive.read("NekoRimWorld/1.6/Assemblies/NekoRimWorld.dll").startswith(b"MZ"))
            self.assertFalse(any(Path(name).name in {"0Harmony.dll", "Assembly-CSharp.dll"} for name in names))
            for required in ("About/About.xml", "LoadFolders.xml", "README.zh-CN.md", "README.en.md", "LICENSE",
                             "Languages/English/Keyed/Settings.xml", "Languages/ChineseSimplified/Keyed/Settings.xml"):
                self.assertIn("NekoRimWorld/" + required, names)
            about = ET.fromstring(archive.read("NekoRimWorld/About/About.xml"))
            self.assertEqual(about.findtext("packageId"), "neko.rimworld.bridge")
            self.assertEqual(about.findtext("name"), "N.E.K.O RimWorld Bridge")
            self.assertEqual(about.findtext("supportedVersions/li"), "1.6")
        self.assertEqual((self.source / "1.6/Assemblies/NekoRimWorld.dll").read_bytes(), SENTINEL)
        self.assertFalse((self.bundle / ".work").exists())
        self.assertFalse(list(self.bundle.glob("*.partial")))

    def test_manifest_checks_every_archive_entry_and_zip_checksum(self):
        manifest = json.loads((self.bundle / (PACKAGE_NAME + ".manifest.json")).read_text(encoding="utf-8"))
        self.assertEqual(manifest["archiveSha256"], digest(self.archive))
        self.assertEqual((self.bundle / (PACKAGE_NAME + ".zip.sha256")).read_text().split()[0], digest(self.archive))
        with zipfile.ZipFile(self.archive) as archive:
            self.assertEqual(set(manifest["files"]), set(archive.namelist()))
            for name, expected in manifest["files"].items():
                self.assertEqual(hashlib.sha256(archive.read(name)).hexdigest(), expected, name)
            build = json.loads(archive.read("NekoRimWorld/BUILD.json"))
            self.assertEqual(build["configuration"], "Release")
            self.assertEqual(build["targetFramework"], "net472")
            self.assertTrue(build["smokeTests"]["passed"])
            self.assertIn("Source/NekoRimWorldMod.cs", build["inputSha256"])
            self.assertIn("Tests/BridgeSmoke/Program.cs", build["inputSha256"])
            self.assertEqual(build, manifest["build"])

    def test_every_distributed_dependency_has_its_license_and_locked_version(self):
        locks = json.loads((ROOT / "Source/packages.lock.json").read_text())["dependencies"][".NETFramework,Version=v4.7.2"]
        with zipfile.ZipFile(self.archive) as archive:
            build = json.loads(archive.read("NekoRimWorld/BUILD.json"))
            for item in build["dependencies"]:
                self.assertEqual(item["version"], locks[item["package"]]["resolved"])
                prefix = "NekoRimWorld/Licenses/" + item["package"] + "/"
                licenses = [name for name in archive.namelist() if name.startswith(prefix) and Path(name).name.upper().startswith("LICENSE.")]
                self.assertTrue(licenses, item["package"])
                for license_file in licenses:
                    self.assertIn(b"Permission is hereby granted", archive.read(license_file))
            notes = archive.read("NekoRimWorld/THIRD-PARTY-NOTICES.md").decode()
            self.assertIn("c2583a5060200842a1ed0592604af0967d1a671a", notes)

    def test_existing_output_is_rejected_without_changing_any_file(self):
        before = {str(p.relative_to(self.bundle)): digest(p) for p in self.bundle.rglob("*") if p.is_file()}
        code, _ = self.run_script(self.source, self.bundle.parent)
        self.assertNotEqual(code, 0)
        after = {str(p.relative_to(self.bundle)): digest(p) for p in self.bundle.rglob("*") if p.is_file()}
        self.assertEqual(before, after)

    def test_mismatched_translation_parameters_are_rejected_before_build(self):
        source, output = self.case("invalid-translation")
        path = source / "Languages/ChineseSimplified/Keyed/Settings.xml"
        path.write_text(path.read_text(encoding="utf-8").replace("127.0.0.1:{0}", "127.0.0.1:{1}"), encoding="utf-8")
        code, message = self.run_script(source, output)
        self.assertNotEqual(code, 0)
        self.assertIn("Translation parameters differ", message)
        self.assertFalse(output.exists())

    def test_harmony_without_a_download_link_is_rejected(self):
        source, output = self.case("missing-dependency-link")
        path = source / "About/About.xml"
        tree = ET.parse(path)
        dependency = tree.find("modDependencies/li")
        dependency.remove(dependency.find("downloadUrl"))
        dependency.remove(dependency.find("steamWorkshopUrl"))
        tree.write(path, encoding="utf-8", xml_declaration=True)
        code, message = self.run_script(source, output)
        self.assertNotEqual(code, 0)
        self.assertIn("Harmony requires", message)
        self.assertFalse(output.exists())

    def test_missing_upstream_license_is_rejected_before_build(self):
        source, output = self.case("missing-license")
        (source / "ThirdParty/GraphQL-Parser/LICENSE.md").unlink()
        code, message = self.run_script(source, output)
        self.assertNotEqual(code, 0)
        self.assertIn("Missing pinned upstream license", message)
        self.assertFalse(output.exists())

    def test_unreviewed_dependency_list_cannot_silently_drop_a_runtime_dll(self):
        source, output = self.case("missing-runtime-dll")
        path = source / "Tools/RuntimeDependencies.json"
        recipe = json.loads(path.read_text())
        path.write_text(json.dumps([item for item in recipe if item["assembly"] != "GraphQL.dll"]), encoding="utf-8")
        code, message = self.run_script(source, output)
        self.assertNotEqual(code, 0)
        self.assertIn("Runtime DLL list differs", message)
        self.assertFalse(list(output.rglob("*.zip")))
        self.assertTrue((output / PACKAGE_NAME / "build.log").is_file())

    def test_installation_guides_use_current_game_labels(self):
        english = ET.parse(ROOT / "Languages/English/Keyed/Settings.xml").getroot()
        chinese = ET.parse(ROOT / "Languages/ChineseSimplified/Keyed/Settings.xml").getroot()
        self.assertEqual({node.tag for node in english}, {node.tag for node in chinese})
        for language, filename in ((english, "README.en.md"), (chinese, "README.zh-CN.md")):
            guide = (ROOT / filename).read_text(encoding="utf-8")
            for key in ("NekoRimWorld_EnableConnection", "NekoRimWorld_GeneratePairingCode", "NekoRimWorld_Port", "NekoRimWorld_PairingCode"):
                self.assertIn(language.findtext(key), guide)
            for node in language:
                self.assertTrue(node.text and node.text.strip())
                self.assertNotRegex(node.text, r"<size|NekoRimWorld_")


if __name__ == "__main__":
    unittest.main()
