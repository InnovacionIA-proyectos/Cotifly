# 5. Stack tecnológico

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

## 5.1 Resumen

- .NET 10 / ASP.NET Core 10, C# `latest`, `Nullable` y `TreatWarningsAsErrors` activos, `AnalysisLevel` fijo en `10.0` (`Directory.Build.props`).
- Configuración: `Microsoft.Extensions.Options` con DataAnnotations y `ValidateOnStart`.
- Central Package Management con versiones fijas (`Directory.Packages.props`).
- Persistencia: EF Core 10 (`Microsoft.EntityFrameworkCore.SqlServer`), health checks (`Microsoft.Extensions.Diagnostics.HealthChecks`).
- Pruebas: xUnit v3 sobre Microsoft.Testing.Platform, AwesomeAssertions (Apache-2.0), ArchUnitNET, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.OpenApi` + `Microsoft.OpenApi.YamlReader`, Testcontainers.MsSql (SQL Server 2019), Reqnroll (`Reqnroll.xunit.v3`).
- Dobles de prueba: NSubstitute (BSD-3-Clause), desde #10.
- HTTP saliente (#13): `Microsoft.Extensions.Http` (`IHttpClientFactory`, clientes tipados) y `Microsoft.Extensions.Http.Resilience` 10.10.0 (MIT, sobre Polly 8, BSD-3-Clause) para SAP TM OData; `Microsoft.Extensions.Logging.Abstractions` en Aplicacion (warning de SAP caído). Las pruebas usan un `HttpMessageHandler` falso (`tests/Comun/clsSapTmFalso`), sin red.
- PDF (#11): **PDFsharp-MigraDoc 6.2.4** (MIT; build multiplataforma sin GDI ni WPF) con Liberation Sans embebida (SIL OFL 1.1). Correo: el microservicio interno MailSender por HTTP (HU-033, #199, ADR-023; MailKit se retiró). Solo pruebas: **PdfPig 0.1.16** (Apache-2.0) para extraer texto, fuentes e imágenes de los PDF.
- Agente (#12 parte C, todo MIT): **Microsoft.SemanticKernel.Core 1.80.1** (Kernel, `KernelFunction`, `IFunctionInvocationFilter`; en Aplicación), **Microsoft.Extensions.AI 10.10.0** / **Abstractions 10.10.1** (`IChatClient`, `ChatClientBuilder`, `UseLogging`), **Microsoft.Extensions.AI.OpenAI 10.10.1** con **OpenAI 2.14.0** (openai, azure_openai, openai_compatible, gemini, ollama) y **Anthropic 12.50.0** (SDK oficial de Anthropic con su `IChatClient`). Google Chat (retirado en HU-013.9, #105) no usaba SDK de Google: el JWT y el token de la cuenta de servicio iban con `Microsoft.IdentityModel.JsonWebTokens`. Solo pruebas: NSubstitute también en `Cotifly.Infraestructura.Tests`.
- Integración continua (#14): GitHub Actions (`.github/workflows/backend-dotnet.yml`), reportes TRX y de cobertura sobre MTP, ReportGenerator para unir la cobertura, `redocly lint` del contrato. Detalle en [08-testing](08-testing.md#87-integración-continua).

## 5.2 Paquetes NuGet y licencias

Todas las versiones se fijan en `Directory.Packages.props` (Central Package Management con pinning transitivo). Criterio de licencias de [ADR-010](../specs/decisiones/ADR-010-metodologia-sdd-tdd-bdd.md) §7: solo MIT, Apache-2.0, BSD, SIL OFL o similares; nada con licencia comercial condicionada a ingresos (por eso no QuestPDF ni FluentAssertions 8).

**Producción** (viajan en la imagen):

| Paquete | Versión | Licencia | Capa | Uso |
|---|---|---|---|---|
| Microsoft.EntityFrameworkCore.SqlServer | 10.0.12 | MIT | Infraestructura | EF Core Database First sobre SQL Server 2019 |
| Microsoft.Extensions.DependencyInjection.Abstractions, Hosting.Abstractions, Logging.Abstractions | 10.0.12 | MIT | Aplicacion / Infraestructura | Registro de dependencias, servicios en segundo plano, logs |
| Microsoft.Extensions.Options, Options.ConfigurationExtensions, Options.DataAnnotations | 10.0.12 | MIT | Aplicacion / Infraestructura | Configuración validada al arrancar |
| Microsoft.Extensions.Diagnostics, Diagnostics.HealthChecks | 10.0.12 | MIT | Infraestructura | `IMeterFactory` (medidor `Cotifly.Agente`) y health checks |
| Microsoft.Extensions.Http | 10.0.12 | MIT | Infraestructura | `IHttpClientFactory` y clientes tipados |
| Microsoft.Extensions.Http.Resilience | 10.10.0 | MIT (trae Polly 8, BSD-3-Clause) | Infraestructura | Reintentos y límites de SAP TM OData |
| PDFsharp-MigraDoc | 6.2.4 | MIT | Infraestructura | Maquetación del PDF (ADR-006) |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.12 | MIT | Api | Validación del access token |
| Microsoft.IdentityModel.JsonWebTokens | 8.19.2 | MIT | Infraestructura | Emisión del JWT (hasta HU-013.9 también el token de la cuenta de servicio de Google y la verificación del JWT de Google Chat) |
| Microsoft.SemanticKernel.Core | 1.80.1 | MIT | Aplicacion | Kernel, `KernelFunction`, `IFunctionInvocationFilter` |
| Microsoft.Extensions.AI | 10.10.0 | MIT | Infraestructura | `ChatClientBuilder`, `UseLogging` |
| Microsoft.Extensions.AI.Abstractions | 10.10.1 | MIT | transitiva, fijada en CPM | `IChatClient`, `ChatResponseFormat`, `AIJsonUtilities` |
| Microsoft.Extensions.AI.OpenAI (trae OpenAI 2.14.0, MIT) | 10.10.1 | MIT | Infraestructura | Proveedores `openai`, `azure_openai`, `openai_compatible`, `gemini`, `ollama` |
| Anthropic | 12.50.0 | MIT | Infraestructura | Proveedor `anthropic` (SDK oficial con su `IChatClient`) |

Recursos embebidos: Liberation Sans 2.1.5 (SIL Open Font License 1.1; la licencia viaja en `/app/licencias/LiberationSans-OFL.txt`), logo e imágenes por línea de Botero Soto.

**Pruebas** (no viajan en la imagen):

| Paquete | Versión | Licencia | Uso |
|---|---|---|---|
| xunit.v3 | 4.0.1 | Apache-2.0 | Framework de pruebas sobre Microsoft.Testing.Platform 2.4 |
| AwesomeAssertions | 9.6.0 | Apache-2.0 | Aserciones (fork de FluentAssertions 7) |
| NSubstitute | 5.3.0 | BSD-3-Clause | Dobles de prueba de los puertos |
| TngTech.ArchUnitNET | 0.13.4 | Apache-2.0 | Reglas de arquitectura |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | MIT | `WebApplicationFactory` |
| Microsoft.Extensions.Configuration | 10.0.12 | MIT | Configuración en memoria de las pruebas |
| Microsoft.OpenApi, Microsoft.OpenApi.YamlReader | 2.12.2 | MIT | Lectura de `specs/openapi.yaml` para las pruebas de contrato |
| Testcontainers.MsSql | 4.15.0 | MIT | SQL Server 2019 en contenedor |
| Reqnroll.xunit.v3 | 3.3.4 | BSD-3-Clause | BDD con los `.feature` de `specs/features/` |
| PdfPig | 0.1.16 | Apache-2.0 | Lectura de los PDF generados |
| Microsoft.Testing.Extensions.TrxReport | 2.4.0 | MIT | Reporte TRX (`--report-trx`), desde #14 |
| Microsoft.Testing.Extensions.CodeCoverage | 18.11.2 | Microsoft Software License Terms (gratuita) | Cobertura (`--coverage`), desde #14. Excepción acotada al criterio de licencias: la preveía ADR-010 para CI, solo corre en las pruebas y no se distribuye. El paquete exige aceptar la licencia (`requireLicenseAcceptance`) y su cláusula 4.a permite recolectar datos de uso: el CI fija `TESTINGPLATFORM_TELEMETRY_OPTOUT` (ADR-010 §7) |

**Herramientas de CI** (no son dependencias del código): ReportGenerator 5.5.11 (Apache-2.0), Redocly CLI 2.54.2 (MIT), actionlint (MIT, solo para validar el workflow en local).

## 5.3 Imágenes de contenedor

| Imagen | Uso | Licencia |
|---|---|---|
| `mcr.microsoft.com/dotnet/sdk:10.0.401-noble` | Etapa de build del `Dockerfile`; build y pruebas en local | MIT (.NET) |
| `mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra` | Runtime de la imagen `cotifly-api` | MIT (.NET) sobre Ubuntu 24.04 mínimo |
| `mcr.microsoft.com/mssql/server:2019-latest` | Testcontainers y entorno local | EULA de SQL Server; edición Developer, solo desarrollo y pruebas |
| (la misma `mcr.microsoft.com/mssql/server:2019-latest`) | `sqlcmd` 18 de los scripts de entorno local (`/opt/mssql-tools18/bin/sqlcmd`); `mcr.microsoft.com/mssql-tools18` no existe en MCR | EULA de SQL Server |
