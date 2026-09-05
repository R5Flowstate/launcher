# Releasing

1. Add the entry for the new version to
   `src/R5Flowstate.Shell/Notes/LAUNCHER_NOTES.json`, newest first, titled
   `Launcher X.Y.Z`.
2. Bump `<Version>` in `src/R5Flowstate.Shell/R5Flowstate.Shell.csproj` to
   match. The release itself takes its version from the tag; this keeps the
   in-app version honest.
3. Read the notes entry back and confirm it is what players should see. This
   step is a human one and stays that way.
4. Tag and push:

```
git tag launcher-vX.Y.Z
git push origin launcher-vX.Y.Z
```

The `release` workflow packs on a Windows runner and publishes the GitHub
Release with the wizard, the Velopack payload, the nupkg, the portable zip,
and the three feed files. The release body is the notes entry.

To pull a bad release: `gh release delete launcher-vX.Y.Z`, then retag from
the previous good commit.
