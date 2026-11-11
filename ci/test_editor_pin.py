# Copyright 2026 Haiven
# SPDX-License-Identifier: Apache-2.0
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
            "https://github.com/haiven-dev/haiven-sdk-unity-bossroom/blob/"
            "cadcd898508410f1753d370f9d8f27d8e2a3f9e4/"
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
            self.assertNotIn("blob/main", text)

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

    def test_haiven_paths_are_apache_and_unity_license_stays(self):
        for rel in (
            "Packages/com.haiven.sdk/LICENSE",
            "Packages/com.haiven.demo.bossroom/LICENSE",
            "Assets/HaivenGenerated/LICENSE.txt",
            "ci/LICENSE",
        ):
            text = (ROOT / rel).read_text()
            self.assertIn("Apache License", text, rel)
            self.assertIn("Version 2.0, January 2004", text, rel)
        for name in ("com.haiven.sdk", "com.haiven.demo.bossroom"):
            manifest = json.loads((ROOT / "Packages" / name / "package.json").read_text())
            self.assertEqual(manifest["license"], "Apache-2.0", name)
        for rel in (
            "LICENSE.md",
            "Packages/com.unity.multiplayer.samples.coop/LICENSE.md",
        ):
            text = (ROOT / rel).read_text()
            self.assertIn("Unity Companion License", text, rel)
            self.assertNotIn("Apache License", text, rel)

if __name__ == "__main__":
    unittest.main()
