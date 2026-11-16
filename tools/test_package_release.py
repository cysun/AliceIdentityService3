"""Verify the release boundary rejects files that must never be distributed."""
import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("package_release", Path(__file__).with_name("package-release.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class PublishBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for name in release.REQUIRED:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture")

    def test_required_files_and_portable_launch(self):
        release.validate_publish(self.root)
        (self.root / "AliceIdentityService.dll").unlink()
        with self.assertRaises(ValueError):
            release.validate_publish(self.root)

    def test_private_material_and_native_apphost_are_rejected(self):
        for name in ("appsettings.json", "appsettings.Production.json", "private.pfx", "keys/key.xml",
                     "Migrations/Initial.cs", ".env", "source.dump", "AliceIdentityService.Tests.dll", "AliceIdentityService"):
            with self.subTest(name=name):
                path = self.root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("must not ship")
                try:
                    with self.assertRaises(ValueError):
                        release.validate_publish(self.root)
                finally:
                    path.unlink()

    def test_symlink_cannot_smuggle_configuration(self):
        (self.root / "linked.txt").symlink_to(self.root / "AliceIdentityService.dll")
        with self.assertRaises(ValueError):
            release.validate_publish(self.root)


if __name__ == "__main__":
    unittest.main()
