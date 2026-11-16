#!/usr/bin/env python3
"""Build a manual-deployment bundle. Never reads private configuration or deploys."""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import tarfile
import tempfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REQUIRED = {
    "AliceIdentityService.dll", "AliceIdentityService.deps.json",
    "AliceIdentityService.runtimeconfig.json", "wwwroot/admin/index.html",
    "wwwroot/css/account.css", "wwwroot/js/account.js",
    *[f"EmailTemplates/{kind}.{suffix}" for kind in ("EmailVerification", "ResetPassword")
      for suffix in ("Subject.txt", "Body.txt", "Body.html")],
}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_publish(folder):
    files = set()
    for path in folder.rglob("*"):
        relative = path.relative_to(folder).as_posix()
        if path.is_symlink():
            raise ValueError("Published output contains a symlink.")
        if not path.is_file():
            continue
        files.add(relative)
        lowered = relative.lower()
        if (path.name.lower().startswith("appsettings") or path.name.startswith(".env")
                or path.suffix.lower() in {".pfx", ".p12", ".pem", ".key", ".dump", ".backup", ".cs", ".log"}
                or any(part.lower() in {"migrations", "keys", "logs", "node_modules", ".dev"} for part in path.parts)
                or "aliceidentityservice.tests" in lowered or "oidcreviewclient" in lowered):
            raise ValueError("Published output contains configuration, private material or development tooling.")
    if not REQUIRED <= files:
        raise ValueError("Published output is missing required runtime, UI or mail-template files.")
    if "AliceIdentityService" in files or "AliceIdentityService.exe" in files:
        raise ValueError("Publish must use UseAppHost=false for the portable dotnet-DLL deployment.")


def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)


def package(output):
    # A new destination and clean temporary publish prevent stale settings/assets entering a release.
    output.mkdir(parents=True, exist_ok=False)
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip())
    metadata = (ROOT / "AliceIdentityService/Models/ApplicationInfo.cs").read_text()
    match = re.search(r'new\("Alice Identity Service", "([^"]+)", (\d+)\)', metadata)
    if match is None:
        raise ValueError("Cannot read application release metadata.")
    version, step = match.groups()
    with tempfile.TemporaryDirectory(prefix="ais3-release-") as temporary:
        bundle = Path(temporary) / "ais3"
        app = bundle / "app"
        run("npm", "ci", "--prefix", "AliceIdentityService.Admin")
        run("npm", "run", "check", "--prefix", "AliceIdentityService.Admin")
        run("dotnet", "restore", "--locked-mode")
        run("dotnet", "publish", "AliceIdentityService", "--no-restore", "-c", "Release",
            "-p:UseAppHost=false", "-p:UseSharedCompilation=false", "-o", str(app))
        validate_publish(app)
        # Copy an explicit list. Private settings, ignored deploy scripts and test reports are never inputs.
        includes = {
            "AliceIdentityService/appsettings.Production.sample.json": "configuration/appsettings.Production.sample.json",
            "AliceIdentityService/Scripts/CreateSchema.sql": "database/CreateSchema.sql",
            "AliceIdentityService/Scripts/PopulateSchema.sql": "database/PopulateSchema.sql",
            "deploy/ais3.service": "deploy/ais3.service",
            "deploy/production.conf": "deploy/production.conf",
            "deploy/nginx.conf": "deploy/nginx.conf",
            "docs/cutover.md": "docs/cutover.md",
            "docs/deployment.md": "docs/deployment.md",
            "docs/migration.md": "docs/migration.md",
            "docs/database.md": "docs/database.md",
            "docs/reviews/step-15.md": "docs/reviews/step-15.md",
            "docs/reviews/step-13.md": "docs/reviews/step-13.md",
            "docs/reviews/step-14.md": "docs/reviews/step-14.md",
            "LICENSE.txt": "LICENSE.txt",
        }
        for source, destination in includes.items():
            source = ROOT / source
            if source.is_symlink() or not source.is_file():
                raise ValueError("A release input is missing or is a symlink.")
            target = bundle / destination
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
        (bundle / "README.txt").write_text(
            "Start with docs/cutover.md for manual production deployment.\n"
            "app/ is the runtime release; configuration/, database/ and deploy/ are operator inputs.\n"
            "Do not apply CreateSchema.sql to an AIS2 restore. Do not place sample settings in app/.\n"
            "The other guides retain repository-relative examples; run those from the source checkout.\n"
            "Verify SHA256SUMS before installing. This bundle performs no deployment.\n")
        manifest = {
            "version": version, "implementationStep": int(step), "sourceCommit": revision,
            "workingTreeModified": dirty, "createdUtc": datetime.now(timezone.utc).isoformat(),
            "deployment": "Framework-dependent ASP.NET Core 10; run app/AliceIdentityService.dll with dotnet.",
            "productionDeploymentPerformed": False,
            "files": {p.relative_to(bundle).as_posix(): digest(p) for p in sorted(bundle.rglob("*")) if p.is_file()},
        }
        (bundle / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        (bundle / "SHA256SUMS").write_text("".join(
            f"{digest(p)}  {p.relative_to(bundle).as_posix()}\n"
            for p in sorted(bundle.rglob("*")) if p.is_file()))
        archive = output / "ais3.tar.gz"
        with tarfile.open(archive, "w:gz") as tar:
            tar.add(bundle, arcname="ais3")
        (output / "ais3.tar.gz.sha256").write_text(f"{digest(archive)}  {archive.name}\n")
        shutil.copyfile(bundle / "release-manifest.json", output / "release-manifest.json")
    print(f"Manual deployment bundle: {archive}")
    if dirty:
        print("Manifest records an uncommitted working tree; sourceCommit alone does not identify these binaries.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New output directory, normally under artifacts/releases")
    arguments = parser.parse_args()
    package(arguments.output.resolve())
