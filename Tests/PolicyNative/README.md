# Native policy, bed and area contract tests

Run `dotnet run --project Tests/PolicyNative/PolicyNative.csproj` from the Mod repository.

This project compiles the production `PolicyReadings`, `PolicyWrite`, `BedWrite`, `AreaZoneReadings` and `AreaZoneWrite` files against a small deterministic native fixture. It exercises their actual validation, state transitions, readback, retained-object undo and ledger reconstruction. The fixture models the relevant observed RimWorld 1.6 behaviors, including lazy policy assignment, food-policy ID reuse, world/temporary references, native bed capacity, roof conflicts, automatic Home expansion and production references to stockpiles.

The fixture does not substitute for the Mod build against the pinned RimWorld reference assemblies or for real-game verification. API compatibility is checked by an isolated production build; Unity lifecycle hooks and real-game progression require the coordinated runtime verification workflow.
