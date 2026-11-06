"""The published Boss Room demo stays on Boss Room v3.0.0's Unity 6.0 pin."""

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class EditorPinTests(unittest.TestCase):
    def test_project_version_is_boss_room_v3(self):
        text = (ROOT / "ProjectSettings/ProjectVersion.txt").read_text()
        self.assertEqual(
            text,
            "m_EditorVersion: 6000.0.52f1\n"
            "m_EditorVersionWithRevision: 6000.0.52f1 (9e4086222921)\n",
        )

    def test_haiven_packages_require_unity_6_0(self):
        for name in ("com.haiven.sdk", "com.haiven.demo.bossroom"):
            manifest = json.loads((ROOT / "Packages" / name / "package.json").read_text())
            self.assertEqual(manifest["unity"], "6000.0", name)

    def test_docs_describe_the_embedded_package(self):
        readme_path = ROOT / "Packages/com.haiven.sdk/README.md"
        package_path = ROOT / "Packages/com.haiven.sdk/package.json"
        menu_path = ROOT / "Packages/com.haiven.sdk/Editor/HaivenSdkMenu.cs"
        readme = readme_path.read_text()
        package_text = package_path.read_text()
        menu = menu_path.read_text()
        docs = (
            "https://github.com/haiven-dev/haiven-sdk-unity-bossroom/blob/main/"
            "Packages/com.haiven.sdk/README.md"
        )
        sdk = json.loads(package_text)
        self.assertEqual(sdk["documentationUrl"], docs)
        self.assertIn(docs, menu)
        self.assertIn("`Packages/com.haiven.sdk`", readme)
        self.assertIn("BuiltInJsonCodec", readme)
        self.assertIn("UnitySocket", readme)
        self.assertIn("already embeds the package", readme)
        self.assertTrue(
            (ROOT / "Packages/com.haiven.sdk/Runtime/Core/Sdk/BuiltInJson.cs").is_file()
        )
        stub_docs = (
            "https://github.com/haiven-dev/haiven-sdk-unity/blob/main/"
            "com.haiven.sdk/README.md"
        )
        stub_git = "https://github.com/haiven-dev/haiven-sdk-unity.git"
        for text in (readme, package_text, menu):
            self.assertNotIn(stub_docs, text)
            self.assertNotIn(stub_git, text)
            self.assertNotIn("sdk-spike", text)
            self.assertNotIn("haiven-sdk.git", text)
            self.assertNotIn("sdks/unity", text)
            self.assertNotIn("?path=", text)

    def test_demo_root_keeps_haiven_and_drops_upstream_tests(self):
        self.assertTrue((ROOT / "Assets").is_dir())
        self.assertTrue((ROOT / "ProjectSettings").is_dir())
        self.assertTrue((ROOT / "Packages/com.haiven.sdk/package.json").is_file())
        self.assertTrue((ROOT / "Packages/com.haiven.demo.bossroom/package.json").is_file())
        self.assertTrue((ROOT / "Assets/HaivenGenerated/Resources/Haiven/BossRoomDemoSettings.json").is_file())
        self.assertFalse((ROOT / "haiven-contracts").exists())
        self.assertFalse((ROOT / "Assets/Tests").exists())
        self.assertTrue((ROOT / "Assets/Tests~").is_dir())
        manifest = json.loads((ROOT / "Packages/manifest.json").read_text())
        self.assertNotIn("com.unity.netcode.gameobjects", manifest.get("testables", []))


if __name__ == "__main__":
    unittest.main()
