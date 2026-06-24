# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet restore
dotnet build
dotnet run
```

Target framework: `net10.0-windows10.0.17763.0` (WPF WinExe). Custom entry point is `Program.Main()` (not App), which initializes Generic Host + DI, then launches `MainWindow`.

No test project is configured yet.

## Architecture Overview

**MVVM + DI** — CommunityToolkit.Mvvm for source-generated MVVM, Microsoft.Extensions.Hosting for DI container. All ViewModels and services are registered in `Program.ConfigureServices()`.

**Navigation** — Custom region-based system (`Services/NavigationService.cs`). Views register `ContentControl` regions (`RegionNames` constants: `Shell`, `Main`, `Modal`, `Sidebar`). Navigation maps views to ViewModels via `[NavigationViewModel]` attribute, manual registration, or naming convention. Supports history (`GoBackAsync`), weak-reference caching, and interceptor pipeline (`INavigationInterceptor`).

**MainWindow** acts as the shell — it hosts `BaseLayoutView` which contains the main `ContentControl` where all page Views are navigated.

### Device Connection Layer (current branch: `DeviceConnect`)

Three hardware devices managed by a unified `IDeviceConnectionManager`:

| Device | Interface | Driver | Protocol |
|---|---|---|---|
| PLC (松下 FP0H) | `IPlcDevice` | `PlcCommunicationAdapter` → `TcpClientPLCMotionService` | Modbus TCP :502 |
| 万用表 (固纬 GDM-9060) | `IMultimeterDevice` | `GwInstekGDM9060Driver` | SCPI over TCP :5025 |
| 扫描枪 (霍尼韦尔 H1900) | `IScannerDevice` | `HoneywellH1900Scanner` | Serial (USB虚拟串口) |

All devices implement `ICommunicationDevice` (`ConnectAsync`/`DisconnectAsync`/`IsConnected`/`ConnectionStateChanged`). The `DeviceConnectionManager` (singleton) loads config from `IDeviceSettingsService` at startup, injects settings into drivers, then connects all three in parallel with exponential backoff retry. ViewModels subscribe to its events rather than managing connections directly.

`PlcCommunicationAdapter` adapts `TcpClientPLCMotionService` (which uses `Action<bool>` delegates) to the `ICommunicationDevice` interface (which uses `EventHandler<bool>`).

### Detection Flow

`InspectionEngine` orchestrates the full test cycle: PLC relay switching → multimeter measurement → judgment → next pin → save. It uses `SemaphoreSlim` for thread safety and `CancellationToken` for abort. Test items come from `PlanModel.InspectItems`, each having a pin name, check condition (OPEN/SHORT), and physical units.

### Data Model

- **EF Core entities**: `LogRecord` (main test record) → `PinResult` (per-pin detail). SQLite via `AppDbContext`.
- **Plan storage**: JSON files on disk per machine type — `{root}/机种名/方案名.json`. `PlanModel` contains `MachineType`, `PlanName`, and `List<PlanItem>`. `IPlanStorageService` handles CRUD.
- **Test record storage**: `ITestRecordStorage` interface with dual implementations — `CsvTestRecordStorage` (active, injected) and `SqliteTestRecordStorage` (legacy). CSV storage uses `CsvStoragePathManager` with UTF-8 BOM encoding.
- **Operator management**: `IOperatorStateService` (current operator, login/logout) + `IOperatorStorageService` (JSON file persistence).

### ViewModels

11 ViewModels. Key ones:
- `TestPageViewModel` — the main test page; subscribes to `IDeviceConnectionManager` events, shows real-time connection status, controls detection via `InspectionEngine`. Has a `TestUIState` enum: `Ready → CanStart → Testing → PendingSave`.
- `PlanSettingViewModel` / `PlanEditViewModel` — manage detection plans (machine type → plan hierarchy).
- `LogDataViewModel` — reads CSV log files, dynamic column generation, composite search.
- `SystemSettingsViewModel` — device communication parameters (IP, port, baud rate).

### WPF Value Converters

Located in `Common/Converters/`. Notable: `JudgmentToBackgroundConverter` (OK=green, NG=red), `JudgmentToForegroundConverter`, `ConnectionStatusToBackgroundConverter`, `TestStatusToColorConverter`.

## Configuration

`appsettings.json` — database connection strings (SQLite/SQL Server), Serilog settings, `CsvStorage` (root path, max rows, encoding), `PlanStorage` (max schemes per file).

`DeviceSettings.json` (in AppConfig/DeviceConfigs) — hardware communication parameters per device.

Database: `app.db` (SQLite, in repo root). In DEBUG mode, `Program.cs` sets `RECREATE_DATABASE_ON_EACH_RUN = true` — the database is dropped and recreated on each launch with seed data.

## Key Conventions

- **Chinese UI/comments** — user-facing strings and code comments are in Chinese. This is a Chinese-language industrial inspection system.
- **ObservableProperty** — CommunityToolkit.Mvvm source generators used throughout ViewModels and models (e.g., `[ObservableProperty]` on private fields).
- **Async patterns** — services use `async`/`await` with `CancellationToken` support. Navigation lifecycle: `INavigationAware.OnNavigatedToAsync` / `OnNavigatedFromAsync`.
- **Dispatcher** — all UI-bound state updates route through `Application.Current.Dispatcher.Invoke()` (see `DeviceConnectionManager` event handlers).
- **File-scoped namespaces** — standard in newer .NET.

## Legacy / Unused Code

- `FinsTcpUtil.cs` (欧姆龙 FINS protocol) — not used; current PLC uses Modbus TCP.
- `ITcpServerPLCMotionService` — reserved for future server mode, not implemented.
- `MainViewModel.cs` / `MainWindowViewModel.cs` — empty stubs.
- `SqliteTestRecordStorage` — registered but not injected via `ITestRecordStorage`; CSV implementation is active.

