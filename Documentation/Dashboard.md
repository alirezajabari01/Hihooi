# Dashboard (embedded in Hihooi NuGet)

The dashboard ships **inside the single `Hihooi` package** as `Hihooi.AspNetCore.dll` + embedded `wwwroot/index.html`.

## Console / Worker
Do **not** call `AddHihooiDashboard` / `MapHihooiDashboard`.  
`EnableDashboard` on options is ignored without an ASP.NET Core host. No AspNetCore framework reference is forced by the main `Hihooi.dll`.

## ASP.NET Core host
```csharp
builder.Services.AddHihooi(o =>
{
    o.EnableDashboard = true;
    o.DashboardPath = "/hihooi"; // default
    o.DashboardAuth.RequireAuthorization = true;
    o.DashboardAuth.ApiKey = "secret"; // header X-Hihooi-Dashboard-Key
});
builder.Services.AddHihooiDashboard();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHihooiDashboard();
```

UI: `{DashboardPath}/`  
API: `{DashboardPath}/api/metrics`  
Hub: `{DashboardPath}/hubs/metrics`

Auth: API key header and/or already-authenticated host user. Set `RequireAuthorization = false` only for trusted local demos.

## Sample
`samples/Hihooi.WebHost` references **only** `PackageReference Include="Hihooi"` from `artifacts/`.
