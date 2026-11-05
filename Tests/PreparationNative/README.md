# Construction preparation native contract tests

Run `dotnet run --project Tests/PreparationNative/PreparationNative.csproj` from the Mod repository.

The project compiles the production `ConstructionPreparation.cs` against deterministic native fixtures. It checks native qualification, exact Thing and map binding, cell-based mining identity, precise cancellation, retained designation undo, and the actual side effects that the adapter must guard: god-mode execution, instant deconstruction, mining cancelling SmoothWall, plant cutting cancelling other instructions, and thing designation clearing Forbidden.

The adapter issues ordinary work designations. It does not finish construction, mining, deconstruction or plant cutting. API compatibility is separately checked by the production Mod build against the pinned RimWorld 1.6 reference assemblies; runtime execution remains part of the coordinated real-game checks.
