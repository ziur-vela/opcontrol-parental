import datetime
import hashlib
import json
import os
import pathlib

version = os.environ["RELEASE_VERSION"].lstrip("v")
dist = pathlib.Path("dist")
assets = {}
for path in sorted(dist.iterdir()):
    if path.is_file() and path.name not in ("release-manifest.json", "SHA256SUMS"):
        data = path.read_bytes()
        assets[path.name] = {"sha256": hashlib.sha256(data).hexdigest(), "size": len(data)}

def read(path, fallback):
    p = pathlib.Path(path)
    return p.read_text(encoding="utf-8").strip() if p.exists() else fallback

manifest = {
    "version": version,
    "image": "ghcr.io/ziur-vela/parental-opcontrol",
    "imageDigest": os.environ["WEB_DIGEST"],
    "updaterImage": "ghcr.io/ziur-vela/parental-opcontrol-updater",
    "updaterImageDigest": os.environ["UPDATER_DIGEST"],
    "minimumVersion": os.environ.get("MINIMUM_UPGRADE_VERSION", "0.13.0"),
    "prerelease": "-" in version,
    "publishedUtc": os.environ.get("RELEASE_DATE") or datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "assets": assets,
    "notes": {
        "en": read(f".github/release-notes/v{version}.en.md", "See the release notes on GitHub."),
        "es": read(f".github/release-notes/v{version}.es.md", "Consulta las notas de la versión en GitHub."),
    },
}
(dist / "release-manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
(dist / "SHA256SUMS").write_text("".join(f"{meta['sha256']}  {name}\n" for name, meta in assets.items()), encoding="utf-8")
