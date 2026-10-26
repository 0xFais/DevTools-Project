# Contributing

1. Build and validate with `./Build.ps1 -Test` on Windows using the .NET 10 SDK.
2. Put feature logic in its module folder. Keep registry/process work out of views.
3. Register built-in modules in `BuiltInModules.Create`; independent modules should
   use the extension contract instead.
4. Use observable page state, async actions and the supplied cancellation token.
   Ask for confirmation in the product before changing Windows settings.
5. Add focused tests for parsing, ownership/restoration, process behavior or other
   logic affected by the change. Avoid tests that alter the developer's Windows state.
6. Use the shared brushes and templates. Add page-specific views only when the
   declarative form is insufficient, keeping their feature logic separate.
7. Include the problem, resulting behavior and relevant checks in change descriptions.

`dotnet format DevTools.sln --no-restore` applies the included formatting rules.
The test harness has no NuGet test-framework dependency; new checks belong in
`tests/DevTools.Tests`. Off-screen render output is written under `artifacts/tests`.
For changes to registry/service/download behavior, document the target-machine
manual checks performed in a controlled Windows installation.
