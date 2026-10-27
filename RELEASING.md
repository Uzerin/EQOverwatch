# Publishing a version

The app checks `https://github.com/Uzerin/EQOverwatch` for the latest **published release** when it opens. If that release's
version is newer than the running one, a bar offers **Download** (opens the release page), **Later**, and **Skip this version**.

To publish v1.0.12 (use your own number):

1. The version in `src/EQCompanion.App/EQCompanion.App.csproj` and `src/EQCompanion.Core/EQCompanion.Core.csproj` is already 1.0.12
   (and the tests that assert it).
2. Commit and push to `main`.
3. Tag it and push the tag: `git tag v1.0.12` then `git push origin v1.0.12`.
4. The **Release** workflow (`.github/workflows/release.yml`) runs the tests, builds `EQOverwatch.exe`, zips it and creates the
   GitHub release. Users see the update bar the next time they open the app.

Draft and pre-release releases are never announced. Delete a release (or its tag) and the app stops announcing it.
