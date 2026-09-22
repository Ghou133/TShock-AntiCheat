# Third-party notices

The project-owned source is licensed under GPL-3.0-or-later. The following material has separate provenance and terms.

- `reference/mklp/MKLP/Config.cs` and `reference/mklp/MKLP/Modules/SurvivalManager.cs` are test fixtures from NightKLP/TShock-GSKLP-Moderation, commit `e908905541edfa78fa3500bb9499f08c943c40d5`. They are MIT licensed; the complete notice is in [`reference/mklp/LICENSE`](reference/mklp/LICENSE). The operation inventory and progression candidate data were adapted from the audited source and retain that attribution.
- TShock and TSAPI are external GPL dependencies. The repository does not include their runtime assemblies or copied handler implementations. The fixed revisions and per-file audit provenance are recorded in `reference/source-lock.json`.
- OTAPI and Terraria assemblies, decompiled output, game assets and runtime packages are not distributed here.
- ProgressRestrict and Watcher were read-only audit references. Their implementation code is not included. NoCheat was not used.

Test data and fixtures do not become automatic ban rules merely by appearing in this repository.
