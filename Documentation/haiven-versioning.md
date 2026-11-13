# Haiven versioning

This page describes how this demo, haiven-sdk-unity-bossroom, gets its version number, and how that version relates to the Unity SDK, [haiven-sdk-unity](https://github.com/haiven-dev/haiven-sdk-unity).

## What the release tooling does today

Releases are cut by release-please, configured in `release-please-config.json` and `.release-please-manifest.json` at the repository root. It computes the demo's version from this repository's own conventional commits only. It does not read the Unity SDK's version or the core SDK's version.

- Below 1.0, a `feat:` commit bumps the patch version (`bump-patch-for-minor-pre-major`), and a breaking change bumps the minor version (`bump-minor-pre-major`). So today a feature that shows part of the platform in Boss Room ships in a patch release.
- From 1.0 on, the default rules apply: a `feat:` commit bumps the minor version and a breaking change bumps the major version.
- Nothing keeps the demo's major.minor in step with the Unity SDK automatically. To release a version that matches an SDK major.minor, a maintainer adds a `Release-As: X.Y.Z` footer to a commit that lands on `main`, the branch release-please releases from.
- Each release stamps the demo's version into two package manifests: `Packages/com.haiven.demo.bossroom/package.json` and the embedded copy of the Unity SDK at `Packages/com.haiven.sdk/package.json`.

## Effect on the Unity SDK

A demo-only release does not change the haiven-sdk-unity repository or its releases. It does change the `version` field of the embedded `Packages/com.haiven.sdk/package.json` in this repository, which then carries the demo's version. That string can differ from the haiven-sdk-unity release the embedded code came from, so do not read it as an SDK release number.

## Proposed policy (not enforced)

The intended scheme, which the tooling above does not enforce, is:

- haiven-sdk-unity follows semver through conformance with the core SDK: a feature ships in the shared major.minor release, in parity with the other Haiven target SDKs, and a patch is a fix that can ship on its own.
- This demo tracks the same major.minor as haiven-sdk-unity but does not follow semver: a Boss Room feature can ship in a patch release.

Until the tooling enforces it, alignment depends on the `Release-As:` footer described above. Once the demo reaches 1.0, a `feat:` commit bumps the minor version unless a `Release-As:` footer overrides it.
