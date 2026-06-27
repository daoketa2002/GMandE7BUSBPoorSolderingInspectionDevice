# PLC Module Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor PLC code so application business logic depends on one PLC device abstraction while preserving extensibility for Modbus diagnostics, polling, alarms, and future PLC actions.

**Architecture:** Introduce `IPlcDevice` as the single business-facing PLC entrypoint. Add a lower-level `IModbusTcpClient` for reusable Modbus TCP communication. Implement `Fp0hPlcDevice` on top of the Modbus client. Migrate `InspectionEngine` away from direct `ITcpClientPLCMotionService` usage before cleaning old PLC wrappers.

**Tech Stack:** C# 13, .NET 10 WPF, Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Logging, Serilog-backed logging, existing Modbus TCP helper classes.

---

## File Structure

- Modify: `Interfaces/IPlcDevice.cs`  
  Expand from lifecycle-only marker interface to the business-facing PLC device interface.

- Create: `Interfaces/IModbusTcpClient.cs`  
  Defines reusable Modbus TCP operations independent of FP0H business meaning.

- Create: `Models/PLC动作控制/PlcAddressMap.cs`  
  Moves PLC address mapping out of `InspectionEngine` into a reusable model.

- Create: `Models/PLC动作控制/PlcOperationResult.cs`  
  Provides a consistent PLC operation result model.

- Create: `Models/PLC动作控制/PlcNotification.cs`  
  Provides communication notification data for logging, UI, and diagnostics.

- Create or update: `Models/PLC动作控制/AlarmState.cs`  
  Keep or simplify the existing alarm model as the future PLC alarm state container.

- Create: `AppConfig/DeviceConfigs/ModbusTcpClientOptions.cs`  
  Holds runtime TCP and Modbus connection options derived from `FP0HCommunicationConfig`.

- Create: `Services/TcpModbus/ModbusTcpClient.cs`  
  Provides reusable Modbus TCP connection and request/response handling.

- Create: `Devices/Plc/Fp0hPlcDevice.cs`  
  Implements `IPlcDevice` for Panasonic FP0H through `IModbusTcpClient`.

- Modify: `Program.cs`  
  Registers `IModbusTcpClient` and `IPlcDevice` with the new implementations.

- Modify: `Services/InspectionEngine.cs`  
  Depends on `IPlcDevice` instead of `ITcpClientPLCMotionService`.

- Modify: `Services/DeviceConnections/DeviceConfigurationApplier.cs`  
  Applies PLC configuration through `IPlcDevice.ApplyConfig(...)` instead of type-checking `PlcCommunicationAdapter`.

- Later cleanup candidates: `Services/TcpModbus/PlcCommunicationAdapter.cs`, `Services/TcpModbus/TcpPLCMotionWPFUIModbusService.cs`, `Interfaces/ITcpClientPLCMotionService.cs`, `Interfaces/ITcpServerPLCMotionService.cs`, `PLC通讯模块/FinsTcpUtil.cs`.

---

### Task 1: Expand PLC Device Contract

**Files:**
- Modify: `Interfaces/IPlcDevice.cs`
- Create: `Models/PLC动作控制/PlcOperationResult.cs`
- Create: `Models/PLC动作控制/PlcNotification.cs`
- Create: `Models/PLC动作控制/PlcAddressMap.cs`

- [ ] **Step 1: Extend `IPlcDevice`**

Add configuration and business methods to `IPlcDevice` while keeping `ICommunicationDevice` inheritance:

```csharp
void ApplyConfig(FP0HCommunicationConfig config);
Task<PlcOperationResult> ReadStartSignalAsync(CancellationToken ct = default);
Task<PlcOperationResult> SetBusyAsync(bool value, CancellationToken ct = default);
Task<PlcOperationResult> SetOkAsync(bool value, CancellationToken ct = default);
Task<PlcOperationResult> SetNgAsync(bool value, CancellationToken ct = default);
Task<PlcOperationResult> SetErrorAsync(bool value, CancellationToken ct = default);
Task<PlcOperationResult> SelectTestPointAsync(int testPointIndex, CancellationToken ct = default);
event EventHandler<PlcNotification>? NotificationReceived;
event EventHandler<AlarmState>? AlarmStateChanged;
```

- [ ] **Step 2: Add `PlcOperationResult`**

Create a small result model with `IsSuccess`, `Message`, and optional `ModbusResponse`.

- [ ] **Step 3: Add `PlcNotification`**

Create a notification model with notification type, message, operation name, and timestamp.

- [ ] **Step 4: Move PLC address map**

