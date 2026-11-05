# Save-scoped native identities

Run `dotnet run --project Tests/NativeIdentity/NativeIdentity.csproj` from the Mod repository.

This project compiles the production `NativeObjectIdentities.cs` against a deterministic Scribe fixture with saving, loading variables, cross-reference resolution and post-load initialization phases. It checks identity continuity across a reconstructed game graph, native ID reuse, omission of deleted originals from saved cross-references, preservation of deleted originals in RAM for same-epoch undo, missing references, type mismatches and ambiguous records.

The fixture verifies the registry logic without launching RimWorld. The production Mod build separately checks compatibility with the pinned native reference assemblies. Real save/load validation is part of the coordinated game verification workflow.
