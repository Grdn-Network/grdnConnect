# Versioning

GRDN Connect follows [Semantic Versioning 2.0.0](https://semver.org): `MAJOR.MINOR.PATCH`.

- While pre-1.0 (`0.y.z`), behaviour and the HTTP API may change between minor versions.
- Releases are tagged `vX.Y.Z`. The git tag is the source of truth: the release
  workflow stamps it into `Info.json` at build time.
- Older non-SemVer tags (`v0.91.x`) are kept as history; clean SemVer applies from
  `0.92.0` forward.