Move the `PlcAddressMap` currently nested in `InspectionEngine` into `Models/PLC动作控制/PlcAddressMap.cs`. Keep current default addresses unchanged.

- [ ] **Step 5: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: New models compile. Existing classes that implement `IPlcDevice` may fail until Task 3 updates the implementation; record those compile errors as expected transitional errors if working task-by-task.

---

### Task 2: Add Modbus TCP Client Contract

**Files:**
- Create: `Interfaces/IModbusTcpClient.cs`
- Create: `AppConfig/DeviceConfigs/ModbusTcpClientOptions.cs`

- [ ] **Step 1: Define `IModbusTcpClient`**

Include connection lifecycle and common Modbus operations:

```csharp
bool IsConnected { get; }
event EventHandler<bool>? ConnectionStateChanged;
event EventHandler<PlcNotification>? NotificationReceived;
Task ConnectAsync(ModbusTcpClientOptions options, CancellationToken ct = default);
Task DisconnectAsync();
Task<ModbusResponse?> ReadCoilsAsync(byte unitId, ushort startAddress, ushort quantity, CancellationToken ct = default);
Task<ModbusResponse?> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity, CancellationToken ct = default);
Task<ModbusResponse?> WriteSingleCoilAsync(byte unitId, ushort address, bool value, CancellationToken ct = default);
Task<ModbusResponse?> WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken ct = default);
Task<ModbusResponse?> WriteMultipleRegistersAsync(byte unitId, ushort startAddress, ushort[] values, CancellationToken ct = default);
Task<ModbusResponse?> SendCustomRequestAsync(byte[] requestFrame, CancellationToken ct = default);
```

- [ ] **Step 2: Define `ModbusTcpClientOptions`**

Map fields from `FP0HCommunicationConfig`: host, port, slave ID, receive timeout, send timeout, reconnect delay, max reconnect attempts, health check mode, health check interval, and last-data timeout.

- [ ] **Step 3: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: Interfaces and options compile independently.

---

### Task 3: Implement FP0H PLC Device

**Files:**
- Create: `Devices/Plc/Fp0hPlcDevice.cs`
- Create or modify: `Services/TcpModbus/ModbusTcpClient.cs`
- Modify: `Program.cs`

- [ ] **Step 1: Implement `ModbusTcpClient`**

Reuse the stable pieces of `TcpClientPLCMotionService`: TCP connect/disconnect, receive loop, transaction ID matching, timeout handling, Modbus response parsing, and request sending. Keep `ModbusTcpMessageHelper` and `ModbusResponse` as protocol helpers/models.

- [ ] **Step 2: Implement `Fp0hPlcDevice`**

`Fp0hPlcDevice` should:

- store the latest `FP0HCommunicationConfig`;
- convert it to `ModbusTcpClientOptions`;
- implement `ConnectAsync` and `DisconnectAsync` by delegating to `IModbusTcpClient`;
- forward `ConnectionStateChanged`;
- translate business methods to Modbus operations using `PlcAddressMap`;
- log user/audit-relevant PLC operations with `Warning` where appropriate.

- [ ] **Step 3: Register new services**

In `Program.cs`, register:

```csharp
services.AddSingleton<IModbusTcpClient, ModbusTcpClient>();
services.AddSingleton<Fp0hPlcDevice>();
services.AddSingleton<IPlcDevice>(sp => sp.GetRequiredService<Fp0hPlcDevice>());
```

Keep old `TcpClientPLCMotionService` registration during the migration if any class still depends on it.

- [ ] **Step 4: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: `IPlcDevice` has a concrete implementation and DI registrations compile.

---

### Task 4: Migrate Configuration Application

**Files:**
- Modify: `Services/DeviceConnections/DeviceConfigurationApplier.cs`

- [ ] **Step 1: Remove PLC adapter type check**

Change PLC configuration application to call:

```csharp
plcDevice.ApplyConfig(settings.FP0HCommunication);
```

Do not require `plcDevice is PlcCommunicationAdapter`.

- [ ] **Step 2: Preserve warnings**

If `settings.FP0HCommunication` is null, keep `Warning` logging and skip application.

- [ ] **Step 3: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: Device configuration no longer depends on the PLC adapter.

---

### Task 5: Migrate InspectionEngine To IPlcDevice

**Files:**
- Modify: `Services/InspectionEngine.cs`

- [ ] **Step 1: Replace dependency**

Replace constructor dependency from `ITcpClientPLCMotionService` to `IPlcDevice`.

