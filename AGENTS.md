# Repository development contract

Applies to human-assisted and AI-assisted changes in this repository.

## Read only what the task needs

Start with `START_HERE.md`, `docs/ROADMAP.md`, `docs/STATUS.md`, then the relevant section of `docs/ARCHITECTURE.md` and its source/tests. Record the task ID and base commit. Do not repeatedly re-audit the entire milestone ledger for a focused change.

## Scope

Target Terraria 1.4.5.8. Keep bounded, low-context anti-cheat and public-asset safety work. OS firewall integration, REST protection, old-version compatibility and PvP-specific work are out of scope. Full damage reconstruction, complete item provenance, generic liquid/wiring simulation and world rollback remain frozen. Existing narrow guards are not permission to expand those models.

Terraria MCP is an independent general-purpose development harness. AntiCheat may consume it; do not move AntiCheat rule logic into it, rebuild it here, or alter the separate project without an explicit task.

## Execution boundaries

Use owned isolated worlds, accounts and runtime copies. State preparation, internal observation and native method calls are allowed in that harness. Tests claiming real packet/Hook/account enforcement must still traverse those actual boundaries. Never replace proof inputs with a test-only success flag. Do not deploy to a real server, alter real worlds/databases, or clean another worktree.

Default `ObserveOnly` must not produce new permanent cheating sanctions. Existing independent safety/resource/maintenance controls are separate and must be described accurately. Do not expand production admission, bypass runtime pins or enable candidate account sanctions to make a test pass. Resource excess is not an account-cheating proof. Repeated Unknown results do not become a proof. Service kicks must not be recorded as permanent bans.

## Change discipline

- Work on a task branch. Do not force-push, rewrite shared history or delete source branches without explicit authorization.
- Separate mechanical moves, behavior fixes, qualification changes and documentation into reviewable commits. Avoid whole-tree formatting.
- New code uses domain names, not new M-number, R-number, round, final, v2 or fix suffixes. Preserve existing serialized keys, public identifiers, rule versions and reflection-facing names during directory moves.
- Prefer extending the relevant module over creating a parallel detector. Keep parsing, contextual observation, pure rules, enforcement and persistence separate.
- Every new state collection needs a size bound, lifecycle/reset rules and expiry/overflow behavior. Associate state with server run, world epoch, connection generation and authenticated identity as appropriate.
- Preserve prior cancellation. Do not reuse a canceled request as an accepted transaction. Validate frame offsets, optional fields and finite numeric inputs before native access.
- Do not delete failing tests, widen exclusions, swallow errors, weaken assertions or add fabricated fixtures to obtain green CI. Platform-only skips require an explicit platform reason.
- Preserve required licenses and provenance. Public feature names describe behavior, not third-party testing tools. Do not add comparison/credit narratives to product documentation.

## Verification

Run `python tools/repository-checks/check_repository.py` and relevant tests. For all public projects use `dotnet restore AntiCheat.Public.slnf --locked-mode` then `dotnet test AntiCheat.Public.slnf -c Release --no-restore`.

Public CI does not build the complete adapter or prove live gameplay. Native changes require the exact external runtime, plugin build and affected adapter tests. Missing dependencies are a named blocker, not a reason to fabricate API stubs or claim success. Continue independent tasks while a native test is blocked.

Use a failing regression before a fix when feasible. Include legal counterexamples, identity changes, slot/entity reuse, cancellation, reconnect, cleanup and resource bounds for relevant detectors. GUI tests are required when the claim depends on unmodified-client behavior, not for every pure function change.

## Keep public facts synchronized

Edit `docs/capabilities.json` for capability changes and run `python tools/repository-checks/check_repository.py --write`. Generated tables in README and STATUS must not be hand-edited. Update the matching ROADMAP task and known limitation in the same change. Explain architecture changes in `docs/ARCHITECTURE.md`; record durable design decisions in `docs/DEVELOPMENT.md` rather than adding another milestone report.

## Handoff

Report task ID, commit, changed modules, actual test commands, passed/failed/skipped/blocked results, current behavior, remaining risks and next task. Distinguish source integration, public unit tests, native tests, client tests and production admission. Do not claim all files were audited unless they were. Do not claim coverage percentages from test counts. Leave the working tree and failures inspectable.
