# Cure Please

Party healing/buffing bot for Final Fantasy XI. Actively maintained continuation of [DanielHazzard/Cure-Please](https://github.com/DanielHazzard/Cure-Please) (discontinued at 2.0.4.4), being modernized to .NET 10 / WPF on top of [FFXI.Memory](https://github.com/MaxDev86/FFXI.Memory).

## Repository layout

- `src/CurePlease/` — the new .NET 10 WPF application (port in progress)
- `legacy/` — original 2.0.4.4 WinForms source, kept as the reference for the port.
  Does not build as-is: the committed proprietary EliteAPI binaries were removed.
- `AshitaAddon/`, `WindowerAddon/` — companion addons for Ashita and Windower

## Building

Requires the .NET 10 SDK. The app is x86-only because the FFXI client is 32-bit.

```
dotnet build CurePlease.slnx
```

FFXI.Memory is referenced as a project and is expected to be cloned alongside this
repository (e.g. `D:\Code\repos\Cure-Please` next to `D:\Code\repos\FFXI.Memory`).

## License

GPL v2 — see [LICENSE](LICENSE).
