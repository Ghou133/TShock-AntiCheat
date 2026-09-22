# M18 isolated experiment import review

This branch brings the first-party M18 implementation, regression tests, and
supporting NetworkLab/GameplayScaffold source from the Luna isolated experiment
into the curated TShock-AntiCheat source tree. It starts at target `main`
`f58d320fe569ad825d781b5edc2e0ef5df498c33`. The experiment baseline was
`6ad4f92d2341e22d4ef5412052eb526c6a36f160`; the imported work was still
uncommitted there. [M18-IMPORT-SOURCE.json](M18-IMPORT-SOURCE.json) records the
SHA-256 of each source file and its imported counterpart. Of 69 imported source
files, 68 retain the same bytes. The Adapter test project uses the target
repository's relative TShock compatibility source path.

The target repository's published README, license, third-party notices,
`.gitignore`, scope handoff, and `docs/m3-target-ledger.json` are unchanged.
The experiment's `active_v2_development` flag is specific to that isolated
copy; this branch does not change the target project's `paused_by_user` status.
The imported M18 rules remain candidates. Their default configuration is
ObserveOnly, and this import does not grant production qualification.

## Review findings

1. **F06/F08 attribution across login needs a regression and a fix before
   permanent sanction qualification.** Both behavior trackers retain control
   and sequence history when the same session changes from account ID 0 to an
   authenticated ID. `GetState` updates `AccountId` in place in
   `src/AntiCheat.Rules/M18NpcStrikeQueueRules.cs` and
   `src/AntiCheat.Rules/M18GroundItemClearQueueRules.cs`. Earlier unauthenticated
   packet13 declarations may therefore contribute to later account evidence.
   Reset the behavior history at the authentication boundary and test the
   0-to-account transition for both paths.
2. **Previously cancelled control declarations can enter F06/F08 sequence
   evidence.** Both `ObserveControl` methods retain packet13 positions marked
   `AlreadyCancelled`. F06 has a test that intentionally uses a cancelled
   return declaration to complete a target sequence. A safer proof needs to
   distinguish observed client attempts from accepted movement before using
   them for permanent account sanctions. A pre-hook cancellation check alone
   would not establish native acceptance. Keep the resource stop-loss path
   separate while testing this boundary.
3. **F08 legal movement and pickup coverage is incomplete.** The current
   candidate can mark the third distinct packet151 target as `ProvenCheat`
   when `WipeSequenceDetected` is true, even if all three requests align with
   the server position and `SuspiciousClearCount` is zero. The existing unit
   test deliberately asserts that shape. Additional ordinary movement,
   pickup, handoff, and native cancellation controls are needed before that
   predicate can support a production sanction.

These are code-review findings, not claims of a reproduced real-client false
ban. This import keeps the experiment implementation and its existing tests
intact so the candidate can be reviewed without silently changing its proof
contract. It must not be deployed as a newly qualified sanction rule.

## Verification in the target branch

| Check | Result |
| --- | --- |
| `dotnet test tests/AntiCheat.Rules.Tests/AntiCheat.Rules.Tests.csproj -c Release` | 610 passed |
| `dotnet test tests/AntiCheat.Adapter.Tests/AntiCheat.Adapter.Tests.csproj -c Release --filter 'FullyQualifiedName!~M14ResumePhantasm_InvalidIndexActuallyThrowsInNativeAiButBlocksBeforeCreationAndSameAccountRecovers'` | 1335 passed; three parameterized native-fault cases were not executed |
| `dotnet test tests/AntiCheat.Persistence.Tests/AntiCheat.Persistence.Tests.csproj -c Release` | 38 passed |
| `dotnet build tools/AntiCheat.NetworkLab/AntiCheat.NetworkLab.csproj -c Release` | passed; zero warnings |
| `dotnet build tools/AntiCheat.GameplayScaffold/GameplayScaffold.csproj -c Release` | passed; three warnings (one existing nullable warning, two obsolete `Item.NewItem` calls in new fixtures) |

No branch-specific NetworkLab server run or real-client run was performed.
The isolated experiment's historical F06/F08 `Applied=true` and reconnect
refusal evidence belongs to its exact TestLab candidate and runtime identity.
The final R3 Rules/Adapter TRX there recorded 610/610 and 1335/1335; its three
Phantasm native-fault parameter cases were deliberately not executed. The
historical type315 normal-pickup failure was not freshly reproduced by R3.
Those outcomes are retained in the experiment archive and are not transferred
as qualification for this branch's build.

## Material retained only in the isolated experiment

The experiment's `artifacts/`, `.lab/`, `checkpoints/`, `review/`, local
database/world/client files, archives, third-party tool copies, and
environment-specific scripts remain outside this curated source branch.
The separate TerraAngel driver contains a local default test password and a
copied tool operation whose redistribution was not reviewed. The TCP trace
proxy records raw traffic bytes. Neither tool is included here.