- [ ] **Step 2: Replace direct Modbus writes**

Replace current direct write calls with business methods:

```csharp
await _plcDevice.SelectTestPointAsync(stepIndex, ct);
await _plcDevice.SetOkAsync(true, ct);
await _plcDevice.SetNgAsync(true, ct);
await _plcDevice.SetBusyAsync(true, ct);
await _plcDevice.SetErrorAsync(true, ct);
```

Choose exact method calls according to the existing detection flow. Do not change test result semantics.

- [ ] **Step 3: Remove nested address map**

Remove the nested `PlcAddressMap` type from `InspectionEngine` after all references point to the shared model.

- [ ] **Step 4: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: `InspectionEngine` no longer references `ITcpClientPLCMotionService`.

---

### Task 6: Review ViewModel And System Settings Usage

**Files:**
- Verify: `ViewModels/TestPageViewModel.cs`
- Verify: `ViewModels/SystemSettingsViewModel.cs`
- Verify: other ViewModels that mention PLC connection state

- [ ] **Step 1: Keep UI connection flow stable**

ViewModels should continue using `IDeviceConnectionManager` for connection status and reconnect commands.

- [ ] **Step 2: Keep temporary connection test until replacement is needed**

`SystemSettingsViewModel` may keep its current temporary Modbus test connection in this phase. Do not force it through `IPlcDevice` unless doing so does not affect saved settings or UI behavior.

- [ ] **Step 3: Build check**

Run:

```powershell
dotnet build --no-restore
```

Expected: ViewModels do not require broad PLC-related changes.

---

### Task 7: Clean Up Old PLC Entry Points

**Files:**
- Candidate removal or archival only after references are gone:
  - `Services/TcpModbus/PlcCommunicationAdapter.cs`
  - `Services/TcpModbus/TcpPLCMotionWPFUIModbusService.cs`
  - `Interfaces/ITcpClientPLCMotionService.cs`
  - `Interfaces/ITcpServerPLCMotionService.cs`
  - `PLC通讯模块/FinsTcpUtil.cs`

- [ ] **Step 1: Check references**

Run:

```powershell
rg -n "PlcCommunicationAdapter|TcpPLCMotionWPFUIModbusService|ITcpClientPLCMotionService|ITcpServerPLCMotionService|FinsTcpUtil" .
```

Expected: Only unused registrations, docs, or legacy comments remain before deletion.

- [ ] **Step 2: Remove DI registrations**

Remove registrations for old PLC adapters and UI wrapper only after no runtime class depends on them.

- [ ] **Step 3: Decide archive vs delete**

If project policy prefers retaining historical protocol code, move FINS-related files to a clearly named legacy folder. Otherwise delete unused files after confirming build passes.

- [ ] **Step 4: Full build**

Run:

```powershell
dotnet build
```

Expected: Build succeeds. If NuGet access is blocked, record the exact `NU1301` or network error and run `dotnet build --no-restore` after dependencies are available.

---

### Task 8: Final Verification

**Files:**
- Verify: `Interfaces/IPlcDevice.cs`
- Verify: `Interfaces/IModbusTcpClient.cs`
- Verify: `Devices/Plc/Fp0hPlcDevice.cs`
- Verify: `Services/InspectionEngine.cs`
- Verify: `Program.cs`

- [ ] **Step 1: Confirm business dependency direction**

Run:

```powershell
rg -n "ITcpClientPLCMotionService|ExecuteWriteOperationAsync|ExecuteReadOperationAsync" Services ViewModels Interfaces Program.cs
```

Expected: `InspectionEngine` does not use `ITcpClientPLCMotionService`. Remaining references should be limited to legacy implementation or diagnostics if intentionally retained.

- [ ] **Step 2: Confirm PLC API usage**

Run:

```powershell
rg -n "SetBusyAsync|SetOkAsync|SetNgAsync|SetErrorAsync|SelectTestPointAsync|ReadStartSignalAsync" Services ViewModels Interfaces Devices
```

Expected: Detection flow uses `IPlcDevice` business methods.

- [ ] **Step 3: Review diff scope**

Run:

```powershell
git diff --stat
```

Expected: Diffs are limited to PLC interfaces, PLC implementation, DI registration, and direct PLC call migration.

- [ ] **Step 4: Hardware verification when available**

With the FP0H connected, verify:

- PLC connects using saved settings;
- manual PLC reconnect updates UI state;
- Busy/OK/NG/Error outputs write correctly;
- test point selection writes the expected register or relay address;
- disconnect and reconnect recover without restarting the application.
