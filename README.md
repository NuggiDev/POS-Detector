# POS / Cash Detector

Native .NET Windows app for telling apart card/POS payments from normal cash.

It supports:

- POS reader sends text like a keyboard: scan/tap while the app is focused, then Enter if the reader does not do it automatically.
- POS reader sends serial data over a COM port: enter the port, for example `COM3`, then Start Serial.
- Cash payment: press `F9` or click Cash.
- Every detection is saved to `payments.csv`.

## Run

```powershell
dotnet run
```

## Publish an EXE

```powershell
dotnet publish -c Release -r win-arm64 --self-contained false
```

The app will be under `bin\Release\net8.0-windows\win-arm64\publish`.

If your PC has x64 .NET instead of ARM64 .NET, use:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

## GitHub Releases

The release workflow builds three Windows executables:

- `POS-Detector-win-x86.exe`
- `POS-Detector-win-x64.exe`
- `POS-Detector-win-arm64.exe`

Create and push a version tag to publish a GitHub release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Configure

The first run creates `detector_config.json`.

```json
{
  "PosKeywords": ["APPROVED", "AUTH", "PAID", "CARD", "POS", "TRANSACTION"],
  "MinimumSignalLength": 3,
  "DefaultBaudRate": 9600,
  "LogFile": "payments.csv"
}
```

If your reader sends a specific word or code, add it to `PosKeywords`.

## How it decides

- If the POS reader fires any valid signal, the app records `POS`.
- If no POS signal fires and you press cash, the app records `CASH`.
- Very short signals are marked `UNKNOWN`, so random noise does not become a payment.

If your reader works through an API, webhook, printer cable, or a cash register protocol instead of keyboard/COM, this can be adapted once you know the model and connection type.
