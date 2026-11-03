# Installation

## Single package
Only **`Hihooi`** is published (v0.3.0+).

```bash
dotnet pack src/Hihooi/Hihooi.csproj -c Release -o artifacts
dotnet add package Hihooi --source ./artifacts
```

Package contents (`lib/net8.0/`):
- `Hihooi.dll`
- `Hihooi.Core.dll`
- `Hihooi.SqlServer.dll`
- `Hihooi.Redis.dll`
- `Hihooi.AspNetCore.dll` (dashboard; used only from ASP.NET Core hosts)

## Verify with sample
```bash
dotnet restore samples/Hihooi.WebHost/Hihooi.WebHost.csproj
dotnet run --project samples/Hihooi.WebHost -c Release
# open / and /hihooi/ (API key: dev-hihooi-key)
```
