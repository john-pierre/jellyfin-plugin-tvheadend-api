# AI Implementation Instructions

Follow this execution flow for most tasks.

## 1) Understand the Requested Change

- Identify impacted files and behavior boundaries.
- Confirm whether change affects runtime behavior, script outputs, or docs.

## 2) Implement With Minimal Diff

- Prefer focused edits over refactors.
- Keep existing naming patterns and style.
- Avoid unrelated code movement.

## 3) Update Documentation

When behavior or workflow changes, update:

- `README.md` for user/developer-facing guidance
- `CHANGELOG.md` for user-visible behavior changes
- AI docs when agent workflow or testing rules change

## 4) Validate Locally

Run the minimum relevant checks:

1. `dotnet restore Jellyfin.Plugin.TvHeadendApi.sln`
2. `dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore`
3. PowerShell parser check for modified `.ps1` files
4. Optional smoke test relevant to the changed behavior.

## 5) Verify Script Outputs

If script behavior changed:

- output paths and filenames are deterministic
- output remains consistent with documented behavior

## 6) Prepare Review Notes

Summarize:

- what changed
- where changed
- which checks ran
- known limitations or assumptions

